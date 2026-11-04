// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureTimelineTestData;

namespace XMAT.Tests
{
    public class CaptureTimelineViewTests
    {
        [Fact]
        public void UnmeasuredTimeline_DoesNotConstructHistoricalVisualsOrSubscribe()
        {
            WpfTestHost.Run(window =>
            {
                var model = Create();
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { model }
                };

                Assert.Empty(Canvas(view, "TimelineCanvas").Children);
                Assert.Empty(Canvas(view, "TimeAxisCanvas").Children);
                Assert.False(view.HasPendingRefresh);
                Assert.Equal(0, HandlerCount(model));
            });
        }

        [Fact]
        public void CaptureBurst_IsCoalescedAndFinalStopBypassesTheLiveTimer()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 1000).Select(index => Create(index)));
                var view = new CaptureTimelineView { ItemsSource = source, IsCaptureRunning = true };
                WpfTestHost.Show(window, view);
                var initialRenders = view.RenderCount;
                for (var index = 1001; index <= 1050; index++)
                {
                    source.Add(Create(index));
                }

                Assert.Equal(initialRenders, view.RenderCount);
                Assert.True(view.HasPendingRefresh);
                var timer = (DispatcherTimer)typeof(CaptureTimelineView)
                    .GetField("_refreshTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                Assert.Equal(TimeSpan.FromMilliseconds(100), timer.Interval);
                var rendersAtInput = -1;
                view.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => rendersAtInput = view.RenderCount));
                view.IsCaptureRunning = false;
                WpfTestHost.Pump();

                Assert.Equal(initialRenders, rendersAtInput);
                Assert.Equal(initialRenders + 1, view.RenderCount);
                Assert.False(view.HasPendingRefresh);
            });
        }

        [Fact]
        public void ResponseUpdates_RefreshStatusDurationAndReuseTheVisibleBar()
        {
            WpfTestHost.Run(window =>
            {
                var model = Create(start: Origin);
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { model }
                };
                WpfTestHost.Show(window, view);
                var bar = Assert.Single(Bars(view));
                var previousBrush = bar.Background;

                Update(model, duration: TimeSpan.FromSeconds(1), status: "503");
                WpfTestHost.Pump();

                Assert.Same(bar, Assert.Single(Bars(view)));
                Assert.NotSame(previousBrush, bar.Background);
                Assert.True(bar.Background.IsFrozen);
                Assert.Contains("Status: 503", (string)bar.ToolTip);
                Assert.Contains("1000 ms", (string)bar.ToolTip);
                Assert.Equal(500, bar.Width);
            });
        }

        [Fact]
        public void SustainedCapture_DoesNotRenderFasterThanTheConfirmedCadence()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 1000).Select(index => Create(index)));
                var view = new CaptureTimelineView { ItemsSource = source, IsCaptureRunning = true };
                WpfTestHost.Show(window, view);
                var initialRenders = view.RenderCount;
                var frame = new DispatcherFrame();
                var elapsed = Stopwatch.StartNew();
                var incoming = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(5)
                };
                incoming.Tick += (sender, args) =>
                {
                    for (var index = 0; index < 10; index++)
                    {
                        source.Add(Create(source.Count + 1));
                    }
                };
                var stop = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(1200)
                };
                stop.Tick += (sender, args) => frame.Continue = false;
                try
                {
                    incoming.Start();
                    stop.Start();
                    Dispatcher.PushFrame(frame);
                }
                finally
                {
                    incoming.Stop();
                    stop.Stop();
                }
                view.IsCaptureRunning = false;
                WpfTestHost.Pump();

                Assert.True(source.Count > 1000);
                Assert.InRange(view.RenderCount - initialRenders, 1,
                    (int)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 100) + 1);
                Assert.False(view.HasPendingRefresh);
            });
        }

        [Fact]
        public void LongHistory_RendersOnlyViewportRowsAndTicksAtEitherScrollExtreme()
        {
            WpfTestHost.Run(window =>
            {
                var first = Create(1, Origin);
                var last = Create(2, Origin.AddDays(1));
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { first, last }
                };
                WpfTestHost.Show(window, view);
                Assert.Same(first, Assert.Single(Bars(view)).Tag);
                var scroller = (ScrollViewer)view.FindName("TimelineScrollViewer");
                Assert.True(scroller.ExtentWidth > 40000000);
                scroller.ScrollToHorizontalOffset(scroller.ExtentWidth);
                WpfTestHost.Pump();

                Assert.Same(last, Assert.Single(Bars(view)).Tag);
                Assert.InRange(Canvas(view, "TimeAxisCanvas").Children.Count, 1, 24);
                Assert.Equal(scroller.ViewportWidth, Canvas(view, "TimelineCanvas").Width);
                Assert.All(Canvas(view, "TimeAxisCanvas").Children.OfType<TextBlock>(), label =>
                    Assert.InRange(System.Windows.Controls.Canvas.GetLeft(label), -202, scroller.ViewportWidth + 202));
            });
        }

        [Fact]
        public void VerticalScrolling_RecyclesBarsWithoutLosingClickSelectionOrTooltips()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 100).Select(index => Create(index, Origin)));
                var view = new CaptureTimelineView { ItemsSource = source };
                WpfTestHost.Show(window, view);
                var firstBar = Bars(view).First();
                var oldModel = firstBar.Tag;
                var scroller = (ScrollViewer)view.FindName("TimelineScrollViewer");
                scroller.ScrollToVerticalOffset(320);
                WpfTestHost.Pump();

                Assert.Same(firstBar, Bars(view).First());
                Assert.NotSame(oldModel, firstBar.Tag);
                firstBar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                });
                Assert.Same(firstBar.Tag, view.SelectedItem);
                Assert.Contains(((ProxyConnectionModel)firstBar.Tag).Path, (string)firstBar.ToolTip);
                Assert.InRange(Bars(view).Count(), 1, 20);
            });
        }

        [Fact]
        public void HiddenAndReplacedSources_DetachAndRefreshCurrentStateOnReturn()
        {
            WpfTestHost.Run(window =>
            {
                var original = Create();
                var source = new ObservableCollection<ProxyConnectionModel> { original };
                var view = new CaptureTimelineView { ItemsSource = source, IsCaptureRunning = true };
                WpfTestHost.Show(window, view);
                Assert.Equal(1, HandlerCount(original));
                view.Visibility = Visibility.Collapsed;
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(original));
                Update(original, status: "503");
                source.Add(Create(2));
                Assert.False(view.HasPendingRefresh);
                view.Visibility = Visibility.Visible;
                WpfTestHost.Pump();
                Assert.Equal(1, HandlerCount(original));
                Assert.Contains(Bars(view), bar => ((string)bar.ToolTip).Contains("Status: 503"));

                var replacement = Create(3);
                view.ItemsSource = new ObservableCollection<ProxyConnectionModel> { replacement };
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(original));
                Assert.Equal(1, HandlerCount(replacement));
                Assert.Same(replacement, Assert.Single(Bars(view)).Tag);
                window.Content = null;
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(replacement));
                Assert.False(view.HasPendingRefresh);
            });
        }

        [Fact]
        public void InsertReplaceAndMove_PreserveCollectionOrderForTiedTimestamps()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 3).Select(index => Create(index, Origin)));
                var view = new CaptureTimelineView { ItemsSource = source };
                WpfTestHost.Show(window, view);
                source[1] = Create(4, Origin);
                source.Insert(0, Create(5, Origin));
                source.Move(3, 0);
                WpfTestHost.Pump();

                Assert.Equal(source, Bars(view).Select(bar => (ProxyConnectionModel)bar.Tag));
                Assert.All(source, model => Assert.Equal(1, HandlerCount(model)));
            });
        }

        [Fact]
        public void RebindingTheSameModel_FlushesAResponseThatWasStillWaitingForTheTimer()
        {
            WpfTestHost.Run(window =>
            {
                var model = Create();
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { model },
                    IsCaptureRunning = true
                };
                WpfTestHost.Show(window, view);
                Update(model, status: "503");
                view.ItemsSource = new ObservableCollection<ProxyConnectionModel> { model };
                WpfTestHost.Pump();

                Assert.Contains("Status: 503", (string)Assert.Single(Bars(view)).ToolTip);
                Assert.Equal(1, HandlerCount(model));
            });
        }

        [Fact]
        public void SourceChangeDuringANotification_DoesNotAddAnOldSourcesRow()
        {
            WpfTestHost.Run(window =>
            {
                var original = new ObservableCollection<ProxyConnectionModel>();
                var replacement = Create(2);
                var nextSource = new ObservableCollection<ProxyConnectionModel> { replacement };
                var view = new CaptureTimelineView { ItemsSource = original };
                original.CollectionChanged += (sender, args) => view.ItemsSource = nextSource;
                WpfTestHost.Show(window, view);
                var stale = Create();
                original.Add(stale);
                WpfTestHost.Pump();

                Assert.Same(replacement, Assert.Single(Bars(view)).Tag);
                Assert.Equal(0, HandlerCount(stale));
                Assert.Equal(1, HandlerCount(replacement));
            });
        }

        [Fact]
        public void ResizingViewport_TrimsPreviouslyRealizedControls()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 1000).Select(index => Create(index, Origin)));
                var view = new CaptureTimelineView { ItemsSource = source };
                WpfTestHost.Show(window, view);
                var previousCount = Bars(view).Count();
                window.Height = 140;
                WpfTestHost.Pump();

                Assert.True(Bars(view).Count() < previousCount);
                Assert.InRange(Bars(view).Count(), 1, 12);
                Assert.False(view.HasPendingRefresh);
            });
        }

        [Fact]
        public void GroupingResetAndThemeChanges_PreserveBoundedControlsAndDynamicResources()
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 1000).Select(index => Create(index, Origin, $"host-{index % 10}")));
                var view = new CaptureTimelineView { ItemsSource = source };
                WpfTestHost.Show(window, view);
                ((RadioButton)view.FindName("ByHostMode")).IsChecked = true;
                WpfTestHost.Pump();
                var label = Assert.Single(Canvas(view, "TimelineCanvas").Children.OfType<TextBlock>());
                window.Resources["PrimaryForegroundBrush"] = Brushes.Blue;
                window.Resources["SecondaryForegroundBrush"] = Brushes.Red;
                WpfTestHost.Pump();

                Assert.Same(Brushes.Blue, label.Foreground);
                Assert.All(Canvas(view, "TimeAxisCanvas").Children.OfType<TextBlock>(),
                    tick => Assert.Same(Brushes.Red, tick.Foreground));
                Assert.InRange(Canvas(view, "TimelineCanvas").Children.Count, 1, 22);
                source.Clear();
                WpfTestHost.Pump();
                Assert.Empty(Canvas(view, "TimelineCanvas").Children);
                Assert.Empty(Canvas(view, "TimeAxisCanvas").Children);
                Assert.False(view.HasPendingRefresh);
            });
        }

        [Theory]
        [InlineData("vertical")]
        [InlineData("horizontal")]
        [InlineData("resize")]
        public void ViewportChanges_CoverTheFirstCompositionFrameBeforeBackgroundWork(string change)
        {
            WpfTestHost.Run(window =>
            {
                var source = new ObservableCollection<ProxyConnectionModel>(
                    Enumerable.Range(1, 1000).Select(index => Create(index, Origin.AddSeconds(index))));
                var view = new CaptureTimelineView { ItemsSource = source, IsCaptureRunning = true };
                WpfTestHost.Show(window, view);
                var scroller = (ScrollViewer)view.FindName("TimelineScrollViewer");
                var canvas = Canvas(view, "TimelineCanvas");
                var previousWidth = scroller.ViewportWidth;
                var backgroundRan = false;
                var captured = false;
                var composedBeforeBackground = false;
                double uncovered = double.NaN;
                EventHandler rendering = (sender, args) =>
                {
                    if (captured || (change == "vertical" ? scroller.VerticalOffset == 0 :
                        change == "horizontal" ? scroller.HorizontalOffset == 0 :
                        scroller.ViewportWidth <= previousWidth))
                    {
                        return;
                    }
                    captured = true;
                    composedBeforeBackground = !backgroundRan;
                    var position = canvas.TranslatePoint(new Point(), scroller);
                    uncovered = change == "vertical"
                        ? scroller.ViewportHeight - (position.Y + canvas.ActualHeight)
                        : scroller.ViewportWidth - (position.X + canvas.ActualWidth);
                };
                CompositionTarget.Rendering += rendering;
                try
                {
                    view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        backgroundRan = true;
                        Thread.Sleep(100);
                    }));
                    if (change == "vertical")
                    {
                        scroller.ScrollToVerticalOffset(48);
                    }
                    else if (change == "horizontal")
                    {
                        scroller.ScrollToHorizontalOffset(300);
                    }
                    else
                    {
                        window.Width += 100;
                    }
                    WpfTestHost.Pump();

                    Assert.True(captured, "The changed viewport was not observed during composition.");
                    Assert.True(composedBeforeBackground, "The regression must inspect the frame before queued background work.");
                    Assert.InRange(uncovered, -1, 1);
                    Assert.False(view.HasPendingRefresh);
                }
                finally
                {
                    CompositionTarget.Rendering -= rendering;
                }
            });
        }

        private static Canvas Canvas(CaptureTimelineView view, string name) => (Canvas)view.FindName(name);
        private static IEnumerable<Border> Bars(CaptureTimelineView view) =>
            Canvas(view, "TimelineCanvas").Children.OfType<Border>();

        internal static int HandlerCount(ProxyConnectionModel model) =>
            ((Delegate)typeof(ProxyConnectionModel).GetField(
                "PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model))
                ?.GetInvocationList().Count(handler => handler.Method.DeclaringType == typeof(CaptureTimelineView)) ?? 0;
    }
}
