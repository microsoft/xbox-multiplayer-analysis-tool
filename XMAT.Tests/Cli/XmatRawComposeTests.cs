// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using XMAT.Cli;
using XMAT.Operations.Replay;
using XMAT.Tests.HeadlessParity;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat compose --raw (slice L, contract 2.9; tests 2.12 item 12 and AA-1): its usage errors; the raw file, refused over
    // its limit; consent; a whole raw send over plain HTTP to loopback in a child process; and, in process through seams,
    // the publication of a run cancelled once a send began, with its finalization and that finalization's bound. Loopback
    // servers and seams only; nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class XmatRawComposeTests
    {
        private const string Target = "http://127.0.0.1:9";
        private const string Get = "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n";
        private const string Ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

        // compose's arguments, '|' between them, that refuse a run before anything is read, with their codes.
        [Theory]
        [InlineData("--raw|r.http|--out|o", "cli.usage.missing-option")]
        [InlineData("--url|http://h.test/|--target|http://h.test|--out|o", "cli.usage.missing-option")]
        [InlineData("--target|http://h.test|--out|o", "cli.usage.missing-option")]
        [InlineData("--url|http://h.test/|--allow-raw|--out|o", "cli.usage.missing-option")]
        [InlineData("--url|http://h.test/|--fix-content-length|--out|o", "cli.usage.missing-option")]
        [InlineData("--raw|r.http|--target|http://h.test|--url|http://h.test/|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--method|GET|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--header|X-A: 1|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--remove-header|X-A|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--clear-headers|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--body-file|b|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--empty-body|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--form|a=b|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--form-string|a=b|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--form-type|1=text/plain|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--request-file|f|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--sequence|1-2|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--http-version|2|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--http-version|auto|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--proxy|http://127.0.0.1:3128|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--allow-proxy|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--client-cert|AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--allow-client-cert|https://h.test|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--windows-auth|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--allow-windows-auth|https://h.test|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--follow-redirects|1|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test|--allow-origin-change|--out|o", "cli.usage.conflicting-options")]
        [InlineData("--raw|r.http|--target|http://h.test/path|--out|o", "cli.usage.invalid-value")]
        [InlineData("--raw|r.http|--target|http://user@h.test|--out|o", "cli.usage.invalid-value")]
        [InlineData("--raw|r.http|--target|http://h.test/?q|--out|o", "cli.usage.invalid-value")]
        [InlineData("--raw|r.http|--target|ftp://h.test|--out|o", "cli.usage.invalid-value")]
        [InlineData("--raw|r.http|--target|http://h.test", "cli.usage.missing-option")]
        public void TheCommandLine_RefusesWhatRawModeNeverTakes(string args, string code)
        {
            CliParseResult parsed = CliCommandLine.Parse(new[] { "compose" }.Concat(args.Split('|')).ToArray());

            Assert.False(parsed.Succeeded);
            Assert.Equal(code, parsed.Error.Code);
        }

        // --raw takes the send options that Raw mode uses, and itself selects HTTP/1.1.
        [Fact]
        public void Raw_TakesItsOptions_AndSelectsHttp11()
        {
            CliParseResult parsed = CliCommandLine.Parse(new[]
            {
                "compose", "--raw", "r.http", "--target", "https://H.test:8443/", "--allow-raw", "--fix-content-length",
                "--http-version", "1.1", "--follow-redirects", "0", "--connect-to", "127.0.0.1:9", "--allow-connect-to",
                "--repeat", "2", "--parallel", "2", "--timeout", "5", "--max-response-mib", "1", "--save-saz",
                "--allow-unsafe-methods", "--out", "o",
            });
            CliParseResult plain = CliCommandLine.Parse(new[] { "compose", "--raw", "r.http", "--target", "http://h.test", "--out", "o" });

            Assert.True(parsed.Succeeded, parsed.Error?.Message);
            CliReplay replay = parsed.Request.Replay;
            Assert.Equal(
                ("r.http", "https://h.test:8443", true, true, ReplayHttpVersion.Http11, 2, 2),
                (replay.RawFile, replay.RawTarget.ToString(), replay.AllowRaw, replay.FixContentLength, replay.HttpVersion, replay.Repeat, replay.Parallel));
            Assert.True(plain.Succeeded, plain.Error?.Message);
            Assert.Equal(
                (ReplayHttpVersion.Http11, false, false),
                (plain.Request.Replay.HttpVersion, plain.Request.Replay.AllowRaw, plain.Request.Replay.FixContentLength));
        }

        // replay, save-request and curl know none of Raw mode's options.
        [Theory]
        [InlineData("--raw|r.http")]
        [InlineData("--target|http://h.test")]
        [InlineData("--allow-raw")]
        [InlineData("--fix-content-length")]
        public void OtherCommands_KnowNoneOfTheRawOptions(string option)
        {
            foreach (string[] command in new[]
            {
                new[] { "replay", "capture.saz", "--request", "1", "--out", "o" },
                new[] { "save-request", "--url", "http://h.test/", "--out", "o" },
                new[] { "curl", "--url", "http://h.test/", "--out", "o" },
            })
            {
                CliParseResult parsed = CliCommandLine.Parse(command.Concat(option.Split('|')).ToArray());

                Assert.False(parsed.Succeeded);
                Assert.Equal("cli.usage.unknown-option", parsed.Error.Code);
            }
        }

        // The file is read up to RawRequest.MaxFileBytes and refused beyond it before anything is read; a missing file or a
        // folder cannot be read.
        [Fact]
        public async Task TheFile_IsReadUpToItsLimit_AndRefusedPastIt()
        {
            using var directory = new CliTestDirectory();
            string atLimit = Path.Join(directory.Root, "at-limit.http");
            string over = Path.Join(directory.Root, "over.http");
            using (FileStream file = File.Create(atLimit))
            {
                file.SetLength(RawRequest.MaxFileBytes);
            }

            using (FileStream file = File.Create(over))
            {
                file.SetLength(RawRequest.MaxFileBytes + 1);
            }

            (ReadOnlyMemory<byte> bytes, string sha256, string code) = await CliReplayCommand.ReadRawFileAsync(atLimit, CancellationToken.None);

            Assert.Null(code);
            Assert.Equal(RawRequest.MaxFileBytes, bytes.Length);
            Assert.Equal(CliAssert.Sha256(atLimit), sha256);
            Assert.Equal(RawCodes.InputTooLarge, (await CliReplayCommand.ReadRawFileAsync(over, CancellationToken.None)).Code);
            Assert.Equal(
                RawCodes.FileUnreadable,
                (await CliReplayCommand.ReadRawFileAsync(Path.Join(directory.Root, "missing.http"), CancellationToken.None)).Code);
            Assert.Equal(RawCodes.FileUnreadable, (await CliReplayCommand.ReadRawFileAsync(directory.Root, CancellationToken.None)).Code);
        }

        // A whole raw send over plain HTTP to loopback, in a child process: the server gets the file's bytes, and the run
        // publishes its image as request.http, its normalized session in replays.saz, and summary.json with the raw-file
        // input, the options and the send's write state and framing; never the file's path or the target.
        [Fact]
        public async Task ARawSend_OverPlainHttpToLoopback_IsPublished_WithItsImageAndWriteState()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("pong"));
            string raw = Path.Join(directory.Root, "request.raw");
            byte[] image = Encoding.ASCII.GetBytes(
                $"POST /p?q HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-A:  spaced  \r\nContent-Length: 4\r\n\r\nping");
            File.WriteAllBytes(raw, image);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--raw", raw, "--target", server.Origin, "--allow-raw", "--allow-unsafe-methods", "--save-saz",
                "--out", "results");

            CliAssert.ExitCode(0, run);
            string published = Path.Join(directory.Root, "results");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(published, "summary.json"))))
            {
                JsonElement root = summary.RootElement;
                Assert.Equal("succeeded", root.GetProperty("status").GetString());
                Assert.Empty(root.GetProperty("errors").EnumerateArray());
                Assert.Equal(
                    ("raw-file", CliAssert.Sha256(raw)),
                    (root.GetProperty("input").GetProperty("kind").GetString(), root.GetProperty("input").GetProperty("sha256").GetString()));
                JsonElement options = root.GetProperty("options");
                Assert.Equal(
                    (true, true, false, "1.1"),
                    (options.GetProperty("raw").GetBoolean(), options.GetProperty("allowRaw").GetBoolean(),
                        options.GetProperty("fixContentLength").GetBoolean(), options.GetProperty("httpVersion").GetString()));
                JsonElement send = Assert.Single(root.GetProperty("replays").EnumerateArray());
                Assert.Equal(
                    (true, "complete", (long)image.Length, "content-length", "direct", "1.1", 200),
                    (send.GetProperty("raw").GetBoolean(), send.GetProperty("requestWrite").GetString(),
                        send.GetProperty("requestBytesWritten").GetInt64(), send.GetProperty("responseFraming").GetString(),
                        send.GetProperty("transport").GetString(), send.GetProperty("httpVersion").GetString(),
                        send.GetProperty("status").GetInt32()));
            }

            Assert.Equal(image, File.ReadAllBytes(Path.Join(published, "replays", "00001", "request.http")));
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", File.ReadAllText(Path.Join(published, "replays", "00001", "response.http")), StringComparison.Ordinal);
            LoopbackRequest received = Assert.Single(server.Requests);
            Assert.Equal(("POST", "/p?q", "ping", "spaced"), (received.Method, received.Target, Encoding.ASCII.GetString(received.Body), received.Header("X-A")));
            using (ZipArchive archive = ZipFile.OpenRead(Path.Join(published, "replays.saz")))
            {
                Assert.Contains("x-xmat-replay-raw=true", Flags(archive, 1));
                Assert.Contains("x-xmat-replay-request-write=complete", Flags(archive, 1));
            }

            Assert.DoesNotContain(server.Origin, run.Stdout, StringComparison.Ordinal);
            CliAssert.NoLeaks(run, directory, published);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A raw request's bytes are content, never diagnostics. Canaries in its request line, a field and its body reach the
        // published content (request.http and the archive's request session) and nothing else: not standard output or
        // error, summary.json or a published path, nor the messages of a refusal, which name the rule and the line only.
        [Fact]
        public async Task ARawRequestsBytes_ReachOnlyItsPublishedContent_NeverADiagnostic()
        {
            const string HeadCanary = "XMATRAWHEADCANARY7A1";
            const string BodyCanary = "XMATRAWBODYCANARY7A2";
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("pong"));
            string raw = Path.Join(directory.Root, "request.raw");
            File.WriteAllBytes(raw, Encoding.ASCII.GetBytes(
                $"POST /{HeadCanary}?q={HeadCanary} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-Canary: {HeadCanary}\r\n" +
                $"Content-Length: {BodyCanary.Length}\r\n\r\n{BodyCanary}"));

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--raw", raw, "--target", server.Origin, "--allow-raw", "--allow-unsafe-methods", "--save-saz",
                "--out", "results");

            CliAssert.ExitCode(0, run);
            string published = Path.Join(directory.Root, "results");
            string request = File.ReadAllText(Path.Join(published, "replays", "00001", "request.http"), Encoding.Latin1);
            using (ZipArchive archive = ZipFile.OpenRead(Path.Join(published, "replays.saz")))
            using (var session = new StreamReader(archive.GetEntry("raw/1_c.txt").Open(), Encoding.Latin1))
            {
                string saved = session.ReadToEnd();
                Assert.All(new[] { request, saved }, content => Assert.Contains(HeadCanary, content, StringComparison.Ordinal));
                Assert.All(new[] { request, saved }, content => Assert.Contains(BodyCanary, content, StringComparison.Ordinal));
            }

            var diagnostics = new List<(string Source, string Text)>
            {
                ("stdout", run.Stdout),
                ("stderr", run.Stderr),
                ("summary.json", File.ReadAllText(Path.Join(published, "summary.json"))),
            };
            diagnostics.AddRange(
                Directory.GetFileSystemEntries(published, "*", SearchOption.AllDirectories)
                    .Select(entry => ("a published path", Path.GetRelativePath(directory.Root, entry))));
            AssertNoCanary(diagnostics, HeadCanary, BodyCanary);

            File.WriteAllBytes(raw, Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-Canary: {HeadCanary}\r\nx-canary: {BodyCanary}\r\n\r\n"));
            XmatRun refused = await XmatProcess.RunAsync(
                directory.Root, "compose", "--raw", raw, "--target", server.Origin, "--allow-raw", "--out", "refused");

            CliAssert.ExitCode(3, refused);
            Assert.Contains(RawCodes.DuplicateField, refused.ErrorCodes());
            AssertNoCanary(new[] { ("stdout", refused.Stdout), ("stderr", refused.Stderr) }, HeadCanary, BodyCanary);
            Assert.Single(server.Requests);
            CliAssert.NoStagingFolders(directory.Root);
        }

        private static void AssertNoCanary(IEnumerable<(string Source, string Text)> diagnostics, params string[] canaries)
        {
            foreach ((string source, string text) in diagnostics)
            {
                foreach (string canary in canaries.Where(canary => text.Contains(canary, StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.Fail($"{source} contains the request's {canary}.");
                }
            }
        }

        // Refusals in a child process: nothing is sent, and nothing is published.
        [Theory]
        [InlineData("no-consent", 3, RawCodes.PolicyRaw)]
        [InlineData("invalid", 3, RawCodes.Invalid)]
        [InlineData("too-large", 3, RawCodes.InputTooLarge)]
        [InlineData("missing", 3, RawCodes.FileUnreadable)]
        [InlineData("target-without-raw", 2, "cli.usage.missing-option")]
        public async Task Refusals_SendNothing_AndPublishNothing(string refusal, int exitCode, string code)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string raw = Path.Join(directory.Root, "request.raw");
            if (refusal == "too-large")
            {
                using FileStream file = File.Create(raw);
                file.SetLength(RawRequest.MaxFileBytes + 1);
            }
            else if (refusal != "missing")
            {
                File.WriteAllText(
                    raw,
                    refusal == "invalid" ? "GET / HTTP/1.1\nHost: 127.0.0.1\n\n" : $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");
            }

            string[] args = refusal switch
            {
                "no-consent" => new[] { "compose", "--raw", raw, "--target", server.Origin, "--out", "results" },
                "target-without-raw" => new[] { "compose", "--url", server.Origin + "/", "--target", server.Origin, "--out", "results" },
                _ => new[] { "compose", "--raw", raw, "--target", server.Origin, "--allow-raw", "--out", "results" },
            };

            XmatRun run = await XmatProcess.RunAsync(directory.Root, args);

            CliAssert.ExitCode(exitCode, run);
            Assert.Contains(code, run.ErrorCodes());
            Assert.Empty(server.Requests);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // AA-1: --repeat 4 --parallel 3 --save-saz, with three sends begun and held in different phases (one in setup, one
        // reading after its write, one that ended and waits before the sink), then a cancellation. The run publishes the
        // three sends that began, and nothing for the fourth, which never began; exit code 130 with replay.cancelled and
        // cli.cancelled-after-send. Every output agrees, send by send: the report, the standard-output document that xmat
        // writes from it, summary.json, the request files and the archive's sessions.
        [Fact]
        public async Task ACancelledRun_PublishesTheSendsThatBegan_InEveryOutput()
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            var held = new HeldSends();
            using var cancellation = new CancellationTokenSource();

            Task<CliReport> running = RunInProcessAsync(
                held.Apply, null, cancellation.Token, RawArgs(directory, raw, "--repeat", "4", "--parallel", "3", "--save-saz"));
            await held.AllHeld.WaitAsync(Wait);
            cancellation.Cancel();
            held.Release();
            CliReport report = await running.WaitAsync(Wait);

            string[] errors = { "replay.cancelled", CliReplayCommand.CancelledAfterSendCode };
            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.Equal(errors, report.Errors.Select(error => error.Code));
            Assert.Equal(
                new[] { "summary:summary.json", "replays:replays", "saz:replays.saz" },
                report.Artifacts.Select(artifact => artifact.Kind + ":" + artifact.Path));
            Assert.Equal(3, held.Connects);
            string published = Path.Join(directory.Root, "results");
            int[] ids = { 1, 2, 3 };
            long imageLength = new FileInfo(raw).Length;
            Dictionary<int, (string, long?)> reported = report.Replay.Items.ToDictionary(
                item => item.Id, item => (item.RequestWrite, item.RequestBytesWritten));

            using var output = new MemoryStream();
            using var error = new StringWriter();
            CliApplication.Write(new CliConsole(output, error), report);
            Dictionary<int, (string, long?)> written;
            using (JsonDocument document = JsonDocument.Parse(output.ToArray()))
            {
                JsonElement root = document.RootElement;
                Assert.Equal(("cancelled", CliExitCodes.Cancelled), (root.GetProperty("status").GetString(), root.GetProperty("exitCode").GetInt32()));
                Assert.Equal(errors, root.GetProperty("errors").EnumerateArray().Select(entry => entry.GetProperty("code").GetString()));
                written = WriteStates(root);
            }

            Dictionary<int, (string, long?)> summarized;
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(published, "summary.json"))))
            {
                Assert.Equal("cancelled", summary.RootElement.GetProperty("status").GetString());
                Assert.Equal(errors, summary.RootElement.GetProperty("errors").EnumerateArray().Select(entry => entry.GetProperty("code").GetString()));
                summarized = WriteStates(summary.RootElement);
            }

            Dictionary<int, string> archived;
            using (ZipArchive archive = ZipFile.OpenRead(Path.Join(published, "replays.saz")))
            {
                Assert.Equal(
                    ids,
                    archive.Entries.Where(entry => entry.FullName.EndsWith("_m.xml", StringComparison.Ordinal))
                        .Select(entry => int.Parse(entry.FullName[4..entry.FullName.IndexOf('_')], System.Globalization.CultureInfo.InvariantCulture))
                        .Order());
                Assert.All(ids, id => Assert.Contains("x-xmat-replay-raw=true", Flags(archive, id)));
                archived = ids.ToDictionary(
                    id => id,
                    id => Flags(archive, id).Single(flag => flag.StartsWith("x-xmat-replay-request-write=", StringComparison.Ordinal)).Split('=')[1]);
            }

            Assert.Equal(ids, reported.Keys.Order());
            Assert.Equal(reported, written);
            Assert.Equal(reported, summarized);
            Assert.Equal(reported.ToDictionary(pair => pair.Key, pair => pair.Value.Item1), archived);

            // The send that answered, and the one that wrote everything before it waited to read, are complete; the one held
            // in setup wrote nothing.
            Assert.Equal((RawWriteState.Complete, (long?)imageLength), reported[held.EndedId]);
            Assert.Equal(
                new[] { (RawWriteState.Complete, (long?)imageLength), (RawWriteState.Complete, (long?)imageLength), (RawWriteState.None, (long?)0) },
                reported.Values.OrderBy(state => state.Item1, StringComparer.Ordinal));
            Assert.Equal(
                ids.Select(id => id.ToString("D5")),
                Directory.GetDirectories(Path.Join(published, "replays")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.All(ids, id => Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(Path.Join(published, "replays", id.ToString("D5"), "request.http"))));
            Assert.Equal(
                new[] { held.EndedId },
                ids.Where(id => File.Exists(Path.Join(published, "replays", id.ToString("D5"), "response.http"))));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Y1: a cancellation ordered just before the first send's Begin publishes nothing (cli.cancelled); one just after it
        // publishes that send.
        [Theory]
        [InlineData("before-begin", false)]
        [InlineData("after-begin", true)]
        public async Task ACancellationAtTheFirstSendsBegin_PublishesItOnlyIfItBegan(string point, bool published)
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            using var cancellation = new CancellationTokenSource();
            Action<ReplayOperation> seam = operation => operation.RawSenderFactory = setup => new RawSender(setup with
            {
                Connector = async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Stream.Null;
                },
                Hooks = new RawSenderHooks(At: (at, started) =>
                {
                    if ((point == "before-begin" && at == RawSendPoint.BeforeBegin) ||
                        (point == "after-begin" && at == RawSendPoint.AfterInvoke && started == RawOperation.Connect))
                    {
                        cancellation.Cancel();
                    }
                }),
            });

            CliReport report = await RunInProcessAsync(seam, null, cancellation.Token, RawArgs(directory, raw)).WaitAsync(Wait);

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            string[] codes = report.Errors.Select(error => error.Code).ToArray();
            Assert.Equal(published, Directory.Exists(Path.Join(directory.Root, "results")));
            if (published)
            {
                Assert.Equal(new[] { "replay.cancelled", CliReplayCommand.CancelledAfterSendCode }, codes);
                Assert.Single(report.Replay.Items);
            }
            else
            {
                Assert.Contains("cli.cancelled", codes);
                Assert.DoesNotContain(CliReplayCommand.CancelledAfterSendCode, codes);
                Assert.Empty(report.Artifacts);
            }

            CliAssert.NoStagingFolders(directory.Root);
        }

        // A cancellation that comes after the operation returned, here while the archive is completed, leaves the run its
        // completion outcome: published, with exit code 12 for the send that got no response.
        [Fact]
        public async Task ACancellationAfterTheOperationReturned_KeepsTheCompletionOutcome()
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            using var cancellation = new CancellationTokenSource();
            int connects = 0;
            Action<ReplayOperation> seam = operation => operation.RawSenderFactory = setup => new RawSender(setup with
            {
                Connector = (_, _) =>
                {
                    if (Interlocked.Increment(ref connects) == 1)
                    {
                        var answering = new SeamConnection();
                        answering.Respond(Ok);
                        return Task.FromResult<Stream>(answering);
                    }

                    return Task.FromException<Stream>(new IOException("refused"));
                },
            });

            CliReport report = await RunInProcessAsync(
                seam,
                file => new ArchiveSeam(file, onCentralDirectory: () => cancellation.Cancel()),
                cancellation.Token,
                RawArgs(directory, raw, "--repeat", "2", "--save-saz")).WaitAsync(Wait);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(CliExitCodes.RequestsFailed, report.ExitCode);
            Assert.Empty(report.Errors);
            Assert.Equal(3, report.Artifacts.Count);
            Assert.True(File.Exists(Path.Join(directory.Root, "results", "replays.saz")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A finalization that fails to write (exit code 6) or passes its bound (130) publishes nothing, and the run then
        // never says that its sends were published.
        [Theory]
        [InlineData("write-failure", CliExitCodes.Failed, "cli.output.write-failed")]
        [InlineData("bound", CliExitCodes.Cancelled, "cli.cancelled")]
        public async Task AFinalizationThatFailsOrPassesItsBound_PublishesNothing(string failure, int exitCode, string code)
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            using var cancellation = new CancellationTokenSource();
            Action<ReplayOperation> seam = operation => operation.RawSenderFactory = setup => new RawSender(setup with
            {
                Connector = async (_, token) =>
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                    return Stream.Null;
                },
            });
            Action onCentralDirectory = failure == "write-failure"
                ? () => throw new IOException("The disk is full.")
                : () => Thread.Sleep(500);
            CliStagedRun.FinalizationBoundSeam.Value = failure == "bound" ? TimeSpan.FromMilliseconds(50) : null;
            CliReport report;
            try
            {
                report = await RunInProcessAsync(
                    seam, file => new ArchiveSeam(file, onCentralDirectory), cancellation.Token, RawArgs(directory, raw, "--save-saz"))
                    .WaitAsync(Wait);
            }
            finally
            {
                CliStagedRun.FinalizationBoundSeam.Value = null;
            }

            Assert.Equal(exitCode, report.ExitCode);
            string[] codes = report.Errors.Select(error => error.Code).ToArray();
            Assert.Contains(code, codes);
            Assert.DoesNotContain(CliReplayCommand.CancelledAfterSendCode, codes);
            Assert.Empty(report.Artifacts);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // L1-A1: a send's archive session that fails to write while the run is being cancelled, the failure arriving after the
        // cancellation or before it, publishes nothing, not even the sends whose outputs were written: exit code 6
        // (cli.output.write-failed), never cli.cancelled-after-send.
        [Theory]
        [InlineData("cancel-then-fail")]
        [InlineData("fail-then-cancel")]
        public async Task AWriteThatFailsWhileTheRunIsCancelled_PublishesNothing(string order)
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            bool failFirst = order == "fail-then-cancel";
            using var cancellation = new CancellationTokenSource();
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<ReplayOperation> seam = operation =>
            {
                operation.RawSenderFactory = setup => new RawSender(setup with { Connector = AnsweringAsync });
                if (failFirst)
                {
                    // The third send ends while the second is still before the sink; once the second's session failed,
                    // the third cancels the run.
                    operation.RawSendEnded = async id =>
                    {
                        if (id == 2)
                        {
                            await thirdEnded.Task;
                        }
                        else if (id == 3)
                        {
                            thirdEnded.TrySetResult();
                            await failed.Task;
                            cancellation.Cancel();
                        }
                    };
                }
            };
            ArchiveSeam archive = null;

            CliReport report = await RunInProcessAsync(
                seam,
                file => archive = new ArchiveSeam(file, null, 2, () =>
                {
                    if (!failFirst)
                    {
                        cancellation.Cancel();
                    }

                    failed.TrySetResult();
                    throw new IOException("The disk is full.");
                }),
                cancellation.Token,
                RawArgs(directory, raw, "--repeat", failFirst ? "3" : "2", "--parallel", failFirst ? "2" : "1", "--save-saz"))
                .WaitAsync(Wait);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(CliExitCodes.Failed, report.ExitCode);
            string[] codes = report.Errors.Select(error => error.Code).ToArray();
            Assert.Contains("cli.output.write-failed", codes);
            Assert.DoesNotContain(CliReplayCommand.CancelledAfterSendCode, codes);
            Assert.Empty(report.Artifacts);
            Assert.False(archive.CentralDirectoryWritten);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // L1-A2: a finalization that passes its bound while a later send's session is added, after an earlier session was
        // written, publishes nothing and never tries to complete the archive that the stopped session left unfinished: exit
        // code 130 (cli.cancelled), never cli.cancelled-after-send or an internal error. With sends in parallel, a send that
        // reaches the sink after that is not written either.
        [Theory]
        [InlineData("sequential")]
        [InlineData("parallel")]
        public async Task AFinalizationThatPassesItsBoundWhileASessionIsAdded_PublishesNothing(string mode)
        {
            using var directory = new CliTestDirectory();
            string raw = WriteRaw(directory, Get);
            bool parallel = mode == "parallel";
            using var cancellation = new CancellationTokenSource();
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<ReplayOperation> seam = operation =>
            {
                operation.RawSenderFactory = setup => new RawSender(setup with { Connector = AnsweringAsync });
                if (parallel)
                {
                    // The third send ends while the second is still before the sink, and reaches the sink while the
                    // second's session is being added.
                    operation.RawSendEnded = async id =>
                    {
                        if (id == 2)
                        {
                            await thirdEnded.Task;
                        }
                        else if (id == 3)
                        {
                            thirdEnded.TrySetResult();
                            await second.Task;
                        }
                    };
                }
            };
            ArchiveSeam archive = null;
            CliStagedRun.FinalizationBoundSeam.Value = TimeSpan.FromMilliseconds(50);
            CliReport report;
            try
            {
                // The second session is being added when the run is cancelled, and the bound passes before it is done.
                report = await RunInProcessAsync(
                    seam,
                    file => archive = new ArchiveSeam(file, null, 2, () =>
                    {
                        second.TrySetResult();
                        cancellation.Cancel();
                        Thread.Sleep(500);
                    }),
                    cancellation.Token,
                    RawArgs(directory, raw, "--repeat", parallel ? "3" : "2", "--parallel", parallel ? "2" : "1", "--save-saz"))
                    .WaitAsync(Wait);
            }
            finally
            {
                CliStagedRun.FinalizationBoundSeam.Value = null;
            }

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            string[] codes = report.Errors.Select(error => error.Code).ToArray();
            Assert.Contains("cli.cancelled", codes);
            Assert.DoesNotContain(CliReplayCommand.CancelledAfterSendCode, codes);
            Assert.DoesNotContain("cli.internal", codes);
            Assert.Empty(report.Artifacts);
            Assert.False(archive.CentralDirectoryWritten);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // docs/cli.md names every code that compose --raw can report. It leaves out only the operation's refusals of options
        // that the command line never passes with --raw, which are usage errors there, and the recheck of an image that
        // changed after it was made, which a file read once cannot reach.
        [Fact]
        public void TheDocs_NameEveryCodeThatComposeRawCanReport()
        {
            string[] unreachable =
            {
                RawCodes.Changed, RawCodes.Proxy, RawCodes.ClientCert, RawCodes.WindowsAuth, RawCodes.HttpVersion,
                RawCodes.Redirects, RawCodes.UnsupportedOption,
            };
            string docs = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "cli.md"));
            string[] codes = typeof(RawCodes).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue())
                .ToArray();

            Assert.Subset(codes.ToHashSet(), unreachable.ToHashSet());
            Assert.All(codes.Except(unreachable), code => Assert.Contains("`" + code + "`", docs, StringComparison.Ordinal));
        }

        // Tests may only shorten the finalization's bound: a value that is not shorter, or that is negative, as
        // Timeout.InfiniteTimeSpan is, leaves it at 30 seconds, so no seam can lengthen it or turn it off.
        [Theory]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        [InlineData(29_999, 29_999)]
        [InlineData(30_000, 30_000)]
        [InlineData(3_600_000, 30_000)]
        [InlineData(-1, 30_000)]
        [InlineData(-5_000, 30_000)]
        public void TheFinalizationBound_CanOnlyBeShortened(int seamMilliseconds, int expectedMilliseconds)
        {
            try
            {
                Assert.Equal(TimeSpan.FromSeconds(30), CliStagedRun.EffectiveFinalizationBound);
                CliStagedRun.FinalizationBoundSeam.Value = TimeSpan.FromMilliseconds(seamMilliseconds);
                Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), CliStagedRun.EffectiveFinalizationBound);
            }
            finally
            {
                CliStagedRun.FinalizationBoundSeam.Value = null;
            }
        }

        private static string WriteRaw(CliTestDirectory directory, string request)
        {
            string path = Path.Join(directory.Root, "request.raw");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(request));
            return path;
        }

        private static string[] RawArgs(CliTestDirectory directory, string raw, params string[] more) =>
            new[] { "compose", "--raw", raw, "--target", Target, "--allow-raw", "--out", Path.Join(directory.Root, "results") }
                .Concat(more)
                .ToArray();

        // A connect that answers 200 at once, on a connection of its own.
        private static Task<Stream> AnsweringAsync(System.Net.IPEndPoint endpoint, CancellationToken token)
        {
            var connection = new SeamConnection();
            connection.Respond(Ok);
            return Task.FromResult<Stream>(connection);
        }

        private static async Task<CliReport> RunInProcessAsync(
            Action<ReplayOperation> operationSeam, Func<Stream, Stream> archiveSeam, CancellationToken cancellationToken, params string[] args)
        {
            CliParseResult parsed = CliCommandLine.Parse(args);
            Assert.True(parsed.Succeeded, parsed.Error?.Message);
            CliReplayCommand.RawOperationSeam.Value = operationSeam;
            CliReplayCommand.SazStreamSeam.Value = archiveSeam;
            try
            {
                return await CliReplayCommand.RunAsync(parsed.Request, "run", configureStorage: false, cancellationToken);
            }
            finally
            {
                CliReplayCommand.RawOperationSeam.Value = null;
                CliReplayCommand.SazStreamSeam.Value = null;
            }
        }

        private static string[] Flags(ZipArchive archive, long id)
        {
            using Stream metadata = archive.GetEntry($"raw/{id}_m.xml").Open();
            return XDocument.Load(metadata).Root.Element("SessionFlags").Elements("SessionFlag")
                .Select(flag => (string)flag.Attribute("N") + "=" + (string)flag.Attribute("V"))
                .ToArray();
        }

        // The write state and the bytes written of each send of a document's replays array, by id.
        private static Dictionary<int, (string, long?)> WriteStates(JsonElement root) =>
            root.GetProperty("replays").EnumerateArray().ToDictionary(
                send => send.GetProperty("id").GetInt32(),
                send =>
                {
                    JsonElement bytes = send.GetProperty("requestBytesWritten");
                    return (send.GetProperty("requestWrite").GetString(), bytes.ValueKind == JsonValueKind.Null ? (long?)null : bytes.GetInt64());
                });

        // Three raw sends held in different phases through the operation's seams: the first connect never completes (held in
        // setup), the second connection takes the write and then waits to be read from (held reading), and the third answers
        // and its send then waits before the sink (ended). A fourth connect fails the test.
        private sealed class HeldSends
        {
            private readonly TaskCompletionSource _setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _wrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly SeamConnection _reading;
            private readonly SeamConnection _answering = new();
            private int _connects;
            private int _endedId;

            internal HeldSends()
            {
                _reading = new SeamConnection { AfterWrite = _ => _wrote.TrySetResult() };
                _answering.Respond(Ok);
            }

            internal int Connects => Volatile.Read(ref _connects);

            internal int EndedId => Volatile.Read(ref _endedId);

            internal Task AllHeld => Task.WhenAll(_setup.Task, _wrote.Task, _reading.ReadWaiting.Task, _ended.Task);

            internal void Release() => _release.TrySetResult();

            internal void Apply(ReplayOperation operation)
            {
                operation.RawSenderFactory = setup => new RawSender(setup with { Connector = ConnectAsync });
                operation.RawSendEnded = id =>
                {
                    // The first send to end is the one that answered; the others end once the run is cancelled.
                    if (Interlocked.CompareExchange(ref _endedId, id, 0) == 0)
                    {
                        _ended.TrySetResult();
                    }

                    return _release.Task;
                };
            }

            private async Task<Stream> ConnectAsync(System.Net.IPEndPoint endpoint, CancellationToken token)
            {
                switch (Interlocked.Increment(ref _connects))
                {
                    case 1:
                        _setup.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                        throw new InvalidOperationException("The held setup ended on its own.");
                    case 2:
                        return _reading;
                    case 3:
                        return _answering;
                    default:
                        throw new InvalidOperationException("A fourth send began.");
                }
            }
        }

        // The archive's stream as the seam gives it to the writer: onCentralDirectory runs once, as the archive's completion
        // writes its central directory, and onSession once, as session sessionId's request entry is written (its name is in
        // the entry's header); either may fail, delay or cancel the run. CentralDirectoryWritten says whether the archive's
        // completion began. The file under it is the sink's to close.
        private sealed class ArchiveSeam(Stream inner, Action onCentralDirectory, long sessionId = 0, Action onSession = null)
            : Stream
        {
            private static readonly byte[] CentralDirectoryHeader = { 0x50, 0x4B, 0x01, 0x02 };
            private readonly byte[] _sessionName = Encoding.ASCII.GetBytes($"raw/{sessionId}_c.txt");
            private bool _seen;
            private bool _sessionSeen;

            internal bool CentralDirectoryWritten => _seen;

            public override bool CanRead => false;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => true;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush() => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (onSession != null && !_sessionSeen && buffer.IndexOf(_sessionName) >= 0)
                {
                    _sessionSeen = true;
                    onSession();
                }

                if (!_seen && buffer.IndexOf(CentralDirectoryHeader) >= 0)
                {
                    _seen = true;
                    onCentralDirectory?.Invoke();
                }

                inner.Write(buffer);
            }
        }
    }
}
