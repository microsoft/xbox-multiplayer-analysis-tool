// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit.Abstractions;
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
        private readonly ITestOutputHelper _output;

        public XmatCaptureTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("--shape secret-canary", "cli.usage.invalid-value", "--shape")]
        [InlineData("--shape off --down-kbps 1000", "cli.usage.invalid-value", "--down-kbps")]
        [InlineData("--shape-profile secret-canary --up-kbps 100", "cli.usage.invalid-value", "--shape-profile")]
        [InlineData("--body-timeout-seconds 1801", "cli.usage.invalid-value", "--body-timeout-seconds")]
        [InlineData("--listen secret-canary", "cli.usage.invalid-value", "--listen")]
        [InlineData("--latency-ms 50 --jitter-ms 60", "cli.usage.invalid-value", "--jitter-ms")]
        [InlineData("--down-kbps 1000001", "cli.usage.invalid-value", "--down-kbps")]
        public void InvalidCaptureOptionsAreDataFreeAndCannotStartAListener(string options, string code, string option)
        {
            CliParseResult result = CliCommandLine.Parse(
                ("capture --port 8888 --duration-seconds 10 --out capture.xmatcap " + options).Split(' '));
            Assert.False(result.Succeeded);
            Assert.Equal(code, result.Error.Code);
            Assert.Contains(option, result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-canary", result.Error.Message);
        }

        [Theory]
        [InlineData("capture --duration-seconds 10 --out capture.xmatcap", "cli.usage.missing-option", "--port")]
        [InlineData("capture --port 8888 --out capture.xmatcap", "cli.usage.missing-option", "--duration-seconds")]
        [InlineData("capture --port 8888 --duration-seconds 10", "cli.usage.missing-option", "--out")]
        [InlineData("capture --port 8888 --duration-seconds 10 --out secret-canary.saz", "cli.usage.invalid-value", "--out")]
        [InlineData("capture --port 0 --duration-seconds 10 --out capture.xmatcap", "cli.usage.invalid-value", "--port")]
        [InlineData("capture --port 8888 --duration-seconds 86401 --out capture.xmatcap", "cli.usage.invalid-value", "--duration-seconds")]
        [InlineData("capture secret-canary --port 8888 --duration-seconds 10 --out capture.xmatcap", "cli.usage.unexpected-argument", "INPUT")]
        public void CaptureUsageErrorsNameTheOption(string commandLine, string code, string option)
        {
            CliParseResult result = CliCommandLine.Parse(commandLine.Split(' '));
            Assert.False(result.Succeeded);
            Assert.Equal(code, result.Error.Code);
            Assert.Contains(option, result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-canary", result.Error.Message);
        }

        [Fact]
        public async Task OccupiedListeningPortIsAConflictAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "capture", "--port", port.ToString(), "--duration-seconds", "1", "--out", "capture.xmatcap");
            CliAssert.ExitCode(5, run);
            Assert.Equal(new[] { "capture.listen.unavailable" }, run.ErrorCodes());
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr);
            Assert.Empty(directory.Entries());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not a certificate")]
        public async Task UnusableRootAuthorityIsInvalidInputBeforeListening(string contents)
        {
            using var directory = new CliTestDirectory();
            if (contents != null)
            {
                File.WriteAllText(Path.Join(directory.Root, "root.pfx"), contents);
            }
            int port = AvailablePort();
            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "capture", "--port", port.ToString(), "--duration-seconds", "1",
                "--out", "capture.xmatcap", "--root-pfx", "root.pfx");
            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { "capture.ca.invalid" }, run.ErrorCodes());
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr);
            Assert.DoesNotContain("capture.xmatcap", directory.Entries());
            AssertListenerReleased(port);
        }

        [Theory]
        [InlineData("missing\\capture.xmatcap", 2, "cli.output.parent-missing")]
        [InlineData("existing.xmatcap", 5, "cli.output.exists")]
        public async Task OutputMistakesFailBeforeListening(string output, int exitCode, string code)
        {
            using var directory = new CliTestDirectory();
            File.WriteAllText(Path.Join(directory.Root, "existing.xmatcap"), "keep");
            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "capture", "--port", AvailablePort().ToString(), "--duration-seconds", "1", "--out", output);
            CliAssert.ExitCode(exitCode, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Equal(new[] { "existing.xmatcap" }, directory.Entries());
            Assert.Equal("keep", File.ReadAllText(Path.Join(directory.Root, "existing.xmatcap")));
        }

        [Theory]
        [InlineData(null, "capture.profile.unreadable")]
        [InlineData("{\"version\":1,\"initial\":{\"mode\":\"off\"},\"secret-canary\":1}", "capture.profile.invalid")]
        [InlineData("{\"version\":2,\"initial\":{\"mode\":\"off\"}}", "capture.profile.invalid")]
        [InlineData("{\"version\":1,\"initial\":{\"mode\":\"off\"},\"schedule\":[{\"afterSeconds\":2,\"profile\":{\"mode\":\"broken\"}}]}",
            "capture.profile.invalid")]
        public async Task UnusableShapingProfileIsInvalidInputBeforeListening(string profile, string code)
        {
            using var directory = new CliTestDirectory();
            if (profile != null)
            {
                File.WriteAllText(Path.Join(directory.Root, "profile.json"), profile);
            }
            int port = AvailablePort();
            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "capture", "--port", port.ToString(), "--duration-seconds", "2",
                "--out", "capture.xmatcap", "--shape-profile", "profile.json");
            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.DoesNotContain("secret-canary", run.Stdout + run.Stderr);
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr);
            Assert.DoesNotContain("capture.xmatcap", directory.Entries());
            AssertListenerReleased(port);
        }

        [Fact]
        public async Task CancellationWhileReadingTheShapingProfileIsCancelledNotInternal()
        {
            using var directory = new CliTestDirectory();
            string profile = Path.Join(directory.Root, "profile.json");
            File.WriteAllText(profile, "{\"version\":1,\"initial\":{\"mode\":\"off\"}}");
            CliParseResult parsed = CliCommandLine.Parse(new[]
            {
                "capture", "--port", AvailablePort().ToString(), "--duration-seconds", "1",
                "--out", Path.Join(directory.Root, "capture.xmatcap"), "--shape-profile", profile,
            });
            using var stdout = new MemoryStream();
            using var stderr = new StringWriter();

            CliReport report = await CliCaptureCommand.RunAsync(
                parsed.Request, "cancelled", new CliConsole(stdout, stderr), new CancellationToken(true));

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.Equal(new[] { "cli.cancelled" }, report.Errors.Select(error => error.Code));
            Assert.Equal(new[] { "profile.json" }, directory.Entries());
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
            File.WriteAllBytes(Path.Join(directory.Root, "root.pfx"), root.Export(X509ContentType.Pfx));
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
            using var capture = new SqLtReadonlyDatabase(Path.Join(directory.Root, "capture.xmatcap"));
            var row = Assert.Single(capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords,
                row => row.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) == "POST");
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
            Assert.Equal(new[] { "capture.xmatcap", "root.pfx" }, directory.Entries());
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task LongStagingPathCapturesBodiesAndPublishesWithoutSidecars()
        {
            using var directory = new CliTestDirectory();
            // A normal-length output can require a staging path beyond Windows' classic limit.
            int padding = Math.Max(1, 240 - Path.Join(directory.Root, "capture.xmatcap").Length - 1);
            string folder = Path.Join(directory.Root, new string('p', padding));
            Directory.CreateDirectory(folder);
            string output = Path.Join(folder, "capture.xmatcap");
            byte[] body = Encoding.UTF8.GetBytes("synthetic capture body");
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "2", "--out", output);
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            using var content = new ByteArrayContent(body);
            using HttpResponseMessage response = await client.PostAsync(origin.Url("/upload"), content);
            Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
            XmatRun run = await child.CompleteAsync();
            CliAssert.ExitCode(0, run);
            Assert.Equal(new[] { output }, Directory.GetFileSystemEntries(folder));
            using var capture = new SqLtReadonlyDatabase(output);
            var row = Assert.Single(capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
            Assert.Equal(body, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
            AssertListenerReleased(port);
        }

        [Fact]
        public async Task CapturedServiceCallsAreAnalyzed()
        {
            using var directory = new CliTestDirectory();
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "2", "--out", "capture.xmatcap");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url("/users"));
            // The rules analyze Xbox services calls only; this header marks the request as one.
            request.Headers.Add("x-xbl-api-build-version", "adk");
            using HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            CliAssert.ExitCode(0, await child.CompleteAsync());

            XmatRun analysis = await XmatProcess.RunAsync(
                directory.Root, "analyze", "capture.xmatcap", "--out", "analysis");

            CliAssert.ExitCode(0, analysis);
            Assert.Equal(1, analysis.Json().GetProperty("totals").GetProperty("calls").GetInt32());
        }

        [Fact]
        public async Task CapturedRequestsCanBeReplayedAndSanitizedWithoutLegacyConsent()
        {
            using var directory = new CliTestDirectory();
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "2", "--out", "capture.xmatcap");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url("/quoted"));
            const string quoted = "\"double\" 'single'";
            request.Headers.Add("X-Quoted", quoted);
            using HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            CliAssert.ExitCode(0, await child.CompleteAsync());

            XmatRun replay = await XmatProcess.RunAsync(
                directory.Root, "replay", "capture.xmatcap", "--request", "1", "--out", "replayed");
            CliAssert.ExitCode(0, replay);
            Assert.Equal(2, origin.Requests.Count);
            Assert.All(origin.Requests, observed => Assert.Equal(quoted, observed.Headers["X-Quoted"]));

            XmatRun export = await XmatProcess.RunAsync(
                directory.Root, "export", "capture.xmatcap", "--format", "sanitized-saz", "--out", "sanitized.saz");
            CliAssert.ExitCode(0, export);
            Assert.True(File.Exists(Path.Join(directory.Root, "sanitized.saz")));
            Assert.DoesNotContain(export.Json().GetProperty("warnings").EnumerateArray(),
                warning => warning.GetProperty("code").GetString() == "export.sanitization.header-provenance-unknown");
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
            var clock = Stopwatch.StartNew();
            using var directory = new CliTestDirectory();
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = AvailablePort();
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "3", "--out", "capture.xmatcap",
                "--body-timeout-seconds", "1");
            await WaitForListenerAsync(child, port);
            _output.WriteLine($"Deadline listener ready at {clock.Elapsed.TotalMilliseconds:F1} ms; timestamp {Stopwatch.GetTimestamp()}");
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"POST {origin.Url("/slow")} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n" +
                "Content-Length: 2\r\nConnection: close\r\n\r\na"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string status = await reader.ReadLineAsync(timeout.Token);
            _output.WriteLine($"Deadline response at {clock.Elapsed.TotalMilliseconds:F1} ms: {status ?? "<null>"}; timestamp {Stopwatch.GetTimestamp()}");
            Assert.Empty(origin.Requests);
            XmatRun run = await child.CompleteAsync();
            _output.WriteLine($"Deadline child exit {run.ExitCode}: {run.Stderr}\n{run.Stdout}");
            Assert.StartsWith("HTTP/1.1 408", status);
            CliAssert.ExitCode(0, run);
            using var capture = new SqLtReadonlyDatabase(Path.Join(directory.Root, "capture.xmatcap"));
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
            File.WriteAllText(Path.Join(directory.Root, "profile.json"), """
                {"version":1,"initial":{"mode":"shape","downKbps":1000},
                 "schedule":[{"afterSeconds":2,"profile":{"mode":"off"}}],"bodyTimeoutSeconds":1800}
                """);
            int port = AvailablePort();
            // Both requests and the inter-phase wait must fit even at the allowed timing bounds.
            using XmatChild child = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "capture",
                "--port", port.ToString(), "--duration-seconds", "10", "--out", "capture.xmatcap",
                "--shape-profile", "profile.json");
            await WaitForListenerAsync(child, port);
            using var client = Client(port);
            var elapsed = Stopwatch.StartNew();
            using var content = new ByteArrayContent(body);
            using HttpResponseMessage response = await client.PostAsync(origin.Url("/upload"), content);
            byte[] firstBody = await response.Content.ReadAsByteArrayAsync();
            _output.WriteLine($"Shaped HTTP complete {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(body, firstBody);
            _output.WriteLine($"Shaped with equality {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 850, 3000);
            await Task.Delay(TimeSpan.FromSeconds(2.1));
            elapsed.Restart();
            byte[] secondBody = await client.GetByteArrayAsync(origin.Url("/after"));
            TimeSpan httpElapsed = elapsed.Elapsed;
            Assert.Equal(body, secondBody);
            TimeSpan assertionElapsed = elapsed.Elapsed;
            _output.WriteLine($"Off HTTP {httpElapsed.TotalMilliseconds:F1} ms; with equality {assertionElapsed.TotalMilliseconds:F1} ms; timestamp {Stopwatch.GetTimestamp()}");
            XmatRun run = await child.CompleteAsync();
            _output.WriteLine($"Shaping child exit {run.ExitCode}: {run.Stderr}\n{run.Stdout}");
            Assert.True(assertionElapsed < TimeSpan.FromMilliseconds(800), $"HTTP {httpElapsed.TotalMilliseconds:F1} ms, with equality {assertionElapsed.TotalMilliseconds:F1} ms");
            CliAssert.ExitCode(0, run);
            Assert.False(run.WindowSeen);
            Assert.Equal(2, run.Json().GetProperty("capture").GetProperty("records").GetInt64());
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr);
            Assert.Equal(new[] { "capture.xmatcap", "profile.json" }, directory.Entries());
            Assert.Equal(body, origin.Requests.First().Body);
            using var capture = new SqLtReadonlyDatabase(Path.Join(directory.Root, "capture.xmatcap"));
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
                new[] { NetworkShapingMode.Shape, NetworkShapingMode.Off },
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
            using var capture = new SqLtReadonlyDatabase(Path.Join(directory.Root, "capture.xmatcap"));
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
