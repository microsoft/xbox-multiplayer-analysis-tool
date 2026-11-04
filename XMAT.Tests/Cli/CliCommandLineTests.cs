// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations;
using XMAT.Operations.Capabilities;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Cli
{
    // The parser, the registry-driven command catalog, help and the JSON documents, in process. analyze runs in its own
    // process (XmatAnalyzeTests): it must configure HeadlessStorage before anything uses PublicUtilities, which this test
    // host already does.
    public class CliCommandLineTests
    {
        [Theory]
        [InlineData("", "cli.usage.no-command")]
        [InlineData("frobnicate", "cli.usage.unknown-command")]
        [InlineData("--bogus", "cli.usage.unknown-option")]
        [InlineData("--version extra", "cli.usage.unexpected-argument")]
        [InlineData("help frobnicate", "cli.usage.unknown-command")]
        [InlineData("analyze", "cli.usage.missing-argument")]
        [InlineData("analyze a.saz b.saz --out results", "cli.usage.unexpected-argument")]
        [InlineData("analyze a.saz", "cli.usage.missing-option")]
        [InlineData("analyze a.saz --out", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out=", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out --device d1", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out results --out other", "cli.usage.duplicate-option")]
        [InlineData("analyze a.saz --out results -o", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on warnings", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device 1", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device d0", "cli.usage.invalid-value")]
        [InlineData("doctor", "cli.usage.missing-option")]
        [InlineData("doctor analyze", "cli.usage.unexpected-argument")]
        [InlineData("doctor --for frobnicate", "cli.usage.unknown-target")]
        [InlineData("--proxy-guard 42", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --telemetry-sample", "cli.usage.unknown-option")]
        // Commands, options, values and doctor targets planned for later releases are usage errors until they ship.
        [InlineData("convert a.xmatcap --to saz --out results", "cli.usage.unknown-command")]
        [InlineData("help capture", "cli.usage.unknown-command")]
        [InlineData("analyze a.saz --out results --ruleset lta", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on warning", "cli.usage.invalid-value")]
        [InlineData("doctor --for capture-xbox", "cli.usage.unknown-target")]
        [InlineData("replay", "cli.usage.missing-argument")]
        [InlineData("replay a.saz b.saz --request 1 --out results", "cli.usage.unexpected-argument")]
        [InlineData("replay a.saz --out results", "cli.usage.missing-option")]
        [InlineData("replay a.saz --request 1", "cli.usage.missing-option")]
        [InlineData("replay a.saz --request 0 --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1-10001 --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --request 2 --out results", "cli.usage.duplicate-option")]
        [InlineData("replay a.saz --request 1 --out results --device 1", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --repeat 0", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --repeat 1001", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --parallel 17", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --timeout 601", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --max-response-mib 513", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --follow-redirects 00", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header X-Name:caf\u00e9", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --follow-redirects 11", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --max-records 0", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url http://a.test/ --origin http://b.test", "cli.usage.conflicting-options")]
        [InlineData("replay a.saz --request 1 --out results --url ftp://a.test/", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url http://user@a.test/", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url /relative", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --origin http://a.test/path", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --method CONNECT", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --method GE(T", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header NoSeparator", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Host:a.test", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header content-length:1", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Transfer-Encoding:chunked", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Bad(Name):x", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --remove-header Bad:Name", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --verify=yes", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --verify --verify", "cli.usage.duplicate-option")]
        [InlineData("replay a.saz --request 1 --out results --fail-on findings", "cli.usage.unknown-option")]
        [InlineData("compose --out results", "cli.usage.missing-option")]
        [InlineData("compose --url http://a.test/", "cli.usage.missing-option")]
        [InlineData("compose extra --url http://a.test/ --out results", "cli.usage.unexpected-argument")]
        [InlineData("compose --url http://a.test/ --out results --verify", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --unconditional", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --request 1", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --remove-header X", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --origin http://b.test", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --allow-origin-change", "cli.usage.unknown-option")]
        [InlineData("replay a.saz --request 1 --out results --parallel 3 --max-response-mib 512", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/ --out results --parallel 16 --max-response-mib 65", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/item/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a#.test/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test:#/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 3-1", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 1-10001", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 1234567890123456-1234567890123457", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --sequence 1-2", "cli.usage.unknown-option")]
        public void Parse_RejectsInvalidCommandLinesWithExitCode2(string commandLine, string code)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));
            CliReport report = CliReport.ForParseError("run", result);

            Assert.False(result.Succeeded);
            Assert.Null(result.Request);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(OperationDiagnosticSeverity.Error, result.Error.Severity);
            Assert.Equal(CliExitCodes.Usage, report.ExitCode);
            Assert.Equal("failed", report.Status);
        }

        [Fact]
        public void Parse_ReadsAnalyzeOptionsInEitherForm()
        {
            CliParseResult spaced = CliCommandLine.Parse(
                new[] { "analyze", "a.saz", "--out", "results", "--device", "d2", "--fail-on", "findings" });
            CliParseResult joined = CliCommandLine.Parse(
                new[] { "analyze", "--out=results", "--device=d2", "--fail-on=findings", "--", "-a.saz" });

            Assert.Equal(
                new CliRequest(
                    CliCommands.Analyze, Input: "a.saz", Output: "results", DeviceId: "d2", FailOnFindings: true,
                    Limits: CliLimits.Default),
                spaced.Request);
            Assert.Equal(
                new CliRequest(
                    CliCommands.Analyze, Input: "-a.saz", Output: "results", DeviceId: "d2", FailOnFindings: true,
                    Limits: CliLimits.Default),
                joined.Request);
        }

        [Theory]
        [InlineData("help", null)]
        [InlineData("--help", null)]
        [InlineData("-h", null)]
        [InlineData("/?", null)]
        [InlineData("help analyze", "analyze")]
        [InlineData("--help doctor", "doctor")]
        [InlineData("analyze --help", "analyze")]
        [InlineData("doctor -?", "doctor")]
        [InlineData("help replay", "replay")]
        [InlineData("compose --help", "compose")]
        public void Parse_RecognizesHelp(string commandLine, string topic)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));

            Assert.True(result.Succeeded);
            Assert.Equal(new CliRequest(CliCommands.Help, Topic: topic), result.Request);
        }

        [Fact]
        public void Parse_RecognizesVersionAndDoctor()
        {
            Assert.Equal(new CliRequest(CliCommands.Version), CliCommandLine.Parse(new[] { "--version" }).Request);
            Assert.Equal(
                new CliRequest(CliCommands.Doctor, Target: "analyze"),
                CliCommandLine.Parse(new[] { "doctor", "--for", "analyze" }).Request);
        }

        [Fact]
        public void Parse_ReadsReplayOptions()
        {
            CliRequest request = CliCommandLine.Parse(new[]
            {
                "replay", "a.saz", "--request=1,5-7", "--out", "results", "--device", "d2", "--repeat", "3",
                "--parallel=4", "--timeout", "45", "--max-response-mib", "64", "--follow-redirects", "2", "--origin",
                "http://127.0.0.1:8080", "--method", "PUT", "--header", "Accept: application/json", "--header=X-Two:2",
                "--remove-header", "Cookie", "--remove-header", "X-Trace", "--body-file", "body.bin", "--unconditional",
                "--verify", "--allow-unsafe-methods", "--allow-origin-change", "--max-records", "50",
            }).Request;

            Assert.Equal(
                (CliCommands.Replay, "a.saz", "results", "d2", 50),
                (request.Command, request.Input, request.Output, request.DeviceId, request.Limits.MaxRecords));
            CliReplay replay = request.Replay;
            Assert.Equal(
                ("1,5-7", 3, 4, 45, 64, 2),
                (replay.Selection, replay.Repeat, replay.Parallel, replay.TimeoutSeconds, replay.MaxResponseMib, replay.FollowRedirects));
            Assert.Equal(
                ((string)null, "http://127.0.0.1:8080", "PUT", "body.bin"),
                (replay.Url, replay.Origin, replay.Method, replay.BodyFile));
            Assert.Equal(
                new[] { new KeyValuePair<string, string>("Accept", "application/json"), new KeyValuePair<string, string>("X-Two", "2") },
                replay.Headers);
            Assert.Equal(new[] { "Cookie", "X-Trace" }, replay.RemoveHeaders);
            Assert.True(replay.Unconditional && replay.Verify && replay.AllowUnsafeMethods && replay.AllowOriginChange);
        }

        [Fact]
        public void Parse_ReadsComposeOptionsAndTheDefaults()
        {
            CliRequest minimal = CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/x?y=1", "--out", "r" }).Request;
            CliRequest replay = CliCommandLine.Parse(new[] { "replay", "a.saz", "--request", "all", "--out", "r" }).Request;

            Assert.Equal((CliCommands.Compose, null, "r"), (minimal.Command, minimal.Input, minimal.Output));
            CliReplay compose = minimal.Replay;
            Assert.Equal(("https://a.test/x?y=1", null, null), (compose.Url, compose.Method, compose.Selection));
            Assert.Equal((1, 1, 30, 16, 0), (compose.Repeat, compose.Parallel, compose.TimeoutSeconds, compose.MaxResponseMib, compose.FollowRedirects));
            Assert.Empty(compose.Headers);
            Assert.False(compose.AllowUnsafeMethods || compose.AllowOriginChange || compose.Verify || compose.Unconditional);
            Assert.Null(minimal.Limits);
            Assert.Equal(CliLimits.Default, replay.Limits);
            Assert.Equal("all", replay.Replay.Selection);
            Assert.Equal(
                0,
                CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/", "--out", "r", "--follow-redirects", "0" })
                    .Request.Replay.FollowRedirects);
            Assert.True(CliCommandLine.Parse(
                new[] { "replay", "a.saz", "--request", "1", "--out", "r", "--parallel", "2", "--max-response-mib", "512" }).Succeeded);
            Assert.True(CliCommandLine.Parse(
                new[] { "compose", "--url", "https://a.test/", "--out", "r", "--parallel", "16", "--max-response-mib", "64" }).Succeeded);
        }

        // --sequence turns --url into a template; a '#' without it is refused with a pointer to --sequence.
        [Fact]
        public void Parse_ReadsComposeSequences_AndPointsAFragmentAtThem()
        {
            CliParseResult sequence = CliCommandLine.Parse(Split("compose --url http://a.test/item/#?page=# --sequence 08-11 --out r"));
            CliParseResult fragment = CliCommandLine.Parse(Split("compose --url http://a.test/item# --out r"));

            Assert.True(sequence.Succeeded);
            Assert.Equal(("http://a.test/item/#?page=#", "08-11"), (sequence.Request.Replay.Url, sequence.Request.Replay.Sequence));
            Assert.Null(CliCommandLine.Parse(Split("compose --url http://a.test/ --out r")).Request.Replay.Sequence);
            Assert.Equal(CliCommandLine.SequenceNeededMessage, fragment.Error.Message);
        }

        // Errors name commands and options but never repeat values, which can be paths.
        [Theory]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --bogus=C:\secret\x")]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --device C:\secret")]
        [InlineData(@"C:\secret\a.saz")]
        [InlineData(@"help C:\secret")]
        [InlineData(@"--out=C:\secret")]
        [InlineData(@"replay C:\secret\a.saz --request C:\secret --out C:\secret\results")]
        [InlineData(@"replay a.saz --request 1 --out results --url http://secret.test/#secret")]
        [InlineData(@"replay a.saz --request 1 --out results --origin http://secret.test/secret")]
        [InlineData(@"replay a.saz --request 1 --out results --remove-header secret:secret")]
        [InlineData(@"compose --url secret --out results")]
        [InlineData(@"compose --url http://secret.test/ --out results --header secret")]
        [InlineData(@"compose --url http://secret.test/ --out results --method se(cret")]
        [InlineData(@"compose --url http://secret.test/ --out results --repeat secret")]
        public void Parse_ErrorsNeverRepeatValues(string commandLine)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));

            Assert.False(result.Succeeded);
            Assert.DoesNotContain("secret", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Unknown commands, options, doctor targets and help topics are never repeated either, even when they look like
        // names: a lowercase word with digits can be a token or a password. Only catalog names are ever printed.
        [Theory]
        [InlineData("s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("--s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("--s3cretcanary9=s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("help s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("--help s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("analyze a.saz --out results --s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --s3cretcanary9=s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on s3cretcanary9", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device s3cretcanary9", "cli.usage.invalid-value")]
        [InlineData("doctor --for s3cretcanary9", "cli.usage.unknown-target")]
        [InlineData("doctor --s3cretcanary9 analyze", "cli.usage.unknown-option")]
        public void Parse_NeverRepeatsUnknownWords(string commandLine, string code)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));
            CliReport report = CliReport.ForParseError("run", result);

            Assert.Equal(CliExitCodes.Usage, report.ExitCode);
            Assert.Equal(code, result.Error.Code);
            Assert.DoesNotContain("s3cretcanary9", result.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("s3cretcanary9", Encoding.UTF8.GetString(CliJson.Serialize(report)), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EverySupportedCapability_IsACommandThatXmatRuns()
        {
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                string command = CliCatalog.CommandOf(capability.CliRoute);
                Assert.True(
                    CliCatalog.IsImplemented(command),
                    $"{capability.Id} is Supported, but xmat has no {command} command. Add it under XMAT/Cli.");
            }
        }

        [Fact]
        public void EveryCommandThatXmatRuns_HasASupportedCapability()
        {
            foreach (string command in CliCommands.Implemented)
            {
                Assert.Contains(
                    CapabilityRegistry.All,
                    item => item.Status == CapabilityStatus.Supported && CliCatalog.CommandOf(item.CliRoute) == command);
            }
        }

        // help covers only the commands that xmat runs, with every Supported command line.
        [Fact]
        public void Help_ListsOnlyTheCommandsThatXmatRuns()
        {
            CliReport report = CliHelp.Create("run", topic: null);

            Assert.Equal(
                new[] { CliCommands.Analyze, CliCommands.Compose, CliCommands.Doctor, CliCommands.Replay },
                report.Commands.Select(command => command.Name));
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                Assert.Contains("  xmat " + capability.CliRoute, report.HumanLines);
            }
        }

        // The command lines of replay and compose in the capability registry, which help and docs/cli.md show, name
        // exactly the options and switches that the parser accepts.
        [Theory]
        [InlineData("request.replay", CliCommands.Replay)]
        [InlineData("request.compose", CliCommands.Compose)]
        public void ReplayCommandLines_NameExactlyTheOptionsThatTheParserAccepts(string capabilityId, string command)
        {
            Capability capability = CapabilityRegistry.All.Single(item => item.Id == capabilityId);
            IReadOnlySet<string> accepted = command == CliCommands.Replay
                ? CliCommandLine.ReplayOptionNames
                : CliCommandLine.ComposeOptionNames;

            IEnumerable<string> named = capability.CliRoute
                .Split(new[] { ' ', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("--", StringComparison.Ordinal));

            Assert.Equal(command, CliCatalog.CommandOf(capability.CliRoute));
            Assert.Equal(
                accepted.OrderBy(name => name, StringComparer.Ordinal),
                named.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Theory]
        [InlineData(CliExitCodes.VerifyMismatch, 11, "mismatchFound")]
        [InlineData(CliExitCodes.RequestsFailed, 12, "requestsFailed")]
        public void ReplayExitCodes_AreAddedWithTheirStatus(int code, int documented, string status)
        {
            Assert.Equal(documented, code);
            Assert.Equal(status, CliExitCodes.Status(code));
            Assert.True(CliExitCodes.IsPublished(code));
        }

        // The JSON of a replay holds numbers, ids, outcomes, codes and artifact paths only, and lists the files of each
        // send only once they were published.
        [Fact]
        public void ReplayJson_HoldsOnlyAllowListedFields()
        {
            var report = new CliReport(CliCommands.Replay, "run")
            {
                Replay = new CliReplayReport(
                    new CliReplay(Selection: "1", Url: "http://secret.test/", Method: "SECRET", BodyFile: @"C:\secret"),
                    CliLimits.Default)
                {
                    InputKind = "saz",
                    InputSha256 = new string('a', 64),
                    Items = new[]
                    {
                        new ReplayItemResult(
                            1, "d1", 7, 1, ReplayOutcome.Completed, 200, TimeSpan.FromMilliseconds(12.7), 5, 0, null,
                            new ReplayVerification(new[] { ReplayVerifier.BodyDifference })),
                        new ReplayItemResult(
                            2, "d1", 7, 2, ReplayOutcome.Failed, 0, TimeSpan.Zero, 0, 0, "replay.send.failed", null),
                    },
                },
            };

            using JsonDocument unpublished = JsonDocument.Parse(CliJson.Serialize(report));
            report.Artifacts.Add(new CliArtifact("summary", "summary.json"));
            using JsonDocument published = JsonDocument.Parse(CliJson.Serialize(report));
            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeReplaySummary(report));

            Assert.DoesNotContain("secret", Encoding.UTF8.GetString(CliJson.Serialize(report)), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "input", "limits", "options", "replays", "totals", "warnings" },
                summary.RootElement.EnumerateObject().Select(property => property.Name));
            JsonElement[] replays = published.RootElement.GetProperty("replays").EnumerateArray().ToArray();
            Assert.Equal(
                new[] { "id", "device", "request", "sequence", "iteration", "outcome", "status", "durationMs", "responseBytes", "redirects", "code", "verify", "files" },
                replays[0].EnumerateObject().Select(property => property.Name));
            Assert.Equal(("completed", 200, 12L), (replays[0].GetProperty("outcome").GetString(), replays[0].GetProperty("status").GetInt32(), replays[0].GetProperty("durationMs").GetInt64()));
            Assert.Equal("mismatch", replays[0].GetProperty("verify").GetProperty("result").GetString());
            Assert.Equal("replays/00001/response.http", replays[0].GetProperty("files").GetProperty("response").GetString());
            Assert.Equal(JsonValueKind.Null, replays[1].GetProperty("status").ValueKind);
            Assert.Equal(JsonValueKind.Null, replays[1].GetProperty("files").GetProperty("response").ValueKind);
            Assert.Equal("replays/00002/request.http", replays[1].GetProperty("files").GetProperty("request").GetString());
            Assert.False(unpublished.RootElement.GetProperty("replays")[0].TryGetProperty("files", out _));
            JsonElement totals = published.RootElement.GetProperty("totals");
            Assert.Equal((2, 1, 1, 0, 1), (totals.GetProperty("replays").GetInt32(), totals.GetProperty("completed").GetInt32(), totals.GetProperty("failed").GetInt32(), totals.GetProperty("matched").GetInt32(), totals.GetProperty("mismatched").GetInt32()));
        }

        [Theory]
        [InlineData(nameof(OperationOutcome.Succeeded), 0)]
        [InlineData(nameof(OperationOutcome.InvalidInput), 3)]
        [InlineData(nameof(OperationOutcome.NoAnalyzableData), 3)]
        [InlineData(nameof(OperationOutcome.PrerequisiteMissing), 4)]
        [InlineData(nameof(OperationOutcome.Conflict), 5)]
        [InlineData(nameof(OperationOutcome.Failed), 6)]
        [InlineData(nameof(OperationOutcome.CleanupFailed), 7)]
        [InlineData(nameof(OperationOutcome.InternalError), 70)]
        [InlineData(nameof(OperationOutcome.Cancelled), 130)]
        public void ExitCodes_FollowTheDocumentedTable(string outcome, int exitCode) =>
            Assert.Equal(exitCode, CliExitCodes.FromOutcome(Enum.Parse<OperationOutcome>(outcome)));

        // summary.json carries the run's warnings (the same data-free codes and messages as standard output) and none of
        // the run's outcome fields.
        [Fact]
        public void Summary_HoldsTheAnalysisAndItsWarnings()
        {
            var report = new CliReport(CliCommands.Analyze, "run") { Analysis = new CliAnalysisReport() };
            report.Warnings.Add(new OperationDiagnostic(
                "analysis.device.skipped", OperationDiagnosticSeverity.Warning, "A non-web-proxy device was skipped."));

            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeSummary(report));

            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "input", "devices", "totals", "warnings" },
                summary.RootElement.EnumerateObject().Select(property => property.Name));
            JsonElement warning = Assert.Single(summary.RootElement.GetProperty("warnings").EnumerateArray());
            Assert.Equal("analysis.device.skipped", warning.GetProperty("code").GetString());
            Assert.Equal("A non-web-proxy device was skipped.", warning.GetProperty("message").GetString());
        }

        [Fact]
        public void HeadlessStorage_CannotBeSetOnceFrozen()
        {
            // PublicUtilities' static constructor freezes it; freezing here never touches the GUI's log or settings.
            Assert.Null(HeadlessStorage.Freeze());

            Assert.Throws<InvalidOperationException>(() => HeadlessStorage.Configure(
                Path.Combine(AppContext.BaseDirectory, "HeadlessTestArtifacts", "Cli", "never-used")));
            Assert.Null(HeadlessStorage.Root);
        }

        private static string[] Split(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
