// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

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
