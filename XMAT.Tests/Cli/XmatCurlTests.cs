// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XMAT.Operations.Replay;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat curl, run as a child process against captures whose requests target loopback servers in the test process:
    // consent comes before anything is read, each request becomes a script and its body a body file, the output holds no
    // captured value, what curl cannot send the same way is refused, and nothing is ever sent. Nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatCurlTests
    {
        private const string Canary = "XMATCURLCANARY41";

        // Without --include-sensitive, xmat refuses before it opens any input or reserves --out: the files are held without
        // sharing, so a run that tried to read one would fail with another code.
        [Fact]
        public async Task Curl_RefusesWithoutConsent_BeforeReadingAnythingOrReservingOutput()
        {
            using var directory = new CliTestDirectory();
            string capture = ReplayCaptures.WriteSaz(directory.Root, "capture.saz", new CapturedExchange("GET", "http://127.0.0.1:9/x"));
            string requestFile = Path.Join(directory.Root, "request.json");
            string bodyFile = Path.Join(directory.Root, "body.bin");
            File.WriteAllText(requestFile, "{}");
            File.WriteAllBytes(bodyFile, new byte[] { 1 });

            var runs = new List<XmatRun>();
            using (new FileStream(capture, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(requestFile, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(bodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                runs.Add(await XmatProcess.RunAsync(
                    directory.Root, "curl", capture, "--request", "1", "--body-file", bodyFile, "--out", "out"));
                runs.Add(await XmatProcess.RunAsync(
                    directory.Root, "curl", "--request-file", requestFile, "--allow-origin-change", "--out", "out"));
                runs.Add(await XmatProcess.RunAsync(directory.Root, "curl", "--url", "http://127.0.0.1:9/x", "--out", "out"));
            }

            foreach (XmatRun run in runs)
            {
                CliAssert.ExitCode(3, run);
                Assert.Equal(new[] { CurlExportCodes.ConsentRequired }, run.ErrorCodes());
                Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            }

            Assert.Equal(new[] { "body.bin", "capture.saz", "request.json" }, directory.Entries());
        }

        // One script per request and a body file per body that is not empty, written from the bytes whose length and hash
        // the script checks. Only the scripts and body files hold captured content; nothing is sent.
        [Fact]
        public async Task Curl_PublishesAScriptPerRequest_AndABodyFilePerBody_WithOutputThatHoldsNoCapturedValue()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            byte[] body = Encoding.UTF8.GetBytes($"{{\"secret\":\"{Canary}\"}}").Concat(new byte[] { 0, 0xFF, 0x0D, 0x0A }).ToArray();
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange(
                    "POST",
                    server.Origin + $"/item?token={Canary}",
                    $"Authorization: Bearer {Canary}\r\nContent-Type: application/json\r\n",
                    body),
                new CapturedExchange("GET", server.Origin + "/read", "Accept: application/json\r\n"));
            string inputHash = CliAssert.Sha256(input);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "curl", input, "--request", "all", "--include-sensitive", "--out", "out");

            CliAssert.ExitCode(0, run);
            string output = Path.Join(directory.Root, "out");
            Assert.Equal(
                new[] { "bodies/00001.bin", "scripts/00001.ps1", "scripts/00002.ps1", "summary.json" },
                Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(output, file).Replace('\\', '/'))
                    .OrderBy(file => file, StringComparer.Ordinal));
            Assert.Equal(body, File.ReadAllBytes(Path.Join(output, "bodies", "00001.bin")));
            string first = File.ReadAllText(Path.Join(output, "scripts", "00001.ps1"));
            string second = File.ReadAllText(Path.Join(output, "scripts", "00002.ps1"));
            Assert.Contains("'../bodies/00001.bin'", first, StringComparison.Ordinal);
            Assert.Contains(Convert.ToHexString(SHA256.HashData(body)), first, StringComparison.Ordinal);
            Assert.Contains($"'Authorization: Bearer {Canary}'", first, StringComparison.Ordinal);
            Assert.DoesNotContain("bodies", second, StringComparison.Ordinal);

            JsonElement json = run.Json();
            Assert.Equal(
                ("saz", inputHash),
                (json.GetProperty("input").GetProperty("kind").GetString(), json.GetProperty("input").GetProperty("sha256").GetString()));
            Assert.Equal(
                new[] { ("summary", "summary.json"), ("scripts", "scripts"), ("bodies", "bodies") },
                json.GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => (artifact.GetProperty("kind").GetString(), artifact.GetProperty("path").GetString())));
            JsonElement[] scripts = json.GetProperty("scripts").EnumerateArray().ToArray();
            Assert.Equal(2, scripts.Length);
            Assert.Equal(
                (1, (long?)1, "scripts/00001.ps1", "bodies/00001.bin", (long)body.Length, "auto", false, 1, 1),
                Describe(scripts[0]));
            Assert.Equal((2, (long?)2, "scripts/00002.ps1", (string)null, 0L, "auto", false, 0, 0), Describe(scripts[1]));

            string summary = File.ReadAllText(Path.Join(output, "summary.json"));
            using (JsonDocument document = JsonDocument.Parse(summary))
            {
                Assert.Equal("xmat-curl/1", document.RootElement.GetProperty("schema").GetString());
                Assert.Equal(
                    scripts.Select(Describe),
                    document.RootElement.GetProperty("scripts").EnumerateArray().Select(Describe));
            }

            foreach (string text in new[] { run.Stdout, run.Stderr, summary })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(server.Origin, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(directory.Name, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(0, server.Connections);
            CliAssert.OnlyCurrentUserCanOpen(output);
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Equal(inputHash, CliAssert.Sha256(input));
        }

        // Each refusal is exit code 3 with its code and publishes nothing; the same requests export once the reason is gone.
        [Fact]
        public async Task Curl_RefusesWhatCurlCannotSendTheSameWay_AndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            string root = directory.Root;
            string input = ReplayCaptures.WriteSaz(
                root, "capture.saz", new CapturedExchange("GET", "http://127.0.0.1:9/read", "Accept: application/json\r\n"));
            string bodyFile = Path.Join(root, "body.bin");
            File.WriteAllBytes(bodyFile, new byte[] { 1, 2, 3 });
            CliAssert.ExitCode(
                0,
                await XmatProcess.RunAsync(
                    root, "save-request", "--url", "https://127.0.0.1:9/x", "--include-sensitive", "--http-version", "2",
                    "--out", "http2"));
            CliAssert.ExitCode(0, await XmatProcess.RunAsync(root, "save-request", "--url", "https://127.0.0.1:9/x", "--out", "template"));
            string http2 = Path.Join(root, "http2", "requests", "00001.json");
            string template = Path.Join(root, "template", "requests", "00001.json");

            (string Code, string[] Args)[] refusals =
            {
                ("replay.curl.unsupported", new[] { "--request-file", http2 }),
                ("replay.request.incomplete", new[] { "--request-file", template }),
                ("replay.policy.origin-change", new[] { input, "--request", "1", "--url", "http://127.0.0.2:9/read" }),
                ("replay.request.control-header", new[] { "--url", "http://127.0.0.1:9/x", "--header", "X-OverrideHost: a.test" }),
                ("replay.curl.arguments-too-large", new[] { "--url", "http://127.0.0.1:9/x", "--header", "X-Big: " + new string('a', 16200) }),
                ("replay.curl.unsupported", new[] { "--url", "http://127.0.0.1:9/item/#" }),
                ("replay.curl.head-body", new[] { "--url", "http://127.0.0.1:9/x", "--method", "HEAD", "--body-file", bodyFile }),
            };
            foreach ((string code, string[] args) in refusals)
            {
                XmatRun refused = await XmatProcess.RunAsync(root, args.Prepend("curl").Concat(new[] { "--include-sensitive", "--out", "refused" }).ToArray());

                CliAssert.ExitCode(3, refused);
                Assert.Equal(new[] { code }, refused.ErrorCodes());
                Assert.False(Directory.Exists(Path.Join(root, "refused")), code);
            }

            // Usage errors: HTTP/2 only and an option that only a send uses.
            foreach (string[] args in new[] { new[] { "--http-version", "2" }, new[] { "--repeat", "2" } })
            {
                XmatRun usage = await XmatProcess.RunAsync(
                    root, new[] { "curl", "--url", "https://127.0.0.1:9/x", "--include-sensitive", "--out", "refused" }.Concat(args).ToArray());
                CliAssert.ExitCode(2, usage);
            }

            CliAssert.ExitCode(
                0,
                await XmatProcess.RunAsync(
                    root, "curl", "--request-file", http2, "--http-version", "1.1", "--include-sensitive", "--out", "overridden"));
            CliAssert.ExitCode(
                0,
                await XmatProcess.RunAsync(
                    root, "curl", input, "--request", "1", "--url", "http://127.0.0.2:9/read", "--allow-origin-change",
                    "--include-sensitive", "--out", "moved"));
            Assert.Equal(
                "1.1",
                Assert.Single(JsonDocument.Parse(File.ReadAllText(Path.Join(root, "overridden", "summary.json")))
                    .RootElement.GetProperty("scripts").EnumerateArray()).GetProperty("httpVersion").GetString());
            CliAssert.NoStagingFolders(root);
        }

        // At most 10,000 scripts: a larger selection is refused, as the requests are counted before any is built.
        [Fact]
        public async Task Curl_RefusesMoreThan10000Requests()
        {
            using var directory = new CliTestDirectory();
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                Enumerable.Range(1, ReplayOptions.MaxSends + 1)
                    .Select(number => new CapturedExchange("GET", $"http://127.0.0.1:9/{number}"))
                    .ToArray());

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "curl", input, "--request", "all", "--include-sensitive", "--out", "out");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { "replay.plan.too-large" }, run.ErrorCodes());
            Assert.Equal(new[] { "capture.saz" }, directory.Entries());
        }

        // A published script sends its request with its body file from the published layout: the captured request line,
        // headers and body bytes reach the server.
        [CurlScriptFact]
        public async Task Curl_PublishedScript_SendsTheCapturedRequest_WithItsBodyFile()
        {
            using var directory = new CliTestDirectory();
            using var server = new RawLoopback();
            byte[] body = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("POST", server.Origin + "/upload?a=1", "Content-Type: application/octet-stream\r\n", body));
            CliAssert.ExitCode(
                0, await XmatProcess.RunAsync(directory.Root, "curl", input, "--request", "1", "--include-sensitive", "--out", "out"));

            Task<ReceivedRequest> received = server.AcceptAsync();
            ScriptRun script = await CurlScripts.RunAsync(Path.Join(directory.Root, "out", "scripts", "00001.ps1"), directory.Root);
            ReceivedRequest request = await received;

            Assert.True(script.ExitCode == 0, script.ToString());
            Assert.Equal("POST /upload?a=1 HTTP/1.1", request.Lines[0]);
            Assert.Contains("Content-Type:application/octet-stream", request.Lines);
            Assert.Equal(body, request.Body);
        }

        private static (int Id, long? Request, string Script, string Body, long BodyBytes, string HttpVersion, bool UnicodeCurl,
            int Headers, int QueryParameters) Describe(JsonElement script) =>
            (script.GetProperty("id").GetInt32(),
                script.GetProperty("request").ValueKind == JsonValueKind.Null ? null : script.GetProperty("request").GetInt64(),
                script.GetProperty("script").GetString(),
                script.GetProperty("body").GetString(),
                script.GetProperty("bodyBytes").GetInt64(),
                script.GetProperty("httpVersion").GetString(),
                script.GetProperty("unicodeCurl").GetBoolean(),
                script.GetProperty("recognized").GetProperty("headers").GetInt32(),
                script.GetProperty("recognized").GetProperty("queryParameters").GetInt32());
    }
}
