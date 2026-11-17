// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests.Operations.Analysis
{
    // AnalysisOptions.ProcessFilter selects sessions through the shared selection before any service conversion: the
    // analysis covers exactly the selected rows, and its scope counts every row of the selected devices, including the
    // ones without process information.
    public sealed class ProcessFilteredAnalysisTests : IDisposable
    {
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "ProcessFilteredAnalysis-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task Include_AnalyzesTheSelectedRowsAndCountsTheScopeBeforeConversion()
        {
            string path = ProcessCaptures.WriteXmatcap(_directory, "mixed.xmatcap", ProcessCaptures.MixedSessions);
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });

            OperationResult<AnalysisResult> result = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(ProcessFilter: spec));

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(3, result.Value.Summary.TotalCalls);
            Assert.NotNull(result.Value.IncludedKeys);
            Assert.Equal(ProcessCaptures.MixedGameRowIds, RowIds(result.Value.IncludedKeys));
            AssertScope(
                result.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(8, 4, 4, 2),
                ("d1", new ProcessFilterCounts(8, 4, 4, 2)));
        }

        [Fact]
        public async Task FilterThatHidesEverySession_ReportsNoDataAndKeepsTheScope()
        {
            string path = ProcessCaptures.WriteXmatcap(_directory, "mixed.xmatcap", ProcessCaptures.MixedSessions);
            ProcessFilterSpec spec = Spec(new[] { "nomatch.exe" });

            OperationResult<AnalysisResult> result = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(ProcessFilter: spec));

            Assert.Equal(OperationOutcome.NoAnalyzableData, result.Outcome);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.no-data");
            Assert.NotNull(result.Value);
            Assert.NotNull(result.Value.IncludedKeys);
            Assert.Empty(result.Value.IncludedKeys);
            AssertScope(
                result.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(8, 0, 8, 2),
                ("d1", new ProcessFilterCounts(8, 0, 8, 2)));
        }

        // Without a filter the analysis is unchanged; the scope still counts the rows without process information.
        [Fact]
        public async Task WithoutAFilter_AnalyzesEveryRowAndIncludesTheWholeScope()
        {
            string path = ProcessCaptures.WriteXmatcap(_directory, "mixed.xmatcap", ProcessCaptures.MixedSessions);

            OperationResult<AnalysisResult> result = await Analyze(AnalysisInput.FromFile(path), new AnalysisOptions());

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(7, result.Value.Summary.TotalCalls);
            Assert.NotNull(result.Value.IncludedKeys);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, RowIds(result.Value.IncludedKeys));
            AssertScope(
                result.Value.ProcessScope,
                ProcessFilterSpec.Empty,
                new ProcessFilterCounts(8, 8, 0, 2),
                ("d1", new ProcessFilterCounts(8, 8, 0, 2)));
        }

        [Fact]
        public async Task SazInput_IsFilteredOnItsImportedProcessInformation()
        {
            string path = ProcessCaptures.WriteSaz(
                _directory,
                "processes.saz",
                new[]
                {
                    new ProcessSazFrame("x-ProcessInfo", "Game.exe:4100"),
                    new ProcessSazFrame("x-ProcessInfo", "svchost.exe:900"),
                    new ProcessSazFrame("X-PROCESSINFO", "game.exe:4100"),
                    new ProcessSazFrame("x-ProcessInfo", null),
                });
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });

            OperationResult<AnalysisResult> result = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(ProcessFilter: spec));

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(2, result.Value.Summary.TotalCalls);
            Assert.NotNull(result.Value.IncludedKeys);
            Assert.Equal(new long[] { 1, 3 }, RowIds(result.Value.IncludedKeys));
            AssertScope(
                result.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(4, 2, 2, 1),
                ("d1", new ProcessFilterCounts(4, 2, 2, 1)));
        }

        [Fact]
        public async Task Scope_IsCountedPerSelectedDeviceUnderItsOrdinalId()
        {
            string path = ProcessCaptures.WriteXmatcap(
                _directory,
                "devices.xmatcap",
                new[]
                {
                    new ProcessSession("Game.exe", 4100, "local", Device: "Alpha PC"),
                    new ProcessSession("Game.exe", 4100, "local", Device: "Alpha PC"),
                    new ProcessSession("svchost.exe", 900, "local", Device: "Alpha PC"),
                    new ProcessSession("Game.exe", 7, "local", Device: "Beta PC"),
                    new ProcessSession(string.Empty, 0, string.Empty, Device: "Beta PC"),
                });
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });

            OperationResult<AnalysisResult> all = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(ProcessFilter: spec));
            OperationResult<AnalysisResult> second = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(DeviceId: "d2", ProcessFilter: spec));

            Assert.Equal(OperationOutcome.Succeeded, all.Outcome);
            Assert.Equal(3, all.Value.Summary.TotalCalls);
            Assert.NotNull(all.Value.IncludedKeys);
            Assert.Equal(new long[] { 1, 2, 4 }, RowIds(all.Value.IncludedKeys));
            AssertScope(
                all.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(5, 3, 2, 1),
                ("d1", new ProcessFilterCounts(3, 2, 1, 0)),
                ("d2", new ProcessFilterCounts(2, 1, 1, 1)));
            Assert.Equal(OperationOutcome.Succeeded, second.Outcome);
            Assert.Equal(1, second.Value.Summary.TotalCalls);
            Assert.NotNull(second.Value.IncludedKeys);
            Assert.Equal(new long[] { 4 }, RowIds(second.Value.IncludedKeys));
            AssertScope(
                second.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(2, 1, 1, 1),
                ("d2", new ProcessFilterCounts(2, 1, 1, 1)));
        }

        // The GUI analyzes the records of its open capture; they select the same rows as the file.
        [Fact]
        public async Task RecordsInput_SelectsTheSameRowsAsTheFile()
        {
            string path = ProcessCaptures.WriteXmatcap(_directory, "mixed.xmatcap", ProcessCaptures.MixedSessions);
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });
            OperationResult<AnalysisResult> fromFile = await Analyze(
                AnalysisInput.FromFile(path),
                new AnalysisOptions(ProcessFilter: spec));

            OperationResult<AnalysisResult> fromRecords;
            using (var database = new SqLtReadonlyDatabase(path))
            {
                List<IDataRecord> records = database.TableByName(WebServiceCaptureMethod.TableName)
                    .Dataset.DataRecords.ToList();
                fromRecords = await Analyze(
                    AnalysisInput.FromRecords(records, ProcessCaptures.Device),
                    new AnalysisOptions(ProcessFilter: spec));
            }

            Assert.Equal(OperationOutcome.Succeeded, fromRecords.Outcome);
            Assert.Equal(3, fromRecords.Value.Summary.TotalCalls);
            Assert.Equal(fromFile.Value.Summary.TotalCalls, fromRecords.Value.Summary.TotalCalls);
            Assert.NotNull(fromRecords.Value.IncludedKeys);
            Assert.Equal(ProcessCaptures.MixedGameRowIds, RowIds(fromRecords.Value.IncludedKeys));
            AssertScope(
                fromRecords.Value.ProcessScope,
                spec,
                new ProcessFilterCounts(8, 4, 4, 2),
                ("d1", new ProcessFilterCounts(8, 4, 4, 2)));
        }

        // The analysis accepts the shared selection only when it lists each analyzed session's key once and no other
        // key. A duplicate or a foreign key keeps the number of keys, so only exact set equality with unique keys
        // rejects it.
        [Theory]
        [InlineData("exact", true)]
        [InlineData("duplicate", false)]
        [InlineData("foreign", false)]
        [InlineData("missing", false)]
        public void SelectionCheck_AcceptsEachAnalyzedSessionsKeyExactlyOnce(string selection, bool accepted)
        {
            var source = Guid.NewGuid();
            var first = new CaptureRowKey(source, 0, 1, 1);
            var second = new CaptureRowKey(source, 0, 2, 2);
            var analyzed = new HashSet<CaptureRowKey> { first, second };
            CaptureRowKey[] selected = selection switch
            {
                "exact" => new[] { second, first },
                "duplicate" => new[] { first, first },
                "foreign" => new[] { first, new CaptureRowKey(Guid.NewGuid(), 0, 2, 2) },
                _ => new[] { first },
            };

            Assert.Equal(accepted, AnalyzeCaptureOperation.IsExactSelection(selected, analyzed));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private static Task<OperationResult<AnalysisResult>> Analyze(AnalysisInput input, AnalysisOptions options) =>
            new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(input, options),
                OperationContext.None,
                CancellationToken.None);

        private static void AssertScope(
            ProcessFilterScope scope,
            ProcessFilterSpec filter,
            ProcessFilterCounts counts,
            params (string Id, ProcessFilterCounts Counts)[] devices)
        {
            Assert.NotNull(scope);
            Assert.NotNull(scope.Filter);
            Assert.Equal(Items(filter.Include), Items(scope.Filter.Include));
            Assert.Equal(Items(filter.Exclude), Items(scope.Filter.Exclude));
            Assert.Equal(counts, scope.Counts);
            Assert.NotNull(scope.Devices);
            Assert.Equal(
                devices.Select(device => device.Id).OrderBy(id => id, StringComparer.Ordinal),
                scope.Devices.Keys.OrderBy(id => id, StringComparer.Ordinal));
            foreach ((string id, ProcessFilterCounts expected) in devices)
            {
                Assert.Equal(expected, scope.Devices[id]);
            }
        }
    }
}
