// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations;
using XMAT.Operations.Capabilities;

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

        // With several problems, the first in the parser's order is reported: INPUT, --out and --device, then analyze's
        // --fail-on or export's format and --host-mode, the limits, the process selectors and export's other selectors.
        // The missing INPUT and --out messages keep each command's wording.
        [Theory]
        [InlineData("analyze --device 1 --fail-on x --max-records 0", "missing-argument", "analyze needs the INPUT")]
        [InlineData("analyze a b --device 1", "unexpected-argument", "analyze takes a single INPUT")]
        [InlineData("analyze a --device 1 --fail-on x", "missing-option", "analyze needs --out DIR")]
        [InlineData("analyze a --out o --device 1 --fail-on x", "invalid-value", "--device")]
        [InlineData("analyze a --out o --fail-on x --max-records 0", "invalid-value", "--fail-on")]
        [InlineData("analyze a --out o --max-records 0 --process a/b", "invalid-value", "--max-records")]
        [InlineData("export --device 1 --host-mode x", "missing-argument", "export takes a single INPUT")]
        [InlineData("export a b --device 1", "unexpected-argument", "export takes a single INPUT")]
        [InlineData("export a --device 1 --host-mode x", "missing-option", "export needs --out FILE")]
        [InlineData("export a --out o.txt --device 1", "invalid-value", "--device")]
        [InlineData("export a --out o.txt --host-mode x", "invalid-value", "--out must")]
        [InlineData("export a --out o.saz --host-mode x --max-records 0", "invalid-value", "--host-mode")]
        [InlineData("export a --out o.saz --max-records 0 --process a/b", "invalid-value", "--max-records")]
        [InlineData("export a --out o.saz --process a/b --host=", "invalid-value", "--process")]
        public void Parse_ReportsTheFirstProblemInValidationOrder(string commandLine, string code, string message)
        {
            OperationDiagnostic error = Assert.IsType<OperationDiagnostic>(CliCommandLine.Parse(Split(commandLine)).Error);

            Assert.Equal("cli.usage." + code, error.Code);
            Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
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
        [InlineData("export --help --device 1", "export")]
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
                Assert.All(capability.CliRoutes.Select(CliCatalog.CommandOf), command => Assert.True(
                    CliCatalog.IsImplemented(command),
                    $"{capability.Id} is Supported, but xmat has no {command} command. Add it under XMAT/Cli."));
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
                new[] { CliCommands.Analyze, CliCommands.Doctor, CliCommands.Export },
                report.Commands.Select(command => command.Name));
            foreach (string route in CapabilityRegistry.All
                .Where(item => item.Status == CapabilityStatus.Supported)
                .SelectMany(item => item.CliRoutes))
            {
                Assert.Contains("  xmat " + route, report.HumanLines);
            }
        }

        // export selects sessions by process as analyze does, so both list capture.process-filter, each with only its
        // own command lines: export's help gains an export command line, and analyze's help is unchanged.
        [Fact]
        public void Help_ListsProcessFilteringUnderEachCommandWithItsOwnCommandLines()
        {
            CliCommandHelp export = CliHelp.Describe(CliCommands.Export);
            CliCommandHelp analyze = CliHelp.Describe(CliCommands.Analyze);

            Assert.Contains("capture.process-filter", export.Capabilities);
            Assert.Contains(
                "export INPUT --out FILE [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...", export.Usage);
            Assert.All(export.Usage, usage => Assert.Equal(CliCommands.Export, CliCatalog.CommandOf(usage)));
            Assert.Equal(
                new[] { "analysis.xbox-live", "capture-file.import-saz", "capture-file.read-xmatcap", "capture.process-filter" },
                analyze.Capabilities);
            Assert.Equal(
                CliCatalog.CapabilitiesOf(CliCommands.Analyze).Select(item => item.CliRoute), analyze.Usage);
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
