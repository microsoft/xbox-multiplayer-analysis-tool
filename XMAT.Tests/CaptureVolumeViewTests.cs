// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureTimelineTestData;

namespace XMAT.Tests
{
    // The timeline's Volume view: requests per second in the timeline's well, in the new look only.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureVolumeViewTests
    {
        private const string KeyboardHelp = ". The connections list shows the same requests and works with the keyboard.";
        private readonly CaptureUiApplicationFixture _application;

        public CaptureVolumeViewTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void ChoosingVolume_ReplacesTheBarsInTheSameWellAndRedrawsWithTheTheme()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    var view = new CaptureTimelineView
                    {
                        ItemsSource = new ObservableCollection<ProxyConnectionModel>
                        {
                            Create(1, Origin), Create(2, Origin.AddMilliseconds(400)), Create(3, Origin.AddSeconds(4))
                        }
                    };
                    WpfTestHost.Show(window, view);
                    var waterfall = Mode(view, "WaterfallMode");
                    var byHost = Mode(view, "ByHostMode");
                    var volume = Mode(view, "VolumeMode");
                    var scroller = (ScrollViewer)view.FindName("TimelineScrollViewer");
                    var chart = Chart(view);
                    var modes = (Panel)volume.Parent;
                    Assert.Equal(modes.Children.IndexOf(byHost) + 1, modes.Children.IndexOf(volume));
                    Assert.Equal(waterfall.Margin, byHost.Margin);
                    Assert.True(volume.IsVisible);
                    Assert.Same(scroller.Parent, chart.Parent);
                    Assert.False(chart.IsVisible);
                    int bars = Bars(view).Count();
                    Assert.NotEqual(0, bars);

                    volume.IsChecked = true;
                    WpfTestHost.Pump();

                    Assert.False(scroller.IsVisible);
                    Assert.True(chart.IsVisible);
                    Assert.Empty(Bars(view));
                    Assert.False(view.IsFollowingLiveVolume);
                    Assert.Equal(new[] { 2, 0, 0, 0, 1 }, chart.Totals);
                    Assert.Equal("3 requests \u00B7 peak 2 per second", chart.Readout);
                    Assert.Equal(0, VisualTreeHelper.GetChildrenCount(chart));

                    window.Resources["AccentBrush"] = Brushes.Purple;
                    window.Resources["TimelineAxisFontSize"] = 20.0;
                    WpfTestHost.Pump();
                    Assert.Equal(20, chart.AxisFontSize);
                    GeometryDrawing line = Assert.Single(Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GeometryDrawing>(),
                        drawing => ReferenceEquals(drawing.Pen?.Brush, Brushes.Purple) && drawing.Pen.Thickness == 1.5);
                    Assert.IsType<StreamGeometry>(line.Geometry);
                    Assert.True(line.Geometry.IsFrozen);

                    waterfall.IsChecked = true;
                    WpfTestHost.Pump();
                    Assert.True(scroller.IsVisible);
                    Assert.False(chart.IsVisible);
                    Assert.Equal(bars, Bars(view).Count());
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        [Fact]
        public void OriginalLook_HidesVolumeAndGoesBackToTheWaterfall()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    var view = new CaptureTimelineView
                    {
                        ItemsSource = new ObservableCollection<ProxyConnectionModel> { Create(1, Origin) }
                    };
                    WpfTestHost.Show(window, view);
                    var waterfall = Mode(view, "WaterfallMode");
                    var volume = Mode(view, "VolumeMode");
                    volume.IsChecked = true;
                    WpfTestHost.Pump();
                    Assert.True(Chart(view).IsVisible);

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();

                    Assert.False(volume.IsVisible);
                    Assert.True(waterfall.IsChecked);
                    Assert.False(Chart(view).IsVisible);
                    Assert.Single(Bars(view));
                    // The original look's modes keep their spacing.
                    Assert.Equal(new Thickness(0, 0, 8, 0), waterfall.Margin);
                    Assert.Equal(new Thickness(0), Mode(view, "ByHostMode").Margin);

                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                    Assert.True(volume.IsVisible);
                    Assert.True(waterfall.IsChecked);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        [Fact]
        public void LiveVolume_ShowsTheLastMinuteAndMovesOnEachSecondOnlyWhileShown()
        {
            _application.Run(window =>
            {
                var now = Origin.AddSeconds(100.5);
                var start = CaptureVolumeSeries.SecondOf(Origin);
                var source = new ObservableCollection<ProxyConnectionModel>
                {
                    Create(1, Origin.AddSeconds(30)), Create(2, Origin.AddSeconds(98.2)), Create(3, Origin.AddSeconds(99.1)),
                    Create(4, Origin.AddSeconds(99.6)), Create(5, Origin.AddSeconds(100.1))
                };
                var view = new CaptureTimelineView { ItemsSource = source, IsCaptureRunning = true, Clock = () => now };
                WpfTestHost.Show(window, view);
                Assert.False(view.IsFollowingLiveVolume);
                Mode(view, "VolumeMode").IsChecked = true;
                WpfTestHost.Pump();
                var chart = Chart(view);

                Assert.True(chart.IsLive);
                Assert.True(view.IsFollowingLiveVolume);
                Assert.Equal(new CaptureVolumeSeries.PlotSpan(start + 40, start + 100, 1), chart.Span);
                Assert.Equal((1, 2), (chart.Totals[^2], chart.Totals[^1]));
                Assert.Equal("2 per second", chart.Readout);
                Assert.Equal("2 per second" + KeyboardHelp, AutomationProperties.GetHelpText(chart));

                // A late request in a completed second joins the counts at the next coalesced refresh, without a reset.
                int resets = Layout(view).ResetCount;
                source.Add(Create(6, Origin.AddSeconds(99.3)));
                Assert.True(view.HasPendingRefresh);
                PumpUntil(() => chart.Totals[^1] == 3);
                Assert.Equal(resets, Layout(view).ResetCount);

                now = Origin.AddSeconds(101.5);
                PumpUntil(() => chart.Span.End == start + 101);
                Assert.Equal((3, 1), (chart.Totals[^2], chart.Totals[^1]));
                Assert.Equal("1 per second", chart.Readout);
                Assert.Equal(new[] { "0:20", "0:30", "0:40", "0:50", "1:00" }, chart.TimeLabels);

                chart.SetPointer(chart.PointAt(59));
                Assert.Equal("1:10 \u00B7 1 request", chart.HoverText);
                Assert.True(chart.SelectAt(chart.PointAt(59)));
                Assert.Same(source[4], view.SelectedItem);

                view.Visibility = Visibility.Collapsed;
                WpfTestHost.Pump();
                Assert.False(view.IsFollowingLiveVolume);
                view.Visibility = Visibility.Visible;
                WpfTestHost.Pump();
                Assert.True(view.IsFollowingLiveVolume);
                Mode(view, "ByHostMode").IsChecked = true;
                WpfTestHost.Pump();
                Assert.False(view.IsFollowingLiveVolume);
                Mode(view, "VolumeMode").IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(view.IsFollowingLiveVolume);

                view.IsCaptureRunning = false;
                WpfTestHost.Pump();
                Assert.False(view.IsFollowingLiveVolume);
                Assert.False(chart.IsLive);
                Assert.Equal("6 requests \u00B7 peak 3 per second", chart.Readout);

                // With nothing captured yet, the line runs along zero and has no capture time to show.
                view.IsCaptureRunning = true;
                source.Clear();
                WpfTestHost.Pump();
                Assert.True(view.IsFollowingLiveVolume);
                Assert.Equal("0 per second", chart.Readout);
                Assert.All(chart.Totals, total => Assert.Equal(0, total));
                Assert.Empty(chart.TimeLabels);
                chart.SetPointer(chart.PointAt(59));
                Assert.Equal(-1, chart.HoveredIndex);

                window.Content = null;
                WpfTestHost.Pump();
                Assert.False(view.IsFollowingLiveVolume);
            });
        }

        [Fact]
        public void StoppedVolume_ReadsOutTotalsAndPeaksAndShowsAndSelectsAWiderStep()
        {
            _application.Run(window =>
            {
                CultureInfo culture = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                    var source = new ObservableCollection<ProxyConnectionModel>();
                    var view = new CaptureTimelineView { ItemsSource = source };
                    WpfTestHost.Show(window, view);
                    Mode(view, "VolumeMode").IsChecked = true;
                    WpfTestHost.Pump();
                    var chart = Chart(view);
                    AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(chart);

                    Assert.Equal("No requests", chart.Readout);
                    Assert.Equal("Requests per second", peer.GetName());
                    Assert.Equal(Localization.GetLocalizedString("TIMELINE_VOLUME_AUTOMATION_NAME"), peer.GetName());
                    Assert.False(peer.IsKeyboardFocusable());
                    Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(chart));
                    source.Add(Create(1, Origin));
                    WpfTestHost.Pump();
                    Assert.Equal("1 request \u00B7 peak 1 per second", chart.Readout);

                    // Half an hour, with 52 requests in 2:10 and 4 in each of the next 8 seconds.
                    var busiest = Enumerable.Range(0, 52).Select(index => Create(index + 2, Origin.AddSeconds(130.9 - (index * 0.01))));
                    var following = Enumerable.Range(131, 8).SelectMany(second =>
                        Enumerable.Range(0, 4).Select(index => Create((second * 10) + index, Origin.AddSeconds(second))));
                    var steady = Enumerable.Range(300, 1198).Select(second => Create(second + 10000, Origin.AddSeconds(second)));
                    foreach (var model in busiest.Concat(following).Concat(steady).Append(Create(99999, Origin.AddSeconds(1799))))
                    {
                        source.Add(model);
                    }
                    WpfTestHost.Pump();

                    Assert.Equal("1,284 requests \u00B7 peak 52 per second", chart.Readout);
                    Assert.Equal(chart.Readout + KeyboardHelp, peer.GetHelpText());
                    Assert.Equal((10L, 180), (chart.Span.Step, chart.Span.Count));
                    Assert.Equal(10, chart.TopValue);
                    chart.SetPointer(chart.PointAt(13));
                    Assert.Equal(13, chart.HoveredIndex);
                    Assert.Equal("2:10\u20132:20 \u00B7 84 requests", chart.HoverText);
                    Assert.Same(Cursors.Hand, chart.Cursor);
                    Assert.True(chart.SelectAt(chart.PointAt(13)));
                    Assert.Same(source[52], view.SelectedItem);

                    chart.SetPointer(chart.PointAt(160));
                    Assert.Equal("26:40\u201326:50 \u00B7 0 requests", chart.HoverText);
                    Assert.False(chart.SelectAt(chart.PointAt(160)));
                    Assert.Same(source[52], view.SelectedItem);
                    chart.SetPointer(null);
                    Assert.Equal(-1, chart.HoveredIndex);
                }
                finally
                {
                    CultureInfo.CurrentCulture = culture;
                }
            });
        }

        private static RadioButton Mode(CaptureTimelineView view, string name) => (RadioButton)view.FindName(name);

        private static CaptureVolumeChart Chart(CaptureTimelineView view) => (CaptureVolumeChart)view.FindName("VolumeChart");

        private static IEnumerable<Border> Bars(CaptureTimelineView view) =>
            ((Canvas)view.FindName("TimelineCanvas")).Children.OfType<Border>();

        private static CaptureTimelineLayout Layout(CaptureTimelineView view) =>
            (CaptureTimelineLayout)typeof(CaptureTimelineView)
                .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(view);

        private static IEnumerable<Drawing> Drawings(Drawing drawing) =>
            drawing is DrawingGroup group ? group.Children.SelectMany(Drawings) : new[] { drawing };

        // Lets timers run until CONDITION holds, for up to five seconds.
        private static void PumpUntil(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                var frame = new DispatcherFrame();
                var wait = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
                wait.Tick += (sender, args) =>
                {
                    wait.Stop();
                    frame.Continue = false;
                };
                wait.Start();
                Dispatcher.PushFrame(frame);
            }
            Assert.True(condition(), "The timeline did not refresh in time.");
        }
    }
}
