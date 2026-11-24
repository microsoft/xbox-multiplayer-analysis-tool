// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using XMAT.NetworkTrace;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using Ellipse = System.Windows.Shapes.Ellipse;
using Point = System.Windows.Point;
using Visual = System.Windows.Media.Visual;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureConnectionViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CaptureConnectionViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("Invalid device context")]
        [InlineData(42)]
        public void TryGetCaptureContext_RejectsMissingOrIncorrectContext(object dataContext)
        {
            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                dataContext,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_RejectsMissingController()
        {
            var context = new TestCaptureDeviceContext(null);

            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_RejectsOtherCaptureController()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC);
            var context = new TestCaptureDeviceContext(controller);

            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_ReturnsWebProxyContextAndController()
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device",
                DeviceType.Unknown,
                true);
            var context = new TestCaptureDeviceContext(controller);

            Assert.True(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Same(context, captureDeviceContext);
            Assert.Same(controller, captureController);
        }

        [Theory]
        [InlineData(true, 1280)]
        [InlineData(true, 960)]
        [InlineData(false, 1280)]
        [InlineData(false, 960)]
        public void ShapingStatusDoesNotOverlapTheToolbarOrDetails(bool newLook, double width)
        {
            _application.Run(window =>
            {
                bool previousLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = newLook;
                    window.Resources = ThemeManager.CreateLook(newLook);
                    ThemeManager.LoadTheme(window.Resources, "Light", newLook, highContrast: false);
                    window.Width = width;
                    window.Height = 720;
                    var view = View(Controller());
                    WpfTestHost.Show(window, view);
                    var status = (TextBlock)view.FindName("ShapingStatus");
                    Assert.False(string.IsNullOrEmpty(status.Text));
                    Point start = status.TranslatePoint(new Point(2, status.ActualHeight / 2), view);

                    var hit = VisualTreeHelper.HitTest(view, start)?.VisualHit;

                    Assert.Same(status, hit);
                    var toolbar = Descendants(view).OfType<ToolBar>().Single();
                    var inspector = Assert.IsType<TabControl>(((TabItem)view.FindName("ReqestResponseTab")).Parent);
                    Rect bounds = status.TransformToAncestor(view).TransformBounds(new Rect(status.RenderSize));
                    Assert.False(bounds.IntersectsWith(toolbar.TransformToAncestor(view).TransformBounds(new Rect(toolbar.RenderSize))),
                        "The shaping status must not cover capture actions.");
                    Assert.False(bounds.IntersectsWith(inspector.TransformToAncestor(view).TransformBounds(new Rect(inspector.RenderSize))),
                        "The shaping status must not cover the inspector.");
                }
                finally
                {
                    window.Content = null;
                    LookState.Current.IsNewLook = previousLook;
                    WpfTestHost.Pump();
                }
            });
        }

        [Fact]
        public void ReloadingView_KeepsOneSubscriptionAndDetachesOnUnload()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var model = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(model);
                var view = View(controller);
                WpfTestHost.Show(window, view);
                Assert.Null(((DataGrid)view.FindName("ConnectionList")).Items.Filter);

                for (var load = 0; load < 4; load++)
                {
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.Equal(1, HandlerCount(controller));
                    Assert.Equal(1, HandlerCount(model));
                    window.Content = null;
                    WpfTestHost.Pump();
                    Assert.Equal(0, HandlerCount(controller));
                    Assert.Equal(0, HandlerCount(model));
                    window.Content = view;
                    WpfTestHost.Pump();
                }

                var added = CaptureTimelineTestData.Create(2);
                controller.ProxyConnections.Add(added);
                WpfTestHost.Pump();
                Assert.Equal(1, HandlerCount(added));
                Assert.Contains(controller.StatusFilterList, item => item.Text == "200");
                Assert.Contains(controller.HostFilterList, item => item.Text == "example.test");
            });
        }

        [Fact]
        public void ResetReplacementAndContextChanges_DetachTheOriginalItems()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var original = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(original);
                var view = View(controller);
                WpfTestHost.Show(window, view);
                controller.ProxyConnections.Clear();
                Assert.Equal(0, HandlerCount(original));

                var replaced = CaptureTimelineTestData.Create(2);
                controller.ProxyConnections.Add(replaced);
                var replacement = CaptureTimelineTestData.Create(3);
                controller.ProxyConnections = new ProxyConnectionsCollection { replacement };
                controller.RaisePropertyChange(nameof(controller.ProxyConnections));
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(replaced));
                Assert.Equal(1, HandlerCount(replacement));

                var nextController = Controller();
                var nextItem = CaptureTimelineTestData.Create(4);
                nextController.ProxyConnections.Add(nextItem);
                view.DataContext = new TestCaptureDeviceContext(nextController);
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(controller));
                Assert.Equal(0, HandlerCount(replacement));
                Assert.Equal(1, HandlerCount(nextController));
                Assert.Equal(1, HandlerCount(nextItem));
                view.DataContext = null;
                WpfTestHost.Pump();
                Assert.Equal(0, HandlerCount(nextController));
                Assert.Equal(0, HandlerCount(nextItem));
            });
        }

        [Fact]
        public void FilteredOutItems_StillHaveOneOwnedSubscriptionAndPopulateFilterValues()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var model = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(model);
                var view = View(controller);
                WpfTestHost.Show(window, view);
                var filters = (CheckBoxFilters)view.FindName("StatusFilters");
                var enableFilters = (CheckBox)filters.FindName("EnableFilters");
                enableFilters.IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(filters.IsFilterEnabled);
                controller.StatusFilterList.Single(item => item.Text == "200").IsChecked = false;
                WpfTestHost.Pump();
                var grid = (DataGrid)view.FindName("ConnectionList");
                Assert.NotNull(grid.Items.Filter);
                Assert.False(grid.Items.Filter(model));
                CollectionViewSource.GetDefaultView(grid.ItemsSource).Refresh();
                Assert.Empty(grid.Items);
                CaptureTimelineTestData.Update(model, status: "503");

                Assert.Contains(controller.StatusFilterList, item => item.Text == "503");
                Assert.Equal(1, HandlerCount(model));
                controller.ProxyConnections.Remove(model);
                Assert.Equal(0, HandlerCount(model));
                enableFilters.IsChecked = false;
                WpfTestHost.Pump();
                Assert.Null(grid.Items.Filter);
            });
        }

        [Fact]
        public void StoppingAndRestartingCapture_FlushesFinalResponseWithoutClearingHistory()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var model = CaptureTimelineTestData.Create(start: CaptureTimelineTestData.Origin);
                controller.ProxyConnections.Add(model);
                controller.IsRunning = true;
                var view = View(controller);
                WpfTestHost.Show(window, view);
                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                Assert.True(timeline.IsCaptureRunning);
                CaptureTimelineTestData.Update(model, status: "503");
                controller.IsRunning = false;
                WpfTestHost.Pump();

                Assert.False(timeline.HasPendingRefresh);
                Assert.Contains(((Canvas)timeline.FindName("TimelineCanvas")).Children.OfType<Border>(),
                    bar => ((string)bar.ToolTip).Contains("Status: 503"));
                controller.IsRunning = true;
                WpfTestHost.Pump();
                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(2));
                controller.IsRunning = false;
                WpfTestHost.Pump();
                Assert.Equal(2, controller.ProxyConnections.Count);
                Assert.False(timeline.HasPendingRefresh);
            });
        }

        [Fact]
        public void AutoScroll_FollowsNewRowsButPreservesTheUsersScrolledPosition()
        {
            _application.Run(window =>
            {
                window.Height = 700;
                var controller = Controller();
                var view = View(controller);
                view.AutoScroll = true;
                WpfTestHost.Show(window, view);
                foreach (var index in Enumerable.Range(1, 60))
                {
                    controller.ProxyConnections.Add(CaptureTimelineTestData.Create(index));
                }
                WpfTestHost.Pump();
                var grid = (DataGrid)view.FindName("ConnectionList");
                var scroller = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
                Assert.InRange(scroller.ExtentHeight - scroller.ViewportHeight - scroller.VerticalOffset, 0, 1);

                scroller.ScrollToTop();
                WpfTestHost.Pump();
                var offset = scroller.VerticalOffset;
                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(61));
                WpfTestHost.Pump();
                Assert.Equal(offset, scroller.VerticalOffset);
            });
        }

        [Fact]
        public void ContextChangeDuringAnItemNotification_DoesNotUseDetachedSubscriptions()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var model = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(model);
                var view = View(controller);
                model.PropertyChanged += (sender, args) => view.DataContext = null;
                WpfTestHost.Show(window, view);

                CaptureTimelineTestData.Update(model, status: "503");
                WpfTestHost.Pump();

                Assert.Equal(0, HandlerCount(model));
                Assert.Equal(0, HandlerCount(controller));
            });
        }

        // The original look keeps a filter on when its list is replaced, so the new list's values and checked states apply.
        // (The new look turns the filter off: Filters_ShowOnlyThePickedValues_AndClearFiltersShowsEveryRequest.)
        [Fact]
        public void EnabledFilters_UseTheNewContextsValuesAfterContextAndSourceReplacement()
        {
            _application.Run(window => InOriginalLook(() =>
            {
                var controller = Controller();
                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(host: "old.test"));
                var view = View(controller);
                WpfTestHost.Show(window, view);
                var filters = (CheckBoxFilters)view.FindName("HostFilters");
                ((CheckBox)filters.FindName("EnableFilters")).IsChecked = true;
                WpfTestHost.Pump();
                var next = Controller();
                var model = CaptureTimelineTestData.Create(2, host: "next.test");
                next.ProxyConnections.Add(model);

                view.DataContext = new TestCaptureDeviceContext(next);
                WpfTestHost.Pump();
                var grid = (DataGrid)view.FindName("ConnectionList");
                Assert.NotNull(grid.Items.Filter);
                Assert.Same(model, Assert.Single(grid.Items.Cast<ProxyConnectionModel>()));

                var replacement = CaptureTimelineTestData.Create(3, host: "replacement.test");
                next.ProxyConnections = new ProxyConnectionsCollection { replacement };
                next.RaisePropertyChange(nameof(next.ProxyConnections));
                WpfTestHost.Pump();
                Assert.Same(replacement, Assert.Single(grid.Items.Cast<ProxyConnectionModel>()));
                Assert.Equal(0, HandlerCount(model));
            }));
        }

        [Fact]
        public void ContextChangeDuringACollectionNotification_DoesNotObserveTheOldSourcesNewItem()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var view = View(controller);
                controller.ProxyConnections.CollectionChanged += (sender, args) => view.DataContext = null;
                WpfTestHost.Show(window, view);
                var model = CaptureTimelineTestData.Create();

                controller.ProxyConnections.Add(model);
                WpfTestHost.Pump();

                Assert.Equal(0, HandlerCount(model));
                Assert.Equal(0, HandlerCount(controller));
            });
        }

        [Fact]
        public void AttachingRetainedRows_DoesNotEnumerateFilterOptionsForEveryRecord()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var statuses = new CountingFilterOptions { new("200", false) };
                var hosts = new CountingFilterOptions();
                var methods = new CountingFilterOptions { new("GET") };
                for (var index = 0; index < 1000; index++)
                {
                    hosts.Add(new CheckedListItem($"host-{index}"));
                }
                controller.StatusFilterList = statuses;
                controller.HostFilterList = hosts;
                controller.MethodFilterList = methods;
                for (var index = 0; index < 5000; index++)
                {
                    controller.ProxyConnections.Add(CaptureTimelineTestData.Create(
                        index + 1, host: hosts[index % hosts.Count].Text));
                }
                var view = View(controller);
                var initialEnumerations = statuses.Enumerations + hosts.Enumerations + methods.Enumerations;
                try
                {
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

                    Assert.InRange(statuses.Enumerations + hosts.Enumerations + methods.Enumerations -
                        initialEnumerations, 0, 6);
                    Assert.Equal(1000, hosts.Count);
                    Assert.False(statuses[0].IsChecked);
                    Assert.Equal(1, HandlerCount(controller.ProxyConnections[0]));
                }
                finally
                {
                    view.DataContext = null;
                }
            });
        }

        // The view reads the replacement list's own items and checked states (in the original look, which keeps the filter
        // on when its list is replaced).
        [Fact]
        public void ReplacingFilterOptions_UsesNewInstancesAndTheirCheckedState()
        {
            _application.Run(window => InOriginalLook(() =>
            {
                var controller = Controller();
                var model = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(model);
                var original = new CheckedListItem("200", false);
                controller.StatusFilterList.Add(original);
                var view = View(controller);
                WpfTestHost.Show(window, view);
                var filters = (CheckBoxFilters)view.FindName("StatusFilters");
                ((CheckBox)filters.FindName("EnableFilters")).IsChecked = true;
                WpfTestHost.Pump();
                var grid = (DataGrid)view.FindName("ConnectionList");
                Assert.Empty(grid.Items);

                var replacement = new CheckedListItem("200");
                controller.StatusFilterList = new ObservableCollection<CheckedListItem> { replacement };
                controller.RaisePropertyChange(nameof(controller.StatusFilterList));
                WpfTestHost.Pump();

                Assert.Same(model, Assert.Single(grid.Items.Cast<ProxyConnectionModel>()));
                original.Text = "ignored";
                replacement.IsChecked = false;
                Assert.False(grid.Items.Filter(model));
                Assert.Same(replacement, Assert.Single(controller.StatusFilterList));
            }));
        }

        [Theory]
        [InlineData(DeviceType.LocalPC, false, false)]
        [InlineData(DeviceType.XboxConsole, false, true)]
        [InlineData(DeviceType.GenericProxyDevice, false, true)]
        [InlineData(DeviceType.Unknown, false, false)]
        [InlineData(DeviceType.XboxConsole, true, false)]
        [InlineData(DeviceType.GenericProxyDevice, true, false)]
        public void CheckAction_IsUnavailableForLocalPcAndImports(
            DeviceType type, bool readOnly, bool available)
        {
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController("test", type, readOnly);
                var context = new TestCaptureDeviceContext(controller)
                {
                    DeviceType = type,
                    IsReadOnly = readOnly
                };
                var view = new CaptureConnectionView { DataContext = context };
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);
                Assert.Equal(available, WebServiceProxyCommands.CheckDevice.CanExecute(null, view));
                WpfTestHost.Pump();
                var button = (Button)view.FindName("CheckButton");
                Assert.Equal(available, button.IsEnabled);
                Assert.Equal(available ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
                if (available)
                {
                    Assert.Equal(Localization.GetLocalizedString(
                        type == DeviceType.GenericProxyDevice ? "PROXY_SETUP_DETAILS" : "WEBCAP_CHECK"),
                        button.Content);
                }
                view.DataContext = null;
            });
        }

        [Fact]
        public void CheckAction_ContextChangesRefreshAvailabilityAndCaption()
        {
            _application.Run(window =>
            {
                var view = new CaptureConnectionView();
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);
                var button = (Button)view.FindName("CheckButton");

                foreach (var type in new[]
                {
                    DeviceType.XboxConsole, DeviceType.GenericProxyDevice, DeviceType.LocalPC,
                    DeviceType.Unknown, DeviceType.XboxConsole
                })
                {
                    view.DataContext = new TestCaptureDeviceContext(
                        new WebServiceDeviceCaptureController("test", type, false)) { DeviceType = type };
                    WpfTestHost.Pump();
                    var available = type is DeviceType.XboxConsole or DeviceType.GenericProxyDevice;
                    Assert.Equal(available, WebServiceProxyCommands.CheckDevice.CanExecute(null, view));
                    Assert.Equal(available, button.IsEnabled);
                    Assert.Equal(available ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
                    Assert.Equal(Localization.GetLocalizedString(
                        type == DeviceType.GenericProxyDevice ? "PROXY_SETUP_DETAILS" : "WEBCAP_CHECK"),
                        button.Content);
                }

                view.DataContext = null;
                WpfTestHost.Pump();
                Assert.False(WebServiceProxyCommands.CheckDevice.CanExecute(null, view));
                Assert.Equal(Visibility.Collapsed, button.Visibility);
            });
        }

        [Fact]
        public void CheckAction_LocalPcExecutionGuardDoesNotReadNativeSettings()
        {
            _application.Run(window =>
            {
                var configuration = new UnusedConfiguration();
                var controller = new WebServiceDeviceCaptureController(
                    "local", DeviceType.LocalPC, false, new ProxyPortPool(), configuration);
                var view = new CaptureConnectionView
                {
                    DataContext = new TestCaptureDeviceContext(controller) { DeviceType = DeviceType.LocalPC }
                };
                typeof(CaptureConnectionView).GetMethod(
                    "CheckDevice_Executed", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(view, new object[] { view, null });
                Assert.Equal(0, configuration.ReadCount);
                view.DataContext = null;
            });
        }

        [Fact]
        public void NarrowTrafficPane_FoldsTheFiltersIntoAFiltersButtonUntilTheyFitAgain()
        {
            _application.Run(window =>
            {
                var view = View(Controller());
                ShowAt(window, view, 1600);
                var host = (FilterOverflowHost)view.FindName("FiltersHost");
                var row = (Grid)view.FindName("FiltersPanel");
                var button = host.OverflowButton;
                UIElement[] filters = Filters(view);
                Assert.False(host.IsOverflowing);
                Assert.False(button.IsVisible);
                double line = Middle(filters[0], row);
                Assert.All(filters, filter =>
                {
                    Assert.True(filter.IsVisible);
                    Assert.InRange(Middle(filter, row) - line, -0.5, 0.5);
                });

                window.Width = 700;
                WpfTestHost.Pump();
                Assert.True(host.IsOverflowing);
                Assert.True(button.IsVisible);
                Assert.Equal(28, button.ActualWidth);
                Assert.Equal(28, button.ActualHeight);
                Assert.Equal(Localization.GetLocalizedString("GENERIC_FILTERS"), AutomationProperties.GetName(button));
                Assert.Equal(AutomationProperties.GetName(button), button.ToolTip);
                Assert.False(((UIElement)view.FindName("FiltersText")).IsVisible);
                Assert.All(filters, filter => Assert.False(filter.IsVisible));

                // The popup lists the filters themselves, in row order, 8px apart inside 12px of padding: the dropdowns as
                // wide as the list, Clear filters and the Process button at their own widths.
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources("FilterOverflowPopupStyle"));
                button.IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.IsOpen);
                var list = (FrameworkElement)VisualTreeHelper.GetParent(filters[0]);
                var surface = (Border)host.OverflowPopup.Child;
                Assert.True(surface.IsAncestorOf(list));
                Assert.Equal(new Point(13, 13), list.TranslatePoint(new Point(), surface));
                Assert.Same(row, VisualTreeHelper.GetParent((UIElement)view.FindName("FiltersText")));
                Assert.All(filters, filter =>
                {
                    Assert.Same(list, VisualTreeHelper.GetParent(filter));
                    Assert.True(filter.IsVisible);
                    Assert.InRange(Bounds(filter, list).Left, -0.5, 0.5);
                });
                for (var index = 1; index < filters.Length; index++)
                {
                    Assert.InRange(Bounds(filters[index], list).Top - Bounds(filters[index - 1], list).Bottom, 7.5, 8.5);
                }
                Assert.All(filters.Take(3), filter =>
                {
                    Assert.InRange(Bounds(filter, list).Width - list.ActualWidth, -0.5, 0.5);
                    // The gap that follows each dropdown in the row is set aside: the dropdown ends at the list's edge.
                    var dropdown = (UIElement)((CheckBoxFilters)filter).FindName("FilterList");
                    Assert.InRange(Bounds(dropdown, list).Right - list.ActualWidth, -0.5, 0.5);
                });
                Assert.All(filters.Skip(3), filter =>
                {
                    Thickness margin = ((FrameworkElement)filter).Margin;
                    Assert.InRange(Bounds(filter, list).Width - (filter.DesiredSize.Width - margin.Left - margin.Right), -0.5, 0.5);
                    Assert.True(Bounds(filter, list).Width < list.ActualWidth / 2);
                });

                button.IsChecked = false;
                WpfTestHost.Pump();
                Assert.False(host.OverflowPopup.IsOpen);
                Assert.All(filters, filter => Assert.Same(row, VisualTreeHelper.GetParent(filter)));

                window.Width = 1600;
                WpfTestHost.Pump();
                Assert.False(host.IsOverflowing);
                Assert.False(button.IsVisible);
                Assert.All(filters, filter => Assert.True(filter.IsVisible));

                // The original look keeps its row, and the process filter's summary in it, at any width.
                window.Width = 700;
                WpfTestHost.Pump();
                Assert.True(host.IsOverflowing);
                var indicator = (UIElement)view.FindName("ProcessFilterIndicator");
                Assert.Same(host.SummaryLine, VisualTreeHelper.GetParent(indicator));
                LookState.Current.IsNewLook = false;
                try
                {
                    WpfTestHost.Pump();
                    Assert.False(host.IsOverflowing);
                    Assert.False(button.IsVisible);
                    Assert.All(filters, filter => Assert.Same(row, VisualTreeHelper.GetParent(filter)));
                    Assert.Same(row, VisualTreeHelper.GetParent(indicator));
                    Assert.Equal(6, Grid.GetColumn(indicator));
                    Assert.True(filters[0].IsVisible);
                }
                finally
                {
                    LookState.Current.IsNewLook = true;
                }

                WpfTestHost.Pump();
                Assert.Same(host.SummaryLine, VisualTreeHelper.GetParent(indicator));
            });
        }

        [Fact]
        public void NarrowTrafficPane_MovesAutoScrollUnderTheTimelineModesInsteadOfOverThem()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var view = View(controller);
                ShowAt(window, view, 1600);
                controller.IsRunning = true;
                WpfTestHost.Pump();
                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                var header = (ReflowPanel)timeline.FindName("HeaderRow");
                var volume = (UIElement)timeline.FindName("VolumeMode");
                var autoScroll = (CheckBox)timeline.FindName("AutoScrollButton");
                UIElement[] trailing = new[] { (UIElement)timeline.FindName("CapturingIndicator"), autoScroll }
                    .Where(element => element.IsVisible).ToArray();
                Assert.Contains(autoScroll, trailing);

                // The timeline's Auto Scroll is the view's own.
                Assert.False(autoScroll.IsChecked);
                autoScroll.IsChecked = true;
                Assert.True(view.AutoScroll);
                view.AutoScroll = false;
                Assert.False(autoScroll.IsChecked);

                // With room, Capturing and Auto Scroll end the modes' line at its right, 16px or more past Volume.
                Assert.False(header.IsStacked);
                Assert.True(trailing.All(element => Bounds(element, header).Left >= Bounds(volume, header).Right + 15.5));
                Assert.InRange(Bounds(autoScroll, header).Right - header.ActualWidth, -0.5, 0.5);
                Assert.InRange(Middle(autoScroll, header) - Middle(volume, header), -1, 1);

                // A narrow pane moves them to a line of their own under the modes, at its left, 4px down.
                window.Width = 700;
                WpfTestHost.Pump();
                Assert.True(header.IsStacked);
                Assert.True(autoScroll.IsVisible);
                double modesBottom = ((FrameworkElement)VisualTreeHelper.GetParent(volume)).ActualHeight;
                Assert.All(trailing, element => Assert.InRange(Bounds(element, header).Top - modesBottom, 3.5, header.ActualHeight));
                Assert.InRange(trailing.Min(element => Bounds(element, header).Left), -0.5, 0.5);
                Assert.True(Bounds(autoScroll, header).Right <= header.ActualWidth + 0.5);

                window.Width = 1600;
                WpfTestHost.Pump();
                Assert.False(header.IsStacked);
                controller.IsRunning = false;
            });
        }

        // In a tight traffic pane, Clear filters shows only its icon, 28 square and named in its tooltip, so the row keeps
        // every filter rather than folding. Narrower still, the row folds, and the popup lists Clear filters in full. Wide
        // again, Clear filters shows its name.
        [Fact]
        public void ATightFilterRow_ShowsClearFiltersAsItsIcon_BeforeItFolds()
        {
            _application.Run(window =>
            {
                var view = View(Controller());
                ShowAt(window, view, 1600);
                var host = (FilterOverflowHost)view.FindName("FiltersHost");
                var clear = (Button)view.FindName("ClearFiltersButton");
                var process = (UIElement)view.FindName("ProcessFilterButton");
                string name = Localization.GetLocalizedString("FILTERS_CLEAR");
                Assert.False(host.IsOverflowing);
                Assert.False(FilterOverflowHost.GetIsCompact(clear));
                Assert.Equal(name, clear.Content);
                Assert.True(clear.ActualWidth > 28);
                Assert.Null(clear.ToolTip);

                window.Width = 1280;
                WpfTestHost.Pump();
                Assert.False(host.IsOverflowing);
                Assert.True(FilterOverflowHost.GetIsCompact(clear));
                Assert.True(clear.IsVisible);
                Assert.Equal(28, clear.ActualWidth, 1);
                Assert.Equal(name, clear.ToolTip);
                Assert.Equal(name, AutomationProperties.GetName(clear));
                Assert.True(Bounds(clear, host).Right <= Bounds(process, host).Left);
                Assert.True(Bounds(process, host).Right <= host.ActualWidth + 0.5);

                window.Width = 700;
                WpfTestHost.Pump();
                Assert.True(host.IsOverflowing);
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources("FilterOverflowPopupStyle"));
                host.OverflowButton.IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.Child.IsAncestorOf(clear));
                Assert.False(FilterOverflowHost.GetIsCompact(clear));
                Assert.True(clear.ActualWidth > 28);
                host.OverflowButton.IsChecked = false;
                WpfTestHost.Pump();

                window.Width = 1600;
                WpfTestHost.Pump();
                Assert.False(host.IsOverflowing);
                Assert.False(FilterOverflowHost.GetIsCompact(clear));
                Assert.True(clear.ActualWidth > 28);
            });
        }

        [Fact]
        public void FoldedFilters_MarkTheFiltersButtonWhileAnyFilterIsOn()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var view = View(controller);
                ShowAt(window, view, 700);
                var host = (FilterOverflowHost)view.FindName("FiltersHost");
                var button = host.OverflowButton;
                var dot = Descendants(button).OfType<Ellipse>().Single();
                Assert.Equal(Visibility.Collapsed, dot.Visibility);

                var status = (CheckBox)((CheckBoxFilters)view.FindName("StatusFilters")).FindName("EnableFilters");
                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(status).GetPattern(PatternInterface.Toggle)).Toggle();
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Visible, dot.Visibility);
                Assert.Equal("Filters (1 active)", AutomationProperties.GetName(button));

                controller.SetProcessFilter(ProcessFilterTestSupport.Spec(null, new[] { "svchost.exe" }));
                WpfTestHost.Pump();
                Assert.Equal("Filters (2 active)", AutomationProperties.GetName(button));
                Assert.Equal(AutomationProperties.GetName(button), button.ToolTip);

                // The process filter's summary and Show all processes stay in view on their line under the Filters button,
                // 4px below it, and the popup doesn't repeat them. A pane this narrow wraps Show all under the summary
                // rather than cutting either short. Clear filters folds with the row, and the popup lists it.
                var indicator = (TextBlock)view.FindName("ProcessFilterIndicator");
                var showAll = (Button)view.FindName("ShowAllProcessesButton");
                var clear = (Button)view.FindName("ClearFiltersButton");
                Assert.True(indicator.IsVisible);
                Assert.True(showAll.IsVisible);
                Assert.False(clear.IsVisible);
                Assert.InRange(Bounds(indicator, host).Left - Bounds(button, host).Left, -0.5, 0.5);
                Assert.InRange(Bounds(indicator, host).Top - Bounds(button, host).Bottom, 3.5, 4.5);
                Assert.InRange(Bounds(showAll, host).Left - Bounds(indicator, host).Left, -0.5, 0.5);
                Assert.InRange(Bounds(showAll, host).Top - Bounds(indicator, host).Bottom, -0.5, 0.5);
                Assert.InRange(Bounds(showAll, host).Bottom - host.ActualHeight, -0.5, 0.5);
                button.IsChecked = true;
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.IsOpen);
                Assert.False(host.OverflowPopup.Child.IsAncestorOf(indicator));
                Assert.False(host.OverflowPopup.Child.IsAncestorOf(showAll));
                Assert.True(host.OverflowPopup.Child.IsAncestorOf(clear));
                Assert.True(clear.IsEnabled);
                Assert.True(indicator.IsVisible);
                button.IsChecked = false;
                WpfTestHost.Pump();

                status.IsChecked = false;
                controller.SetProcessFilter(ProcessFilterSpec.Empty);
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, dot.Visibility);
                Assert.Equal("Filters", AutomationProperties.GetName(button));
            });
        }

        [Fact]
        public void FiltersButton_OpensFromTheKeyboardOnTheFirstFilterAndEscReturnsToIt()
        {
            _application.Run(window =>
            {
                var view = View(Controller());
                ShowAt(window, view, 700);
                var host = (FilterOverflowHost)view.FindName("FiltersHost");
                var button = host.OverflowButton;
                var peer = UIElementAutomationPeer.CreatePeerForElement(button);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                var expander = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
                Assert.Equal(ExpandCollapseState.Collapsed, expander.ExpandCollapseState);
                var status = (CheckBoxFilters)view.FindName("StatusFilters");
                // The new look has no check box beside a filter, so the first control is the Status dropdown.
                var first = (ComboBox)status.FindName("FilterList");

                Assert.True(button.Focus());
                Press(button, Key.Enter);
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.IsOpen);
                Assert.Equal(ExpandCollapseState.Expanded, expander.ExpandCollapseState);
                Assert.True(first.IsKeyboardFocusWithin);
                // Tab moves on through the filters in the popup.
                Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.False(status.IsKeyboardFocusWithin);
                Assert.True(((CheckBoxFilters)view.FindName("HostFilters")).IsKeyboardFocusWithin);

                Press((UIElement)Keyboard.FocusedElement, Key.Escape);
                WpfTestHost.Pump();
                Assert.False(host.OverflowPopup.IsOpen);
                Assert.True(button.IsKeyboardFocused);
                Assert.Equal(ExpandCollapseState.Collapsed, expander.ExpandCollapseState);
                Assert.Same(view.FindName("FiltersPanel"), VisualTreeHelper.GetParent(status));

                // Screen readers open and close it the same way.
                expander.Expand();
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.IsOpen);
                Assert.True(first.IsKeyboardFocusWithin);
                expander.Collapse();
                WpfTestHost.Pump();
                Assert.False(host.OverflowPopup.IsOpen);
                Assert.True(button.IsKeyboardFocused);
            });
        }

        // The search box filters the table with the other filters and says how many of the capture's requests show while
        // anything filters it, or why a term can't be used. Escape empties it. Exporting the visible rows exports what the
        // search shows, and the search box grows into the room the filters leave and stays when they fold.
        [Fact]
        public void Search_FiltersTheTable_CountsWhatShows_AndEscapeEmptiesIt()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                var source = Guid.NewGuid();
                foreach ((int number, string host, string status) in new[]
                    { (1, "title.mgt.xboxlive.com", "200"), (2, "telemetry.example.test", "503"), (3, "images.example.test", "429") })
                {
                    ProxyConnectionModel row = CaptureTimelineTestData.Create(number, host: host, status: status);
                    row.RowKey = new CaptureRowKey(source, 1, number, number);
                    controller.ProxyConnections.Add(row);
                }

                var view = View(controller);
                ShowAt(window, view, 1600);
                var search = (SearchBox)view.FindName("SearchBox");
                var grid = (DataGrid)view.FindName("ConnectionList");
                var filters = (FilterOverflowHost)view.FindName("FiltersHost");
                Assert.True(search.IsVisible);
                Assert.False(filters.IsOverflowing);
                Assert.InRange(search.ActualWidth, 200.5, 360);
                Assert.Null(search.Summary);

                search.Text = "status:5xx example";
                view.ApplySearch();
                WpfTestHost.Pump();
                ProxyConnectionModel failed = controller.ProxyConnections[1];
                Assert.Equal(new[] { failed }, grid.Items.OfType<ProxyConnectionModel>());
                Assert.Equal("1 of 3", search.Summary);
                Assert.Equal(new[] { failed.RowKey }, view.ExportSelection(selectedOnly: false).Keys);

                search.Text = "status:abc";
                view.ApplySearch();
                WpfTestHost.Pump();
                Assert.Equal("status:abc isn't a status", search.Problem);
                Assert.Equal(3, grid.Items.Count);
                Assert.Null(search.Summary);

                Press(search, Key.Escape);
                WpfTestHost.Pump();
                Assert.Equal(string.Empty, search.Text);
                Assert.Null(search.Problem);
                Assert.Null(view.ExportSelection(selectedOnly: false).Keys);

                window.Width = 700;
                WpfTestHost.Pump();
                Assert.True(filters.IsOverflowing);
                Assert.True(search.IsVisible);
                Assert.InRange(search.ActualWidth, 200, 360);
            });
        }

        // In the new look, a problem with what was typed takes only the room the typed text leaves, but at least half, and
        // trims to fit, so neither hides the other; while there's a problem, the box is outlined like a field whose value
        // isn't valid.
        [Fact]
        public void SearchBox_KeepsTheTypedTextBesideAProblem_AndOutlinesTheBox()
        {
            _application.Run(window =>
            {
                var search = new SearchBox { Width = 320, Text = "status:abc", Problem = "status:abc isn't a status" };
                // Rounded to whole pixels, as the app lays out.
                var host = new Border { Child = search, HorizontalAlignment = HorizontalAlignment.Left, UseLayoutRounding = true };
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/Inputs.xaml")
                });
                WpfTestHost.Show(window, host);
                var typed = (ScrollViewer)search.Template.FindName("PART_ContentHost", search);
                var problem = (TextBlock)search.Template.FindName("Problem", search);
                var outline = (FrameworkElement)search.Template.FindName("ProblemOutline", search);
                void TypedTextShowsInFull() => Assert.True(typed.ExtentWidth <= typed.ViewportWidth + 0.5,
                    $"The problem leaves {typed.ViewportWidth}px of the {typed.ExtentWidth}px typed.");
                double Room(TextBlock text) => text.ActualWidth + text.Margin.Left + text.Margin.Right;
                var whole = new TextBlock { Text = search.Problem, FontFamily = problem.FontFamily, FontSize = problem.FontSize };
                whole.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                TypedTextShowsInFull();
                double shown = problem.DesiredSize.Width - problem.Margin.Left - problem.Margin.Right;
                Assert.True(shown >= whole.DesiredSize.Width - 0.5,
                    $"The problem shows {shown}px of its {whole.DesiredSize.Width}px in a box with room.");
                Assert.True(outline.IsVisible);

                search.Width = 200;
                WpfTestHost.Pump();
                TypedTextShowsInFull();
                Assert.True(problem.ActualWidth > 40);

                search.Problem = "status:abc isn't a status, and this sentence runs far past the end of the box";
                WpfTestHost.Pump();
                TypedTextShowsInFull();

                search.Text = "host:a-very-long-host-name.example.test status:abc method:GET";
                WpfTestHost.Pump();
                Assert.InRange(Room(problem), typed.ActualWidth - 1, typed.ActualWidth + 1);

                search.Problem = null;
                WpfTestHost.Pump();
                Assert.False(outline.IsVisible);
                Assert.False(problem.IsVisible);
            });
        }

        // In the new look the dropdown is the filter, without a check box beside it: picking values shows only those, and
        // with none picked, All is, and every request shows. The dropdown keeps its name and says what it shows in its
        // tooltip. A value that appears while the filter is on wasn't picked, so its requests stay hidden. Clear filters
        // sits in the row, between Method and Process, so the row keeps one line; it is available while anything filters,
        // and turns off the dropdowns, the process filter and the search.
        [Fact]
        public void Filters_ShowOnlyThePickedValues_AndClearFiltersShowsEveryRequest()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                foreach ((int number, string rowHost, string rowStatus) in new[]
                    { (1, "a.example.test", "200"), (2, "b.example.test", "404"), (3, "c.example.test", "503") })
                {
                    controller.ProxyConnections.Add(CaptureTimelineTestData.Create(number, host: rowHost, status: rowStatus));
                }

                var view = View(controller);
                ShowAt(window, view, 1600);
                var status = (CheckBoxFilters)view.FindName("StatusFilters");
                var hosts = (CheckBoxFilters)view.FindName("HostFilters");
                var methods = (CheckBoxFilters)view.FindName("MethodFilters");
                var process = (ToggleButton)view.FindName("ProcessFilterButton");
                var host = (FilterOverflowHost)view.FindName("FiltersHost");
                var grid = (DataGrid)view.FindName("ConnectionList");
                var clear = (Button)view.FindName("ClearFiltersButton");
                var statusList = (ComboBox)status.FindName("FilterList");
                string[] Shown() => grid.Items.OfType<ProxyConnectionModel>().Select(row => row.Status).OrderBy(s => s).ToArray();
                double rowHeight = host.ActualHeight;
                void AssertOneLine(string when)
                {
                    Assert.True(clear.IsVisible, $"INPUT-1: Clear filters isn't in the filter row {when}.");
                    Rect bounds = Bounds(clear, host);
                    Assert.True(Bounds(methods, host).Right <= bounds.Left + 0.5 && bounds.Right <= Bounds(process, host).Left + 0.5,
                        $"INPUT-1: Clear filters isn't between Method and Process {when}.");
                    Assert.True(Math.Abs(host.ActualHeight - rowHeight) < 0.5,
                        $"INPUT-1: the filter row is {host.ActualHeight:0} tall {when}, not one line of {rowHeight:0}.");
                }

                Assert.False(((CheckBox)status.FindName("EnableFilters")).IsVisible, "INPUT-1: a check box sits beside the Status dropdown.");
                AssertOneLine("while nothing filters");
                Assert.False(clear.IsEnabled, "INPUT-1: Clear filters is available while nothing filters.");
                Assert.Equal("Status", status.Summary);
                Assert.Null(statusList.ToolTip);
                Assert.True(PickBox(status, "*").IsChecked, "INPUT-1: All isn't picked while nothing is.");
                Assert.Equal("All", PickBox(status, "*").Content);

                Toggle(PickBox(status, "503"));
                Assert.True(status.IsFilterEnabled, "INPUT-1: picking 503 doesn't turn the Status filter on.");
                Assert.True(Shown().SequenceEqual(new[] { "503" }), "INPUT-1: picking 503 doesn't show only 503.");
                Assert.True(Equals(statusList.ToolTip, "Status: 503"), $"INPUT-1: the Status dropdown's tooltip says \"{statusList.ToolTip}\", not what it shows.");
                Assert.True(statusList.Text == "Status", "INPUT-1: the Status dropdown doesn't keep its name.");
                Assert.Equal("Status: 503", status.Summary);
                Assert.False(PickBox(status, "*").IsChecked);
                Assert.True(PickBox(status, "503").IsChecked);
                Assert.False(PickBox(status, "200").IsChecked);
                Assert.True(clear.IsEnabled, "INPUT-1: Clear filters isn't available while a filter is on.");
                AssertOneLine("while a filter is on");

                Toggle(PickBox(status, "404"));
                Assert.Equal(new[] { "404", "503" }, Shown());
                Assert.Equal("Status: 404, 503", statusList.ToolTip);

                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(4, host: "d.example.test", status: "500"));
                WpfTestHost.Pump();
                Assert.True(Shown().SequenceEqual(new[] { "404", "503" }), "INPUT-1: a status that appeared while filtering shows, though nobody picked it.");
                Assert.False(PickBox(status, "500").IsChecked);

                Toggle(PickBox(status, "404"));
                Toggle(PickBox(status, "503"));
                Assert.False(status.IsFilterEnabled, "INPUT-1: unpicking every value leaves the filter on.");
                Assert.Equal(new[] { "200", "404", "500", "503" }, Shown());
                Assert.Equal("Status", status.Summary);
                Assert.True(PickBox(status, "*").IsChecked);
                Assert.False(clear.IsEnabled);

                Toggle(PickBox(status, "200"));
                Toggle(PickBox(hosts, "b.example.test"));
                Assert.Empty(Shown());
                Assert.Equal("Host: b.example.test", ((ComboBox)hosts.FindName("FilterList")).ToolTip);
                AssertOneLine("while two filters are on");
                var search = (SearchBox)view.FindName("SearchBox");
                search.Text = "example";
                view.ApplySearch();
                controller.SetProcessFilter(ProcessFilterTestSupport.Spec(null, new[] { "svchost.exe" }));
                WpfTestHost.Pump();
                clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestHost.Pump();
                Assert.False(status.IsFilterEnabled || hosts.IsFilterEnabled, "INPUT-1: Clear filters leaves a dropdown filtering.");
                Assert.Equal(string.Empty, search.Text);
                Assert.True(ProcessFilter.IsEmpty(controller.ProcessFilter));
                Assert.Equal(4, grid.Items.Count);
                Assert.False(clear.IsEnabled);
                AssertOneLine("once the filters are cleared");

                // The search alone makes Clear filters available.
                search.Text = "a.example";
                view.ApplySearch();
                WpfTestHost.Pump();
                Assert.True(clear.IsEnabled, "INPUT-1: Clear filters isn't available while the search filters.");
                clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestHost.Pump();
                Assert.Equal(string.Empty, search.Text);
                Assert.False(clear.IsEnabled);

                // A new list holds new values, so the values picked before don't carry over: the filter turns off.
                Toggle(PickBox(status, "404"));
                Assert.True(status.IsFilterEnabled);
                controller.StatusFilterList = new ObservableCollection<CheckedListItem> { new("404", false), new("503") };
                controller.RaisePropertyChange(nameof(controller.StatusFilterList));
                WpfTestHost.Pump();
                Assert.False(status.IsFilterEnabled);
                Assert.All(controller.StatusFilterList, value => Assert.True(value.IsChecked));
                Assert.Equal("Status", status.Summary);
                Assert.Equal(4, grid.Items.Count);
            });
        }

        // The original look keeps its check box beside each filter, its dropdown says only the filter's name, and it has no
        // Clear filters.
        [Fact]
        public void OriginalLook_KeepsTheCheckBoxBesideEachFilter()
        {
            _application.Run(window => InOriginalLook(() =>
            {
                var controller = Controller();
                controller.ProxyConnections.Add(CaptureTimelineTestData.Create(1, status: "404"));
                var view = View(controller);
                ShowAt(window, view, 1600);
                var status = (CheckBoxFilters)view.FindName("StatusFilters");
                var enable = (CheckBox)status.FindName("EnableFilters");
                Assert.True(enable.IsVisible);
                enable.IsChecked = true;
                WpfTestHost.Pump();
                Assert.Equal("Status", status.Summary);
                Assert.False(((Button)view.FindName("ClearFiltersButton")).IsVisible);
                Assert.Equal("*", PickBox(status, "*").Content);
            }));
        }

        // Runs ACTION with the original look, then restores the look.
        private static void InOriginalLook(Action action)
        {
            bool wasNewLook = LookState.Current.IsNewLook;
            try
            {
                LookState.Current.IsNewLook = false;
                action();
            }
            finally
            {
                LookState.Current.IsNewLook = wasNewLook;
            }
        }

        // The check box for TEXT in FILTER's list, which opens to make it.
        private static CheckBox PickBox(CheckBoxFilters filter, string text)
        {
            var list = (ComboBox)filter.FindName("FilterList");
            list.IsDropDownOpen = true;
            WpfTestHost.Pump();
            CheckedListItem item = list.Items.OfType<CheckedListItem>().First(candidate => candidate.Text == text);
            var container = (ComboBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
            return Descendants(container).OfType<CheckBox>().Single();
        }

        // Toggles BOX as a screen reader does, which clicks and keys also do.
        private static void Toggle(CheckBox box)
        {
            ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Toggle)).Toggle();
            WpfTestHost.Pump();
        }

        // The response pane notes a capture that the capture limit truncated and lists the trailers that followed the body.
        // A response that is still streaming is shown in a notice until it ends, and in the list: the new look's status
        // cell says Streaming (StatusMeaningTests), and the original look shows the row as pending. A complete response
        // without trailers shows none of this, and the request pane never does.
        [Fact]
        public void ResponsePane_ShowsTheTruncationNoticeAndTheTrailers()
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                tab.Add(1, status: null);
                Assert.True(tab.Respond(1, forwarded: true, change: response =>
                {
                    response.Trailers.Add("x-checksum", "abc123");
                    response.CaptureTruncatedAt = 64 * 1024;
                }));
                ProxyConnectionModel truncated = tab.Controller.ProxyConnections.Single(row => row.RequestNumber == 1);
                ProxyConnectionModel complete = tab.Add(2);
                ProxyConnectionModel streaming = StartStreaming(tab, 3);
                var view = new CaptureConnectionView { AutoScroll = false, DataContext = tab };
                CaptureProcessFilterViewTests.ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");
                var request = (RequestResponseView)view.FindName("RequestViewControl");
                var response = (RequestResponseView)view.FindName("ResponseViewControl");
                object pending = Application.Current.FindResource("CapturePendingForegroundBrush");

                grid.SelectedItem = truncated;
                WpfTestHost.Pump();

                Assert.Equal(
                    (Visibility.Visible, "Capture truncated at 64 KB; additional bytes are not captured."),
                    Shown<TextBlock>(response, "NoticeText"));
                Assert.Equal((Visibility.Visible, "x-checksum: abc123\r\n"), Shown<TextBox>(response, "TrailersText"));
                Assert.Equal((Visibility.Visible, "Trailers"), Shown<TextBlock>(response, "TrailersLabel"));
                Assert.Equal(Visibility.Collapsed, Shown<TextBlock>(request, "NoticeText").Visibility);
                Assert.Equal(Visibility.Collapsed, Shown<TextBlock>(request, "TrailersLabel").Visibility);

                grid.SelectedItem = streaming;
                WpfTestHost.Pump();

                Assert.Equal(
                    (Visibility.Visible, "Still streaming: the capture is completed when the response ends."),
                    Shown<TextBlock>(response, "NoticeText"));
                StatusText cell = Descendants(CaptureProcessFilterViewTests.Container(grid, streaming)).OfType<StatusText>().Single();
                Assert.True(cell.IsStreaming && cell.Meaning == StatusMeaning.Working,
                    "STATUS-1: the status of a response still streaming isn't Working.");
                Assert.NotSame(pending, CaptureProcessFilterViewTests.Container(grid, streaming).Foreground);
                InOriginalLook(() =>
                {
                    WpfTestHost.Pump();
                    Assert.Same(pending, CaptureProcessFilterViewTests.Container(grid, streaming).Foreground);
                    Assert.NotSame(pending, CaptureProcessFilterViewTests.Container(grid, complete).Foreground);
                });

                grid.SelectedItem = complete;
                WpfTestHost.Pump();

                Assert.Equal(Visibility.Collapsed, Shown<TextBlock>(response, "NoticeText").Visibility);
                Assert.Equal(Visibility.Collapsed, Shown<TextBox>(response, "TrailersText").Visibility);
                Assert.Equal(Visibility.Collapsed, Shown<TextBlock>(response, "TrailersLabel").Visibility);
                view.DataContext = null;
            });
        }

        // Request NUMBER, from a connection the capture does not track, whose streamed response has sent only its headers.
        private static ProxyConnectionModel StartStreaming(LiveCaptureTab tab, int number)
        {
            var request = new ClientRequest
            {
                RequestNumber = number,
                Version = "HTTP/1.1",
                Scheme = "https",
                Host = "stream.test",
                Port = 443,
                Method = "GET",
                Path = "/events",
            };
            Assert.True(WpfTestHost.Complete(
                tab.Handler<Func<HttpRequestEventArgs, CancellationToken, Task<bool>>>("WebProxy_ReceivedWebRequestAsync")(
                    new HttpRequestEventArgs { ConnectionID = number, Timestamp = DateTime.Now, Request = request },
                    CancellationToken.None),
                TimeSpan.FromSeconds(5)));
            var headers = new ServerResponse
            {
                RequestNumber = number,
                Version = "HTTP/1.1",
                Status = "200",
                StatusDescription = "OK",
            };
            headers.ContentHeaders["Content-Type"] = "text/event-stream";
            WpfTestHost.Complete(
                tab.Handler<Func<HttpResponseEventArgs, CancellationToken, Task>>("WebProxy_WebResponseProgressAsync")(
                    new HttpResponseEventArgs
                    {
                        ConnectionID = number,
                        Timestamp = DateTime.Now,
                        Request = request,
                        Response = headers,
                        ResponseForwarded = true,
                    },
                    CancellationToken.None),
                TimeSpan.FromSeconds(5));
            WpfTestHost.Pump();
            return tab.Controller.ProxyConnections.Single(row => row.RequestNumber == number);
        }

        private static (Visibility Visibility, string Text) Shown<T>(RequestResponseView pane, string name)
            where T : FrameworkElement
        {
            T element = Assert.IsType<T>(pane.FindName(name));
            return (element.Visibility, element switch
            {
                TextBlock text => text.Text,
                TextBox text => text.Text,
                _ => null,
            });
        }

        private sealed class UnusedConfiguration : IProxyDeviceConfiguration
        {
            private readonly TaskCompletionSource<ProxyConfiguration> _pending = new();
            internal int ReadCount { get; private set; }
            public Task<ProxyConfiguration> ReadAsync()
            {
                ReadCount++;
                return _pending.Task;
            }
            public Task DisableAsync() => throw new NotSupportedException();
            public Task EnableAsync(ProxyEndpoint endpoint) => throw new NotSupportedException();
        }

        private static WebServiceDeviceCaptureController Controller() =>
            new("Test device", DeviceType.Unknown, true);

        private static CaptureConnectionView View(WebServiceDeviceCaptureController controller)
        {
            var view = new CaptureConnectionView { AutoScroll = false };
            ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
            view.DataContext = new TestCaptureDeviceContext(controller);
            return view;
        }

        // The traffic pane takes about half of the view's width.
        private static void ShowAt(Window window, CaptureConnectionView view, double width)
        {
            window.Width = width;
            window.Height = 720;
            WpfTestHost.Show(window, view);
        }

        // The filters that fold into the Filters button, in row order.
        private static UIElement[] Filters(CaptureConnectionView view) =>
            new[] { "StatusFilters", "HostFilters", "MethodFilters", "ClearFiltersButton", "ProcessFilterButton" }
                .Select(name => (UIElement)view.FindName(name)).ToArray();

        private static double Middle(UIElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).Transform(new Point(0, element.RenderSize.Height / 2)).Y;

        private static Rect Bounds(UIElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private static void Press(UIElement target, Key key) =>
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });

        private static int HandlerCount(object source) =>
            ((Delegate)source.GetType().GetField("PropertyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source))
                ?.GetInvocationList().Count(handler => handler.Method.DeclaringType == typeof(CaptureConnectionView)) ?? 0;

        private sealed class CountingFilterOptions : ObservableCollection<CheckedListItem>, IEnumerable<CheckedListItem>
        {
            internal int Enumerations { get; private set; }

            IEnumerator<CheckedListItem> IEnumerable<CheckedListItem>.GetEnumerator()
            {
                Enumerations++;
                return GetEnumerator();
            }
        }

        private sealed class TestCaptureDeviceContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }

            internal TestCaptureDeviceContext(IDeviceCaptureController captureController)
            {
                CaptureController = captureController;
            }
        }
    }
}
