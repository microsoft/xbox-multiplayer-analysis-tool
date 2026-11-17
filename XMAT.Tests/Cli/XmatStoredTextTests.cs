// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Replay;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    // --accept-stored-text (Q's parity gates): xmat replay, save-request and curl, run as child processes against loopback
    // servers in the test process. An unmarked XMAT capture is used only as stored and only with the flag, for that run's
    // INPUT; without it the run is refused before anything is sent or published. Every other input takes the flag as a
    // no-op, and the JSON reports the flag and, once published, how the stored text was used. Nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatStoredTextTests
    {
        private const string StoredHeaders = "If-None-Match: \"v1\"\r\nX-Adjacent: \"\"\r\n";

        // replay-url edits the selected request's URL, which leaves its stored text in it, so it needs the flag too.
        [Theory]
        [InlineData("replay", "replay.capture-text.ambiguous")]
        [InlineData("replay-url", "replay.capture-text.ambiguous")]
        [InlineData("save-request", "capture.text.ambiguous-export")]
        [InlineData("curl", "capture.text.ambiguous-export")]
        public async Task UnmarkedInput_WithoutTheFlag_IsRefusedAndPublishesNothing(string form, string code)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = Unmarked(directory.Root, server.Origin);
            string sha256 = CliAssert.Sha256(input);

            XmatRun run = await XmatProcess.RunAsync(directory.Root, Arguments(form, input, server.Origin, accept: false));

            CliAssert.ExitCode(3, run);
            JsonElement json = run.Json();
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Equal(Message(code), json.GetProperty("errors")[0].GetProperty("message").GetString());
            Assert.Empty(json.GetProperty("warnings").EnumerateArray());
            Assert.False(json.GetProperty("options").GetProperty("acceptStoredText").GetBoolean());
            Assert.False(json.TryGetProperty("storedText", out _));
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
            CliAssert.NoLeaks(run, directory);
            Assert.Equal(0, server.Connections);
            Assert.Equal(sha256, CliAssert.Sha256(input));
        }

        [Theory]
        [InlineData("replay")]
        [InlineData("replay-url")]
        [InlineData("save-request")]
        [InlineData("curl")]
        public async Task UnmarkedInput_WithTheFlag_UsesItsTextAsStored_AndSaysSo(string form)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = Unmarked(directory.Root, server.Origin);

            XmatRun run = await XmatProcess.RunAsync(directory.Root, Arguments(form, input, server.Origin, accept: true));

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.True(json.GetProperty("options").GetProperty("acceptStoredText").GetBoolean());
            Assert.Equal(
                new[] { "capture.text.ambiguous" },
                json.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetProperty("code").GetString()));
            Assert.Equal(CliAssert.Sha256(input), json.GetProperty("input").GetProperty("sha256").GetString());
            AssertStoredText(json, "ambiguousUnmarked", "acceptedAsStored");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(directory.Root, "results", "summary.json"))))
            {
                AssertStoredText(summary.RootElement, "ambiguousUnmarked", "acceptedAsStored");
            }

            if (form.StartsWith("replay", StringComparison.Ordinal))
            {
                LoopbackRequest request = Assert.Single(server.Requests);
                Assert.Equal(form == "replay" ? "/one" : "/edited", request.Target);
                Assert.Equal(("\"v1\"", "\"\""), (request.Header("If-None-Match"), request.Header("X-Adjacent")));
            }
            else
            {
                Assert.Equal(0, server.Connections);
            }

            CliAssert.NoLeaks(run, directory);
        }

        [Theory]
        [InlineData("replay", "marked", "literal")]
        [InlineData("replay", "saz", "notApplicable")]
        [InlineData("save-request", "marked", "literal")]
        [InlineData("save-request", "saz", "notApplicable")]
        [InlineData("save-request", "request-file", "notApplicable")]
        [InlineData("save-request", "url", "notApplicable")]
        [InlineData("curl", "marked", "literal")]
        [InlineData("curl", "saz", "notApplicable")]
        [InlineData("curl", "request-file", "notApplicable")]
        [InlineData("curl", "url", "notApplicable")]
        public async Task EveryOtherInput_TakesTheFlagAsANoOp(string command, string input, string semantics)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var exchange = new CapturedExchange("GET", server.Origin + "/one", StoredHeaders);
            var args = new List<string> { command };
            switch (input)
            {
                case "marked":
                    args.AddRange(new[] { ReplayCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ("Device 1", new[] { exchange })), "--request", "1" });
                    break;
                case "saz":
                    args.AddRange(new[] { ReplayCaptures.WriteSaz(directory.Root, "capture.saz", exchange), "--request", "1" });
                    break;
                case "request-file":
                    args.AddRange(new[] { "--request-file", RequestFile(directory.Root, server.Origin + "/one") });
                    break;
                default:
                    args.AddRange(new[] { "--url", server.Origin + "/one" });
                    break;
            }

            if (command == "curl" || (command == "save-request" && input != "url"))
            {
                args.Add("--include-sensitive");
            }

            args.AddRange(new[] { "--accept-stored-text", "--out", "results" });

            XmatRun run = await XmatProcess.RunAsync(directory.Root, args.ToArray());

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.True(json.GetProperty("options").GetProperty("acceptStoredText").GetBoolean());
            AssertStoredText(json, semantics, "notNeeded");
            Assert.DoesNotContain(
                "capture.text.ambiguous",
                json.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetProperty("code").GetString()));
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(directory.Root, "results", "summary.json"))))
            {
                AssertStoredText(summary.RootElement, semantics, "notNeeded");
            }

            Assert.Equal(command == "replay" ? 1 : 0, server.Connections);
        }

        // A refusal after the requests were prepared, here an origin change without its consent, publishes nothing and
        // reports the flag but no storedText, which only a published run has.
        [Theory]
        [InlineData("save-request")]
        [InlineData("curl")]
        public async Task ARefusalAfterPreparation_ReportsTheFlagButNoStoredText(string command)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = Unmarked(directory.Root, server.Origin);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                command, input, "--request", "1", "--url", "http://127.0.0.1:9/elsewhere", "--include-sensitive",
                "--accept-stored-text", "--out", "results");

            CliAssert.ExitCode(3, run);
            JsonElement json = run.Json();
            Assert.Equal(new[] { "replay.policy.origin-change" }, run.ErrorCodes());
            Assert.True(json.GetProperty("options").GetProperty("acceptStoredText").GetBoolean());
            Assert.False(json.TryGetProperty("storedText", out _));
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        private static string[] Arguments(string form, string input, string origin, bool accept)
        {
            var args = form switch
            {
                "replay" => new List<string> { "replay", input, "--request", "1" },
                "replay-url" => new List<string> { "replay", input, "--request", "1", "--url", origin + "/edited" },
                "save-request" => new List<string> { "save-request", input, "--request", "1", "--include-sensitive" },
                _ => new List<string> { "curl", input, "--request", "1", "--include-sensitive" },
            };
            if (accept)
            {
                args.Add("--accept-stored-text");
            }

            args.AddRange(new[] { "--out", "results" });
            return args.ToArray();
        }

        // An XMAT capture without the text-format marker, whose one request targets the loopback server and holds quotes.
        private static string Unmarked(string directory, string origin)
        {
            string path = ReplayCaptures.WriteXmatcap(
                directory, "unmarked.xmatcap", ("Device 1", new[] { new CapturedExchange("GET", origin + "/one", StoredHeaders) }));
            HostileSqliteSchemas.SetUserVersion(path, CaptureTextFormat.AmbiguousUserVersion);
            return path;
        }

        private static string RequestFile(string directory, string url)
        {
            string path = Path.Join(directory, "request.json");
            var draft = new RequestReplayDraft { Url = url, HeadersText = StoredHeaders.TrimEnd('\r', '\n') };
            using FileStream stream = File.Create(path);
            RequestFileWriter.Write(draft, ReplayHttpVersion.Auto, RequestFileProfile.Content, stream, CancellationToken.None);
            return path;
        }

        private static void AssertStoredText(JsonElement root, string semantics, string policy)
        {
            JsonElement storedText = root.GetProperty("storedText");
            Assert.Equal(
                (semantics, policy),
                (storedText.GetProperty("semantics").GetString(), storedText.GetProperty("policy").GetString()));
        }

        private static string Message(string code) =>
            code == "replay.capture-text.ambiguous"
                ? "This unmarked XMAT capture requires --accept-stored-text before replay, or xmat mark-capture-text to " +
                    "create a marked copy."
                : "This unmarked XMAT capture requires --accept-stored-text to export its stored text unchanged, or xmat " +
                    "mark-capture-text to create a marked copy.";
    }
}
