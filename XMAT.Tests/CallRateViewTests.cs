// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CallRateViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CallRateViewTests(CaptureUiApplicationFixture application) => _application = application;

        // The Per Endpoint report shows the sample's call rates in the new look: a sentence for each row, badges that name
        // the analyzer's levels, theme brushes on the charts, a readout on hover and a switch to the sustained window.
        [Fact]
        public void Report_ChartsEachEndpointsCallRateInTheNewLook()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    window.Width = 1200;
                    window.Height = 1000;
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AnalysisCharts });
                    var model = new PerEndpointReportModel(new CaptureAppModel(), "sample", CallRateTests.SampleReport());
                    var report = new PerEndpointReportView { DataContext = model };
                    WpfTestHost.Show(window, report);

                    CallRateView view = Descendants(report).OfType<CallRateView>().Single();
                    var section = (Grid)view.FindName("Section");
                    Assert.Equal(Visibility.Visible, section.Visibility);
                    var rows = (ItemsControl)view.FindName("Rows");
                    List<string> sentences = UIElementAutomationPeer.CreatePeerForElement(rows).GetChildren()
                        .Select(row => row.GetName()).ToList();
                    Assert.Equal(model.CallRates.Rows.Select(row => row.Sentence), sentences);
                    Assert.Contains(
                        "profile.xboxlive.com went over its burst limit once; the most was 12 calls in 15 seconds, " +
                        "against a limit of 10.",
                        sentences);

                    CallRateChart statsWrite = Chart(view, "statswrite.xboxlive.com");
                    CallRateChart profile = Chart(view, "profile.xboxlive.com");
                    Assert.Same(profile.FindResource("TextPrimaryBrush"), profile.Stroke);
                    Assert.Same(profile.FindResource("ErrorTextBrush"), profile.OverLimitStroke);
                    Assert.Same(profile.FindResource("TextSecondaryBrush"), profile.GuideBrush);
                    Assert.True(profile.Plot.Line.IsFrozen && profile.Plot.OverLimit.IsFrozen && profile.Plot.Shade.IsFrozen);
                    Assert.False(profile.Plot.OverLimit.Bounds.IsEmpty);
                    Assert.False(profile.Plot.Shade.Bounds.IsEmpty);
                    Assert.True(profile.Plot.OverLimit.Bounds.Bottom <= profile.Plot.LimitY + 1.5);
                    Assert.Equal(0.5, profile.Plot.LimitY % 1);
                    Assert.True(statsWrite.Plot.OverLimit.Bounds.IsEmpty);
                    AssertBadge(Row(profile), "1 warning", "Warning");
                    Assert.Equal("None", Badge(Row(statsWrite)).Content);

                    var tip = (Border)view.FindName("Tip");
                    profile.ShowSample(44);
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Visible, tip.Visibility);
                    Assert.Equal("0:44 \u00b7 12 calls in 15 s \u00b7 limit 10", ((TextBlock)view.FindName("TipText")).Text);
                    Point below = profile.TranslatePoint(new Point(profile.HoverX, profile.PlotHeight), tip);
                    Assert.InRange(below.X, 0, tip.ActualWidth);
                    profile.ShowSample(-1);
                    Assert.Equal(Visibility.Collapsed, tip.Visibility);

                    var sustained = (RadioButton)view.FindName("SustainedWindow");
                    var select = (ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(sustained)
                        .GetPattern(PatternInterface.SelectionItem);
                    select.Select();
                    WpfTestHost.Pump();

                    Assert.Equal(CallRateWindowKind.Sustained, model.CallRates.Window);
                    Assert.Equal("10 / 5 min", Descendants(Row(statsWrite)).OfType<CallRateLimitLabel>().Single().Text);
                    Assert.False(statsWrite.Plot.OverLimit.Bounds.IsEmpty);
                    AssertBadge(Row(statsWrite), "1 error", "Error");
                    Assert.Contains(
                        "statswrite.xboxlive.com went over its sustained limit once, an error;",
                        UIElementAutomationPeer.CreatePeerForElement(rows).GetChildren().Select(row => row.GetName()).First());

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Collapsed, section.Visibility);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        // The table fills its width: the Call rate column takes what Endpoint (190 px), Calls and Over limit leave, but
        // at least 200 px, and the charts and their time axis stretch with it. Once the Call rate column is at that
        // minimum, the Endpoint column gives way, down to 160 px, and trims names with an ellipsis.
        [Theory]
        [InlineData(900, 190, 564)]
        [InlineData(520, 174, 200)]
        [InlineData(480, 160, 200)]
        public void Table_FillsItsWidthAndTheEndpointColumnGivesWayWhenNarrow(double width, double endpoint, double callRate)
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    window.Width = 1000;
                    window.Height = 1000;
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AnalysisCharts });
                    var view = new CallRateView
                    {
                        Width = width,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        DataContext = CallRateReport.From(CallRateTests.SampleReport(
                            CallRateTests.Call(900, "sessiondirectory.xboxlive.com", "/handles", "POST", 27000)))
                    };
                    WpfTestHost.Show(window, view);

                    ColumnDefinitionCollection columns = ((Grid)view.FindName("HeaderColumns")).ColumnDefinitions;
                    Assert.Equal((endpoint, callRate), (columns[0].ActualWidth, columns[3].ActualWidth));
                    var axis = (CallRateAxis)view.FindName("Axis");
                    List<CallRateChart> charts = Descendants(view).OfType<CallRateChart>().ToList();
                    double label = Descendants(view).OfType<CallRateLimitLabel>().First().ActualWidth;
                    Assert.Equal(5, charts.Count);
                    Assert.All(charts, chart => Assert.Equal(axis.ActualWidth, chart.ActualWidth));
                    Assert.Equal(callRate - 7 - label, axis.ActualWidth, 3);

                    TextBlock name = Descendants(view).OfType<TextBlock>().Single(text => text.Text == "sessiondirectory.xboxlive.com");
                    double natural = new FormattedText(
                        name.Text,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch),
                        name.FontSize,
                        Brushes.Black,
                        VisualTreeHelper.GetDpi(name).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
                    Assert.Equal(natural > endpoint - 11, name.ActualWidth < natural);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        private static Uri AnalysisCharts { get; } =
            new("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/AnalysisCharts.xaml");

        private static CallRateChart Chart(CallRateView view, string endpoint) =>
            Descendants(view).OfType<CallRateChart>().Single(chart => ((CallRateRow)chart.DataContext).Endpoint == endpoint);

        // The row a chart is in.
        private static Grid Row(CallRateChart chart) =>
            (Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(chart));

        private static ContentControl Badge(Grid row) => Descendants(row).OfType<ContentControl>().Single();

        // The badge names its count and level in its level's status colors, and screen readers read the same text.
        private static void AssertBadge(Grid row, string text, string role)
        {
            ContentControl badge = Badge(row);
            Assert.Equal(text, badge.Content);
            Assert.Same(badge.FindResource($"{role}SoftBrush"), badge.Background);
            Assert.Same(badge.FindResource($"{role}TextBrush"), badge.Foreground);
            Assert.Equal(text, AutomationProperties.GetName(badge));
            TextBlock shown = Descendants(badge).OfType<TextBlock>().Single();
            Assert.Equal(text, UIElementAutomationPeer.CreatePeerForElement(shown).GetName());
            Assert.Null(badge.ToolTip);
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
