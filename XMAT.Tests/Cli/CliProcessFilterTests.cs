// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations;
using XMAT.Operations.Capabilities;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests.Cli
{
    // xmat analyze's process options, in process: --process and --exclude-process are the only repeatable options, use the
    // shared selector rules and limits, never repeat an invalid selector, and the JSON records the validated selectors
    // with the counts of the selection.
    public class CliProcessFilterTests
    {
        private const string ProcessOption = "--process";
        private const string ExcludeOption = "--exclude-process";

        [Fact]
        public void Parse_CollectsRepeatedSelectorsInEitherForm()
        {
            CliParseResult parsed = CliCommandLine.Parse(new[]
            {
                "analyze", "capture.saz", ProcessOption, "game*.exe", "--out", "results",
                ProcessOption + "=Tool", ExcludeOption, "4100", ExcludeOption + "=svchost.exe",
            });

            Assert.True(parsed.Succeeded, parsed.Error?.Code);
            ProcessFilterSpec expected = Spec(new[] { "game*.exe", "Tool" }, new[] { "4100", "svchost.exe" });
            Assert.NotNull(parsed.Request.ProcessFilter);
            Assert.Equal(Items(expected.Include), Items(parsed.Request.ProcessFilter.Include));
            Assert.Equal(Items(expected.Exclude), Items(parsed.Request.ProcessFilter.Exclude));
            Assert.Equal("capture.saz", parsed.Request.Input);
            Assert.Equal("results", parsed.Request.Output);
        }

        [Fact]
        public void Parse_WithoutProcessOptionsHasNoProcessFilter()
        {
            CliParseResult parsed = CliCommandLine.Parse(new[] { "analyze", "capture.saz", "--out", "results" });

            Assert.True(parsed.Succeeded);
            Assert.Null(parsed.Request.ProcessFilter);
        }

        [Theory]
        [InlineData("--out", "other")]
        [InlineData("--device", "d2")]
        [InlineData("--fail-on", "findings")]
        [InlineData("--max-records", "5")]
        public void Parse_TheOtherOptionsStillRejectDuplicates(string option, string value)
        {
            var args = new List<string>
            {
                "analyze", "capture.saz", ProcessOption, "game.exe", ProcessOption, "tool.exe", ExcludeOption, "1",
                ExcludeOption, "2", "--out", "results", "--device", "d1", "--fail-on", "findings", "--max-records", "10",
                option, value,
            };

            CliParseResult parsed = CliCommandLine.Parse(args);

            Assert.False(parsed.Succeeded);
            Assert.Equal("cli.usage.duplicate-option", parsed.Error.Code);
        }

        // An invalid selector can be a path or another value that must not be repeated, so neither the message nor the
        // JSON echoes it.
        [Theory]
        [InlineData(ProcessOption, @"C:\pfcanary\Game.exe", "pfcanary")]
        [InlineData(ProcessOption, "pfcanary/Game.exe", "pfcanary")]
        [InlineData(ExcludeOption, "pfcanary\u0001.exe", "pfcanary")]
        [InlineData(ExcludeOption, "0", null)]
        public void Parse_InvalidSelectorIsAUsageErrorThatNeverRepeatsIt(string option, string selector, string canary)
        {
            CliParseResult parsed = CliCommandLine.Parse(new[] { "analyze", "capture.saz", "--out", "results", option, selector });

            Assert.False(parsed.Succeeded);
            Assert.Equal("cli.usage.invalid-value", parsed.Error.Code);
            if (canary != null)
            {
                Assert.DoesNotContain(canary, parsed.Error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    canary,
                    Encoding.UTF8.GetString(CliJson.Serialize(CliReport.ForParseError("run", parsed))),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        // The command line keeps the shared limits, up to 64 selectors of up to 260 characters on each side; one more
        // selector, or one more character, is a usage error that never repeats the selector.
        [Fact]
        public void Parse_KeepsTheSharedSelectorLimits()
        {
            var args = new List<string> { "analyze", "capture.saz", "--out", "results" };
            for (var index = 1; index <= 64; index++)
            {
                args.Add(ProcessOption);
                args.Add(index == 1 ? new string('g', 256) + ".exe" : $"game{index}.exe");
                args.Add(ExcludeOption);
                args.Add(index.ToString(CultureInfo.InvariantCulture));
            }

            CliParseResult atLimits = CliCommandLine.Parse(args);
            CliParseResult tooMany = CliCommandLine.Parse(args.Concat(new[] { ExcludeOption, "65" }).ToList());
            CliParseResult tooLong = CliCommandLine.Parse(
                new[] { "analyze", "capture.saz", "--out", "results", ProcessOption, new string('g', 257) + ".exe" });

            Assert.True(atLimits.Succeeded, atLimits.Error?.Code);
            Assert.Equal(64, atLimits.Request.ProcessFilter.Include.Length);
            Assert.Equal(64, atLimits.Request.ProcessFilter.Exclude.Length);
            Assert.Equal("cli.usage.invalid-value", tooMany.Error?.Code);
            Assert.Equal("cli.usage.invalid-value", tooLong.Error?.Code);
            OperationDiagnostic error = Assert.IsType<OperationDiagnostic>(tooLong.Error);
            Assert.DoesNotContain("gggggggg", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_ProcessOptionWithoutAValueIsAUsageError()
        {
            CliParseResult parsed = CliCommandLine.Parse(
                new[] { "analyze", "capture.saz", "--out", "results", ExcludeOption });

            Assert.Equal("cli.usage.missing-value", parsed.Error?.Code);
        }

        // The block follows the limits in standard output and summary.json alike; its devices use ordinal ids in device
        // order.
        [Fact]
        public void Json_RecordsTheValidatedSelectorsAndTheCountsOfTheSelection()
        {
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" }, new[] { "4100" });
            var report = new CliReport(CliCommands.Analyze, "run")
            {
                Analysis = new CliAnalysisReport
                {
                    Limits = CliLimits.Default,
                    ProcessFilter = spec,
                    ProcessScope = new ProcessFilterScope(
                        spec,
                        new ProcessFilterCounts(10, 6, 4, 2),
                        new Dictionary<string, ProcessFilterCounts>(StringComparer.Ordinal)
                        {
                            ["d10"] = new ProcessFilterCounts(1, 1, 0, 0),
                            ["d2"] = new ProcessFilterCounts(4, 2, 2, 1),
                            ["d1"] = new ProcessFilterCounts(5, 3, 2, 1),
                        }),
                },
            };

            using JsonDocument stdout = JsonDocument.Parse(CliJson.Serialize(report));
            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeSummary(report));

            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "input", "limits", "processFilter", "devices", "totals", "warnings" },
                Names(summary.RootElement));
            string[] names = Names(stdout.RootElement);
            Assert.Contains("processFilter", names);
            Assert.Equal("limits", names[Array.IndexOf(names, "processFilter") - 1]);
            Assert.Equal("devices", names[Array.IndexOf(names, "processFilter") + 1]);
            JsonElement block = stdout.RootElement.GetProperty("processFilter");
            Assert.Equal(new[] { "applied", "include", "exclude", "counts", "devices" }, Names(block));
            Assert.True(block.GetProperty("applied").GetBoolean());
            Assert.Equal(Items(spec.Include), Strings(block.GetProperty("include")));
            Assert.Equal(Items(spec.Exclude), Strings(block.GetProperty("exclude")));
            Assert.Equal(new[] { "total", "included", "hidden", "unknown" }, Names(block.GetProperty("counts")));
            Assert.Equal(new long[] { 10, 6, 4, 2 }, Counts(block.GetProperty("counts")));
            JsonElement[] devices = block.GetProperty("devices").EnumerateArray().ToArray();
            Assert.Equal(new[] { "d1", "d2", "d10" }, devices.Select(device => device.GetProperty("id").GetString()));
            Assert.All(devices, device => Assert.Equal(new[] { "id", "total", "included", "hidden", "unknown" }, Names(device)));
            Assert.Equal(new long[] { 5, 3, 2, 1 }, Counts(devices[0]));
            Assert.Equal(new long[] { 4, 2, 2, 1 }, Counts(devices[1]));
            Assert.Equal(new long[] { 1, 1, 0, 0 }, Counts(devices[2]));
            Assert.Equal(Compact(block), Compact(summary.RootElement.GetProperty("processFilter")));
        }

        [Fact]
        public void Json_BeforeTheSelectionCompletes_TheFilterIsNotAppliedAndHasNoCounts()
        {
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });
            var report = new CliReport(CliCommands.Analyze, "run")
            {
                Analysis = new CliAnalysisReport { Limits = CliLimits.Default, ProcessFilter = spec },
            };

            using JsonDocument stdout = JsonDocument.Parse(CliJson.Serialize(report));

            Assert.True(
                stdout.RootElement.TryGetProperty("processFilter", out JsonElement block),
                "A valid command line with process selectors reports them.");
            Assert.False(block.GetProperty("applied").GetBoolean());
            Assert.Equal(Items(spec.Include), Strings(block.GetProperty("include")));
            Assert.Empty(block.GetProperty("exclude").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, block.GetProperty("counts").ValueKind);
            Assert.Empty(block.GetProperty("devices").EnumerateArray());
        }

        [Fact]
        public void Json_ARejectedCommandLineHasNoProcessFilter()
        {
            CliReport rejected = CliReport.ForParseError(
                "run",
                CliCommandLine.Parse(new[] { "analyze", "a.saz", "--out", "results", ProcessOption, "game.exe", "--max-records", "0" }));

            using JsonDocument document = JsonDocument.Parse(CliJson.Serialize(rejected));

            Assert.False(document.RootElement.TryGetProperty("processFilter", out _));
        }

        [Fact]
        public void Registry_ListsProcessFilteringAsASupportedAnalyzeCapability()
        {
            Capability capability = Assert.Single(CapabilityRegistry.All, item => item.Id == "capture.process-filter");

            Assert.Equal(CapabilityStatus.Supported, capability.Status);
            Assert.Equal("Filter captured sessions by client process", capability.Title);
            Assert.Equal("S9", capability.Slice);
            Assert.Equal(typeof(FilterCaptureSessionsOperation).FullName, capability.Operation);
            Assert.Equal(
                "analyze INPUT --out DIR [--process NAME_OR_PID]... [--exclude-process NAME_OR_PID]...",
                capability.CliRoute);
            Assert.NotEmpty(capability.GuiEntryPoints);
            Capability analyze = Assert.Single(CapabilityRegistry.All, item => item.Id == "analysis.xbox-live");
            Assert.Contains("[--process NAME_OR_PID]...", analyze.CliRoute, StringComparison.Ordinal);
            Assert.Contains("[--exclude-process NAME_OR_PID]...", analyze.CliRoute, StringComparison.Ordinal);
            Assert.Contains(ProcessOption, CliCommandLine.AnalyzeOptionNames);
            Assert.Contains(ExcludeOption, CliCommandLine.AnalyzeOptionNames);
        }

        [Fact]
        public void AnalyzeHelp_DescribesTheProcessOptionsAndSessionsWithoutProcessInformation()
        {
            CliReport help = CliHelp.Create("run", CliCommands.Analyze);

            Assert.Contains(help.HumanLines, line => line.Contains("[--process NAME_OR_PID]...", StringComparison.Ordinal));
            Assert.Contains(help.HumanLines, line => line.Contains("[--exclude-process NAME_OR_PID]...", StringComparison.Ordinal));
            Assert.Contains(
                help.HumanLines,
                line => line.Contains("without process information", StringComparison.OrdinalIgnoreCase));
        }

        private static string[] Names(JsonElement element) =>
            element.EnumerateObject().Select(property => property.Name).ToArray();

        private static long[] Counts(JsonElement element) =>
            new[] { "total", "included", "hidden", "unknown" }.Select(name => element.GetProperty(name).GetInt64()).ToArray();
    }
}
