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
using System.Windows.Data;
using System.Windows.Input;
using XMAT.NetworkTrace;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
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

        [Fact]
        public void EnabledFilters_UseTheNewContextsValuesAfterContextAndSourceReplacement()
        {
            _application.Run(window =>
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
            });
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

        [Fact]
        public void ReplacingFilterOptions_UsesNewInstancesAndTheirCheckedState()
        {
            _application.Run(window =>
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
            });
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
                // wide as the list, the Process button at its own width.
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
                Assert.InRange(Bounds(filters[3], list).Width - filters[3].DesiredSize.Width, -0.5, 0.5);
                Assert.True(Bounds(filters[3], list).Width < list.ActualWidth / 2);

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
                    Assert.Equal(5, Grid.GetColumn(indicator));
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

                // The process filter's summary and Show all processes stay in view on their line under the Filters
                // button, 4px below it, and the popup doesn't repeat them. A pane this narrow wraps Show all under the
                // summary rather than cutting either short.
                var indicator = (TextBlock)view.FindName("ProcessFilterIndicator");
                var showAll = (Button)view.FindName("ShowAllProcessesButton");
                Assert.True(indicator.IsVisible);
                Assert.True(showAll.IsVisible);
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
                var first = (CheckBox)status.FindName("EnableFilters");

                Assert.True(button.Focus());
                Press(button, Key.Enter);
                WpfTestHost.Pump();
                Assert.True(host.OverflowPopup.IsOpen);
                Assert.Equal(ExpandCollapseState.Expanded, expander.ExpandCollapseState);
                Assert.Same(first, Keyboard.FocusedElement);
                // Tab moves on through the filters in the popup.
                Assert.True(first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(status.IsKeyboardFocusWithin);
                Assert.NotSame(first, Keyboard.FocusedElement);

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
                Assert.Same(first, Keyboard.FocusedElement);
                expander.Collapse();
                WpfTestHost.Pump();
                Assert.False(host.OverflowPopup.IsOpen);
                Assert.True(button.IsKeyboardFocused);
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
            new[] { "StatusFilters", "HostFilters", "MethodFilters", "ProcessFilterButton" }
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
