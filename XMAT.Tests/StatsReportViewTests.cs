// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.Tests
{
    // StatsReportView loads XAML through the process-wide WPF Application, so these
    // tests share the capture UI fixture's application instead of their own thread.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class StatsReportViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public StatsReportViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        public static TheoryData<string> DetachedContexts => new()
        {
            "null",
            "otherReport",
        };

        [Theory]
        [MemberData(nameof(DetachedContexts))]
        public void LeavingStatsReportContext_KeepsCurrentReportSource(string detachedContext)
        {
            _application.Run(_ =>
            {
                var model = CreateStatsReportModel(@"C:\synthetic\first");
                var view = new StatsReportView { DataContext = model };
                WpfTestHost.Pump();
                var webView = GetWebView(view);
                Assert.Equal(new Uri(model.ReportUrl), webView.Source);

                view.DataContext = detachedContext == "null" ? null : new object();
                WpfTestHost.Pump();

                Assert.Equal(new Uri(model.ReportUrl), webView.Source);
            });
        }

        [Fact]
        public void ChangingToAnotherStatsReport_UpdatesSource()
        {
            _application.Run(_ =>
            {
                var first = CreateStatsReportModel(@"C:\synthetic\first");
                var second = CreateStatsReportModel(@"C:\synthetic\second");
                var view = new StatsReportView { DataContext = first };
                WpfTestHost.Pump();

                view.DataContext = null;
                view.DataContext = second;
                WpfTestHost.Pump();

                Assert.Equal(new Uri(second.ReportUrl), GetWebView(view).Source);
            });
        }

        // The real constructor copies WPF pack resources, which requires the product
        // to be the process entry assembly; only the report URL matters to the view.
        private static StatsReportModel CreateStatsReportModel(string analysisDirectory)
        {
            var model = (StatsReportModel)RuntimeHelpers.GetUninitializedObject(typeof(StatsReportModel));
            typeof(StatsReportModel)
                .GetField("<ReportUrl>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(model, $"file:///{analysisDirectory}/index.html");
            return model;
        }

        private static WebView2 GetWebView(StatsReportView view)
        {
            return Assert.IsType<WebView2>(view.FindName("webView"));
        }
    }
}
