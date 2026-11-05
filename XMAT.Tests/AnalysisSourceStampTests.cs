// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.Tests
{
    // An analysis keeps the stamp of the capture it read. Its session links open that tab only while the tab still holds
    // the analyzed sessions: the stamp, never the capture's name, is resolved before any tab is selected. Once sessions
    // are deleted or cleared, or the tab is closed, the links are disabled and the results say they are historical.
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
                var report = new PerEndpointReportModel(app, source.DeviceName, AnalysisHostIdentityTests.CreateInGameEventsReport());
                report.SourceStamp = analyzed;
                var links = new PerEndpointReportView { DataContext = report };
                var results = new AnalysisResultsModel { SourceStamp = analyzed };
                app.AnalysisRuns.Add(new AnalysisRunModel(null, 1) { AnalysisData = results });
                var reports = new AnalysisReportsView { DataContext = results };
                WpfTestHost.Show(window, new StackPanel { Children = { links, reports } });
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
    }
}
