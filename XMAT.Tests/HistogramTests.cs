// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class HistogramTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public HistogramTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        // Counts come largest first, equal ones by label, each with its share of the total. Past seven values, the six
        // largest show with the rest in one row, whose bar is on the same scale, and Show all lists every value.
        [Fact]
        public void Counts_ComeLargestFirstWithTheirShares_ThenTheRestInOneRow()
        {
            InEnglish(() =>
            {
                HistogramModel model = Model(
                    NetworkTraceMeasure.Packets,
                    ("c", 30), ("a", 254), ("b", 30), ("d", 22), ("e", 21), ("f", 19), ("g", 16), ("h", 9), ("i", 3));

                Assert.Equal("404 packets \u00B7 9 sources", model.Totals);
                Assert.Equal("Histogram: 404 packets, 9 sources", model.AccessibleName);
                Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "3 more sources" }, model.Rows.Select(row => row.Label));
                Assert.Equal(new[] { "254", "30", "30", "22", "21", "19", "28" }, model.Rows.Select(row => row.AmountText));
                Assert.Equal(new[] { "63%", "7%", "7%", "5%", "5%", "5%", "7%" }, model.Rows.Select(row => row.ShareText));
                Assert.Equal(new[] { false, false, false, false, false, false, true }, model.Rows.Select(row => row.IsMore));
                Assert.Equal(1, model.Rows[0].Fraction);
                Assert.Equal(28.0 / 254, model.Rows[6].Fraction, 6);
                Assert.Equal("a, 254 packets, 63 percent", model.Rows[0].AccessibleName);
                Assert.Equal("3 more sources, 28 packets, 7 percent", model.Rows[6].AccessibleName);

                Assert.True(model.CanShowAll);
                Assert.Equal("Show all 9 sources", model.ShowAllText);
                Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" }, model.AllRows.Select(row => row.Label));
                Assert.Equal("<1%", model.AllRows[8].ShareText);
                Assert.Equal("i, 3 packets, less than 1 percent", model.AllRows[8].AccessibleName);

                // The rest can outweigh the largest value; then its bar is the longest.
                HistogramModel even = Model(
                    NetworkTraceMeasure.Bytes, Enumerable.Range(0, 20).Select(index => ($"10.0.0.{index}", 1500)).ToArray());
                Assert.Equal("14 more sources", even.Rows[6].Label);
                Assert.Equal("21,000", even.Rows[6].AmountText);
                Assert.Equal(1, even.Rows[6].Fraction);
                Assert.Equal(1500.0 / 21000, even.Rows[0].Fraction, 6);
            });
        }

        // Averages don't add up: the totals say what they measure, and there are no shares or row for the rest.
        [Fact]
        public void Averages_SayWhatTheyMeasure_WithoutSharesOrARowForTheRest()
        {
            InEnglish(() =>
            {
                HistogramModel model = Model(
                    NetworkTraceMeasure.AverageBytes,
                    ("a", 839), ("b", 1435), ("c", 487), ("d", 397), ("e", 313), ("f", 217), ("g", 191), ("h", 94));

                Assert.Equal("Bytes per packet \u00B7 8 sources", model.Totals);
                Assert.Equal(new[] { "b", "a", "c", "d", "e", "f" }, model.Rows.Select(row => row.Label));
                Assert.Equal(8, model.AllRows.Count);
                Assert.All(model.AllRows, row => Assert.Null(row.ShareText));
                Assert.True(model.CanShowAll);
                Assert.Equal("b, 1,435 bytes per packet", model.Rows[0].AccessibleName);
            });
        }

        // Seven values or fewer are all shown, without Show all; a list without values says so.
        [Fact]
        public void FewValues_AreAllShown_AndAnEmptyListSaysSo()
        {
            InEnglish(() =>
            {
                HistogramModel seven = Model(
                    NetworkTraceMeasure.Packets, ("a", 7), ("b", 6), ("c", 5), ("d", 4), ("e", 3), ("f", 2), ("g", 1));
                Assert.Equal(seven.AllRows, seven.Rows);
                Assert.False(seven.CanShowAll);
                Assert.False(seven.IsEmpty);

                HistogramModel none = Model(NetworkTraceMeasure.Packets);
                Assert.True(none.IsEmpty);
                Assert.Empty(none.Rows);
                Assert.Equal("0 packets \u00B7 0 sources", none.Totals);
            });
        }

        // Show all lists every value without moving the lanes, and histograms in a shared size scope give their bars the
        // same start and end, though their labels and amounts differ in width.
        [Fact]
        public void View_ShowsEveryValueOnShowAll_AndLinesUpItsBarsWithTheOthersInItsScope()
        {
            _application.Run(window =>
            {
                window.Resources = NewLook();
                window.Width = 600;
                window.Height = 900;
                var received = new HistogramView
                {
                    DataContext = Model(
                        NetworkTraceMeasure.Packets,
                        ("203.0.113.31", 254), ("198.51.100.10", 89), ("203.0.113.24", 38), ("192.168.1.1", 37),
                        ("203.0.113.36", 22), ("192.0.2.7", 21), ("198.51.100.95", 19), ("192.168.100.200", 16)),
                };
                var protocols = new HistogramView
                {
                    DataContext = Model(NetworkTraceMeasure.Bytes, ("6 (TCP)", 133287), ("17 (UDP)", 5088)),
                };
                var report = new StackPanel { Width = 400, Children = { received, protocols } };
                Grid.SetIsSharedSizeScope(report, true);
                WpfTestHost.Show(window, report);

                Assert.Equal(7, received.Rows.Items.Count);
                Rect lane = BarLane(received, 0, report);
                Assert.Equal(lane.Left, BarLane(protocols, 0, report).Left, 1);
                Assert.Equal(lane.Right, BarLane(protocols, 0, report).Right, 1);
                AutomationPeer rows = UIElementAutomationPeer.CreatePeerForElement(received.Rows);
                Assert.Equal("Histogram: 496 packets, 8 sources", rows.GetName());
                Assert.Equal("203.0.113.31, 254 packets, 51 percent", rows.GetChildren()[0].GetName());

                var showAll = (ToggleButton)received.Template.FindName("PART_ShowAll", received);
                Assert.Equal(Visibility.Visible, showAll.Visibility);
                Assert.Equal("Show all 8 sources", showAll.Content);
                Assert.Equal(
                    Visibility.Collapsed, ((ToggleButton)protocols.Template.FindName("PART_ShowAll", protocols)).Visibility);
                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(showAll).GetPattern(PatternInterface.Toggle))
                    .Toggle();
                WpfTestHost.Pump();

                Assert.True(received.IsExpanded);
                Assert.Equal(8, received.Rows.Items.Count);
                Assert.Equal("Show fewer", showAll.Content);
                Assert.Equal(lane.Left, BarLane(received, 0, report).Left, 1);
                Assert.Equal(lane.Right, BarLane(received, 0, report).Right, 1);
                Assert.Equal(lane.Left, BarLane(received, 7, report).Left, 1);
            });
        }

        private static HistogramModel Model(NetworkTraceMeasure measure, params (string Label, int Amount)[] values) =>
            HistogramModel.Create(
                "Histogram",
                values.Select(value => new HistogramValue(value.Label, value.Amount)),
                NetworkTraceAnalyzerResultsModel.Units(measure, NetworkTraceGrouping.Source));

        private static ResourceDictionary NewLook()
        {
            ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
            ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
            return look;
        }

        // The lane of the bar in a histogram's row INDEX, relative to ANCESTOR.
        private static Rect BarLane(HistogramView view, int index, Visual ancestor)
        {
            var row = (ContentPresenter)view.Rows.ItemContainerGenerator.ContainerFromIndex(index);
            var lane = (FrameworkElement)((Border)row.ContentTemplate.FindName("Bar", row)).Parent;
            return lane.TransformToAncestor(ancestor).TransformBounds(new Rect(lane.RenderSize));
        }

        private static void InEnglish(Action test)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                test();
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }
    }
}
