// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.CaptureFiles;
using XMAT.Operations.Capabilities;

namespace XMAT.Tests.Cli
{
    // The parser, the registry-driven command catalog, help and the JSON documents, in process. Commands that configure
    // HeadlessStorage run in child-process tests because this test host already used PublicUtilities.
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
        [InlineData("mark-capture-text", "cli.usage.missing-argument")]
        [InlineData("mark-capture-text a.xmatcap b.xmatcap --out results --interpret as-stored", "cli.usage.unexpected-argument")]
        [InlineData("mark-capture-text a.xmatcap --interpret as-stored", "cli.usage.missing-option")]
        [InlineData("mark-capture-text a.xmatcap --out results", "cli.usage.missing-option")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret repair", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --max-input-mib 0", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --max-input-mib 2049", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("--proxy-guard 42", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --telemetry-sample", "cli.usage.unknown-option")]
        // Commands, options, values and doctor targets planned for later releases are usage errors until they ship.
        [InlineData("convert a.xmatcap --to saz --out results", "cli.usage.unknown-command")]
        [InlineData("help capture", "cli.usage.unknown-command")]
        [InlineData("analyze a.saz --out results --ruleset lta", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on warning", "cli.usage.invalid-value")]
        [InlineData("doctor --for capture-xbox", "cli.usage.unknown-target")]
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
        [InlineData("as-stored", (int)CaptureTextInterpretation.AsStored)]
        [InlineData("legacy-one-layer", (int)CaptureTextInterpretation.LegacyOneLayer)]
        public void Parse_ReadsMarkCaptureTextOptions(
            string value,
            int expected)
        {
            CliParseResult result =
                CliCommandLine.Parse(
                    new[]
                    {
                        "mark-capture-text",
                        "capture.xmatcap",
                        "--out",
                        "results",
                        "--interpret",
                        value,
                        "--max-input-mib",
                        "2048",
                    });

            Assert.Equal(
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: "capture.xmatcap",
                    Output: "results",
                    Interpretation:
                        (CaptureTextInterpretation)expected,
                    MaxInputMib: 2048),
                result.Request);
        }

        [Fact]
        public void MarkCaptureTextParseErrorHasNoCommandReportObject()
        {
            CliParseResult parsed =
                CliCommandLine.Parse(
                    new[]
                    {
                        "mark-capture-text",
                        "capture.xmatcap",
                    });
            CliReport report =
                CliReport.ForParseError(
                    "run",
                    parsed);
            using JsonDocument document =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));

            Assert.False(
                document.RootElement.TryGetProperty(
                    "markCaptureText",
                    out _));
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
        [InlineData("help mark-capture-text", "mark-capture-text")]
        [InlineData("mark-capture-text --help", "mark-capture-text")]
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

        // Errors name commands and options but never repeat values, which can be paths.
        [Theory]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --bogus=C:\secret\x")]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --device C:\secret")]
        [InlineData(@"C:\secret\a.saz")]
        [InlineData(@"help C:\secret")]
        [InlineData(@"--out=C:\secret")]
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
                new[]
                {
                    CliCommands.Analyze,
                    CliCommands.Doctor,
                    CliCommands.MarkCaptureText,
                },
                report.Commands.Select(
                    command => command.Name));
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                Assert.Contains("  xmat " + capability.CliRoute, report.HumanLines);
            }
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
        public void MarkSummaryAndStandardOutputHaveTheFrozenSchemas()
        {
            var report =
                new CliReport(
                    CliCommands.MarkCaptureText,
                    "0123456789abcdef0123456789abcdef")
                {
                    MarkCaptureText =
                        new CliMarkCaptureTextReport
                        {
                            Interpretation =
                                CaptureTextInterpretation.LegacyOneLayer,
                            MaxInputMib = 512,
                            InputKind = "xmatcap",
                            InputSha256 = new string('a', 64),
                            OutputSha256 = new string('b', 64),
                            Counts =
                                new MarkCaptureTextCounts(
                                    2,
                                    3,
                                    4,
                                    5),
                        },
                };
            report.SetExitCode(
                CliExitCodes.Success);
            report.Artifacts.Add(
                new CliArtifact(
                    "capture",
                    "capture.xmatcap"));
            report.Artifacts.Add(
                new CliArtifact(
                    "summary",
                    "summary.json"));

            using JsonDocument output =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            using JsonDocument summary =
                JsonDocument.Parse(
                    CliJson.SerializeMarkCaptureTextSummary(
                        report));

            JsonElement mark =
                output.RootElement.GetProperty(
                    "markCaptureText");
            Assert.Equal(
                "legacyOneLayer",
                mark.GetProperty(
                    "interpretation")
                    .GetString());
            Assert.Equal(
                5,
                mark.GetProperty(
                    "counts")
                    .GetProperty(
                        "valuesChanged")
                    .GetInt64());
            Assert.Equal(
                new[]
                {
                    "input",
                    "limits",
                    "interpretation",
                    "source",
                    "output",
                    "counts",
                },
                mark.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("input")
                    .GetProperty("kind")
                    .ValueKind);
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("input")
                    .GetProperty("sha256")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "kind",
                    "sha256",
                },
                mark.GetProperty("input")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                "xmatcap",
                mark.GetProperty("input")
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(
                new string('a', 64),
                mark.GetProperty("input")
                    .GetProperty("sha256")
                    .GetString());
            Assert.Equal(
                JsonValueKind.Number,
                mark.GetProperty("limits")
                    .GetProperty("maxInputMib")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "maxInputMib",
                },
                mark.GetProperty("limits")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                512,
                mark.GetProperty("limits")
                    .GetProperty("maxInputMib")
                    .GetInt32());
            Assert.Equal(
                JsonValueKind.Number,
                mark.GetProperty("source")
                    .GetProperty("userVersion")
                    .ValueKind);
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("source")
                    .GetProperty("semantics")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "userVersion",
                    "semantics",
                },
                mark.GetProperty("source")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                CaptureTextFormat.AmbiguousUserVersion,
                mark.GetProperty("source")
                    .GetProperty("userVersion")
                    .GetInt32());
            Assert.Equal(
                "ambiguousUnmarked",
                mark.GetProperty("source")
                    .GetProperty("semantics")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "file",
                    "sha256",
                    "userVersion",
                },
                mark.GetProperty("output")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                "capture.xmatcap",
                mark.GetProperty("output")
                    .GetProperty("file")
                    .GetString());
            Assert.Equal(
                new string('b', 64),
                mark.GetProperty("output")
                    .GetProperty("sha256")
                    .GetString());
            Assert.Equal(
                CaptureTextFormat.LiteralUserVersion,
                mark.GetProperty("output")
                    .GetProperty("userVersion")
                    .GetInt32());
            Assert.Equal(
                new[]
                {
                    "tables",
                    "rows",
                    "valuesExamined",
                    "valuesChanged",
                },
                mark.GetProperty("counts")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.All(
                mark.GetProperty("counts")
                    .EnumerateObject(),
                property =>
                    Assert.Equal(
                        JsonValueKind.Number,
                        property.Value.ValueKind));
            Assert.Equal(
                new long[]
                {
                    2,
                    3,
                    4,
                    5,
                },
                mark.GetProperty("counts")
                    .EnumerateObject()
                    .Select(property => property.Value.GetInt64()));
            Assert.Equal(
                "xmat-mark-capture-text/1",
                summary.RootElement.GetProperty(
                    "schema")
                    .GetString());
            Assert.Equal(
                CliReport.ToolVersion,
                summary.RootElement.GetProperty(
                    "version")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "schema",
                    "command",
                    "runId",
                    "version",
                    "input",
                    "limits",
                    "interpretation",
                    "source",
                    "output",
                    "counts",
                    "warnings",
                },
                summary.RootElement.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
            foreach (string property in new[]
            {
                "input",
                "limits",
                "interpretation",
                "source",
                "output",
                "counts",
            })
            {
                Assert.True(
                    JsonElement.DeepEquals(
                        mark.GetProperty(
                            property),
                        summary.RootElement.GetProperty(
                            property)),
                    property);
            }
        }

        [Fact]
        public void FailedMarkOutput_NeverPublishesSourceOutputOrCounts()
        {
            var report =
                new CliReport(
                    CliCommands.MarkCaptureText,
                    "run")
                {
                    MarkCaptureText =
                        new CliMarkCaptureTextReport
                        {
                            Interpretation =
                                CaptureTextInterpretation.AsStored,
                            MaxInputMib = 512,
                            InputKind = "xmatcap",
                            InputSha256 = new string('a', 64),
                            OutputSha256 = new string('b', 64),
                            Counts =
                                new MarkCaptureTextCounts(
                                    1,
                                    2,
                                    3,
                                    4),
                        },
                };
            report.Fail(
                CliExitCodes.Conflict,
                "cli.output.exists",
                "Nothing was published.");

            using JsonDocument output =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            JsonElement mark =
                output.RootElement.GetProperty(
                    "markCaptureText");

            Assert.False(
                mark.TryGetProperty(
                    "source",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "output",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "counts",
                    out _));
            Assert.Equal(
                "xmatcap",
                mark.GetProperty(
                        "input")
                    .GetProperty(
                        "kind")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "input",
                    "limits",
                    "interpretation",
                },
                mark.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
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
