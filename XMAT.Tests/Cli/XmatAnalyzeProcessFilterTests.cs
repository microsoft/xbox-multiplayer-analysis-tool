// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Operations.Processes;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Analysis;
using XMAT.Tests.Proxy;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests.Cli
{
    // xmat analyze --process/--exclude-process, run as a child process: only the output is filtered, the JSON and
    // summary.json record the validated selectors and the counts, and no observed process name, device name or address
    // is ever written.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeProcessFilterTests
    {
        [Fact]
        public async Task Analyze_ProcessFilter_AnalyzesTheMatchingSessionsAndRecordsTheSelection()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            string inputHash = CliAssert.Sha256(input);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--process", "game*.exe", "--exclude-process", "4200");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(3, json.GetProperty("totals").GetProperty("calls").GetInt32());
            JsonElement block = ProcessFilterBlock(json);
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" }, new[] { "4200" });
            Assert.True(block.GetProperty("applied").GetBoolean());
            Assert.Equal(Items(spec.Include), Strings(block.GetProperty("include")));
            Assert.Equal(Items(spec.Exclude), Strings(block.GetProperty("exclude")));
            Assert.Equal(new long[] { 8, 4, 4, 2 }, Counts(block.GetProperty("counts")));
            JsonElement device = Assert.Single(block.GetProperty("devices").EnumerateArray());
            Assert.Equal("d1", device.GetProperty("id").GetString());
            Assert.Equal(new long[] { 8, 4, 4, 2 }, Counts(device));

            string published = Path.Join(directory.Root, "results");
            string summaryText = File.ReadAllText(Path.Join(published, "summary.json"));
            using (JsonDocument summary = JsonDocument.Parse(summaryText))
            {
                Assert.Equal(Compact(block), Compact(summary.RootElement.GetProperty("processFilter")));
            }

            foreach (string observed in new[] { "Game.exe", "svchost", ProcessCaptures.Device, ProcessCaptures.ClientIp })
            {
                Assert.DoesNotContain(observed, run.Stdout, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(observed, run.Stderr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(observed, summaryText, StringComparison.OrdinalIgnoreCase);
            }

            CliAssert.NoLeaks(run, directory, published);
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Equal(inputHash, CliAssert.Sha256(input));
        }

        [Fact]
        public async Task Analyze_FilterThatHidesEverySession_Returns3WithItsSelectionAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--process", "nomatch.exe");

            CliAssert.ExitCode(3, run);
            JsonElement json = run.Json();
            Assert.Equal("failed", json.GetProperty("status").GetString());
            Assert.Contains("analysis.no-data", run.ErrorCodes());
            JsonElement block = ProcessFilterBlock(json);
            Assert.True(block.GetProperty("applied").GetBoolean());
            Assert.Equal(new long[] { 8, 0, 8, 2 }, Counts(block.GetProperty("counts")));
            Assert.Equal(new long[] { 8, 0, 8, 2 }, Counts(Assert.Single(block.GetProperty("devices").EnumerateArray())));
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Analyze_InvalidSelector_IsAUsageErrorThatNeverRepeatsIt()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(directory.Root, "capture.saz");
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--process", @"C:\pfcanary\Game.exe");

            CliAssert.ExitCode(2, run);
            Assert.Equal(new[] { "cli.usage.invalid-value" }, run.ErrorCodes());
            Assert.DoesNotContain("pfcanary", run.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pfcanary", run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(run.Json().TryGetProperty("processFilter", out _));
            Assert.Equal(before, directory.Entries());
        }

        [Fact]
        public async Task Analyze_MissingInputWithAFilter_ReportsTheSelectorsAsNotApplied()
        {
            using var directory = new CliTestDirectory();
            string input = Path.Join(directory.Root, "missing.xmatcap");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--process", "game*.exe");

            CliAssert.ExitCode(3, run);
            Assert.Contains("analysis.input.not-found", run.ErrorCodes());
            JsonElement block = ProcessFilterBlock(run.Json());
            Assert.False(block.GetProperty("applied").GetBoolean());
            Assert.Equal(Items(Spec(new[] { "game*.exe" }).Include), Strings(block.GetProperty("include")));
            Assert.Empty(block.GetProperty("exclude").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, block.GetProperty("counts").ValueKind);
            Assert.Empty(block.GetProperty("devices").EnumerateArray());
            CliAssert.NoLeaks(run, directory);
        }

        // Without selectors every session is analyzed, and the counts still say how many have no process information.
        [Fact]
        public async Task Analyze_WithoutAFilter_RecordsTheWholeCaptureAsIncluded()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(7, json.GetProperty("totals").GetProperty("calls").GetInt32());
            JsonElement block = ProcessFilterBlock(json);
            Assert.True(block.GetProperty("applied").GetBoolean());
            Assert.Empty(block.GetProperty("include").EnumerateArray());
            Assert.Empty(block.GetProperty("exclude").EnumerateArray());
            Assert.Equal(new long[] { 8, 8, 0, 2 }, Counts(block.GetProperty("counts")));
            CliAssert.NoLeaks(run, directory, Path.Join(directory.Root, "results"));
        }

        private static JsonElement ProcessFilterBlock(JsonElement json)
        {
            Assert.True(
                json.TryGetProperty("processFilter", out JsonElement block),
                "The analyze JSON has no processFilter block.");
            return block;
        }

        private static long[] Counts(JsonElement element) =>
            new[] { "total", "included", "hidden", "unknown" }.Select(name => element.GetProperty(name).GetInt64()).ToArray();
    }
}
