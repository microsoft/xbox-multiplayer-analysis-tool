// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests.Cli
{
    // xmat export's command line and JSON, in process. The format follows --out's extension unless a sanitized archive is
    // asked for. An empty literal selects the empty value where the GUI can show one, so [""], --match-none ([]) and an
    // option that was not given (null) differ. The allow-listed export block counts host, status and method values
    // instead of writing them, and reports the publication itself: artifacts stays empty.
    public class CliExportCommandLineTests
    {
        private static readonly string[] CountNames =
        {
            "total", "matched", "selected", "eligible", "exported", "unavailable", "skippedIncomplete", "noRecordedResponse",
        };

        [Fact]
        public void Parse_ReadsEverySelectorAndTakesTheFormatFromTheExtension()
        {
            CliRequest request = Request(
                "--out", "subset.saz", "--device", "d2", "--process", "game*.exe", "--exclude-process", "4200",
                "--host", "*.xboxlive.com", "--exclude-host", "telemetry.xboxlive.com", "--status", "200", "--status=",
                "--method", "POST", "--max-records", "10");

            ProcessFilterSpec process = Spec(new[] { "game*.exe" }, new[] { "4200" });
            CaptureSelectionSpec selection = request.Selection;
            Assert.Equal(
                ("capture.xmatcap", "subset.saz", "d2", 10, (CaptureExportFormat?)CaptureExportFormat.Saz),
                (request.Input, request.Output, request.DeviceId, request.Limits.MaxRecords, request.Format));
            Assert.Equal(Items(process.Include), Items(selection.Process.Include));
            Assert.Equal(Items(process.Exclude), Items(selection.Process.Exclude));
            Assert.Equal(
                (HostMatchMode.Glob, (int?)1, 1, false),
                (selection.Host.Mode, selection.Host.Include?.Length, selection.Host.Exclude.Length, selection.MatchNone));
            Assert.Equal(new[] { string.Empty, "200" }, Sorted(selection.Statuses));
            Assert.Equal(new[] { "POST" }, Sorted(selection.Methods));
            Assert.Null(selection.Keys);
        }

        [Fact]
        public void Parse_KeepsEmptyLiteralsDistinctFromMatchNoneAndFromNoFilter()
        {
            CaptureSelectionSpec given = Request(
                "--out", "a.saz", "--method", string.Empty, "--host-mode", "exact", "--host=", "--exclude-host=").Selection;
            CaptureSelectionSpec none = Request("--out", "a.saz", "--match-none").Selection;
            CaptureSelectionSpec absent = Request("--out", "a.saz").Selection;

            Assert.Equal(HostMatchMode.Exact, given.Host.Mode);
            Assert.Equal(new[] { string.Empty }, Sorted(given.Host.Include));
            Assert.Equal(new[] { string.Empty }, Sorted(given.Host.Exclude));
            Assert.Equal(new[] { string.Empty }, Sorted(given.Methods));
            Assert.True(none.MatchNone);
            Assert.Equal(
                (false, (string[])null, (string[])null, (string[])null),
                (absent.MatchNone, Sorted(absent.Host.Include), Sorted(absent.Statuses), Sorted(absent.Methods)));
        }

        [Theory]
        [InlineData("subset.xmatcap", null, "Xmatcap")]
        [InlineData("subset.SAZ", null, "Saz")]
        [InlineData("subset.saz", "saz", "Saz")]
        [InlineData("subset.saz", "sanitized-saz", "SanitizedSaz")]
        public void Parse_InfersOrdinaryFormatsButNeverASanitizedOne(string output, string format, string expected)
        {
            string[] options = format == null ? new[] { "--out", output } : new[] { "--out", output, "--format", format };

            Assert.Equal(Enum.Parse<CaptureExportFormat>(expected), Request(options).Format);
        }

        // Usage errors never repeat what was typed.
        [Theory]
        [InlineData("subset.saz|--format|xmatcap", "cli.usage.invalid-value")]
        [InlineData("subset.xmatcap|--format|sanitized-saz", "cli.usage.invalid-value")]
        [InlineData("subset.saz|--format|pfcanary", "cli.usage.invalid-value")]
        [InlineData("subset.txt", "cli.usage.invalid-value")]
        [InlineData("subset.saz|--host=", "cli.usage.invalid-value")]
        [InlineData("subset.saz|--match-none=yes", "cli.usage.invalid-value")]
        [InlineData("subset.saz|--process=", "cli.usage.missing-value")]
        [InlineData("subset.saz|--status", "cli.usage.missing-value")]
        [InlineData("subset.saz|--method|--status|200", "cli.usage.missing-value")]
        public void Parse_RejectsContradictoryFormatsAndMissingOrUnsupportedEmptyValues(string tail, string code)
        {
            CliParseResult parsed = CliCommandLine.Parse(
                new[] { "export", "capture.saz", "--out" }.Concat(tail.Split('|')).ToArray());
            string json = Encoding.UTF8.GetString(CliJson.Serialize(CliReport.ForParseError("run", parsed)));

            Assert.Equal(code, parsed.Error?.Code);
            Assert.DoesNotContain("pfcanary", parsed.Error.Message + json, StringComparison.Ordinal);
        }

        // xmat export never publishes where it cannot restrict access to the file: exit 6, export.output.unprotected, and
        // nothing written next to the input.
        [Fact]
        public async Task Export_RefusesADestinationWhereAccessCannotBeRestricted()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            CliParseResult parsed = CliCommandLine.Parse(
                new[] { "export", input, "--out", Path.Join(directory.Root, "subset.saz") });
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = new CaptureExportPublisher { StagingIsPrivate = _ => false },
            };

            CliReport report = await CliExportCommand.RunAsync(parsed.Request, "unprotected", CancellationToken.None, operation);

            Assert.Equal(CliExitCodes.Failed, report.ExitCode);
            Assert.Contains(report.Errors, error => error.Code == CaptureExportErrorCodes.OutputUnprotected);
            Assert.False(report.Export.Result.Published);
            Assert.Equal(new[] { input }, Directory.GetFileSystemEntries(directory.Root));
        }

        // xmat's own dispatch runs export without configuring HeadlessStorage, which export does not use. This host has
        // already fixed it, as every process does once PublicUtilities is in use, so configuring it would fail the run.
        [Fact]
        public async Task Dispatch_ExportsWithoutConfiguringHeadlessStorage()
        {
            Assert.Null(HeadlessStorage.Freeze());
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            string output = Path.Join(directory.Root, "subset.saz");
            using var stdout = new MemoryStream();
            using var stderr = new StringWriter();

            int exitCode = await CliApplication.RunAsync(
                new[] { "export", input, "--out", output }, new CliConsole(stdout, stderr));

            Assert.True(exitCode == CliExitCodes.Success, stderr.ToString());
            Assert.True(File.Exists(output));
        }

        // Until selection completes (for example when the input cannot be read) nothing is counted or published.
        [Fact]
        public void Json_WritesTheAllowListedExportBlockAndNoArtifacts()
        {
            ProcessFilterSpec process = Spec(new[] { "game*.exe" });
            var selection = new CaptureSelectionSpec(
                process,
                new HostSelection(HostMatchMode.Exact, ImmutableArray.Create("jcanary.example"), ImmutableArray<string>.Empty),
                ImmutableArray.Create("200", string.Empty),
                null,
                null,
                false);
            var counts = new CaptureExportCounts(10, 6, 6, 5, 4, 1, 1, 1);
            var report = new CliExportReport
            {
                Format = CaptureExportFormat.SanitizedSaz,
                Limits = CliLimits.Default,
                DeviceId = "d1",
                Selection = selection,
            };

            JsonElement pending = Root(report).GetProperty("export");
            report.Result = new ExportCaptureResult(
                CaptureExportFormat.SanitizedSaz,
                selection,
                counts,
                new Dictionary<string, CaptureExportCounts>(StringComparer.Ordinal) { ["d1"] = counts },
                new ProcessFilterScope(process, new ProcessFilterCounts(10, 6, 4, 2), null),
                new SanitizedExportSummary { Sessions = 4, RedactedValues = 3, OmittedBodies = 1, OmittedHeaders = 2 },
                "xmatcap",
                "ab12",
                2048,
                "cd34",
                true,
                OutputProtected: true);
            JsonElement root = Root(report);

            JsonElement block = root.GetProperty("export");
            Assert.Equal(
                new[] { "format", "input", "limits", "selection", "processFilter", "counts", "devices", "sanitization", "output" },
                block.EnumerateObject().Select(property => property.Name));
            Assert.Equal("sanitized-saz", block.GetProperty("format").GetString());
            Assert.Equal("{\"kind\":\"xmatcap\",\"sha256\":\"ab12\"}", Compact(block.GetProperty("input")));
            Assert.Equal(
                "{\"device\":\"d1\",\"host\":{\"mode\":\"exact\",\"include\":1,\"exclude\":0},\"statuses\":2,\"methods\":null," +
                    "\"matchNone\":false}",
                Compact(block.GetProperty("selection")));
            Assert.True(block.GetProperty("processFilter").GetProperty("applied").GetBoolean());
            Assert.Equal(new long[] { 10, 6, 6, 5, 4, 1, 1, 1 }, Counts(block.GetProperty("counts")));
            JsonElement device = Assert.Single(block.GetProperty("devices").EnumerateArray());
            Assert.Equal("d1", device.GetProperty("id").GetString());
            Assert.Equal(new long[] { 10, 6, 6, 5, 4, 1, 1, 1 }, Counts(device));
            Assert.Equal(
                "{\"redactedValues\":3,\"omittedBodies\":1,\"omittedHeaders\":2}",
                Compact(block.GetProperty("sanitization")));
            Assert.Equal("{\"published\":true,\"bytes\":2048,\"sha256\":\"cd34\"}", Compact(block.GetProperty("output")));
            Assert.Empty(root.GetProperty("artifacts").EnumerateArray());
            Assert.DoesNotContain("jcanary", root.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("counts").ValueKind);
            Assert.Empty(pending.GetProperty("devices").EnumerateArray());
            Assert.False(pending.GetProperty("processFilter").GetProperty("applied").GetBoolean());
            Assert.False(pending.TryGetProperty("sanitization", out _));
            Assert.Equal("{\"published\":false,\"bytes\":null,\"sha256\":null}", Compact(pending.GetProperty("output")));
        }

        // The eight counts of an export block or one of its devices, in their documented order.
        internal static long[] Counts(JsonElement element) =>
            CountNames.Select(name => element.GetProperty(name).GetInt64()).ToArray();

        private static CliRequest Request(params string[] options)
        {
            CliParseResult parsed = CliCommandLine.Parse(new[] { "export", "capture.xmatcap" }.Concat(options).ToArray());
            Assert.True(parsed.Succeeded, parsed.Error?.Code);
            return parsed.Request;
        }

        private static JsonElement Root(CliExportReport export)
        {
            using JsonDocument document = JsonDocument.Parse(
                CliJson.Serialize(new CliReport(CliCommands.Export, "run") { Export = export }));
            return document.RootElement.Clone();
        }

        private static string[] Sorted(ImmutableArray<string>? values) =>
            values?.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}
