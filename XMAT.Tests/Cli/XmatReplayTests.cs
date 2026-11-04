// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using XMAT.Operations.Replay;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat replay and xmat compose, run as child processes against loopback servers in the test process: exit codes, the
    // data-free JSON, the published messages and the isolation from the GUI. Nothing leaves the machine. The collection
    // disables parallelization, so no other test writes the GUI's storage while the read-only GuiStorageProbe watches it.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatReplayTests
    {
        private const string Canary = "XMATREPLAYCANARY51";

        [Fact]
        public async Task Replay_PublishesEachMessageAndADataFreeReport()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok($"{{\"echo\":\"{Canary}-response\"}}"));
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + $"/one?token={Canary}", $"Authorization: Bearer {Canary}\r\n"),
                new CapturedExchange("GET", server.Origin + "/two"));
            string inputHash = CliAssert.Sha256(input);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "replay", input, "--request", "1-2", "--repeat", "2", "--out", "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(("replay", "succeeded"), (json.GetProperty("command").GetString(), json.GetProperty("status").GetString()));
            Assert.Equal(("saz", inputHash), (json.GetProperty("input").GetProperty("kind").GetString(), json.GetProperty("input").GetProperty("sha256").GetString()));
            JsonElement[] replays = json.GetProperty("replays").EnumerateArray().ToArray();
            Assert.Equal(new[] { 1, 2, 1, 2 }, replays.Select(replay => replay.GetProperty("request").GetInt32()));
            Assert.Equal(new[] { 1, 1, 2, 2 }, replays.Select(replay => replay.GetProperty("iteration").GetInt32()));
            Assert.All(replays, replay => Assert.Equal(("completed", 200), (replay.GetProperty("outcome").GetString(), replay.GetProperty("status").GetInt32())));
            Assert.Equal(
                new[] { "summary:summary.json", "replays:replays" },
                json.GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("kind").GetString() + ":" + artifact.GetProperty("path").GetString()));
            Assert.Equal(new[] { "/one?token=" + Canary, "/two", "/one?token=" + Canary, "/two" }, server.Requests.Select(request => request.Target));
            Assert.Equal("Bearer " + Canary, server.Requests[0].Header("Authorization"));

            string published = Path.Join(directory.Root, "results");
            string[] files = Directory.GetFiles(published, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(published, file).Replace('\\', '/'))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[] { "replays/00001/request.http", "replays/00001/response.http", "replays/00002/request.http", "replays/00002/response.http",
                    "replays/00003/request.http", "replays/00003/response.http", "replays/00004/request.http", "replays/00004/response.http", "summary.json" },
                files);
            string request = File.ReadAllText(Path.Join(published, "replays", "00001", "request.http"));
            Assert.StartsWith($"GET /one?token={Canary} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n", request);
            Assert.Contains($"Authorization: Bearer {Canary}\r\n", request);
            Assert.EndsWith($"{{\"echo\":\"{Canary}-response\"}}", File.ReadAllText(Path.Join(published, "replays", "00001", "response.http")));
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(published, "summary.json"))))
            {
                Assert.Equal("xmat-replay/1", summary.RootElement.GetProperty("schema").GetString());
                Assert.Equal(4, summary.RootElement.GetProperty("totals").GetProperty("completed").GetInt32());
            }

            // The messages hold captured content by design; the report and the console output never do.
            foreach (string text in new[] { run.Stdout, run.Stderr, File.ReadAllText(Path.Join(published, "summary.json")) })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(directory.Name, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/", text, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
            }

            CliAssert.OnlyCurrentUserCanOpen(published);
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Equal(inputHash, CliAssert.Sha256(input));
            Assert.False(run.WindowSeen);
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task Replay_Verify_ReportsADifferentResponseWithExitCode11()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok(request.Target == "/two" ? "changed" : "same"));
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one", ResponseBody: "same"u8.ToArray()),
                new CapturedExchange("GET", server.Origin + "/two", ResponseBody: "same"u8.ToArray()));

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "replay", input, "--request", "all", "--verify", "--out", "results");

            CliAssert.ExitCode(11, run);
            JsonElement json = run.Json();
            Assert.Equal("mismatchFound", json.GetProperty("status").GetString());
            JsonElement[] replays = json.GetProperty("replays").EnumerateArray().ToArray();
            Assert.Equal("match", replays[0].GetProperty("verify").GetProperty("result").GetString());
            Assert.Equal("mismatch", replays[1].GetProperty("verify").GetProperty("result").GetString());
            Assert.Equal(new[] { "body" }, replays[1].GetProperty("verify").GetProperty("differences").EnumerateArray().Select(item => item.GetString()));
            Assert.True(Directory.Exists(Path.Join(directory.Root, "results")));
            Assert.Contains("exit code 11", run.Stderr, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Replay_ARequestWithoutAResponse_StillPublishesWithExitCode12()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", $"http://127.0.0.1:{closedPort}/gone"),
                new CapturedExchange("GET", server.Origin + "/here"));

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "replay", input, "--request", "1,2", "--out", "results");

            CliAssert.ExitCode(12, run);
            JsonElement json = run.Json();
            Assert.Equal("requestsFailed", json.GetProperty("status").GetString());
            JsonElement failed = json.GetProperty("replays")[0];
            Assert.Equal(("failed", "replay.send.failed"), (failed.GetProperty("outcome").GetString(), failed.GetProperty("code").GetString()));
            Assert.Equal(JsonValueKind.Null, failed.GetProperty("files").GetProperty("response").ValueKind);
            Assert.Equal("completed", json.GetProperty("replays")[1].GetProperty("outcome").GetString());
            string published = Path.Join(directory.Root, "results");
            Assert.True(File.Exists(Path.Join(published, "replays", "00001", "request.http")));
            Assert.False(File.Exists(Path.Join(published, "replays", "00001", "response.http")));
            Assert.True(File.Exists(Path.Join(published, "replays", "00002", "response.http")));
        }

        [Fact]
        public async Task Replay_AnUnsafeMethodNeedsPermission_OtherwiseNothingIsSentOrPublished()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/read"),
                new CapturedExchange("DELETE", server.Origin + "/item/7"));

            XmatRun denied = await XmatProcess.RunAsync(directory.Root, "replay", input, "--request", "all", "--out", "denied");
            XmatRun moved = await XmatProcess.RunAsync(
                directory.Root, "replay", input, "--request", "1", "--origin", "http://127.0.0.2:9", "--out", "moved");

            CliAssert.ExitCode(3, denied);
            Assert.Equal(new[] { "replay.policy.unsafe-method" }, denied.ErrorCodes());
            CliAssert.ExitCode(3, moved);
            Assert.Equal(new[] { "replay.policy.origin-change" }, moved.ErrorCodes());
            Assert.Empty(server.Requests);
            Assert.Empty(denied.Json().GetProperty("artifacts").EnumerateArray());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "denied")));
            Assert.False(Directory.Exists(Path.Join(directory.Root, "moved")));
            CliAssert.NoStagingFolders(directory.Root);

            XmatRun allowed = await XmatProcess.RunAsync(
                directory.Root, "replay", input, "--request", "all", "--allow-unsafe-methods", "--out", "allowed");

            CliAssert.ExitCode(0, allowed);
            Assert.Equal(new[] { "GET /read", "DELETE /item/7" }, server.Requests.Select(request => request.Method + " " + request.Target));
        }

        [Fact]
        public async Task Compose_SendsTheRequestThatItsOptionsDescribe()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(201, "Created", Body: "made"u8.ToArray()));
            string bodyFile = Path.Join(directory.Root, "payload.json");
            File.WriteAllBytes(bodyFile, Encoding.UTF8.GetBytes($"{{\"secret\":\"{Canary}\"}}"));
            string[] command =
            {
                "compose", "--url", server.Origin + "/items?x=1", "--method", "PUT", "--header", "Content-Type: application/json",
                "--header", $"X-Token: {Canary}", "--body-file", bodyFile,
            };

            XmatRun denied = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "denied" }).ToArray());
            XmatRun sent = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--allow-unsafe-methods", "--out", "results" }).ToArray());

            CliAssert.ExitCode(3, denied);
            Assert.Equal(new[] { "replay.policy.unsafe-method" }, denied.ErrorCodes());
            CliAssert.ExitCode(0, sent);
            LoopbackRequest received = Assert.Single(server.Requests);
            Assert.Equal(("PUT", "/items?x=1"), (received.Method, received.Target));
            Assert.Equal("application/json", received.Header("Content-Type"));
            Assert.Equal(Canary, received.Header("X-Token"));
            Assert.Equal(File.ReadAllBytes(bodyFile), received.Body);
            JsonElement json = sent.Json();
            Assert.False(json.TryGetProperty("input", out _));
            JsonElement replay = Assert.Single(json.GetProperty("replays").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, replay.GetProperty("request").ValueKind);
            Assert.Equal(201, replay.GetProperty("status").GetInt32());
            foreach (string text in new[] { sent.Stdout, sent.Stderr, denied.Stdout, denied.Stderr })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(directory.Name, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // One request per number of --sequence; the output says which number each send used, never its URL.
        [Fact]
        public async Task Compose_SendsOneRequestPerSequenceNumber()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/item/#?page=#", "--sequence", "08-10", "--out", "results");

            CliAssert.ExitCode(0, run);
            Assert.Equal(
                new[] { "/item/08?page=08", "/item/09?page=09", "/item/10?page=10" },
                server.Requests.Select(request => request.Target));
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllBytes(Path.Join(directory.Root, "results", "summary.json")));
            foreach (JsonElement json in new[] { run.Json(), summary.RootElement })
            {
                Assert.Equal(
                    new long[] { 8, 9, 10 },
                    json.GetProperty("replays").EnumerateArray().Select(replay => replay.GetProperty("sequence").GetInt64()));
            }

            Assert.DoesNotContain("/item/", run.Stdout, StringComparison.Ordinal);
        }

        // The --body-file is read before anything is reserved or sent: one that is missing, cannot be read or is larger
        // than 64 MiB is exit code 3 with its own code, and nothing is sent or published.
        [Theory]
        [InlineData("missing", ReplayBodyFile.NotFoundCode)]
        [InlineData("folder", ReplayBodyFile.UnreadableCode)]
        [InlineData("too-large", ReplayBodyFile.TooLargeCode)]
        public async Task Compose_ABodyFileThatCannotBeUsed_SendsNothing(string file, string code)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string bodyFile = Path.Join(directory.Root, "body.bin");
            if (file == "folder")
            {
                Directory.CreateDirectory(bodyFile);
            }
            else if (file == "too-large")
            {
                using var stream = new FileStream(bodyFile, FileMode.CreateNew);
                stream.SetLength(ReplayBodyFile.MaxBytes + 1);
            }

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/items", "--method", "PUT", "--allow-unsafe-methods",
                "--body-file", bodyFile, "--out", "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Empty(server.Requests);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // --connect-to sends to another address than the URL's host, so it needs --allow-connect-to; without it nothing is
        // even connected. With it, TLS still checks the certificate against the URL's host, here one from a root that
        // Windows does not trust, so the send fails before any HTTP. The output says that the send used connect-to and
        // never names the endpoint.
        [Fact]
        public async Task Compose_ConnectTo_NeedsItsOwnPermission_AndTheOutputNeverNamesTheEndpoint()
        {
            const string Host = "xmat-connect-to.test";
            using var directory = new CliTestDirectory();
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(Host);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate));
            string[] command = { "compose", "--url", $"https://{Host}:{server.Port}/item", "--connect-to", $"127.0.0.1:{server.Port}" };

            XmatRun denied = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "denied" }).ToArray());

            CliAssert.ExitCode(3, denied);
            Assert.Equal(new[] { ReplayOperation.ConnectToPolicyCode }, denied.ErrorCodes());
            Assert.Equal(0, server.Connections);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "denied")));

            XmatRun sent = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--allow-connect-to", "--out", "results" }).ToArray());

            CliAssert.ExitCode(12, sent);
            JsonElement json = sent.Json();
            JsonElement options = json.GetProperty("options");
            Assert.Equal((true, true), (options.GetProperty("connectTo").GetBoolean(), options.GetProperty("allowConnectTo").GetBoolean()));
            JsonElement replay = Assert.Single(json.GetProperty("replays").EnumerateArray());
            Assert.Equal(
                ("failed", ReplaySender.SendFailedCode, ReplayTransportOptions.ConnectToName),
                (replay.GetProperty("outcome").GetString(), replay.GetProperty("code").GetString(), replay.GetProperty("transport").GetString()));
            Assert.Equal(JsonValueKind.Null, replay.GetProperty("responseVersion").ValueKind);
            Assert.Empty(server.Requests);
            Assert.NotEmpty(server.ServerNames);
            Assert.All(server.ServerNames, name => Assert.Equal(Host, name));
            string published = Path.Join(directory.Root, "results");
            IEnumerable<string> outputs = new[] { denied.Stdout, denied.Stderr, sent.Stdout, sent.Stderr }
                .Concat(Directory.GetFiles(published, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.All(outputs, text => Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal));
        }

        // A --connect-to that is not an address and port, or an HTTP version that XMAT does not offer, is a usage error
        // that never repeats the value; HTTP/2 or connect-to for an http URL is refused before anything is sent.
        [Theory]
        [InlineData("--connect-to", Canary + ":443", 2, "cli.usage.invalid-value")]
        [InlineData("--http-version", Canary, 2, "cli.usage.invalid-value")]
        [InlineData("--http-version", "2", 3, ReplayOperation.Http2HttpsCode)]
        [InlineData("--connect-to", "127.0.0.1:{0}", 3, ReplayOperation.ConnectToHttpsCode)]
        public async Task Compose_ATransportThatCannotBeUsed_SendsNothing(string option, string value, int exitCode, string code)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/item", option,
                string.Format(CultureInfo.InvariantCulture, value, server.Port), "--allow-connect-to", "--out", "results");

            CliAssert.ExitCode(exitCode, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Equal(0, server.Connections);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            Assert.DoesNotContain(Canary, run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
        }

        // A Fiddler capture can carry Fiddler's control headers. xmat refuses such a request, also when its Connection
        // header nominates the control header, which would then not be sent, and sends it once --remove-header removes it.
        [Fact]
        public async Task Replay_ACapturedFiddlerControlHeader_IsRefusedUntilRemoved()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/item", "Connection: keep-alive, Fiddler-Host\r\nFiddler-Host: 10.0.0.1:443\r\n"));

            XmatRun refused = await XmatProcess.RunAsync(directory.Root, "replay", input, "--request", "1", "--out", "refused");

            CliAssert.ExitCode(3, refused);
            Assert.Equal(new[] { ReplayOperation.ControlHeaderCode }, refused.ErrorCodes());
            Assert.Equal(0, server.Connections);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "refused")));

            XmatRun removed = await XmatProcess.RunAsync(
                directory.Root, "replay", input, "--request", "1", "--remove-header", "Fiddler-Host", "--out", "removed");

            CliAssert.ExitCode(0, removed);
            Assert.Null(Assert.Single(server.Requests).Header("Fiddler-Host"));
        }
    }
}
