// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Cli
{
    [Collection(IsolatedProxyTests.Name)]
    public class XmatCaptureTests
    {
        [Theory]
        [InlineData("--shape secret-canary")]
        [InlineData("--shape off --down-kbps 1000")]
        [InlineData("--shape-profile secret-canary --up-kbps 100")]
        [InlineData("--body-timeout-seconds 1801")]
        [InlineData("--listen secret-canary")]
        public void InvalidCaptureOptionsAreDataFreeAndCannotStartAListener(string options)
        {
            CliParseResult result = CliCommandLine.Parse(
                ("capture --port 8888 --duration-seconds 10 --out capture.xmatcap " + options).Split(' '));
            Assert.False(result.Succeeded);
            Assert.DoesNotContain("secret-canary", result.Error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SuppliedAuthorityCapturesHttpsBodiesWithoutGuiStorage(bool http2)
        {
            using var directory = new CliTestDirectory();
            GuiStorageProbe gui = GuiStorageProbe.Capture();
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=XMAT synthetic CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(Path.Combine(directory.Root, "root.pfx"), root.Export(X509ContentType.Pfx));
            byte[] body = new byte[32 * 1024];
            Array.Fill(body, (byte)42);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask(), useTls: true);
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "3", "--out", "capture.xmatcap",
                "--root-pfx", "root.pfx", "--down-kbps", "1000");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            using var message = new HttpRequestMessage(HttpMethod.Post, origin.Url("/secure"))
            {
                Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(body),
            };
            using HttpResponseMessage response = await client.SendAsync(message);
            Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(0, run);
            using var capture = new SqLtReadonlyDatabase(Path.Combine(directory.Root, "capture.xmatcap"));
            var row = Assert.Single(capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords,
                row => row.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) == "POST");
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
            Assert.Equal(new[] { "capture.xmatcap", "root.pfx" }, directory.Entries());
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task EmptyBlockedCaptureStillKeepsHistoryForHeadlessAnalysis()
        {
            using var directory = new CliTestDirectory();
            XmatRun capture = await XmatProcess.RunAsync(
                directory.Root, "capture", "--port", AvailablePort().ToString(),
                "--duration-seconds", "1", "--out", "blocked.xmatcap", "--shape", "broken");
            CliAssert.ExitCode(0, capture);
            Assert.Equal(0, capture.Json().GetProperty("capture").GetProperty("records").GetInt64());
            XmatRun analysis = await XmatProcess.RunAsync(
                directory.Root, "analyze", "blocked.xmatcap", "--out", "analysis");
            Assert.True(analysis.Json().TryGetProperty("networkShaping", out _), analysis.Stdout);
            Assert.Equal(new[] { "blocked.xmatcap" }, directory.Entries());
        }

        [Fact]
        public async Task ExplicitBodyDeadlineReachesTheProductionReader()
        {
            using var directory = new CliTestDirectory();
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "3", "--out", "capture.xmatcap",
                "--body-timeout-seconds", "1");
            await WaitForListenerAsync(child, port);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"POST {origin.Url("/slow")} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n" +
                "Content-Length: 2\r\nConnection: close\r\n\r\na"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert.StartsWith("HTTP/1.1 408", await reader.ReadLineAsync(timeout.Token));
            Assert.Empty(origin.Requests);
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(0, run);
            using var capture = new SqLtReadonlyDatabase(Path.Combine(directory.Root, "capture.xmatcap"));
            NetworkShapingMetadata history = NetworkShapingMetadata.Parse(
                Assert.Single(capture.TableByName("CaptureDevices").Dataset.DataRecords).Str(NetworkShapingMetadata.DeviceField));
            Assert.Equal(1, Assert.Single(history.Traces).BodyTimeoutSeconds);
        }

        [Fact]
        public async Task ForegroundCaptureShapesThenDisablesAndSavesBytesAndHistory()
        {
            using var directory = new CliTestDirectory();
            GuiStorageProbe gui = GuiStorageProbe.Capture();
            byte[] body = Enumerable.Range(0, 128 * 1024).Select(index => (byte)(index % 251)).ToArray();
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            File.WriteAllText(Path.Combine(directory.Root, "profile.json"), """
                {"version":1,"initial":{"mode":"shape","downKbps":1000},
                 "schedule":[{"afterSeconds":2,"profile":{"mode":"off"}}],"bodyTimeoutSeconds":1800}
                """);
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "4", "--out", "capture.xmatcap",
                "--shape-profile", "profile.json");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            var elapsed = Stopwatch.StartNew();
            using HttpResponseMessage response = await client.PostAsync(origin.Url("/upload"), new ByteArrayContent(body));
            Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 850, 3000);
            await Task.Delay(TimeSpan.FromSeconds(2.1));
            elapsed.Restart();
            Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/after")));
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(800));
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(0, run);
            Assert.False(run.WindowSeen);
            Assert.Equal(2, run.Json().GetProperty("capture").GetProperty("records").GetInt64());
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr);
            Assert.Equal(new[] { "capture.xmatcap", "profile.json" }, directory.Entries());
            Assert.Equal(body, origin.Requests.First().Body);
            using var capture = new SqLtReadonlyDatabase(Path.Combine(directory.Root, "capture.xmatcap"));
            var rows = capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(body, Convert.FromBase64String(rows[0].Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
            Assert.All(rows, row =>
                Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody))));
            NetworkShapingMetadata history = NetworkShapingMetadata.Parse(
                Assert.Single(capture.TableByName("CaptureDevices").Dataset.DataRecords).Str(NetworkShapingMetadata.DeviceField));
            NetworkShapingTrace trace = Assert.Single(history.Traces);
            Assert.Equal(1800, trace.BodyTimeoutSeconds);
            Assert.Equal(
                new[] { NetworkShapingMode.Off, NetworkShapingMode.Shape, NetworkShapingMode.Off },
                trace.Changes.Select(change => change.Profile.Mode));
            AssertListenerReleased(port);
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task CancelledForegroundCaptureReleasesListenerAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            using var helper = new CliTestDirectory();
            using ConsoleSignal signal = await ConsoleSignal.StartAsync(helper.Root);
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "30", "--out", "capture.xmatcap");
            await WaitForListenerAsync(child, port);
            await signal.SendAsync(child.Id, ConsoleSignal.CtrlBreak);
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(130, run);
            Assert.Empty(directory.Entries());
            Assert.Equal("completed", run.Json().GetProperty("cleanup").GetProperty("state").GetString());
            AssertListenerReleased(port);
        }

        [Fact]
        public async Task OpaqueTlsStillForwardsWithoutAUserCertificate()
        {
            using var directory = new CliTestDirectory();
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "2", "--out", "capture.xmatcap");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/opaque")));
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(0, run);
            Assert.Contains(run.Json().GetProperty("warnings").EnumerateArray(),
                warning => warning.GetProperty("code").GetString() == "capture.tls.opaque");
            using var capture = new SqLtReadonlyDatabase(Path.Combine(directory.Root, "capture.xmatcap"));
            var row = Assert.Single(capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
            Assert.Equal("CONNECT", row.Str(WebServiceCaptureMethod.FieldKey_RequestMethod));
            Assert.Empty(row.Str(WebServiceCaptureMethod.FieldKey_RequestBody));
        }

        private static HttpClient Client(int port) => new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }) { Timeout = TimeSpan.FromSeconds(10) };

        private static int AvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static void AssertListenerReleased(int port)
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }

        private static async Task WaitForListenerAsync(XmatChild child, int port)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (child.HasExited)
                {
                    XmatRun run = await child.CompleteAsync();
                    Assert.Fail($"Capture exited before listening: {run.Stderr}{run.Stdout}");
                }
                try
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(IPAddress.Loopback, port);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(25);
                }
            }
            Assert.Fail("Capture did not start listening.");
        }
    }
}
