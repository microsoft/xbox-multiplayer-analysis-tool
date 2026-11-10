// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using XMAT.Cli;
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

        [Theory]
        [InlineData("url")]
        [InlineData("request-file")]
        [InlineData("capture")]
        [InlineData("sequence")]
        public async Task ScopedDestination_IsRefusedBeforeOutputReservation(string source)
        {
            using var directory = new CliTestDirectory();
            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "keep.txt"), "keep");
            string[] arguments;
            if (source == "request-file")
            {
                string file = Path.Join(directory.Root, "request.json");
                File.WriteAllText(
                    file,
                    "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\"," +
                        "\"url\":\"http://[fe80::1%Ethernet]/item\",\"httpVersion\":\"auto\"," +
                        "\"headers\":[],\"body\":\"\",\"omitted\":[]}");
                arguments = new[] { "compose", "--request-file", file, "--out", output };
            }
            else if (source == "capture")
            {
                string capture = ReplayCaptures.WriteSaz(
                    directory.Root, "capture.saz", new CapturedExchange("GET", "http://[fe80::1%12]/item"));
                arguments = new[] { "replay", capture, "--request", "1", "--out", output };
            }
            else
            {
                arguments = source == "sequence"
                    ? new[]
                    {
                        "compose", "--url", "http://[fe80::1%2518]/item/#",
                        "--sequence", "1-2", "--out", output,
                    }
                    : new[] { "compose", "--url", "http://[fe80::1%0]/item", "--out", output };
            }

            XmatRun run = await XmatProcess.RunAsync(directory.Root, arguments);

            CliAssert.ExitCode(CliExitCodes.InvalidInput, run);
            Assert.Equal(new[] { ReplayDestination.ScopedIpv6Code }, run.ErrorCodes());
            string message =
                Assert.Single(run.Json().GetProperty("errors").EnumerateArray()).GetProperty("message").GetString();
            Assert.Equal(ReplayDestination.ScopedIpv6Message, message);
            Assert.Equal(CliCleanupStates.NotNeeded, run.Json().GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal("keep", File.ReadAllText(Path.Join(output, "keep.txt")));
            Assert.Equal(
                new[] { "keep.txt" },
                Directory.GetFileSystemEntries(output).Select(Path.GetFileName));
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Replay_InputFailureWinsBeforeExistingOutput()
        {
            using var directory = new CliTestDirectory();
            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "keep.txt"), "keep");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "replay", Path.Join(directory.Root, "missing.saz"),
                "--request", "1", "--out", output);

            CliAssert.ExitCode(CliExitCodes.InvalidInput, run);
            Assert.Equal(new[] { "analysis.input.not-found" }, run.ErrorCodes());
            Assert.Equal(CliCleanupStates.NotNeeded, run.Json().GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal("keep", File.ReadAllText(Path.Join(output, "keep.txt")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Theory]
        [InlineData("replay", "invalid", CliExitCodes.Usage, "cli.usage.invalid-value")]
        [InlineData("replay", "unprotected", CliExitCodes.Failed, "cli.output.unprotected")]
        [InlineData("compose", "invalid", CliExitCodes.Usage, "cli.usage.invalid-value")]
        [InlineData("compose", "unprotected", CliExitCodes.Failed, "cli.output.unprotected")]
        public async Task OutputTargetRefusal_PrecedesInvalidReplayOrComposeInput(
            string command,
            string targetKind,
            int exitCode,
            string code)
        {
            using var directory = new CliTestDirectory();
            string output = targetKind == "invalid" ? @"C:\bad|x" : @"\\server\share\out";
            string[] arguments = command == "replay"
                ? new[] { "replay", "missing.saz", "--request", "1", "--out", output }
                : new[] { "compose", "--request-file", "missing.json", "--out", output };

            XmatRun run = await XmatProcess.RunAsync(directory.Root, arguments);

            CliAssert.ExitCode(exitCode, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal(
                CliCleanupStates.NotNeeded,
                run.Json().GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal(Array.Empty<string>(), directory.Entries());
            CliAssert.NoStagingFolders(directory.Root);
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

        // --verify-diff verifies as --verify does and publishes each verified send's diff.json, which holds captured content
        // by design; the report, the summary and the console name only its state and path.
        [Fact]
        public async Task Replay_VerifyDiff_PublishesEachVerifiedSendsDetailAndAReportWithoutContent()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(
                request => LoopbackResponse.Ok(request.Target == "/two" ? $"{Canary}-replayed\n" : "same\n"));
            const string Head = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n";
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one", ResponseHead: Head, ResponseBody: "same\n"u8.ToArray()),
                new CapturedExchange(
                    "GET", server.Origin + "/two", ResponseHead: Head, ResponseBody: Encoding.UTF8.GetBytes($"{Canary}-captured\n")));

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "replay", input, "--request", "all", "--verify-diff", "--out", "results");

            CliAssert.ExitCode(11, run);
            JsonElement json = run.Json();
            Assert.True(json.GetProperty("options").GetProperty("verify").GetBoolean());
            Assert.True(json.GetProperty("options").GetProperty("verifyDiff").GetBoolean());
            JsonElement[] verify = json.GetProperty("replays").EnumerateArray().Select(replay => replay.GetProperty("verify")).ToArray();
            Assert.Equal(
                new[] { ("match", "complete", "replays/00001/diff.json"), ("mismatch", "complete", "replays/00002/diff.json") },
                verify.Select(item => (
                    item.GetProperty("result").GetString(),
                    item.GetProperty("detail").GetProperty("state").GetString(),
                    item.GetProperty("detail").GetProperty("file").GetString())));
            string published = Path.Join(directory.Root, "results");
            string summary = File.ReadAllText(Path.Join(published, "summary.json"));
            using (JsonDocument document = JsonDocument.Parse(summary))
            {
                Assert.Equal(
                    "replays/00002/diff.json",
                    document.RootElement.GetProperty("replays")[1].GetProperty("verify").GetProperty("detail").GetProperty("file").GetString());
            }

            string detail = File.ReadAllText(Path.Join(published, "replays", "00002", "diff.json"));
            using (JsonDocument document = JsonDocument.Parse(detail))
            {
                Assert.Equal(
                    ("xmat.verify-diff", 1, "mismatch"),
                    (document.RootElement.GetProperty("schema").GetString(), document.RootElement.GetProperty("version").GetInt32(),
                        document.RootElement.GetProperty("result").GetString()));
            }

            Assert.Contains(Canary, detail, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(published, "replays", "00001", "diff.json")));
            foreach (string text in new[] { run.Stdout, run.Stderr, summary })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("--verify-diff: wrote 2 diff.json file(s)", run.Stderr, StringComparison.Ordinal);
            CliAssert.OnlyCurrentUserCanOpen(published);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Replay_ARequestWithoutAResponse_StillPublishesWithExitCode12()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var closed = new TcpListener(IPAddress.Loopback, 0);
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

        // --proxy sends through an upstream proxy, so it needs --allow-proxy: without it, nothing reaches the proxy or the
        // server. With it, an http request goes to the proxy whole and never directly, and the output, the published files
        // and the archive say that the send went through a proxy, never which one.
        [Fact]
        public async Task Compose_Proxy_NeedsItsOwnPermission_AndTheOutputNeverNamesTheProxy()
        {
            using var directory = new CliTestDirectory();
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok("direct"));
            await using var proxy = new LoopbackProxy(_ => new ProxyAnswer.Respond(LoopbackResponse.Ok("via proxy")));
            string[] command =
            {
                "compose", "--url", origin.Origin + "/item", "--header", "Authorization: Bearer " + Canary,
                "--proxy", $"http://localhost:{proxy.Port}",
            };

            XmatRun denied = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "denied" }).ToArray());

            CliAssert.ExitCode(3, denied);
            Assert.Equal(new[] { ReplayOperation.ProxyPolicyCode }, denied.ErrorCodes());
            Assert.Equal((0, 0), (proxy.Connections, origin.Connections));
            Assert.False(Directory.Exists(Path.Join(directory.Root, "denied")));

            XmatRun sent = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--allow-proxy", "--save-saz", "--out", "results" }).ToArray());

            CliAssert.ExitCode(0, sent);
            JsonElement json = sent.Json();
            JsonElement options = json.GetProperty("options");
            Assert.Equal((true, true), (options.GetProperty("proxy").GetBoolean(), options.GetProperty("allowProxy").GetBoolean()));
            JsonElement replay = Assert.Single(json.GetProperty("replays").EnumerateArray());
            Assert.Equal(
                ("completed", ReplayTransportOptions.ProxyName),
                (replay.GetProperty("outcome").GetString(), replay.GetProperty("transport").GetString()));
            Assert.Equal(0, origin.Connections);
            LoopbackRequest received = Assert.Single(proxy.Requests);
            Assert.Equal((origin.Origin + "/item", "Bearer " + Canary), (received.Target, received.Header("Authorization")));
            string published = Path.Join(directory.Root, "results");
            string archive;
            using (ZipArchive saz = ZipFile.OpenRead(Path.Join(published, "replays.saz")))
            {
                archive = string.Concat(saz.Entries.Select(entry =>
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.Latin1);
                    return reader.ReadToEnd();
                }));
            }

            Assert.Contains("N=\"x-xmat-replay-transport\" V=\"proxy\"", archive, StringComparison.Ordinal);
            IEnumerable<string> outputs = new[] { denied.Stdout, denied.Stderr, sent.Stdout, sent.Stderr, archive }
                .Concat(Directory.GetFiles(published, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".saz", StringComparison.Ordinal))
                    .Select(File.ReadAllText));
            Assert.All(outputs, text => Assert.DoesNotContain("localhost", text, StringComparison.OrdinalIgnoreCase));
        }

        // --client-cert needs --allow-client-cert naming the run's origin: without it, or for another origin, nothing is
        // looked up or sent. With it, xmat reads the current user's Personal store, only reading it, and a thumbprint that no
        // certificate there has is refused before anything is sent. Neither the output nor its errors name the thumbprint or
        // the origin.
        [Fact]
        public async Task Compose_ClientCertificate_NeedsConsentForTheRunsOrigin_AndAnUnknownOneIsRefused()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string thumbprint = RandomNumberGenerator.GetHexString(40);
            string[] command = { "compose", "--url", $"https://localhost:{server.Port}/item", "--client-cert", thumbprint };

            XmatRun unconsented = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "a" }).ToArray());
            XmatRun otherOrigin = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--allow-client-cert", "https://localhost:1", "--out", "b" }).ToArray());
            XmatRun unknown = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--allow-client-cert", $"https://LOCALHOST:{server.Port}/", "--out", "c" }).ToArray());

            CliAssert.ExitCode(3, unconsented);
            CliAssert.ExitCode(3, otherOrigin);
            CliAssert.ExitCode(3, unknown);
            Assert.Equal(new[] { ReplayOperation.ClientCertificatePolicyCode }, unconsented.ErrorCodes());
            Assert.Equal(new[] { ReplayOperation.ClientCertificatePolicyCode }, otherOrigin.ErrorCodes());
            Assert.Equal(new[] { ReplayCertificateResolver.NotFoundCode }, unknown.ErrorCodes());
            JsonElement options = unknown.Json().GetProperty("options");
            Assert.Equal(
                (true, true), (options.GetProperty("clientCertificate").GetBoolean(), options.GetProperty("allowClientCertificate").GetBoolean()));
            Assert.Equal(0, server.Connections);
            Assert.All(new[] { "a", "b", "c" }, name => Assert.False(Directory.Exists(Path.Join(directory.Root, name))));
            Assert.All(
                new[] { unconsented, otherOrigin, unknown }.SelectMany(run => new[] { run.Stdout, run.Stderr }),
                text =>
                {
                    Assert.DoesNotContain(thumbprint, text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("localhost", text, StringComparison.OrdinalIgnoreCase);
                });
        }

        // --windows-auth needs --allow-windows-auth naming the run's origin, an https URL, HTTP/1.1 and no Authorization
        // header of the request's own, for compose and replay alike: otherwise nothing is sent. The options say that Windows
        // authentication was asked for and allowed, and that it asks for exactly HTTP/1.1 when the version was left
        // automatic; neither the output nor its errors name the origin or the request's header.
        [Fact]
        public async Task WindowsAuth_WithoutWhatItNeeds_IsRefusedBeforeAnythingIsSent()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string url = $"https://localhost:{server.Port}/item";
            string consent = $"https://LOCALHOST:{server.Port}/";
            string input = ReplayCaptures.WriteSaz(directory.Root, "capture.saz", new CapturedExchange("GET", url, ""));
            (string[] Arguments, string Code)[] runs =
            {
                (new[] { "compose", "--url", url, "--windows-auth" }, ReplayOperation.WindowsAuthPolicyCode),
                (new[] { "compose", "--url", url, "--windows-auth", "--allow-windows-auth", "https://localhost:1" }, ReplayOperation.WindowsAuthPolicyCode),
                (new[] { "replay", input, "--request", "1", "--windows-auth" }, ReplayOperation.WindowsAuthPolicyCode),
                (new[] { "compose", "--url", $"http://localhost:{server.Port}/item", "--windows-auth", "--allow-windows-auth", consent }, ReplayOperation.WindowsAuthHttpsCode),
                (new[] { "compose", "--url", url, "--windows-auth", "--allow-windows-auth", consent, "--http-version", "2" }, ReplayOperation.WindowsAuthHttp2Code),
                (new[] { "compose", "--url", url, "--windows-auth", "--allow-windows-auth", consent, "--header", "Authorization: Bearer " + Canary }, ReplayOperation.WindowsAuthAuthorizationCode),
            };
            var results = new List<XmatRun>();

            for (int index = 0; index < runs.Length; index++)
            {
                XmatRun run = await XmatProcess.RunAsync(directory.Root, runs[index].Arguments.Concat(new[] { "--out", "r" + index }).ToArray());
                results.Add(run);
                CliAssert.ExitCode(3, run);
                Assert.Equal(new[] { runs[index].Code }, run.ErrorCodes());
                Assert.False(Directory.Exists(Path.Join(directory.Root, "r" + index)));
            }

            JsonElement automatic = results[0].Json().GetProperty("options");
            JsonElement http2 = results[4].Json().GetProperty("options");
            Assert.Equal(
                ("1.1", true, false),
                (automatic.GetProperty("httpVersion").GetString(), automatic.GetProperty("windowsAuth").GetBoolean(),
                    automatic.GetProperty("allowWindowsAuth").GetBoolean()));
            Assert.Equal(
                ("2", true, true),
                (http2.GetProperty("httpVersion").GetString(), http2.GetProperty("windowsAuth").GetBoolean(),
                    http2.GetProperty("allowWindowsAuth").GetBoolean()));
            Assert.Equal(0, server.Connections);
            Assert.All(
                results.SelectMany(run => new[] { run.Stdout, run.Stderr }),
                text =>
                {
                    Assert.DoesNotContain("localhost", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                });
        }

        // With --allow-windows-auth for the run's origin, the run goes on to send, and Windows authentication answers only a
        // server that proves that it is the URL's host: one whose certificate comes from a root that Windows does not trust
        // fails the send before any HTTP, so no request, and no credential, reaches it. The output says that the send was
        // configured for Windows authentication at exactly HTTP/1.1, and never names the origin.
        [Fact]
        public async Task WindowsAuth_WithConsent_GoesOnToSend_AndAnUntrustedServerGetsNothing()
        {
            using var directory = new CliTestDirectory();
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate));

            XmatRun sent = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/item", "--windows-auth", "--allow-windows-auth", server.Origin,
                "--out", "results");

            CliAssert.ExitCode(12, sent);
            JsonElement json = sent.Json();
            JsonElement options = json.GetProperty("options");
            Assert.Equal(
                ("1.1", true, true),
                (options.GetProperty("httpVersion").GetString(), options.GetProperty("windowsAuth").GetBoolean(),
                    options.GetProperty("allowWindowsAuth").GetBoolean()));
            JsonElement replay = Assert.Single(json.GetProperty("replays").EnumerateArray());
            Assert.Equal(
                ("failed", ReplaySender.SendFailedCode, "1.1", JsonValueKind.Null),
                (replay.GetProperty("outcome").GetString(), replay.GetProperty("code").GetString(),
                    replay.GetProperty("httpVersion").GetString(), replay.GetProperty("authHeaderSchemeObserved").ValueKind));
            Assert.NotEqual(0, server.Connections);
            Assert.Empty(server.Requests);
            Assert.All(new[] { sent.Stdout, sent.Stderr }, text => Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal));
        }

        // A request file's HTTP version is the run's unless --http-version says otherwise, and the output reports the version
        // that the run asked for: a file that asks for HTTP/2 only is refused with Windows authentication before anything is
        // sent, and its options say "2"; --http-version auto overrides the file, and Windows authentication makes that exactly
        // HTTP/1.1; without Windows authentication, the file's HTTP/2 only is the run's. The server's certificate comes from a
        // root that Windows does not trust, so no send gets as far as HTTP.
        [Fact]
        public async Task WindowsAuth_ARequestFilesHttpVersion_IsTheRuns_AndTheOutputReportsWhatTheRunAskedFor()
        {
            using var directory = new CliTestDirectory();
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate));
            string file = Path.Join(directory.Root, "http2.json");
            File.WriteAllText(
                file,
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"" + server.Origin + "/item\"," +
                "\"httpVersion\":\"2\",\"headers\":[],\"body\":\"\",\"omitted\":[]}");
            string[] windows = { "--windows-auth", "--allow-windows-auth", server.Origin };

            XmatRun refused = await XmatProcess.RunAsync(
                directory.Root, new[] { "compose", "--request-file", file, "--out", "refused" }.Concat(windows).ToArray());
            int connectionsWhenRefused = server.Connections;
            XmatRun overridden = await XmatProcess.RunAsync(
                directory.Root,
                new[] { "compose", "--request-file", file, "--http-version", "auto", "--out", "overridden" }.Concat(windows).ToArray());
            XmatRun plain = await XmatProcess.RunAsync(directory.Root, "compose", "--request-file", file, "--out", "plain");

            CliAssert.ExitCode(3, refused);
            Assert.Equal(new[] { ReplayOperation.WindowsAuthHttp2Code }, refused.ErrorCodes());
            Assert.Equal(0, connectionsWhenRefused);
            CliAssert.ExitCode(12, overridden);
            CliAssert.ExitCode(12, plain);
            Assert.Equal(
                new[] { ("2", (string)null), ("1.1", "1.1"), ("2", "2") },
                new[] { refused, overridden, plain }.Select(run =>
                {
                    JsonElement json = run.Json();
                    JsonElement[] replays = json.GetProperty("replays").EnumerateArray().ToArray();
                    return (
                        json.GetProperty("options").GetProperty("httpVersion").GetString(),
                        replays.Length == 0 ? null : Assert.Single(replays).GetProperty("httpVersion").GetString());
                }));
            Assert.Empty(server.Requests);
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
