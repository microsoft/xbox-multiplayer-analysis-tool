// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat save-request and xmat compose --request-file, run as child processes against loopback servers in the test
    // process: templates keep no captured value, content needs consent, and a file's request is sent only once complete and
    // only to its own origin. Nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatSaveRequestTests
    {
        private const string Canary = "XMATREQUESTFILECANARY73";

        [Fact]
        public async Task SaveRequest_SavesTemplatesByDefault_AndContentOnlyWhenAsked()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange(
                    "POST",
                    server.Origin + $"/item?token={Canary}",
                    $"Authorization: Bearer {Canary}\r\nContent-Type: application/json\r\n",
                    Encoding.UTF8.GetBytes($"{{\"secret\":\"{Canary}\"}}")));
            string inputHash = CliAssert.Sha256(input);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun template = await XmatProcess.RunAsync(directory.Root, "save-request", input, "--request", "1", "--out", "template");
            XmatRun content = await XmatProcess.RunAsync(
                directory.Root, "save-request", input, "--request", "1", "--include-sensitive", "--out", "content");
            XmatRun moved = await XmatProcess.RunAsync(
                directory.Root, "save-request", input, "--request", "1", "--include-sensitive", "--url", "http://127.0.0.2:9/x",
                "--out", "moved");

            CliAssert.ExitCode(0, template);
            CliAssert.ExitCode(0, content);
            CliAssert.ExitCode(3, moved);
            Assert.Equal(new[] { "replay.policy.origin-change" }, moved.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "moved")));
            Assert.Empty(server.Requests);

            string templateFile = File.ReadAllText(Path.Join(directory.Root, "template", "requests", "00001.json"));
            string contentFile = File.ReadAllText(Path.Join(directory.Root, "content", "requests", "00001.json"));
            Assert.DoesNotContain(Canary, templateFile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", templateFile, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Canary, contentFile, StringComparison.Ordinal);

            JsonElement json = content.Json();
            Assert.Equal(
                ("saz", inputHash),
                (json.GetProperty("input").GetProperty("kind").GetString(), json.GetProperty("input").GetProperty("sha256").GetString()));
            JsonElement saved = Assert.Single(json.GetProperty("requests").EnumerateArray());
            JsonElement recognized = saved.GetProperty("recognized");
            Assert.Equal(
                ("content", "requests/00001.json", 1, 1),
                (saved.GetProperty("profile").GetString(), saved.GetProperty("file").GetString(),
                    recognized.GetProperty("headers").GetInt32(), recognized.GetProperty("queryParameters").GetInt32()));
            Assert.Equal(
                new[] { "url", "headers", "body" },
                Assert.Single(template.Json().GetProperty("requests").EnumerateArray())
                    .GetProperty("omitted").EnumerateArray().Select(kind => kind.GetString()));
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(directory.Root, "content", "summary.json"))))
            {
                Assert.Equal("xmat-save-request/1", summary.RootElement.GetProperty("schema").GetString());
            }

            // Only the files hold captured content, and only with --include-sensitive.
            foreach (string text in new[]
            {
                template.Stdout, template.Stderr, content.Stdout, content.Stderr,
                File.ReadAllText(Path.Join(directory.Root, "content", "summary.json")),
            })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
            }

            CliAssert.OnlyCurrentUserCanOpen(Path.Join(directory.Root, "content"));
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Equal(inputHash, CliAssert.Sha256(input));
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task SaveRequest_KeepsScopedUrlsAsExactData()
        {
            using var directory = new CliTestDirectory();
            const string Url = "http://[fe80::1%25Ethernet]/typed";
            XmatRun typed = await XmatProcess.RunAsync(
                directory.Root, "save-request", "--url", Url, "--include-sensitive", "--out", "typed");
            string scopedCapture = ReplayCaptures.WriteSaz(
                directory.Root, "scoped.saz", new CapturedExchange("GET", "http://[fe80::1%12]/captured"));
            XmatRun captured = await XmatProcess.RunAsync(
                directory.Root,
                "save-request", scopedCapture, "--request", "1", "--include-sensitive", "--out", "captured");
            string requestFile = Path.Join(directory.Root, "scoped-request.json");
            File.WriteAllText(
                requestFile,
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\"," +
                    "\"url\":\"http://[fe80::1%Ethernet]/loaded\",\"httpVersion\":\"auto\"," +
                    "\"headers\":[],\"body\":\"\",\"omitted\":[]}");
            XmatRun loaded = await XmatProcess.RunAsync(
                directory.Root, "save-request", "--request-file", requestFile, "--include-sensitive", "--out", "loaded");

            CliAssert.ExitCode(0, typed);
            CliAssert.ExitCode(0, captured);
            CliAssert.ExitCode(0, loaded);
            using JsonDocument typedFile = JsonDocument.Parse(
                File.ReadAllText(Path.Join(directory.Root, "typed", "requests", "00001.json")));
            using JsonDocument capturedFile = JsonDocument.Parse(
                File.ReadAllText(Path.Join(directory.Root, "captured", "requests", "00001.json")));
            using JsonDocument loadedFile = JsonDocument.Parse(
                File.ReadAllText(Path.Join(directory.Root, "loaded", "requests", "00001.json")));
            Assert.Equal(Url, typedFile.RootElement.GetProperty("url").GetString());
            Assert.Equal("http://[fe80::1%12]/captured", capturedFile.RootElement.GetProperty("url").GetString());
            Assert.Equal("http://[fe80::1%Ethernet]/loaded", loadedFile.RootElement.GetProperty("url").GetString());
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task SaveRequest_InputFailureWinsBeforeExistingOutput()
        {
            using var directory = new CliTestDirectory();
            string output = Path.Join(directory.Root, "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Join(output, "keep.txt"), "keep");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "save-request", Path.Join(directory.Root, "missing.saz"),
                "--request", "1", "--out", output);

            CliAssert.ExitCode(CliExitCodes.InvalidInput, run);
            Assert.Equal(new[] { "analysis.input.not-found" }, run.ErrorCodes());
            Assert.Equal(CliCleanupStates.NotNeeded, run.Json().GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal("keep", File.ReadAllText(Path.Join(output, "keep.txt")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Theory]
        [InlineData("invalid", CliExitCodes.Usage, "cli.usage.invalid-value")]
        [InlineData("unprotected", CliExitCodes.Failed, "cli.output.unprotected")]
        public async Task SaveRequest_OutputTargetRefusalPrecedesInvalidInput(string targetKind, int exitCode, string code)
        {
            using var directory = new CliTestDirectory();
            string output = targetKind == "invalid" ? @"C:\bad|x" : @"\\server\share\out";

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "save-request", "missing.saz", "--request", "1", "--out", output);

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
        public async Task Compose_SendsARequestFile_OnlyComplete_AndOnlyToItsOrigin()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/read?page=2", "Accept: application/json\r\n"));
            CliAssert.ExitCode(
                0, await XmatProcess.RunAsync(directory.Root, "save-request", input, "--request", "1", "--out", "template"));
            CliAssert.ExitCode(
                0,
                await XmatProcess.RunAsync(
                    directory.Root, "save-request", input, "--request", "1", "--include-sensitive", "--out", "content"));
            string templateFile = Path.Join(directory.Root, "template", "requests", "00001.json");
            string contentFile = Path.Join(directory.Root, "content", "requests", "00001.json");

            XmatRun incomplete = await XmatProcess.RunAsync(
                directory.Root, "compose", "--request-file", templateFile, "--url", server.Origin + "/read", "--out", "incomplete");
            int sentWhenIncomplete = server.Requests.Count;
            XmatRun completed = await XmatProcess.RunAsync(
                directory.Root, "compose", "--request-file", templateFile, "--url", server.Origin + "/read", "--clear-headers",
                "--out", "completed");
            XmatRun sent = await XmatProcess.RunAsync(directory.Root, "compose", "--request-file", contentFile, "--out", "sent");
            XmatRun moved = await XmatProcess.RunAsync(
                directory.Root, "compose", "--request-file", contentFile, "--url", "http://127.0.0.2:9/read", "--out", "moved");

            CliAssert.ExitCode(3, incomplete);
            Assert.Equal(new[] { "replay.request.incomplete" }, incomplete.ErrorCodes());
            Assert.Equal(0, sentWhenIncomplete);
            CliAssert.ExitCode(0, completed);
            CliAssert.ExitCode(0, sent);
            CliAssert.ExitCode(3, moved);
            Assert.Equal(new[] { "replay.policy.origin-change" }, moved.ErrorCodes());
            Assert.Equal(new[] { "/read", "/read?page=2" }, server.Requests.Select(request => request.Target));
            Assert.Null(server.Requests[0].Header("Accept"));
            Assert.Equal("application/json", server.Requests[1].Header("Accept"));
            JsonElement input2 = sent.Json().GetProperty("input");
            Assert.Equal(
                ("request-file", CliAssert.Sha256(contentFile)),
                (input2.GetProperty("kind").GetString(), input2.GetProperty("sha256").GetString()));
            CliAssert.NoStagingFolders(directory.Root);
        }
    }
}
