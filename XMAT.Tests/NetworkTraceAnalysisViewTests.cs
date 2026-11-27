// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkTraceAnalysisViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public NetworkTraceAnalysisViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        // The new look leads with the checks and how many packets they scanned, then shows each list as a histogram, in
        // the analyzer's order, with every bar starting at the same place. The original look keeps its tables.
        [Fact]
        public void NewLook_ShowsTheChecksThenEachListAsAHistogram_WithTheirBarsLinedUp()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                CultureInfo culture = CultureInfo.CurrentCulture;
                try
                {
                    LookState.Current.IsNewLook = true;
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                    ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
                    ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
                    window.Resources = look;
                    window.Width = 1200;
                    window.Height = 900;
                    var view = new NetworkTraceAnalysisView { DataContext = new { AnalysisData = Analysis() } };
                    WpfTestHost.Show(window, view);

                    Assert.Equal("1,280 packets scanned", ((TextBlock)view.FindName("ScannedText")).Text);
                    var checks = (ItemsControl)view.FindName("Checks");
                    Assert.Equal(
                        new[] { "UDP Packets over MTU (1384): 2", "Packets Sent Over Threshold (20): 1,284" },
                        UIElementAutomationPeer.CreatePeerForElement(checks).GetChildren().Select(check => check.GetName()));
                    var busiest = (ContentPresenter)checks.ItemContainerGenerator.ContainerFromIndex(1);
                    Assert.Equal("1,284", ((ContentControl)busiest.ContentTemplate.FindName("Count", busiest)).Content);
                    List<HistogramView> histograms = Descendants(view).OfType<HistogramView>().ToList();
                    Assert.Equal(
                        new[] { "Packet Count by Protocol", "Packets Sent by Source", "Average Packet Length by Source" },
                        histograms.Select(histogram => histogram.Model.Title));
                    Assert.Equal("1,280 packets \u00B7 2 protocols", histograms[0].Model.Totals);
                    Assert.Equal("Bytes per packet \u00B7 2 sources", histograms[2].Model.Totals);
                    double start = BarStart(histograms[0]);
                    Assert.All(histograms, histogram => Assert.Equal(start, BarStart(histogram), 1));
                    Assert.Empty(Descendants(view).OfType<DataGrid>());

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("Report")).Visibility);
                    Assert.Equal(3, Descendants(view).OfType<DataGrid>().Count(grid => grid.IsVisible));
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                    CultureInfo.CurrentCulture = culture;
                }
            });
        }

        // Protocols have short names and sources long ones, so only shared lanes start their bars at the same place.
        private static NetworkTraceAnalyzerModel Analysis()
        {
            var analysis = new NetworkTraceAnalyzerModel { TotalPacketsScanned = 1280 };
            analysis.NumericStatsLists.Add(List(
                "Packet Count by Protocol", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Protocol,
                ("6 (TCP)", 684), ("17 (UDP)", 596)));
            analysis.NumericStatsLists.Add(List(
                "Packets Sent by Source", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source,
                ("192.168.1.20", 582), ("203.0.113.31", 14)));
            analysis.NumericStatsLists.Add(List(
                "Average Packet Length by Source", NetworkTraceMeasure.AverageBytes, NetworkTraceGrouping.Source,
                ("198.51.100.111", 1428), ("192.168.1.20", 229)));
            analysis.NumericStats.Add(new NetworkTraceAnalyzerResultsDataModel { Name = "UDP Packets over MTU (1384)", Value = 2 });
            analysis.NumericStats.Add(new NetworkTraceAnalyzerResultsDataModel { Name = "Packets Sent Over Threshold (20)", Value = 1284 });
            return analysis;
        }

        private static NetworkTraceAnalyzerResultsModel List(
            string topic, NetworkTraceMeasure measure, NetworkTraceGrouping groupedBy, params (string Name, int Value)[] values) =>
            new()
            {
                Topic = topic,
                Measure = measure,
                GroupedBy = groupedBy,
                Values = values.Select(value => new NetworkTraceAnalyzerResultsDataModel { Name = value.Name, Value = value.Value }),
            };

        // Where the bar lane of a histogram's first row starts, from the histogram's left edge.
        private static double BarStart(HistogramView histogram)
        {
            var row = (ContentPresenter)histogram.Rows.ItemContainerGenerator.ContainerFromIndex(0);
            var lane = (FrameworkElement)((Border)row.ContentTemplate.FindName("Bar", row)).Parent;
            return lane.TransformToAncestor(histogram).Transform(new Point()).X;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
        {
            yield return source;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
            {
                foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(source, index)))
                {
                    yield return child;
                }
            }
        }
    }
}
