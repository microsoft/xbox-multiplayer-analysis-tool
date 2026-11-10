// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations.Captures;
using XMAT.SharedInterfaces;
using XMAT.Tests.Cli;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.Tests
{
    // An analysis keeps the stamp of the capture it read. Its session links open that tab only while the tab still holds
    // the analyzed sessions: the stamp, never the capture's name, is resolved before any tab is selected. Once sessions
    // are deleted, cleared or reloaded, or the tab is closed, the links are disabled and the results say they are
    // historical.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class AnalysisSourceStampTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public AnalysisSourceStampTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Fact]
        public void SessionLinks_ResolveTheStampNotTheName_AndAChangedSourceMakesTheAnalysisHistorical()
        {
            _application.Run(window =>
            {
                var app = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                using var twin = new LiveCaptureTab(app);
                twin.Add(1);
                using var source = new LiveCaptureTab(app);
                ProxyConnectionModel first = source.Add(1), second = source.Add(2);
                CaptureSourceStamp analyzed = source.Controller.SourceStamp;
                (PerEndpointReportView links, AnalysisReportsView reports, AnalysisResultsModel results) =
                    ShowAnalysis(window, app, source.DeviceName, analyzed);
                app.SelectedDeviceContext = twin;

                Assert.Same(source, app.ResolveCaptureSource(analyzed));
                CaptureAnalysisCommands.GoToId.Execute("1", links);
                Assert.Same(source, app.SelectedDeviceContext);
                Assert.Same(first, source.Controller.SelectedConnectionModel);
                var banner = Assert.IsType<TextBlock>(reports.FindName("SourceStaleBanner"));
                Assert.NotEqual(Visibility.Visible, banner.Visibility);

                app.SelectedDeviceContext = twin;
                var target = new CaptureDeleteTarget(second.RowKey, new WeakReference<ProxyConnectionModel>(second));
                Assert.True(app.TryDeleteSessions(
                    source.Controller, new[] { target }, new HashSet<CaptureRowKey> { second.RowKey }, out CaptureDeleteResult result));
                WpfTestHost.Pump();
                Assert.Equal(new[] { second.RowKey }, result.Deleted);
                Assert.Null(app.ResolveCaptureSource(analyzed));
                Assert.False(CaptureAnalysisCommands.GoToId.CanExecute("1", links));
                CaptureAnalysisCommands.GoToId.Execute("1", links);
                Assert.Same(twin, app.SelectedDeviceContext);
                Assert.True(results.IsSourceStale);
                Assert.Equal(Visibility.Visible, banner.Visibility);
                Assert.Equal(Localization.GetLocalizedString("ANALYSIS_SOURCE_STALE"), banner.Text);

                CaptureSourceStamp current = source.Controller.SourceStamp;
                Assert.True(app.TryClearAllCaptures(source.Controller));
                Assert.Null(app.ResolveCaptureSource(current));
                current = source.Controller.SourceStamp;
                Assert.Same(source, app.ResolveCaptureSource(current));
                app.CaptureDeviceContexts.Remove(source);
                Assert.Null(app.ResolveCaptureSource(current));
            });
        }

        [Theory]
        [InlineData("2", false)]
        [InlineData("99", false)]
        [InlineData("x", false)]
        [InlineData("1", true)]
        public void SessionLinks_RequireAUniqueRequestIdBeforeSelectingTheStampedSource(
            string parameter,
            bool expected)
        {
            _application.Run(window =>
            {
                var app = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                using var twin = new LiveCaptureTab(app, "Other tab");
                twin.Add(1);
                using var source = new LiveCaptureTab(app, "Analyzed tab");
                ProxyConnectionModel first = source.Add(1);
                ProxyConnectionModel selected = source.Add(2);
                CaptureSourceStamp analyzed = source.Controller.SourceStamp;
                _ = source.Add(2);
                Assert.Equal(analyzed, source.Controller.SourceStamp);
                Assert.Null(source.Controller.ProxyConnections.ById(2));
                PerEndpointReportView links =
                    ShowAnalysis(window, app, source.DeviceName, analyzed).Links;
                source.Controller.SelectedConnectionModel = selected;
                app.SelectedDeviceContext = twin;

                Assert.Equal(expected, CaptureAnalysisCommands.GoToId.CanExecute(parameter, links));
                CaptureAnalysisCommands.GoToId.Execute(parameter, links);
                WpfTestHost.Pump();

                Assert.Same(expected ? source : twin, app.SelectedDeviceContext);
                Assert.Same(expected ? first : selected, source.Controller.SelectedConnectionModel);
            });
        }

        // B3: opening the analyzed capture file again replaces the sessions of its tab. The analysis becomes historical, and
        // its session links neither resolve nor select a tab.
        [Fact]
        public void ReloadingTheAnalyzedCapture_MakesTheAnalysisHistorical_AndItsLinksSelectNoTab()
        {
            using var directory = new CliTestDirectory();
            string path = ProcessCaptures.WriteXmatcap(directory.Root, "analyzed.xmatcap", ProcessCaptures.MixedSessions);
            _application.Run(window =>
            {
                var app = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                using var current = new LiveCaptureTab(app, "Current tab");
                app.LoadDataCaptures(path);
                ICaptureDeviceContext loaded = Assert.Single(app.CaptureDeviceContexts, context => context.IsReadOnly);
                try
                {
                    CaptureSourceStamp analyzed = ((WebServiceDeviceCaptureController)loaded.CaptureController).SourceStamp;
                    (PerEndpointReportView links, AnalysisReportsView reports, AnalysisResultsModel results) =
                        ShowAnalysis(window, app, loaded.DeviceName, analyzed);
                    Assert.Same(loaded, app.ResolveCaptureSource(analyzed));
                    app.SelectedDeviceContext = current;

                    app.LoadDataCaptures(path);
                    WpfTestHost.Pump();

                    Assert.Same(loaded, Assert.Single(app.CaptureDeviceContexts, context => context.IsReadOnly));
                    Assert.Null(app.ResolveCaptureSource(analyzed));
                    Assert.True(results.IsSourceStale);
                    Assert.Equal(Visibility.Visible, ((TextBlock)reports.FindName("SourceStaleBanner")).Visibility);
                    Assert.False(CaptureAnalysisCommands.GoToId.CanExecute("1", links));
                    CaptureAnalysisCommands.GoToId.Execute("1", links);
                    Assert.Same(current, app.SelectedDeviceContext);
                }
                finally
                {
                    app.CaptureDeviceContexts.Remove(loaded);
                    LoadedCaptureDatabaseRegistry.Release(loaded.CaptureController);
                }
            });
        }

        // R5: session links without an analyzed source (no report, a report without the capture application, or one whose
        // stamp names no capture) can't execute, and executing them does nothing.
        [Fact]
        public void SessionLinks_WithoutAnAnalyzedSource_CannotExecuteAndDoNothing()
        {
            _application.Run(window =>
            {
                var app = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                var links = new PerEndpointReportView();
                WpfTestHost.Show(window, links);
                foreach (object context in new object[]
                {
                    null,
                    new PerEndpointReportModel(null, "detached", AnalysisHostIdentityTests.CreateInGameEventsReport()),
                    new PerEndpointReportModel(app, "unstamped", AnalysisHostIdentityTests.CreateInGameEventsReport()),
                })
                {
                    links.DataContext = context;
                    Assert.False(CaptureAnalysisCommands.GoToId.CanExecute("1", links));
                    CaptureAnalysisCommands.GoToId.Execute("1", links);
                }

                Assert.Null(app.SelectedDeviceContext);
            });
        }

        // Shows the session links and the results of an analysis of the capture NAME that STAMP identifies.
        private static (PerEndpointReportView Links, AnalysisReportsView Reports, AnalysisResultsModel Results) ShowAnalysis(
            Window window, CaptureAppModel app, string name, CaptureSourceStamp stamp)
        {
            var report = new PerEndpointReportModel(app, name, AnalysisHostIdentityTests.CreateInGameEventsReport())
            {
                SourceStamp = stamp,
            };
            var results = new AnalysisResultsModel { SourceStamp = stamp };
            app.AnalysisRuns.Add(new AnalysisRunModel(null, 1) { AnalysisData = results });
            var links = new PerEndpointReportView { DataContext = report };
            var reports = new AnalysisReportsView { DataContext = results };
            WpfTestHost.Show(window, new StackPanel { Children = { links, reports } });
            return (links, reports, results);
        }
    }
}
