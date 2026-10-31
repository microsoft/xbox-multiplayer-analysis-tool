// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using Microsoft.Data.Sqlite;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;

namespace XMAT.Tests.Operations.Analysis
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class XboxLiveCaptureAnalyzerAdapterTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public XboxLiveCaptureAnalyzerAdapterTests(
            CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Fact]
        public async Task RunAsync_PreservesLegacyFilesAndReportViewModels()
        {
            using var directory = new TestDirectory();
            var capturePath = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 3));
            using var database = new SqLtReadonlyDatabase(capturePath);
            WebServiceDeviceCaptureController controller =
                LoadController(database);

            long runId = DateTime.UtcNow.ToFileTimeUtc();
            var outputRoot = System.IO.Path.Combine(
                directory.Path,
                "legacy-output");
            Directory.CreateDirectory(outputRoot);
            var outputDirectory = System.IO.Path.Combine(
                outputRoot,
                $"xlca_{runId}");
            Assert.False(Directory.Exists(outputDirectory));

            DirectAnalysisRun analysis =
                StartDirectRun(
                    controller,
                    outputRoot,
                    runId);

            try
            {
                ECaptureAnalyzerResult result =
                    await analysis.Completion
                        .WaitAsync(
                            TimeSpan.FromSeconds(30));

                Assert.Equal(
                    ECaptureAnalyzerResult.Success,
                    result);
                _application.Run(window =>
                {
                    var analysisModel = Assert.IsType<AnalysisResultsModel>(
                        analysis.Run.AnalysisData);
                    Assert.True(analysisModel.IsComplete);
                    Assert.Equal(
                        new[] { "PerEndpointReportModel", "StatsReportModel" },
                        analysisModel.Reports
                            .Select(report => report.GetType().Name)
                            .OrderBy(name => name, StringComparer.Ordinal));
                });

                var prefix = $"xlca_{runId}";
                var expectedFiles = new[]
                {
                    "CallReport.json",
                    "PerEndpointReport.json",
                    "StatsReport.json",
                    "bootstrap.css",
                    "bootstrap.min.js",
                    "calls.js",
                    "index.html",
                    "jquery.js",
                    "report.css",
                    "report_view.js",
                    "site.css",
                    "stats.js",
                    $"{prefix}_results.txt",
                    $"{prefix}_service_call_data.txt",
                    $"{prefix}_service_call_items.txt",
                };
                Assert.Equal(
                    expectedFiles.OrderBy(name => name, StringComparer.Ordinal),
                    Directory.GetFiles(outputDirectory)
                        .Select(System.IO.Path.GetFileName)
                        .OrderBy(name => name, StringComparer.Ordinal));
            }
            finally
            {
                analysis.Dispose();
                if (Directory.Exists(outputDirectory))
                {
                    Directory.Delete(outputDirectory, true);
                }
            }
        }

        [Fact]
        public async Task RunAnalysisAsync_HostlessRowsSucceedWithoutInventingHostProvenance()
        {
            using var directory = new TestDirectory();
            string capturePath =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "hostless.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 12));
            UpdateCapture(
                capturePath,
                "UPDATE WebProxyConnections SET " +
                "RequestHost = '', RequestPort = '', " +
                "RequestLineAndHeaders = REPLACE(" +
                "RequestLineAndHeaders, " +
                "'Host: userpresence.xboxlive.com' || " +
                "char(13) || char(10), '')");
            using var database =
                new SqLtReadonlyDatabase(
                    capturePath);
            WebServiceDeviceCaptureController controller =
                LoadController(database);
            string outputRoot =
                System.IO.Path.Combine(
                    directory.Path,
                    "hostless-output");
            XboxLiveCaptureAnalyzer analyzer = null;
            CaptureAppModel model = null;
            AnalysisRunModel run = null;
            Task<ECaptureAnalyzerResult> analysis = null;

            _application.Run(window =>
            {
                analyzer =
                    new XboxLiveCaptureAnalyzer(
                        AppContext.BaseDirectory,
                        () => outputRoot);
                (model, run) =
                    PrepareModelRun(
                        analyzer,
                        controller);
                analysis = model.RunAnalysisAsync(
                    run,
                    controller);
            });

            try
            {
                Assert.Equal(
                    ECaptureAnalyzerResult.Success,
                    await analysis.WaitAsync(
                        TimeSpan.FromSeconds(30)));
                _application.Run(window =>
                {
                    var results =
                        Assert.IsType<
                            AnalysisResultsModel>(
                                run.AnalysisData);
                    Assert.True(results.IsComplete);
                });
                Assert.Equal(
                    12,
                    controller.ProxyConnections.Count);
                Assert.All(
                    controller.ProxyConnections,
                    record =>
                    {
                        Assert.Equal(
                            string.Empty,
                            record.Host);
                        Assert.Equal(
                            string.Empty,
                            record.Port);
                        Assert.Null(
                            record.ActionableHost);
                    });
            }
            finally
            {
                analyzer?.Shutdown();
                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(
                        outputRoot,
                        true);
                }
            }
        }

        [Fact]
        public async Task RunAsync_HandEditedAssetsUseGuiCompatibilityPolicy()
        {
            using var directory = new TestDirectory();
            var installDirectory = System.IO.Path.Combine(
                directory.Path,
                "package");
            SyntheticAnalysisAssets.CopyShipped(
                installDirectory);
            SyntheticAnalysisAssets.AddHandEditedRules(
                installDirectory);
            SyntheticAnalysisAssets.AddMalformedApiMapLine(
                installDirectory);
            var capturePath = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "hand-edited-assets.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 3));
            using var database = new SqLtReadonlyDatabase(capturePath);
            WebServiceDeviceCaptureController controller =
                LoadController(database);
            var outputRoot = System.IO.Path.Combine(
                directory.Path,
                "legacy-output");
            var runId = DateTime.UtcNow.ToFileTimeUtc();

            DirectAnalysisRun analysis =
                StartDirectRun(
                    controller,
                    outputRoot,
                    runId,
                    installDirectory);

            try
            {
                Assert.Equal(
                    ECaptureAnalyzerResult.Success,
                    await analysis.Completion
                        .WaitAsync(
                            TimeSpan.FromSeconds(30)));
            }
            finally
            {
                analysis.Dispose();
                var output = System.IO.Path.Combine(
                    outputRoot,
                    $"xlca_{runId}");
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, true);
                }
            }
        }

        [Fact]
        public async Task RunAsync_WorkLimitPreservesPriorRunsAndReturnsSpecificFailure()
        {
            using var directory = new TestDirectory();
            string capturePath =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "work-limit.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 3,
                        IncludeCanaries: true));
            using var database =
                new SqLtReadonlyDatabase(
                    capturePath);
            WebServiceDeviceCaptureController controller =
                LoadController(database);
            string outputRoot =
                System.IO.Path.Combine(
                    directory.Path,
                    "legacy-output");
            var observedExceptions =
                new List<Type>();
            XboxLiveCaptureAnalyzer analyzer = null;
            CaptureAppModel model = null;
            AnalysisRunModel priorRun = null;
            AnalysisRunModel failedRun = null;
            Task<ECaptureAnalyzerResult> analysis = null;

            _application.Run(window =>
            {
                model = new CaptureAppModel(
                    System.Windows.Application
                        .Current.Dispatcher);
                analyzer =
                    new XboxLiveCaptureAnalyzer(
                        AppContext.BaseDirectory,
                        () => outputRoot,
                        (type, _) =>
                            observedExceptions.Add(type),
                        static () =>
                            new AnalysisWorkBudget(1));
                analyzer.Initialize(model);
                priorRun = model.AddAnalysisRun(
                    analyzer);
                failedRun = model.AddAnalysisRun(
                    analyzer);
                analysis = model.RunAnalysisAsync(
                    failedRun,
                    controller);
            });

            try
            {
                Assert.Equal(
                    ECaptureAnalyzerResult.WorkLimit,
                    await analysis.WaitAsync(
                        TimeSpan.FromSeconds(30)));
                _application.Run(window =>
                {
                    Assert.Contains(
                        priorRun,
                        model.AnalysisRuns);
                    Assert.DoesNotContain(
                        failedRun,
                        model.AnalysisRuns);
                    var results =
                        Assert.IsType<
                            AnalysisResultsModel>(
                                failedRun.AnalysisData);
                    Assert.Empty(results.Reports);
                    string message =
                        Localization.GetLocalizedString(
                            "WEBCAP_ANALYSIS_ERROR_WORK_LIMIT");
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            message));
                    Assert.DoesNotContain(
                        SyntheticCaptures.Canary,
                        message,
                        StringComparison.Ordinal);
                });
                Assert.Empty(observedExceptions);
            }
            finally
            {
                analyzer?.Shutdown();
                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(
                        outputRoot,
                        true);
                }
            }
        }

        [Fact]
        public async Task RunAsync_NoSuitableData_PreservesLegacyDumpFiles()
            {
                using var directory = new TestDirectory();
                var capturePath = SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "no-data.xmatcap");
                UpdateCapture(
                    capturePath,
                    "UPDATE WebProxyConnections SET RequestMethod = 'CONNECT'");
                using var database = new SqLtReadonlyDatabase(capturePath);
                WebServiceDeviceCaptureController controller =
                    LoadController(database);
                var outputRoot = System.IO.Path.Combine(
                    directory.Path,
                    "legacy-output");
                Directory.CreateDirectory(outputRoot);
                var runId = DateTime.UtcNow.ToFileTimeUtc();
                var output = System.IO.Path.Combine(
                    outputRoot,
                    $"xlca_{runId}");
                Assert.False(Directory.Exists(output));
                DirectAnalysisRun analysis =
                    StartDirectRun(
                        controller,
                        outputRoot,
                        runId);

                try
                {
                    Assert.Equal(
                        ECaptureAnalyzerResult.NoSuitableData,
                        await analysis.Completion
                            .WaitAsync(
                                TimeSpan.FromSeconds(30)));
                    var prefix = $"xlca_{runId}";
                    Assert.Equal(
                        new[]
                        {
                            $"{prefix}_results.txt",
                            $"{prefix}_service_call_data.txt",
                            $"{prefix}_service_call_items.txt",
                        },
                        Directory.GetFiles(output)
                            .Select(System.IO.Path.GetFileName)
                            .OrderBy(name => name, StringComparer.Ordinal));
                }
                finally
                {
                    analysis.Dispose();
                    if (Directory.Exists(output))
                    {
                        Directory.Delete(output, true);
                    }
                }
            }

            [Fact]
            public async Task RunAsync_TwoConsoles_StillPresentsOnlyFirstConsoleReports()
            {
                using var directory = new TestDirectory();
                var capturePath = SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "two-consoles.xmatcap");
                UpdateCapture(
                    capturePath,
                    "UPDATE WebProxyConnections SET ClientIP = " +
                    "CASE WHEN id <= 2 THEN '10.0.0.1' ELSE '10.0.0.2' END; " +
                    "UPDATE WebProxyConnections SET " +
                    "RequestHost = 'profile.xboxlive.com', " +
                    "RequestLineAndHeaders = REPLACE(" +
                    "RequestLineAndHeaders, " +
                    "'Host: userpresence.xboxlive.com', " +
                    "'Host: profile.xboxlive.com') WHERE id <= 2; " +
                    "UPDATE WebProxyConnections SET " +
                    "RequestHost = 'achievements.xboxlive.com', " +
                    "RequestLineAndHeaders = REPLACE(" +
                    "RequestLineAndHeaders, " +
                    "'Host: userpresence.xboxlive.com', " +
                    "'Host: achievements.xboxlive.com') WHERE id > 2");
                using var database = new SqLtReadonlyDatabase(capturePath);
                WebServiceDeviceCaptureController controller =
                    LoadController(database);
                var outputRoot = System.IO.Path.Combine(
                    directory.Path,
                    "legacy-output");
                Directory.CreateDirectory(outputRoot);
                var runId = DateTime.UtcNow.ToFileTimeUtc();
                var output = System.IO.Path.Combine(
                    outputRoot,
                    $"xlca_{runId}");
                Assert.False(Directory.Exists(output));
                DirectAnalysisRun analysis =
                    StartDirectRun(
                        controller,
                        outputRoot,
                        runId);

                try
                {
                    Assert.Equal(
                        ECaptureAnalyzerResult.Success,
                        await analysis.Completion
                            .WaitAsync(
                                TimeSpan.FromSeconds(30)));
                    _application.Run(window =>
                    {
                        var model = Assert.IsType<AnalysisResultsModel>(
                            analysis.Run.AnalysisData);
                        Assert.Equal(2, model.Reports.Count);
                        var statsModel = Assert.Single(
                            model.Reports,
                            report =>
                                report.ReportDocument is
                                StatsReportDocument);
                        var stats = Assert.IsType<StatsReportDocument>(
                            statsModel.ReportDocument);
                        Assert.Equal(
                            "profile.xboxlive.com",
                            Assert.Single(stats.StatsResults).Uri);
                    });
                    Assert.Equal(
                        3,
                        Directory.GetFiles(output, "*Report.json").Length);
                }
                finally
                {
                    analysis.Dispose();
                    if (Directory.Exists(output))
                    {
                        Directory.Delete(output, true);
                    }
                }
            }

            [Fact]
            public async Task RunAnalysisAsync_OperationFailure_RemovesRunAndReleasesAnalysisLease()
            {
                using var directory = new TestDirectory();
                var capturePath = SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "contained-operation-failure.xmatcap");
                List<IDataRecord> records;
                using (var database = new SqLtReadonlyDatabase(capturePath))
                {
                    records = database.TableByName(
                            WebServiceCaptureMethod.TableName)
                        .Dataset.DataRecords.ToList();
                }
                var controller =
                    new WebServiceDeviceCaptureController(
                        "Synthetic device 1",
                        DeviceType.Unknown,
                        true);
                controller.LoadCaptureDataset(
                    new SecondReadFailsDataset(records));
                var outputRoot = System.IO.Path.Combine(
                    directory.Path,
                    "contained-operation-output");
                Directory.CreateDirectory(outputRoot);
                XboxLiveCaptureAnalyzer analyzer = null;
                CaptureAppModel model = null;
                AnalysisRunModel run = null;
                Task<ECaptureAnalyzerResult> analysis = null;
                var observedExceptions =
                    new List<(Type Type, string Stack)>();

                _application.Run(window =>
                {
                    analyzer = new XboxLiveCaptureAnalyzer(
                        AppContext.BaseDirectory,
                        () => outputRoot,
                        (type, stack) =>
                            observedExceptions.Add((type, stack)));
                    (model, run) =
                        PrepareModelRun(
                            analyzer,
                            controller);
                    Assert.True(model.TryBeginAnalysis(out var lease));
                    analysis = RunWithAnalysisLeaseAsync(
                        model,
                        run,
                        controller,
                        lease);
                });

                try
                {
                    Assert.Equal(
                        ECaptureAnalyzerResult.UnknownError,
                        await analysis.WaitAsync(TimeSpan.FromSeconds(30)));
                    var observed = Assert.Single(observedExceptions);
                    Assert.Equal(
                        typeof(InvalidOperationException),
                        observed.Type);
                    Assert.NotEmpty(observed.Stack);
                    _application.Run(window =>
                    {
                        Assert.False(run.IsProcessing);
                        Assert.Empty(model.AnalysisRuns);
                        AssertAnalysisLeaseReleased(model);
                    });
                }
                finally
                {
                    analyzer?.Shutdown();
                }
            }

            [Fact]
            public async Task RunAnalysisAsync_ReportProjectionFailure_ReachesContainmentAndReleasesAnalysisLease()
            {
                using var directory = new TestDirectory();
                var capturePath = SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "contained-projection-failure.xmatcap",
                    new SyntheticXmatcapOptions(RequestsPerDevice: 3));
                using var database = new SqLtReadonlyDatabase(capturePath);
                WebServiceDeviceCaptureController controller =
                    LoadController(database);
                var outputRoot = System.IO.Path.Combine(
                    directory.Path,
                    "contained-projection-output");
                var errors = new List<string>();
                XboxLiveCaptureAnalyzer analyzer = null;
                CaptureAppModel model = null;
                AnalysisRunModel run = null;
                Task<ECaptureAnalyzerResult> analysis = null;

                _application.Run(window =>
                {
                    analyzer = new XboxLiveCaptureAnalyzer(
                        AppContext.BaseDirectory,
                        () => outputRoot);
                    (model, run) =
                        PrepareModelRun(
                            analyzer,
                            controller);
                    var output = System.IO.Path.Combine(
                        outputRoot,
                        $"xlca_{run.Id}");
                    Directory.CreateDirectory(
                        System.IO.Path.Combine(output, "calls.js"));
                    Directory.CreateDirectory(
                        System.IO.Path.Combine(output, "index.html"));
                    Assert.True(model.TryBeginAnalysis(out var lease));
                    analysis = RunWithAnalysisLeaseAsync(
                        model,
                        run,
                        controller,
                        lease,
                        errors.Add);
                });

                try
                {
                    Assert.Equal(
                        ECaptureAnalyzerResult.UnknownError,
                        await analysis.WaitAsync(TimeSpan.FromSeconds(30)));
                    Assert.Contains(
                        "Capture analysis",
                        Assert.Single(errors),
                        StringComparison.Ordinal);
                    _application.Run(window =>
                    {
                        Assert.False(run.IsProcessing);
                        Assert.Empty(model.AnalysisRuns);
                        AssertAnalysisLeaseReleased(model);
                    });
                }
                finally
                {
                    analyzer?.Shutdown();
                }
            }

            [Fact]
            public void Initialize_InvalidAssets_ThrowsAndWritesNothing()
            {
                using var directory = new TestDirectory();
                var outputRoot = System.IO.Path.Combine(
                    directory.Path,
                    "legacy-output");
                var analyzer = new XboxLiveCaptureAnalyzer(
                    directory.Path,
                    () => outputRoot);

                _application.Run(window =>
                {
                    var model = new CaptureAppModel(
                        System.Windows.Application.Current.Dispatcher);
                    var exception = Assert.Throws<AnalysisAssetException>(
                        () => model.AddCaptureAnalyzer(analyzer));
                    Assert.StartsWith("analysis.assets.", exception.Code);
                    Assert.DoesNotContain(
                        analyzer,
                        model.CaptureAnalyzers);
                });
                Assert.False(Directory.Exists(outputRoot));
            }

            private static WebServiceDeviceCaptureController
                LoadController(
                SqLtReadonlyDatabase database)
            {
                var controller =
                    new WebServiceDeviceCaptureController(
                        "Synthetic device 1",
                        DeviceType.Unknown,
                        true);
                controller.LoadCaptureDataset(
                    database.TableByName(
                            WebServiceCaptureMethod.TableName)
                        .Dataset);
                return controller;
            }

            private DirectAnalysisRun StartDirectRun(
                WebServiceDeviceCaptureController controller,
                string outputRoot,
                long runId,
                string installDirectory = null)
            {
                DirectAnalysisRun started = null;
                _application.Run(window =>
                {
                    var analyzer = new XboxLiveCaptureAnalyzer(
                        installDirectory ?? AppContext.BaseDirectory,
                        () => outputRoot);
                    analyzer.Initialize(new CaptureAppModel(
                        System.Windows.Application.Current.Dispatcher));
                    var run = new AnalysisRunModel(analyzer, runId);
                    started = new DirectAnalysisRun(
                        analyzer,
                        run,
                        analyzer.RunAsync(run, controller));
                });
                return started;
            }

            private static (CaptureAppModel Model, AnalysisRunModel Run)
                PrepareModelRun(
                XboxLiveCaptureAnalyzer analyzer,
                WebServiceDeviceCaptureController controller)
            {
                var model = new CaptureAppModel(
                    System.Windows.Application.Current.Dispatcher);
                var context = new TestCaptureDeviceContext(controller);
                model.CaptureDeviceContexts.Add(context);
                model.SelectedDeviceContext = context;
                analyzer.Initialize(model);
                return (model, model.AddAnalysisRun(analyzer));
            }

            private static void UpdateCapture(
                string path,
                string commandText)
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString();
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = commandText;
                command.ExecuteNonQuery();
            }

            private sealed class DirectAnalysisRun :
                IDisposable
            {
                internal DirectAnalysisRun(
                    XboxLiveCaptureAnalyzer analyzer,
                    AnalysisRunModel run,
                    Task<ECaptureAnalyzerResult> completion)
                {
                    Analyzer = analyzer;
                    Run = run;
                    Completion = completion;
                }

                internal XboxLiveCaptureAnalyzer Analyzer { get; }
                internal AnalysisRunModel Run { get; }
                internal Task<ECaptureAnalyzerResult> Completion { get; }
                public void Dispose() => Analyzer.Shutdown();
            }

            private sealed class SecondReadFailsDataset : IDataset
            {
                private readonly IReadOnlyList<IDataRecord> _records;
                private int _reads;

                internal SecondReadFailsDataset(
                    IReadOnlyList<IDataRecord> records)
                {
                    _records = records;
                }

                public IEnumerable<IFieldDefinition> FieldDefs =>
                    Array.Empty<IFieldDefinition>();

                public IEnumerable<IDataRecord> DataRecords =>
                    Read();

                public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId) =>
                    Read();

                public IEnumerable<IDataRecord> DataRecordsFrom(
                    long firstRowId,
                    int limit) =>
                    Read();

                public IEnumerable<IDataRecord> DataRecordsWhen(
                    params IFieldValue[] fieldNamesAndValues) =>
                    Read();

                private IEnumerable<IDataRecord> Read()
                {
                    if (_reads++ == 0)
                    {
                        return _records;
                    }
                    return ThrowingRecords();
                }

                private static IEnumerable<IDataRecord> ThrowingRecords()
                {
                    yield return ThrowRecord();
                }

                private static IDataRecord ThrowRecord() =>
                    throw new InvalidOperationException(
                        "Synthetic record failure.");
            }

        private static async Task<ECaptureAnalyzerResult>
            RunWithAnalysisLeaseAsync(
                CaptureAppModel model,
                AnalysisRunModel run,
                IDeviceCaptureController controller,
                IDisposable lease,
                Action<string> logError = null)
        {
            using (lease)
            {
                return await model.RunAnalysisAsync(
                    run,
                    controller,
                    logError);
            }
        }

        private static void AssertAnalysisLeaseReleased(
            CaptureAppModel model)
        {
            Assert.False(model.IsAnalysisRunning);
            Assert.True(model.CanAnalyzeCaptures);
            Assert.True(model.TryBeginAnalysis(out var retryLease));
            retryLease.Dispose();
            Assert.False(model.IsAnalysisRunning);
        }

        private sealed class TestCaptureDeviceContext :
            ICaptureDeviceContext
        {
            internal TestCaptureDeviceContext(
                IDeviceCaptureController controller)
            {
                CaptureController = controller;
            }

            public DeviceType DeviceType => DeviceType.Unknown;

            public CaptureType CaptureType => CaptureType.WebProxy;

            public string DeviceName => "Synthetic device 1";

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; } = true;

            public System.Drawing.Color DeviceColor { get; set; }

            public IDeviceCaptureController CaptureController { get; }
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "HeadlessTestArtifacts",
                    $"XMAT-analysis-adapter-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
