// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.ApplicationServices;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureInvestigationTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void SearchUsesExplicitMetadataFiltersAndNeverProjectsRawRequestData()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, duration: 10);
                var expected = CaptureInvestigationTestData.AddWeb(database, 2, duration: 150);
                CaptureInvestigationTestData.AddWeb(database, 3, status: 200, duration: 200);
                CaptureInvestigationTestData.AddWeb(database, 4, duration: 500, device: "different");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var filter = new CaptureDataFilter(deviceName: "test-device", host: "API.XBOXLIVE.COM", statusCode: 429,
                    afterUtc: new DateTimeOffset(CaptureInvestigationTestData.Start.AddSeconds(1)),
                    beforeUtc: new DateTimeOffset(CaptureInvestigationTestData.Start.AddSeconds(4)), minimumDurationMilliseconds: 100);
                var result = Complete(Task.Run(() => model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, filter, Options())));
                var row = Assert.Single(result.Records);
                Assert.Equal(expected, row.RecordId);
                Assert.Equal(150, row.DurationMilliseconds);
                Assert.Equal(4, result.ScannedRecords);
                Assert.Equal(1, result.MatchedRecords);
                Assert.Null(model.SelectedDeviceContext);
                Assert.False(model.IsCaptureDataOperationRunning);
                var json = JsonSerializer.Serialize(result);
                Assert.DoesNotContain(CaptureInvestigationTestData.Secret, json);
                Assert.DoesNotContain("Authorization", json);
                Assert.DoesNotContain("xuid(", json);
                Assert.DoesNotContain("ReqBody", json);
                CaptureInvestigationTestData.AddWeb(database, 5);
                Assert.Single(result.Records);
                Assert.Equal(150, result.Metrics.Single(metric => metric.Id == "http.duration.p95").Value);
                CaptureDeviceTestData.Finish(result.DisposeAsync().AsTask());
            });
        }

        [Fact]
        public void DurationMetricsUseNearestRankAndMissingDurationsAreNotFabricated()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                foreach (var value in new[] { 10, 20, 30, 40 })
                    CaptureInvestigationTestData.AddWeb(database, value / 10, duration: value);
                var incomplete = CaptureInvestigationTestData.AddWeb(database, 5, complete: false);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var result = Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null, Options()));
                Assert.Equal(20, result.Metrics.Single(metric => metric.Id == "http.duration.p50").Value);
                Assert.Equal(40, result.Metrics.Single(metric => metric.Id == "http.duration.p95").Value);
                Assert.Equal(25, result.Metrics.Single(metric => metric.Id == "http.duration.mean").Value);
                Assert.Equal(5, result.Metrics.Single(metric => metric.Id == "http.status").Count);
                Assert.Null(result.Records.Single(record => record.RecordId == incomplete).DurationMilliseconds);
                Assert.Equal(4, result.Metrics.Single(metric => metric.Id == "http.duration.mean").Count);
                CaptureDeviceTestData.Finish(result.DisposeAsync().AsTask());
            });
        }

        [Fact]
        public void BuiltInAnalysisProjectsAllConsolesAndOnlyControlledFindingsWithSnapshotEvidence()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                var first = CaptureInvestigationTestData.AddWeb(database, 1);
                var second = CaptureInvestigationTestData.AddWeb(database, 2, console: "10.0.0.2");
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer(new CaptureInvestigationTestData.SensitiveRule()));
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var changedOffDispatcher = false;
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(model.IsAnalysisRunning) && !System.Windows.Application.Current.Dispatcher.CheckAccess())
                        changedOffDispatcher = true;
                };
                var result = Complete(Task.Run(() => model.CaptureInvestigation.AnalyzeAsync(source, CaptureDataKind.WebProxy, null, Options())));
                Assert.Equal(2, result.EligibleServiceCalls);
                Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, result.Findings.Select(finding => finding.Console).Order());
                Assert.All(result.Findings, finding =>
                {
                    Assert.Equal(CaptureFindingSeverity.Error, finding.Severity);
                    Assert.Equal(nameof(CaptureInvestigationTestData.SensitiveRule), finding.RuleId);
                    Assert.Equal(1, finding.AffectedRecords);
                    Assert.Equal(0, finding.UnmappedCalls);
                });
                Assert.Equal(2, result.Findings.Select(finding => finding.ConsoleId).Distinct().Count());
                Assert.Single(result.Findings.Select(finding => finding.EndpointId).Distinct());
                Assert.Equal(new[] { first, second }, result.Evidence.Values.SelectMany(value => value).Select(reference => reference.RecordId).Order());
                Assert.DoesNotContain(CaptureInvestigationTestData.Secret, JsonSerializer.Serialize(result));
                Assert.NotEqual(Guid.Empty, result.SnapshotId);
                Assert.False(model.IsAnalysisRunning);
                Assert.False(changedOffDispatcher);
                Assert.Empty(model.AnalysisRuns);
                Assert.Null(model.SelectedDeviceContext);
                CaptureDeviceTestData.Finish(result.DisposeAsync().AsTask());
            });
        }

        [Fact]
        public void BuiltInAnalysisUsesTheSharedHeadlessWorkBudgetAndReleasesItsSnapshotAndLeaseOnLimit()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                CaptureInvestigationTestData.AddWeb(database, 2);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var analyzer = new XboxLiveCaptureAnalysis.XboxLiveCaptureAnalyzer(
                    AppContext.BaseDirectory, () => throw new InvalidOperationException("No desktop output was requested."),
                    workBudgetFactory: () => new CaptureAnalysisEngine.AnalysisWorkBudget(1));
                analyzer.Initialize(model);
                model.CaptureAnalyzers.Add(analyzer);
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                try
                {
                    var failure = Assert.Throws<CaptureDataException>(() => Complete(
                        model.CaptureInvestigation.AnalyzeAsync(source, CaptureDataKind.WebProxy, null, Options())));
                    Assert.Equal(CaptureDataError.LimitExceeded, failure.Error);
                    Assert.False(model.IsAnalysisRunning);
                    Assert.False(model.IsCaptureDataOperationRunning);
                    var result = Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null, Options()));
                    Assert.Equal(2, result.MatchedRecords);
                    CaptureDeviceTestData.Finish(result.DisposeAsync().AsTask());
                }
                finally { analyzer.Shutdown(); }
            });
        }

        [Fact]
        public void SearchAndAnalysisRespectExistingLeasesAndReleasedSources()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer());
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                Assert.True(model.TryBeginCaptureDataOperation(out var lease));
                using (lease)
                {
                    Assert.Equal(CaptureDataError.SourceBusy, Assert.Throws<CaptureDataException>(() =>
                        Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null, Options()))).Error);
                    Assert.True(model.IsCaptureDataOperationRunning);
                }
                Assert.True(model.TryBeginSourceAnalysis(out var analysis));
                using (analysis)
                {
                    Assert.Equal(CaptureDataError.SourceBusy, Assert.Throws<CaptureDataException>(() =>
                        Complete(model.CaptureInvestigation.AnalyzeAsync(source, CaptureDataKind.WebProxy, null, Options()))).Error);
                }
                model.ActiveDatabase = null;
                Assert.Equal(CaptureDataError.SourceChanged, Assert.Throws<CaptureDataException>(() =>
                    Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null, Options()))).Error);
                Assert.False(model.IsAnalysisRunning);
            });
        }

        [Fact]
        public void TruncatedDeviceLabelsCannotAccidentallyMatchAnExactDeviceFilter()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                var prefix = new string('a', 256);
                CaptureInvestigationTestData.AddWeb(database, 1, device: prefix + "other");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var result = Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy,
                    new CaptureDataFilter(deviceName: prefix), Options()));
                Assert.Empty(result.Records);
                CaptureDeviceTestData.Finish(result.DisposeAsync().AsTask());
            });
        }

        [Fact]
        public void DesktopPacketAnalyzerRetainsItsVisibleFilteredInputAndTopicOrder()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start.AddMilliseconds(10));
                var packets = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords
                    .Select(record => new NetworkTracePacketDataModel(record)).ToArray();
                var grid = new ListView();
                grid.Items.Add(packets[1]);
                var controller = new NetworkTraceCaptureController("test-device", DeviceType.LocalPC) { FilteredItems = grid.Items };
                var run = new AnalysisRunModel(NetworkTraceCaptureAnalyzer.Analyzer, 1);
                Assert.Equal(ECaptureAnalyzerResult.Success, Complete(NetworkTraceCaptureAnalyzer.Analyzer.RunAsync(run, controller)));
                var result = Assert.IsType<NetworkTraceAnalyzerModel>(run.AnalysisData);
                Assert.Equal(1, result.TotalPacketsScanned);
                Assert.Equal(1, result.CanonicalAnalysis.TotalPackets);
                Assert.Equal(6, result.NumericStatsLists.Count);
                Assert.Equal(Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PCBP"), result.NumericStatsLists[0].Topic);
                Assert.Equal(Localization.GetLocalizedString("NETCAP_ANALYSIS_RESULT_PSBS"), result.NumericStatsLists[1].Topic);
                Assert.Single(result.NumericStatsLists[0].Values);
                Assert.Empty(result.NumericStatsLists[2].Values);
            });
        }

        [Fact]
        public void ResultQuotaAndInvalidFilterErrorsLeaveNoAnalysisLeaseOrPartialResult()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                for (var index = 0; index < 100; index++)
                    CaptureInvestigationTestData.AddWeb(database, index);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var limited = Assert.Throws<CaptureDataException>(() =>
                    Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null,
                        new QueryExecutionOptions(TimeSpan.FromSeconds(5), 16 * 1024))));
                Assert.Equal(CaptureDataError.LimitExceeded, limited.Error);
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Equal(CaptureDataError.InvalidFilter, Assert.Throws<CaptureDataException>(() =>
                    Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy,
                        new CaptureDataFilter(protocol: 17), Options()))).Error);
                Assert.False(model.IsAnalysisRunning);
            });
        }

        [Fact]
        public void SnapshotDeadlineIsReportedAsTimeoutWhileCallerCancellationRemainsCancellation()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var service = new CaptureInvestigation(model, Dispatcher.CurrentDispatcher, new DelayedQueries());
                Assert.Equal(CaptureDataError.Timeout, Assert.Throws<CaptureDataException>(() =>
                    Complete(service.SearchAsync(Guid.NewGuid(), CaptureDataKind.WebProxy, null,
                        new QueryExecutionOptions(TimeSpan.FromMilliseconds(20))))).Error);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Complete(service.SearchAsync(
                    Guid.NewGuid(), CaptureDataKind.WebProxy, null, Options(), cancellation.Token)));
                Assert.False(model.IsAnalysisRunning);
                Assert.False(model.IsCaptureDataOperationRunning);
            });
        }

        private sealed class DelayedQueries : ICaptureQueries
        {
            public Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<QuerySourceDescriptor>>([]);
            public Task<IReadOnlyList<QueryTable>> GetSchemaAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<QueryTable>>([]);
            public async Task<IQuerySession> CreateSessionAsync(Guid sourceId, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The delay should be cancelled.");
            }
        }

        private static QueryExecutionOptions Options() => new(TimeSpan.FromSeconds(10), 2 * 1024 * 1024);
        private static T Complete<T>(Task<T> task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The investigation did not complete.");
            return task.GetAwaiter().GetResult();
        }
    }
}
