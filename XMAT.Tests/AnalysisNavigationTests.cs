// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.Tests
{
    // Going from an analysis report to a request goes to the tab that was analyzed, kept by reference, never to a tab found
    // by its name: a live tab and a tab loaded from a capture can have the same name and the same request numbers, with
    // other traffic. Every capture here is synthetic.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AnalysisNavigationTests
    {
        // The name of the device that SyntheticCaptures writes, and of the live tab that shares it.
        private const string DeviceName = "Synthetic device 1";

        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly CaptureUiApplicationFixture _application;

        public AnalysisNavigationTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static AnalysisNavigationTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The analyzer carries the analyzed tab into its results and reports. Going to a request from its report selects
        // the loaded tab that was analyzed, and that tab's row, though a live tab with the same name and request numbers
        // comes first among the tabs.
        [Fact]
        public Task Analysis_GoesToTheAnalyzedTab_NotTheFirstTabWithItsName() => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            string outputRoot = Path.Join(directory, "analysis");
            using SqLtDatabase devices = CreateDevicesDatabase(out IDataTable devicesTable);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { CaptureDevicesTable = devicesTable };
            var analyzer = new XboxLiveCaptureAnalyzer(AppContext.BaseDirectory, () => outputRoot);
            try
            {
                ICaptureDeviceContext live = AddLiveTab(model);
                ICaptureDeviceContext loaded = Load(model, SyntheticCaptures.WriteXmatcap(directory, "capture.xmatcap"));
                var loadedController = (WebServiceDeviceCaptureController)loaded.CaptureController;
                analyzer.Initialize(model);
                AnalysisRunModel run = model.AddAnalysisRun(analyzer);

                Assert.Equal(
                    ECaptureAnalyzerResult.Success,
                    await model.RunAnalysisAsync(run, loadedController).WaitAsync(TimeSpan.FromSeconds(30)));

                var results = Assert.IsType<AnalysisResultsModel>(run.AnalysisData);
                Assert.Same(loadedController, results.Source.Controller);
                Assert.Equal(DeviceName, results.SourceCaptureName);
                Assert.NotEmpty(results.Reports);
                Assert.All(results.Reports, report => Assert.Same(results.Source, report.Source));
                PerEndpointReportModel report = Assert.Single(results.Reports.OfType<PerEndpointReportModel>());
                model.SelectedDeviceContext = live;
                var view = new PerEndpointReportView
                {
                    DataContext = report,
                    ShowNavigationWarning = message => Assert.Fail(message),
                };

                CaptureAnalysisCommands.GoToId.Execute(2u, view);

                Assert.Same(loaded, model.SelectedDeviceContext);
                ProxyConnectionModel selected = loadedController.SelectedConnectionModel;
                Assert.Same(Row(loaded, 2), selected);
                Assert.NotEqual(LiveResponse(2), selected.ResponseBody);
                Assert.Null(Controller(live).SelectedConnectionModel);
            }
            finally
            {
                analyzer.Shutdown();
                await RemoveAllAsync(model);
                TestFolderCleanup.Delete(directory);
            }
        });

        // Whichever of a live tab and a loaded tab with the same name and request numbers a report analyzed, and though it
        // comes second among the tabs, going to a request selects that tab and its row.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task Report_GoesToTheAnalyzedOfTwoTabsWithTheSameNameAndNumbers(bool analyzedLive) => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using SqLtDatabase devices = CreateDevicesDatabase(out IDataTable devicesTable);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { CaptureDevicesTable = devicesTable };
            try
            {
                string capture = SyntheticCaptures.WriteXmatcap(directory, "capture.xmatcap");
                ICaptureDeviceContext live;
                ICaptureDeviceContext loaded;
                if (analyzedLive)
                {
                    loaded = Load(model, capture);
                    live = AddLiveTab(model);
                }
                else
                {
                    live = AddLiveTab(model);
                    loaded = Load(model, capture);
                }

                ICaptureDeviceContext analyzed = analyzedLive ? live : loaded;
                ICaptureDeviceContext other = analyzedLive ? loaded : live;
                Assert.Same(analyzed, model.CaptureDeviceContexts[1]);
                PerEndpointReportView view = ReportView(model, analyzed, message => Assert.Fail(message));
                model.SelectedDeviceContext = other;

                CaptureAnalysisCommands.GoToId.Execute("3", view);

                Assert.Same(analyzed, model.SelectedDeviceContext);
                Assert.Same(Row(analyzed, 3), Controller(analyzed).SelectedConnectionModel);
                Assert.Null(Controller(other).SelectedConnectionModel);
                Assert.Equal(analyzedLive, LiveResponse(3).SequenceEqual(Controller(analyzed).SelectedConnectionModel.ResponseBody));
            }
            finally
            {
                await RemoveAllAsync(model);
                TestFolderCleanup.Delete(directory);
            }
        });

        // Once the analyzed tab is closed, or shows other rows than the analyzed ones because a capture with the same device
        // was loaded into it, going to a request selects nothing, not even a request with that number in a tab with that
        // name, and says why.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task Report_SelectsNothing_OnceItsTabIsClosedOrShowsOtherRows(bool closed) => _application.RunAsync(async () =>
        {
            string directory = FixtureDirectory();
            using SqLtDatabase devices = CreateDevicesDatabase(out IDataTable devicesTable);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { CaptureDevicesTable = devicesTable };
            try
            {
                ICaptureDeviceContext live = AddLiveTab(model);
                ICaptureDeviceContext loaded = Load(model, SyntheticCaptures.WriteXmatcap(directory, "capture.xmatcap"));
                var warnings = new List<string>();
                PerEndpointReportView view = ReportView(model, loaded, warnings.Add);
                if (closed)
                {
                    await model.RemoveDeviceContextAsync(loaded);
                }
                else
                {
                    model.LoadDataCaptures(SyntheticCaptures.WriteXmatcap(
                        directory, "other.xmatcap", new SyntheticXmatcapOptions(IncludeCanaries: true)));
                    Assert.Contains(loaded, model.CaptureDeviceContexts);
                    Assert.NotNull(Row(loaded, 2));
                }

                model.SelectedDeviceContext = live;

                CaptureAnalysisCommands.GoToId.Execute("2", view);

                Assert.Equal(
                    Localization.GetLocalizedString(
                        closed ? "LTA_GOTO_REQUEST_SOURCE_CLOSED" : "LTA_GOTO_REQUEST_SOURCE_CHANGED", 2L, DeviceName),
                    Assert.Single(warnings));
                Assert.Same(live, model.SelectedDeviceContext);
                Assert.All(model.CaptureDeviceContexts, context => Assert.Null(Controller(context).SelectedConnectionModel));
            }
            finally
            {
                await RemoveAllAsync(model);
                TestFolderCleanup.Delete(directory);
            }
        });

        // A live console capture with the synthetic capture's device name and request numbers but other responses, as its
        // proxy captured them, with the proxy not started.
        private static ICaptureDeviceContext AddLiveTab(CaptureAppModel model)
        {
            var live = new CaptureDeviceContextModel(
                DeviceType.XboxConsole,
                DeviceName,
                CaptureType.WebProxy,
                false,
                null,
                deferPortAllocation: true,
                hostListPolicy: model.HostListPolicy,
                proxyFactory: () => new WebServiceProxy());
            var controller = (WebServiceDeviceCaptureController)live.CaptureController;
            for (long number = 1; number <= 4; number++)
            {
                controller.ProxyConnections.Add(new ProxyConnectionModel(
                    number,
                    "200",
                    Start.AddSeconds(number),
                    TimeSpan.FromMilliseconds(25),
                    "https",
                    "userpresence.xboxlive.com:443",
                    "POST",
                    "/live",
                    "POST /live HTTP/1.1\r\nHost: userpresence.xboxlive.com\r\n\r\n",
                    [],
                    "HTTP/1.1 200 OK\r\n\r\n",
                    LiveResponse(number),
                    "10.0.0.9"));
            }

            model.CaptureDeviceContexts.Add(live);
            model.CaptureDevicesTable.AddRow((long)DeviceType.XboxConsole, DeviceName, (long)CaptureType.WebProxy);
            return live;
        }

        private static byte[] LiveResponse(long number) => Encoding.UTF8.GetBytes($"live response {number}");

        // Loads a synthetic capture, whose one device becomes a new read-only tab.
        private static ICaptureDeviceContext Load(CaptureAppModel model, string path)
        {
            model.LoadDataCaptures(path);
            ICaptureDeviceContext loaded = Assert.Single(model.CaptureDeviceContexts, context => context.IsReadOnly);
            Assert.Equal(DeviceName, loaded.DeviceName);
            return loaded;
        }

        // A Per Endpoint report of the analyzed tab, as the analyzer makes it, in its view.
        private static PerEndpointReportView ReportView(
            CaptureAppModel model, ICaptureDeviceContext analyzed, Action<string> showNavigationWarning) =>
            new()
            {
                DataContext = new PerEndpointReportModel(
                    model,
                    DeviceName,
                    AnalysisHostIdentityTests.CreateInGameEventsReport(),
                    new AnalysisSource(Controller(analyzed))),
                ShowNavigationWarning = showNavigationWarning,
            };

        private static WebServiceDeviceCaptureController Controller(ICaptureDeviceContext context) =>
            (WebServiceDeviceCaptureController)context.CaptureController;

        private static ProxyConnectionModel Row(ICaptureDeviceContext context, long number) =>
            Controller(context).ProxyConnections.Single(row => row.RequestNumber == number);

        // The capture devices table that a model lists writable tabs in, so that they can be closed.
        private static SqLtDatabase CreateDevicesDatabase(out IDataTable devicesTable)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            devicesTable = database.CreateTable(
                "CaptureDevices",
                new Field<long>("DeviceType"),
                new Field<string>("DeviceName"),
                new Field<long>("CaptureType"));
            return database;
        }

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "AnalysisNavigationFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }
    }
}
