// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows.Controls;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests.Operations.Analysis
{
    // The GUI analyzes the full capture unless the user opts in to the tab's process filter; a filtered run snapshots the
    // filter when it starts and states it in scope.json and in the report before the run completes.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class ProcessFilteredAnalyzerAdapterTests : IDisposable
    {
        private readonly CaptureUiApplicationFixture _application;
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "ProcessFilteredAdapter-" + Guid.NewGuid().ToString("N"));

        public ProcessFilteredAnalyzerAdapterTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Fact]
        public async Task FilteredRun_WritesItsScopeAndAReportBannerBeforeItCompletes()
        {
            string capture = ProcessCaptures.WriteXmatcap(_directory, "capture.xmatcap", ProcessCaptures.MixedSessions);
            ProcessFilterSpec spec = Spec(new[] { "game*.exe" });

            (AnalysisRunModel run, ECaptureAnalyzerResult result, string output) = await RunAsync(capture, spec);

            Assert.Equal(ECaptureAnalyzerResult.Success, result);
            _application.Run(window =>
            {
                var results = Assert.IsType<AnalysisResultsModel>(run.AnalysisData);
                Assert.True(results.IsComplete);
                Assert.Contains(results.ArtifactManifest, artifact => artifact.RelativePath == "scope.json");
            });
            using (JsonDocument scope = JsonDocument.Parse(File.ReadAllText(Path.Join(output, "scope.json"))))
            {
                JsonElement root = scope.RootElement;
                Assert.Equal(
                    new[] { "applied", "include", "exclude", "counts", "devices" },
                    root.EnumerateObject().Select(property => property.Name));
                Assert.True(root.GetProperty("applied").GetBoolean());
                Assert.Equal(Items(spec.Include), Strings(root.GetProperty("include")));
                Assert.Empty(root.GetProperty("exclude").EnumerateArray());
                Assert.Equal(new long[] { 8, 4, 4, 2 }, Counts(root.GetProperty("counts")));
                JsonElement device = Assert.Single(root.GetProperty("devices").EnumerateArray());
                Assert.Equal("d1", device.GetProperty("id").GetString());
                Assert.Equal(new long[] { 8, 4, 4, 2 }, Counts(device));
            }

            string index = File.ReadAllText(Path.Join(output, "index.html"));
            Assert.Contains("id=\"process-filter-banner\"", index, StringComparison.Ordinal);
            Assert.Contains("game*.exe", index, StringComparison.Ordinal);
        }

        [Fact]
        public async Task UnfilteredRun_WritesNeitherAScopeNorABanner()
        {
            string capture = ProcessCaptures.WriteXmatcap(_directory, "capture.xmatcap", ProcessCaptures.MixedSessions);

            (AnalysisRunModel _, ECaptureAnalyzerResult result, string output) = await RunAsync(capture, ProcessFilterSpec.Empty);

            Assert.Equal(ECaptureAnalyzerResult.Success, result);
            Assert.False(File.Exists(Path.Join(output, "scope.json")));
            Assert.DoesNotContain(
                "process-filter-banner",
                File.ReadAllText(Path.Join(output, "index.html")),
                StringComparison.Ordinal);
        }

        [Fact]
        public void AddAnalysisRun_SnapshotsTheProcessFilterGivenForTheRun()
        {
            _application.Run(window =>
            {
                var model = new CaptureAppModel(System.Windows.Application.Current.Dispatcher);
                var analyzer = new XboxLiveCaptureAnalyzer(AppContext.BaseDirectory, () => _directory);
                ProcessFilterSpec spec = Spec(new[] { "game*.exe" }, new[] { "4100" });

                AnalysisRunModel filtered = model.AddAnalysisRun(analyzer, spec);
                AnalysisRunModel unfiltered = model.AddAnalysisRun(analyzer);

                Assert.Same(spec, filtered.ProcessFilter);
                Assert.Same(ProcessFilterSpec.Empty, unfiltered.ProcessFilter);
            });
        }

        // Analysis covers the full capture by default; the option exists only while the tab has a process filter.
        [Fact]
        public void AnalyzerWindow_OffersTheTabsProcessFilterOnlyAsAnOptIn()
        {
            _application.Run(window =>
            {
                ProcessFilterSpec spec = Spec(new[] { "game*.exe" });
                var filtered = new CaptureAnalyzerWindow(spec);
                var unfiltered = new CaptureAnalyzerWindow(ProcessFilterSpec.Empty);
                try
                {
                    var option = Assert.IsType<CheckBox>(filtered.FindName("AnalyzeProcessFilteredSessions"));
                    Assert.True(option.IsEnabled);
                    Assert.False(option.IsChecked ?? false);
                    Assert.Same(ProcessFilterSpec.Empty, filtered.ProcessFilterForRun);
                    option.IsChecked = true;
                    Assert.Same(spec, filtered.ProcessFilterForRun);

                    var unavailable = Assert.IsType<CheckBox>(unfiltered.FindName("AnalyzeProcessFilteredSessions"));
                    Assert.False(unavailable.IsEnabled);
                    Assert.Same(ProcessFilterSpec.Empty, unfiltered.ProcessFilterForRun);
                }
                finally
                {
                    filtered.Close();
                    unfiltered.Close();
                }
            });
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private async Task<(AnalysisRunModel Run, ECaptureAnalyzerResult Result, string Output)> RunAsync(
            string capture,
            ProcessFilterSpec spec)
        {
            string outputRoot = Path.Join(_directory, "output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputRoot);
            using var database = new SqLtReadonlyDatabase(capture);
            XboxLiveCaptureAnalyzer analyzer = null;
            AnalysisRunModel run = null;
            Task<ECaptureAnalyzerResult> completion = null;
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController(ProcessCaptures.Device, DeviceType.Unknown, true);
                controller.LoadCaptureDataset(database.TableByName(WebServiceCaptureMethod.TableName).Dataset);
                analyzer = new XboxLiveCaptureAnalyzer(AppContext.BaseDirectory, () => outputRoot);
                analyzer.Initialize(new CaptureAppModel(System.Windows.Application.Current.Dispatcher));
                run = new AnalysisRunModel(analyzer, DateTime.UtcNow.ToFileTimeUtc()) { ProcessFilter = spec };
                completion = analyzer.RunAsync(run, controller);
            });

            try
            {
                ECaptureAnalyzerResult result = await completion.WaitAsync(TimeSpan.FromSeconds(30));
                return (run, result, Path.Join(outputRoot, $"xlca_{run.Id}"));
            }
            finally
            {
                analyzer?.Shutdown();
            }
        }

        private static long[] Counts(JsonElement element) =>
            new[] { "total", "included", "hidden", "unknown" }.Select(name => element.GetProperty(name).GetInt64()).ToArray();
    }
}
