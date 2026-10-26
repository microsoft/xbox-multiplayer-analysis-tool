// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls;

namespace XMAT.Tests
{
    // Right-click host actions on capture, timeline and analysis surfaces, and the
    // always-available host-list manager.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class HostListUiTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private readonly CaptureUiApplicationFixture _application;

        public HostListUiTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void CaptureRowMenu_TargetsTheRightClickedRowRatherThanTheSelectedRow()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var selected = CaptureTimelineTestData.Create(1, host: "selected.example");
                var clicked = CaptureTimelineTestData.Create(2, host: "clicked.example");
                CaptureConnectionView view = CaptureView(policy, selected, clicked);
                ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.SelectedItem = selected;
                WpfTestHost.Pump();

                ContextMenu menu = Open(Row(grid, clicked));
                HostListAction block = Actions(menu).Single(action => action.List == HostListKind.Block);
                Assert.Equal("clicked.example", block.Host);
                Assert.Equal(HostListOperation.Add, block.Operation);
                Assert.Same(policy, block.Policy);
                Assert.Equal(Localization.GetLocalizedString("HOST_LIST_ADD_BLOCK", "clicked.example"), Item(menu, block).Header);
                Execute(menu, block);
                menu.IsOpen = false;
                WpfTestHost.Pump();

                Assert.True(policy.BlockList.Contains("clicked.example"));
                Assert.False(policy.BlockList.Contains("selected.example"));
                Assert.Same(selected, grid.SelectedItem);
            });
        }

        [Fact]
        public void KeyboardMenuRoute_UsesTheFocusedRowRatherThanTheStaleSelection()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var selected = CaptureTimelineTestData.Create(1, host: "selected.example");
                var focused = CaptureTimelineTestData.Create(2, host: "focused.example");
                CaptureConnectionView view = CaptureView(policy, selected, focused);
                ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.SelectedItem = selected;
                WpfTestHost.Pump();
                DataGridCell cell = Descendants(Row(grid, focused)).OfType<DataGridCell>().First();

                FocusManager.SetFocusedElement(window, cell);
                WpfTestHost.Pump();
                Assert.Same(cell, FocusManager.GetFocusedElement(window));
                Assert.Same(selected, grid.SelectedItem);

                ContextMenu menu = OpenFromFocus(window);
                Assert.NotEmpty(Actions(menu));
                Assert.All(Actions(menu), action => Assert.Equal("focused.example", action.Host));
                HostListAction block = Actions(menu).Single(action => action.List == HostListKind.Block);
                Execute(menu, block);
                menu.IsOpen = false;
                WpfTestHost.Pump();

                Assert.True(policy.BlockList.Contains("focused.example"));
                Assert.False(policy.BlockList.Contains("selected.example"));
                Assert.Same(selected, grid.SelectedItem);
            });
        }

        [Fact]
        public void OpenCaptureMenu_KeepsItsHostAndOperationWhenRowDataAndPolicyChange()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var clicked = CaptureTimelineTestData.Create(1, host: "clicked.example");
                CaptureConnectionView view = CaptureView(policy, clicked);
                ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");
                DataGridRow row = Row(grid, clicked);

                ContextMenu menu = Open(row);
                HostListAction block = Actions(menu).Single(action => action.List == HostListKind.Block);
                CaptureTimelineTestData.Update(clicked, host: "changed.example");
                Assert.True(policy.BlockList.Add("clicked.example"));
                WpfTestHost.Pump();

                Assert.Same(block, Actions(menu).Single(action => action.List == HostListKind.Block));
                Assert.Equal(Localization.GetLocalizedString("HOST_LIST_ADD_BLOCK", "clicked.example"), Item(menu, block).Header);
                Execute(menu, block);
                Assert.True(policy.BlockList.Contains("clicked.example"));
                Assert.False(policy.BlockList.Contains("changed.example"));
                menu.IsOpen = false;
                WpfTestHost.Pump();

                // Reopening snapshots the row's current host without subscribing the menu to the shared lists.
                int handlers = CollectionHandlerCount(policy.BlockList.BlockedUrls);
                for (int reopen = 0; reopen < 3; reopen++)
                {
                    ContextMenu reopened = Open(row);
                    Assert.Equal(
                        HostListOperation.Add,
                        Actions(reopened).Single(action => action.List == HostListKind.Block).Operation);
                    Assert.All(Actions(reopened), action => Assert.Equal("changed.example", action.Host));
                    reopened.IsOpen = false;
                    WpfTestHost.Pump();
                }
                Assert.Equal(handlers, CollectionHandlerCount(policy.BlockList.BlockedUrls));
            });
        }

        [Fact]
        public void TimelineMenus_TargetTheRightClickedBarOrHostLabel()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var first = CaptureTimelineTestData.Create(1, host: "first.example");
                var second = CaptureTimelineTestData.Create(2, host: "second.example");
                var view = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { first, second },
                    HostListPolicy = policy,
                    SelectedItem = first
                };
                WpfTestHost.Show(window, view);
                var canvas = (Canvas)view.FindName("TimelineCanvas");

                Border bar = canvas.Children.OfType<Border>().Single(candidate => candidate.Tag == second);
                ContextMenu barMenu = Open(bar);
                HostListAction bypass = Actions(barMenu).Single(action => action.List == HostListKind.Bypass);
                Assert.Equal("second.example", bypass.Host);
                Execute(barMenu, bypass);
                barMenu.IsOpen = false;
                Assert.True(policy.BypassList.Contains("second.example"));
                Assert.False(policy.BypassList.Contains("first.example"));

                ((RadioButton)view.FindName("ByHostMode")).IsChecked = true;
                WpfTestHost.Pump();
                TextBlock label = canvas.Children.OfType<TextBlock>().Single(candidate => candidate.Text == "first.example");
                ContextMenu labelMenu = Open(label);
                Assert.Equal(2, Actions(labelMenu).Count);
                Assert.All(Actions(labelMenu), action => Assert.Equal("first.example", action.Host));
                labelMenu.IsOpen = false;
                WpfTestHost.Pump();
            });
        }

        // A host captured in a legacy spelling is shown as sent, but only a strict host identity
        // gets host actions: its row, bar and host label explain that none is available.
        [Fact]
        public void LegacyHostSpellings_OfferNoHostActionsOnCaptureSurfaces()
        {
            _application.Run(window =>
            {
                const string legacyHost = "Reg~Name.Example.Test";
                var policy = new HostListPolicy();
                var legacy = CaptureTimelineTestData.Create(1, host: legacyHost);
                CaptureConnectionView view = CaptureView(policy, legacy);
                ShowCaptureView(window, view);
                AssertNoHostActions(Open(Row((DataGrid)view.FindName("ConnectionList"), legacy)));

                var timeline = new CaptureTimelineView
                {
                    ItemsSource = new ObservableCollection<ProxyConnectionModel> { legacy },
                    HostListPolicy = policy
                };
                WpfTestHost.Show(window, timeline);
                var canvas = (Canvas)timeline.FindName("TimelineCanvas");
                AssertNoHostActions(Open(canvas.Children.OfType<Border>().Single(bar => bar.Tag == legacy)));
                ((RadioButton)timeline.FindName("ByHostMode")).IsChecked = true;
                WpfTestHost.Pump();
                AssertNoHostActions(Open(canvas.Children.OfType<TextBlock>().Single(label => label.Text == legacyHost)));

                Assert.Empty(policy.BlockList.BlockedUrls);
                Assert.Empty(policy.BypassList.BypassedUrls);
            });
        }

        [Fact]
        public void AnalysisSummaryRows_OfferHostActionsOnlyForActionableCapturedEndpoints()
        {
            _application.Run(window =>
            {
                var app = new CaptureAppModel();
                var model = new PerEndpointReportModel(app, "source", AnalysisHostIdentityTests.CreateInGameEventsReport());
                var view = new PerEndpointReportView { DataContext = model };
                WpfTestHost.Show(window, view);
                var grid = (DataGrid)view.FindName("EndpointSummary");

                EndpointValidationCounts synthetic = model.Summary.Endpoints
                    .Single(endpoint => endpoint.Key.Kind == EndpointKind.SyntheticTelemetry);
                ContextMenu syntheticMenu = Open(Row(grid, synthetic));
                Assert.Empty(Actions(syntheticMenu));
                Assert.Contains(syntheticMenu.Items.OfType<MenuItem>(), item =>
                    !item.IsEnabled &&
                    Equals(Localization.GetLocalizedString("HOST_LIST_NO_PHYSICAL_HOST"), item.Header));
                syntheticMenu.IsOpen = false;
                WpfTestHost.Pump();

                EndpointValidationCounts genuine = model.Summary.Endpoints
                    .Single(endpoint => endpoint.Key.Kind == EndpointKind.Captured);
                ContextMenu genuineMenu = Open(Row(grid, genuine));
                HostListAction block = Actions(genuineMenu).Single(action => action.List == HostListKind.Block);
                Assert.Equal("inGameEvents", block.Host);
                Assert.Same(app.HostListPolicy, block.Policy);
                Execute(genuineMenu, block);
                genuineMenu.IsOpen = false;
                WpfTestHost.Pump();
                Assert.True(app.HostListPolicy.BlockList.Contains("ingameevents"));
                Assert.Equal("inGameEvents", Assert.Single(app.HostListPolicy.BlockList.BlockedUrls));
            });
        }

        [Fact]
        public void AnalysisDetailHeaders_OfferHostActionsOnlyForActionableCapturedEndpoints()
        {
            _application.Run(window =>
            {
                var app = new CaptureAppModel();
                var model = new PerEndpointReportModel(app, "source", AnalysisHostIdentityTests.CreateInGameEventsReport());
                var view = new PerEndpointReportView { DataContext = model };
                WpfTestHost.Show(window, view);

                ContextMenu syntheticMenu = Open(DetailHeader(
                    view, new EndpointKey(EndpointKind.SyntheticTelemetry, "inGameEvents")));
                Assert.Empty(Actions(syntheticMenu));
                Assert.Contains(syntheticMenu.Items.OfType<MenuItem>(), item =>
                    !item.IsEnabled &&
                    Equals(Localization.GetLocalizedString("HOST_LIST_NO_PHYSICAL_HOST"), item.Header));
                syntheticMenu.IsOpen = false;
                WpfTestHost.Pump();

                ContextMenu genuineMenu = Open(DetailHeader(
                    view, new EndpointKey(EndpointKind.Captured, "inGameEvents")));
                HostListAction bypass = Actions(genuineMenu).Single(action => action.List == HostListKind.Bypass);
                Assert.Equal("inGameEvents", bypass.Host);
                Assert.Same(app.HostListPolicy, bypass.Policy);
                Execute(genuineMenu, bypass);
                genuineMenu.IsOpen = false;
                WpfTestHost.Pump();
                Assert.True(app.HostListPolicy.BypassList.Contains("ingameevents"));
                Assert.False(app.HostListPolicy.BlockList.Contains("ingameevents"));
            });
        }

        // Summary rows and detail headers tell the captured "inGameEvents" host from the
        // synthesized telemetry by a localized kind, only where a name is shared, while keys,
        // raw names, navigation targets and host actions stay unchanged.
        [Fact]
        public void AnalysisEndpointText_QualifiesOnlySharedNamesWithTheLocalizedKind()
        {
            _application.Run(window =>
            {
                var model = new PerEndpointReportModel(
                    new CaptureAppModel(), "source", AnalysisHostIdentityTests.CreateInGameEventsReport("api.example.test"));
                var view = new PerEndpointReportView { DataContext = model };
                window.Width = 1280;
                window.Height = 720;
                WpfTestHost.Show(window, view);
                var grid = (DataGrid)view.FindName("EndpointSummary");
                var genuineKey = new EndpointKey(EndpointKind.Captured, "inGameEvents");
                var syntheticKey = new EndpointKey(EndpointKind.SyntheticTelemetry, "inGameEvents");
                var otherKey = new EndpointKey(EndpointKind.Captured, "api.example.test");

                foreach ((EndpointKey key, string kindKey) in new[]
                {
                    (genuineKey, "LTA_ENDPOINT_KIND_CAPTURED"),
                    (syntheticKey, "LTA_ENDPOINT_KIND_SYNTHETIC")
                })
                {
                    string kind = Localization.GetLocalizedString(kindKey);
                    Assert.DoesNotContain("NO_IMPL_", kind);
                    EndpointValidationCounts summary = model.Summary.Endpoints.Single(endpoint => endpoint.Key == key);
                    Button endpointButton = EndpointButton(Row(grid, summary));
                    FrameworkElement header = DetailHeader(view, key);
                    foreach (string text in new[] { DisplayedText(endpointButton), DisplayedText(header) })
                    {
                        Assert.Contains("inGameEvents", text);
                        Assert.Contains(kind, text);
                    }
                    Assert.Equal("inGameEvents", summary.EndpointName);
                    Assert.Equal("inGameEvents", ((EndpointValidationDetails)header.DataContext).EndpointName);
                    Assert.Equal(key, endpointButton.CommandParameter);
                }
                Assert.NotEqual(
                    DisplayedText(EndpointButton(Row(grid, model.Summary.Endpoints.Single(e => e.Key == genuineKey)))),
                    DisplayedText(EndpointButton(Row(grid, model.Summary.Endpoints.Single(e => e.Key == syntheticKey)))));

                EndpointValidationCounts other = model.Summary.Endpoints.Single(endpoint => endpoint.Key == otherKey);
                Assert.Equal("api.example.test", DisplayedText(EndpointButton(Row(grid, other))));
                Assert.Equal("api.example.test", DisplayedText(DetailHeader(view, otherKey)));

                EndpointValidationCounts genuine = model.Summary.Endpoints.Single(endpoint => endpoint.Key == genuineKey);
                Assert.Equal("inGameEvents", genuine.ActionableHost);
                Assert.Null(model.Summary.Endpoints.Single(endpoint => endpoint.Key == syntheticKey).ActionableHost);
                ContextMenu menu = Open(Row(grid, genuine));
                Assert.NotEmpty(Actions(menu));
                Assert.All(Actions(menu), action => Assert.Equal("inGameEvents", action.Host));
                menu.IsOpen = false;
                WpfTestHost.Pump();
            });
        }

        [Fact]
        public void EndpointNavigation_UsesTheTypedKeyRatherThanTheDisplayedName()
        {
            _application.Run(window =>
            {
                var model = new PerEndpointReportModel(
                    new CaptureAppModel(), "source", AnalysisHostIdentityTests.CreateInGameEventsReport());
                var view = new PerEndpointReportView { DataContext = model };
                WpfTestHost.Show(window, view);

                Assert.Equal(2, model.Summary.Endpoints.Count);
                foreach (EndpointValidationCounts row in model.Summary.Endpoints)
                {
                    ContentPresenter presenter = view.GetContentForEndpoint(row.Key);
                    Assert.NotNull(presenter);
                    var details = Assert.IsType<EndpointValidationDetails>(presenter.Content);
                    Assert.Equal(row.Key, details.Key);
                }
            });
        }

        [Fact]
        public void CaptureTabs_ReuseTheListViewsForTheSharedSessionPolicy()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                CaptureConnectionView view = CaptureView(policy);
                WpfTestHost.Show(window, view);
                var blockTab = (TabItem)view.FindName("BlockListTab");
                blockTab.IsSelected = true;
                WpfTestHost.Pump();
                var blockView = Assert.IsType<BlockListView>(blockTab.Content);
                var bypassView = Assert.IsType<BypassListView>(((TabItem)view.FindName("BypassListTab")).Content);

                Assert.Same(policy.BlockList, blockView.BlockList);
                Assert.Same(policy.BypassList, bypassView.BypassList);
                Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_SCOPE"), ((TextBlock)blockView.FindName("ScopeText")).Text);
                Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_SCOPE"), ((TextBlock)bypassView.FindName("ScopeText")).Text);
                Assert.DoesNotContain("NO_IMPL_", ((TextBlock)blockView.FindName("ScopeText")).Text);
                // Only the manager hides this note (its intro states the scope); capture tabs show it.
                Assert.Equal(Visibility.Visible, ((TextBlock)blockView.FindName("ScopeText")).Visibility);
                Assert.Equal(Visibility.Visible, ((TextBlock)bypassView.FindName("ScopeText")).Visibility);
            });
        }

        [Fact]
        public void ActionsMenu_OffersLocalizedHostListManagementWithoutASelectedCapture()
        {
            _application.Run(window =>
            {
                XDocument document = ProductMarkup("MainWindow.xaml");
                XElement binding = document.Descendants(Presentation + "CommandBinding")
                    .Single(element => (string)element.Attribute("Executed") == "ManageHostLists_Executed");
                Assert.Equal("{x:Static local:AppCommands.ManageHostLists}", (string)binding.Attribute("Command"));
                Assert.Null(binding.Attribute("CanExecute"));

                // Same product-markup approach as CaptureChromeTests: the window-level binding is
                // stood in for, and the parsed menu proves its command, explicit target and text.
                var model = new CaptureAppModel();
                Assert.Null(model.SelectedDeviceContext);
                var root = Parse<Grid>(document.Root.Element(Presentation + "Grid"));
                int executed = 0;
                window.CommandBindings.Add(new CommandBinding(
                    AppCommands.ManageHostLists, (_, e) => { executed++; e.Handled = true; }));
                window.DataContext = model;
                WpfTestHost.Show(window, root);

                var actions = (MenuItem)root.FindName("ActionsMenu");
                var manage = (MenuItem)root.FindName("ManageHostLists");
                Assert.Contains(manage, actions.Items.Cast<object>());
                Assert.Same(AppCommands.ManageHostLists, manage.Command);
                Assert.Same(root.FindName("AllCaptureDevicesPanel"), manage.CommandTarget);
                Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_MANAGE_MENU"), manage.Header);
                Assert.DoesNotContain("NO_IMPL_", (string)manage.Header);
                Assert.True(AppCommands.ManageHostLists.CanExecute(null, manage.CommandTarget));
                AppCommands.ManageHostLists.Execute(null, manage.CommandTarget);
                Assert.Equal(1, executed);
                window.DataContext = null;
            });
        }

        [Fact]
        public void HostListManager_EditsTheSharedSessionListsWithLocalizedFeedback()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var manager = new HostListManagerWindow(policy);
                try
                {
                    manager.Show();
                    WpfTestHost.Pump();
                    Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_WINDOW_TITLE"), manager.Title);
                    Assert.DoesNotContain("NO_IMPL_", manager.Title);
                    var blockEditor = (BlockListView)manager.FindName("BlockListEditor");
                    var bypassEditor = (BypassListView)manager.FindName("BypassListEditor");
                    Assert.Same(policy.BlockList, blockEditor.BlockList);
                    Assert.Same(policy.BypassList, bypassEditor.BypassList);

                    // A capture tab bound to the same app-owned model sees the manager's edits.
                    var captureTab = new BlockListView { BlockList = policy.BlockList };
                    WpfTestHost.Show(window, captureTab);
                    Submit(blockEditor, "NewBlockTextBox", "AddUrlBlockButton", "managed.example");
                    Assert.True(policy.BlockList.Contains("managed.example"));
                    Assert.Contains("managed.example", ((ListBox)captureTab.FindName("BlockedUrlList")).Items.Cast<string>());

                    // A standalone port is rejected with localized feedback; the text stays for correction.
                    Submit(bypassEditor, "NewBypassTextBox", "AddBypassButton", "[::1]:443");
                    Assert.Empty(policy.BypassList.BypassedUrls);
                    Assert.Equal("[::1]:443", ((TextBox)bypassEditor.FindName("NewBypassTextBox")).Text);
                    Assert.Equal(
                        Localization.GetLocalizedString("HOST_LIST_PORT_NOT_ALLOWED", "[::1]:443"),
                        ((TextBlock)bypassEditor.FindName("EntryErrorText")).Text);

                    Submit(bypassEditor, "NewBypassTextBox", "AddBypassButton", "[::1]");
                    Assert.Equal("::1", Assert.Single(policy.BypassList.BypassedUrls));
                    Assert.True(string.IsNullOrEmpty(((TextBlock)bypassEditor.FindName("EntryErrorText")).Text));
                }
                finally
                {
                    manager.Close();
                }
            });
        }

        // Port attempts are rejected by shape even when the port is empty, zero, out of range
        // or not numeric; both list editors keep the text and show the localized reason.
        [Theory]
        [InlineData("example.com:0")]
        [InlineData("example.com:")]
        [InlineData("[::1]:70000")]
        [InlineData("[::1]:notaport")]
        [InlineData("[example.com]:443")]
        [InlineData("[127.0.0.1]:80")]
        public void HostListManager_RejectsEveryStandalonePortAttemptWithLocalizedFeedback(string text)
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                var manager = new HostListManagerWindow(policy);
                try
                {
                    manager.Show();
                    WpfTestHost.Pump();
                    var blockEditor = (BlockListView)manager.FindName("BlockListEditor");
                    var bypassEditor = (BypassListView)manager.FindName("BypassListEditor");
                    string expected = Localization.GetLocalizedString("HOST_LIST_PORT_NOT_ALLOWED", text);

                    Submit(blockEditor, "NewBlockTextBox", "AddUrlBlockButton", text);
                    Submit(bypassEditor, "NewBypassTextBox", "AddBypassButton", text);

                    Assert.Empty(policy.BlockList.BlockedUrls);
                    Assert.Empty(policy.BypassList.BypassedUrls);
                    Assert.Equal(text, ((TextBox)blockEditor.FindName("NewBlockTextBox")).Text);
                    Assert.Equal(text, ((TextBox)bypassEditor.FindName("NewBypassTextBox")).Text);
                    Assert.Equal(expected, ((TextBlock)blockEditor.FindName("EntryErrorText")).Text);
                    Assert.Equal(expected, ((TextBlock)bypassEditor.FindName("EntryErrorText")).Text);
                    Assert.DoesNotContain("NO_IMPL_", expected);
                }
                finally
                {
                    manager.Close();
                }
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void HostListManager_UsesThemeResources(string theme)
        {
            _application.Run(window =>
            {
                var manager = new HostListManagerWindow(new HostListPolicy());
                try
                {
                    manager.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{theme}Theme.xaml")
                    });
                    manager.Show();
                    WpfTestHost.Pump();

                    Assert.Same(manager.FindResource("WindowBackgroundBrush"), manager.Background);
                    Assert.Same(manager.FindResource("PrimaryForegroundBrush"), manager.Foreground);
                }
                finally
                {
                    manager.Close();
                }
            });
        }

        private static CaptureConnectionView CaptureView(HostListPolicy policy, params ProxyConnectionModel[] rows)
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device", DeviceType.Unknown, true, new ProxyPortPool(), hostListPolicy: policy);
            foreach (ProxyConnectionModel row in rows)
            {
                controller.ProxyConnections.Add(row);
            }
            var view = new CaptureConnectionView { AutoScroll = false };
            ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
            view.DataContext = new Context(controller);
            return view;
        }

        // Shows the capture view at its designed size. In the small default test window the fixed
        // toolbar, filter and timeline rows leave the grid no row viewport, so only its first row
        // container can be realized.
        private static void ShowCaptureView(Window window, CaptureConnectionView view)
        {
            window.Width = 1280;
            window.Height = 720;
            WpfTestHost.Show(window, view);
        }

        private static DataGridRow Row(DataGrid grid, object item)
        {
            grid.ScrollIntoView(item);
            WpfTestHost.Pump();
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
            Assert.NotNull(row);
            return row;
        }

        // The endpoint-name button of a per-endpoint summary row.
        private static Button EndpointButton(DataGridRow row) =>
            Descendants(row).OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, CaptureAnalysisCommands.GoToEndpoint));

        // The text an element actually renders through its text blocks.
        private static string DisplayedText(DependencyObject element) =>
            string.Concat(Descendants(element).OfType<TextBlock>().Select(text => text.Text));

        // Right-click route: the opening event is raised on the clicked element with cursor
        // coordinates and bubbles to the nearest element that owns a context menu.
        private static ContextMenu Open(FrameworkElement clicked) => OpenThroughRoute(clicked, keyboard: false);

        // Menu-key route: the same event is raised on the focused element with the keyboard
        // coordinates (-1, -1). Logical focus stands in for keyboard focus deterministically.
        private static ContextMenu OpenFromFocus(DependencyObject focusScope)
        {
            IInputElement focused = FocusManager.GetFocusedElement(focusScope);
            Assert.NotNull(focused);
            return OpenThroughRoute(focused, keyboard: true);
        }

        private static ContextMenu OpenThroughRoute(IInputElement source, bool keyboard)
        {
            ConstructorInfo constructor = typeof(ContextMenuEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(object), typeof(bool), typeof(double), typeof(double) },
                modifiers: null);
            Assert.NotNull(constructor);
            double position = keyboard ? -1d : 4d;
            var args = (ContextMenuEventArgs)constructor.Invoke(new object[] { source, true, position, position });
            Assert.Same(ContextMenuService.ContextMenuOpeningEvent, args.RoutedEvent);
            source.RaiseEvent(args);

            // As ContextMenuService does when the event is not handled: open the menu of the
            // first element on the route that owns one, for that owner.
            FrameworkElement owner = ContextMenuOwner((DependencyObject)source);
            ContextMenu menu = owner.ContextMenu;
            if (!args.Handled)
            {
                menu.PlacementTarget = owner;
                menu.IsOpen = true;
            }
            WpfTestHost.Pump();
            Assert.True(menu.IsOpen);
            return menu;
        }

        private static FrameworkElement ContextMenuOwner(DependencyObject source)
        {
            for (DependencyObject current = source;
                 current != null;
                 current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement element &&
                    element.ContextMenu != null &&
                    ContextMenuService.GetIsEnabled(element))
                {
                    return element;
                }
            }
            Assert.Fail("No element on the opening route owns a context menu.");
            return null;
        }

        private static FrameworkElement DetailHeader(PerEndpointReportView view, EndpointKey key)
        {
            ContentPresenter presenter = view.GetContentForEndpoint(key);
            Assert.NotNull(presenter);
            var details = Assert.IsType<PerEndpointDetails>(VisualTreeHelper.GetChild(presenter, 0));
            var header = (FrameworkElement)details.FindName("EndpointHeader");
            Assert.NotNull(header);
            Assert.Equal(key, Assert.IsType<EndpointValidationDetails>(header.DataContext).Key);
            return header;
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

        // Parses product markup as CaptureChromeTests does, without the window's code-behind handlers.
        private static T Parse<T>(XElement source)
        {
            var element = new XElement(source);
            var namespaceMap = new Dictionary<string, string>();
            element.Attribute(Xaml + "Key")?.Remove();
            var windowEvents = new[] { "Click", "DeviceTabClosed", "AnalysisTabAdded", "AnalysisTabClosed" };
            foreach (XAttribute attribute in element.DescendantsAndSelf().Attributes()
                .Where(attribute => windowEvents.Contains(attribute.Name.LocalName)).ToArray())
            {
                attribute.Remove();
            }
            foreach (XAttribute declaration in source.Document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) &&
                    !value.Contains(";assembly=", StringComparison.Ordinal))
                {
                    value += ";assembly=XboxMultiplayerAnalysisTool";
                    namespaceMap[declaration.Value] = value;
                }
                element.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement child in element.DescendantsAndSelf())
            {
                if (namespaceMap.TryGetValue(child.Name.NamespaceName, out string qualifiedNamespace))
                {
                    child.Name = XName.Get(child.Name.LocalName, qualifiedNamespace);
                }
            }
            return (T)XamlReader.Parse(element.ToString());
        }

        private static IReadOnlyList<HostListAction> Actions(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().Select(item => item.CommandParameter).OfType<HostListAction>().ToList();

        // A menu for an item without a host identity: only the disabled explanation, then closed.
        private static void AssertNoHostActions(ContextMenu menu)
        {
            Assert.Empty(Actions(menu));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item =>
                !item.IsEnabled && Equals(Localization.GetLocalizedString("HOST_LIST_NO_HOST"), item.Header));
            menu.IsOpen = false;
            WpfTestHost.Pump();
        }

        private static MenuItem Item(ContextMenu menu, HostListAction action) =>
            menu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.CommandParameter, action));

        private static void Execute(ContextMenu menu, HostListAction action)
        {
            MenuItem item = Item(menu, action);
            Assert.True(item.IsEnabled);
            Assert.True(item.Command.CanExecute(item.CommandParameter));
            item.Command.Execute(item.CommandParameter);
            WpfTestHost.Pump();
        }

        private static void Submit(FrameworkElement editor, string textBoxName, string buttonName, string text)
        {
            ((TextBox)editor.FindName(textBoxName)).Text = text;
            ((Button)editor.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WpfTestHost.Pump();
        }

        private static int CollectionHandlerCount(ObservableCollection<string> collection) =>
            ((Delegate)typeof(ObservableCollection<string>)
                .GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(collection))?.GetInvocationList().Length ?? 0;

        private static XDocument ProductMarkup(string name)
        {
            using var stream = typeof(HostListUiTests).Assembly.GetManifestResourceStream("XMAT.Tests." + name);
            Assert.NotNull(stream);
            return XDocument.Load(stream);
        }

        private sealed class Context(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string CaptureTypeString => CaptureType.ToString();
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; } = controller;
        }
    }
}
