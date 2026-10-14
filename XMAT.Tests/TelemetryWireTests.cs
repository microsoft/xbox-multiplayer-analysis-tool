// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XMAT.Telemetry;
using Xunit.Abstractions;

namespace XMAT.Tests
{
    public class TelemetryWireTests(ITestOutputHelper output)
    {
        private const string TestKey = "00000000000000000000000000000000-00000000-0000-0000-0000-000000000000-0000";

        [Fact]
        public async Task EncodedCollectorPayloadContainsOnlyExplicitFieldsAndNoAmbientIdentity()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var receive = ReceivePayloadAsync(listener, cancellation.Token);
            var forbidden = "forbidden-user-token-host-path";
            var originalResourceEnvironment = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", $"enduser.id={forbidden},host.name={forbidden}");
            List<string> diagnostics = [];
            var machineId = "s:11111111-1111-1111-1111-111111111111";
            try
            {
                using var activity = new Activity("foreign-request").AddBaggage("user", forbidden).AddTag("url", forbidden).Start();
                var foreignTrace = activity.TraceId.ToString();
                using var reporter = new OneCollectorTelemetryReporter(new(true, true, TestKey, TelemetryBuildChannel.Test),
                    [
                        new("ext.app.id", "XboxMultiplayerAnalysisTool"),
                        new("ext.app.ver", "1.0.0"),
                        new("ext.os.name", "Windows"),
                        new("ext.device.localId", machineId),
                        new("ext.xbl.sbx", "TEST_SANDBOX"),
                        new("BuildChannel", "Test")
                    ], diagnostics.Add, new Uri($"http://127.0.0.1:{port}/collector/"));
                var session = Guid.NewGuid();
                reporter.Send(new(TelemetryEventKind.AppLaunched), session);
                var payload = await receive;
                output.WriteLine(payload);
                Assert.Contains("Microsoft.Gaming.XMAT.AppLaunched", payload);
                Assert.Contains(session.ToString("N"), payload);
                Assert.Contains(machineId, payload);
                Assert.Contains("TEST_SANDBOX", payload);
                Assert.DoesNotContain(forbidden, payload);
                Assert.DoesNotContain(foreignTrace, payload);
                Assert.DoesNotContain("InstallID", payload);
                Assert.DoesNotContain("service.instance.id", payload);
                using var json = JsonDocument.Parse(payload.Trim());
                Assert.Equal("Microsoft.Gaming.XMAT.AppLaunched", json.RootElement.GetProperty("name").GetString());
                Assert.Equal(session.ToString("N"), json.RootElement.GetProperty("data").GetProperty("SessionId").GetString());
                Assert.Same(activity, Activity.Current);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", originalResourceEnvironment);
            }
        }

        [Fact]
        public void TestTransportCannotRedirectAnApprovedProductionReporter()
        {
            Assert.Throws<ArgumentException>(() => new OneCollectorTelemetryReporter(
                new(true, true, TestKey, TelemetryBuildChannel.Public), [], _ => { },
                new Uri("http://127.0.0.1:12345/")));
            Assert.Throws<ArgumentException>(() => new OneCollectorTelemetryReporter(
                new(true, true, TestKey, TelemetryBuildChannel.Test), [], _ => { },
                new Uri("https://example.com/")));
        }

        private static async Task<string> ReceivePayloadAsync(TcpListener listener, CancellationToken cancellation)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            using var stream = client.GetStream();
            List<byte> header = [];
            var one = new byte[1];
            while (header.Count < 16_384)
            {
                await stream.ReadExactlyAsync(one, cancellation);
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10)
                {
                    break;
                }
            }
            var headers = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var length = int.Parse(headers.Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
            Assert.InRange(length, 1, 1_048_576);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellation);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\n{\"acc\":1}");
            await stream.WriteAsync(response, cancellation);
            if (!headers.Any(x => x.StartsWith("Content-Encoding: deflate", StringComparison.OrdinalIgnoreCase)))
            {
                return Encoding.UTF8.GetString(body);
            }
            using var compressed = new MemoryStream(body);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellation);
        }
    }
}
