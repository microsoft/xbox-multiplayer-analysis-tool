// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // The capture tab's process filter only hides rows: the PROCESS column, the selectors from the editor, presets, row
    // and timeline menus, the indicator and "Show all processes". Rows whose process resolves late are shown or hidden
    // one at a time, without refreshing the whole grid.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureProcessFilterViewTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CaptureProcessFilterViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Theory]
        [InlineData("Game.exe", 1234, ClientProcessStatus.Local, false, "Game.exe (1234)")]
        [InlineData("Game.exe", 1234, ClientProcessStatus.Imported, false, "Game.exe (1234)")]
        [InlineData("game", 0, ClientProcessStatus.Imported, false, "game")]
        [InlineData("", 4321, ClientProcessStatus.Imported, false, "PID 4321")]
        [InlineData("", 0, ClientProcessStatus.Remote, false, "Remote device")]
        [InlineData("", 0, ClientProcessStatus.NotRecorded, false, "Unknown")]
        [InlineData("", 0, ClientProcessStatus.Unresolved, false, "Unknown")]
        [InlineData("", 0, ClientProcessStatus.NotRecorded, true, "Resolving\u2026")]
        public void ProcessText_DescribesEveryProcessState(
            string name, int processId, ClientProcessStatus status, bool resolving, string expected)
        {
            Assert.Equal(
                expected,
                CaptureProcessViewAdapter.DisplayText(new ClientProcessInfo(name, processId, null, status), resolving));
        }

        [Fact]
        public void ProcessColumn_ShowsEachRowsProcessAndFollowsALateResolution()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 1234));
                ProxyConnectionModel pending = Row(2, ClientProcessInfo.NotRecorded, resolving: true);
                Add(controller, game, pending);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);

                DataGridColumn column = Assert.Single(grid.Columns, item => Equals(item.Header, "PROCESS"));
                Assert.Equal("Game.exe (1234)", CellText(grid, column, game));
                Assert.Equal("Resolving\u2026", CellText(grid, column, pending));

                pending.SetClientProcess(Local("Tool.exe", 77));
                WpfTestHost.Pump();

                Assert.Equal("Tool.exe (77)", CellText(grid, column, pending));
            });
        }

        [Fact]
        public void ProcessSelectors_FilterTheExistingRowsTogetherWithTheOtherFilters()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel failedGame = Row(2, Local("game.exe", 4100), status: "503");
                ProxyConnectionModel tool = Row(3, Local("Tool.exe", 4200));
                ProxyConnectionModel service = Row(4, Local("svchost.exe", 900));
                ProxyConnectionModel unknown = Row(5, ClientProcessInfo.NotRecorded);
                Add(controller, game, failedGame, tool, service, unknown);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);

                controller.SetProcessFilter(Spec(new[] { "game*.exe" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { game, failedGame }, Visible(grid));

                controller.SetProcessFilter(Spec(null, new[] { "svchost.exe", "4200" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { game, failedGame, unknown }, Visible(grid));

                HideStatus(view, controller, "503");
                Assert.Equal(new[] { game, unknown }, Visible(grid));

                controller.SetProcessFilter(ProcessFilterSpec.Empty);
                WpfTestHost.Pump();
                Assert.Equal(new[] { game, tool, service, unknown }, Visible(grid));
            });
        }

        // D11: a late resolution changes one row: one Add, never a Reset, and the selection stays.
        [Fact]
        public void LateResolution_RevealsOnlyThatRowAndKeepsTheSelection()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel pending = Row(3, ClientProcessInfo.NotRecorded, resolving: true);
                Add(controller, game, tool, pending);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { game }, Visible(grid));
                TextBlock indicator = Indicator(view);
                Assert.Equal("Process filter: 1/3; 2 hidden; 1 unknown", indicator.Text);
                grid.SelectedItem = game;
                WpfTestHost.Pump();
                List<NotifyCollectionChangedAction> changes = RecordChanges(grid);

                pending.SetClientProcess(Local("Game.exe", 4100));
                WpfTestHost.Pump();

                Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, changes);
                Assert.Equal(new[] { game, pending }, Visible(grid));
                Assert.Same(game, grid.SelectedItem);
                Assert.Equal("Process filter: 2/3; 1 hidden; 0 unknown", indicator.Text);
            });
        }

        [Fact]
        public void HidingSelectedRows_DeselectsOnlyThoseRows()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel service = Row(3, Local("svchost.exe", 900));
                Add(controller, game, tool, service);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                grid.SelectedItems.Add(game);
                grid.SelectedItems.Add(tool);
                WpfTestHost.Pump();

                controller.SetProcessFilter(Spec(null, new[] { "Tool.exe" }));
                WpfTestHost.Pump();

                Assert.Equal(new[] { game, service }, Visible(grid));
                Assert.Equal(new object[] { game }, grid.SelectedItems.Cast<object>().ToArray());
            });
        }

        [Fact]
        public void IndicatorAndShowAll_AreShownOnlyWhileAProcessFilterIsSet()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                Add(controller, game, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                TextBlock indicator = Indicator(view);
                var showAll = Assert.IsType<Button>(view.FindName("ShowAllProcessesButton"));
                Assert.False(indicator.IsVisible);
                Assert.False(showAll.IsVisible);

                controller.SetProcessFilter(Spec(null, new[] { "Tool.exe" }));
                WpfTestHost.Pump();
                Assert.True(indicator.IsVisible);
                Assert.Equal("Process filter: 1/2; 1 hidden; 0 unknown", indicator.Text);
                Assert.True(showAll.IsVisible);

                Click(showAll);

                Assert.Empty(Items(controller.ProcessFilter.Include));
                Assert.Empty(Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game, tool }, Visible(grid));
                Assert.False(indicator.IsVisible);
                Assert.False(showAll.IsVisible);
                Assert.Null(grid.Items.Filter);
            });
        }

        [Fact]
        public void ShowAll_ClearsOnlyTheProcessSelectors()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel failedGame = Row(3, Local("Game.exe", 4100), status: "503");
                Add(controller, game, tool, failedGame);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                HideStatus(view, controller, "503");
                controller.SetProcessFilter(Spec(null, new[] { "Tool.exe" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { game }, Visible(grid));

                Click(Assert.IsType<Button>(view.FindName("ShowAllProcessesButton")));

                Assert.Equal(new[] { game, tool }, Visible(grid));
                Assert.True(((CheckBoxFilters)view.FindName("StatusFilters")).IsFilterEnabled);
            });
        }

        [Fact]
        public void HideSvchostPreset_AddsTheServiceHostToTheExclusionsOnce()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel service = Row(2, Local("svchost.exe", 900));
                ProxyConnectionModel otherService = Row(3, Local("SVCHOST.EXE", 901));
                ProxyConnectionModel unknown = Row(4, ClientProcessInfo.NotRecorded);
                Add(controller, game, service, otherService, unknown);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                ProcessFilterSpec include = Spec(new[] { "game*.exe", "svchost" });
                controller.SetProcessFilter(include);
                WpfTestHost.Pump();
                var preset = Assert.IsType<Button>(view.FindName("HideSvchostButton"));

                Click(preset);
                Click(preset);

                ProcessFilterSpec expected = Spec(new[] { "game*.exe", "svchost" }, new[] { "svchost.exe" });
                Assert.Equal(Items(expected.Include), Items(controller.ProcessFilter.Include));
                Assert.Equal(Items(expected.Exclude), Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game }, Visible(grid));
            });
        }

        [Fact]
        public void FilterEditor_AppliesSemicolonSeparatedSelectorsAndKeepsTheFilterOnInvalidInput()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel otherTool = Row(3, Local("tool.exe", 4300));
                ProxyConnectionModel unknown = Row(4, ClientProcessInfo.NotRecorded);
                Add(controller, game, tool, otherTool, unknown);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var include = Assert.IsType<TextBox>(view.FindName("ProcessIncludeText"));
                var exclude = Assert.IsType<TextBox>(view.FindName("ProcessExcludeText"));
                var apply = Assert.IsType<Button>(view.FindName("ApplyProcessFilterButton"));
                var error = Assert.IsType<TextBlock>(view.FindName("ProcessFilterError"));

                include.Text = " game*.exe ; tool.exe ";
                exclude.Text = "4300";
                Click(apply);

                ProcessFilterSpec expected = Spec(new[] { "game*.exe", "tool.exe" }, new[] { "4300" });
                Assert.Equal(Items(expected.Include), Items(controller.ProcessFilter.Include));
                Assert.Equal(Items(expected.Exclude), Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game, tool }, Visible(grid));
                Assert.NotEqual(Visibility.Visible, error.Visibility);

                include.Text = @"C:\Games\Game.exe";
                Click(apply);

                Assert.Equal(Visibility.Visible, error.Visibility);
                Assert.False(string.IsNullOrWhiteSpace(error.Text));
                Assert.DoesNotContain("NO_IMPL_", error.Text, StringComparison.Ordinal);
                Assert.Equal(Items(expected.Include), Items(controller.ProcessFilter.Include));
                Assert.Equal(new[] { game, tool }, Visible(grid));
            });
        }

        [Fact]
        public void RowMenu_ShowsOnlyOrHidesTheRowsProcessAndShowsAllAgain()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel unknown = Row(3, ClientProcessInfo.NotRecorded);
                Add(controller, game, tool, unknown);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);

                Invoke(Open(Container(grid, game)), "Show only \"Game.exe\"");
                Assert.Equal(Items(Spec(new[] { "Game.exe" }).Include), Items(controller.ProcessFilter.Include));
                Assert.Empty(Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game }, Visible(grid));

                Invoke(Open(Container(grid, game)), "Show all processes");
                Assert.Empty(Items(controller.ProcessFilter.Include));
                Assert.Equal(new[] { game, tool, unknown }, Visible(grid));

                Invoke(Open(Container(grid, tool)), "Hide \"Tool.exe\"");
                Assert.Empty(Items(controller.ProcessFilter.Include));
                Assert.Equal(Items(Spec(null, new[] { "Tool.exe" }).Exclude), Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game, unknown }, Visible(grid));

                AssertNoProcessActions(Open(Container(grid, unknown)));
            });
        }

        [Fact]
        public void TimelineBarMenu_HidesTheBarsProcessInTheGridAndTheTimeline()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100), start: CaptureTimelineTestData.Origin);
                ProxyConnectionModel tool = Row(
                    2, Local("Tool.exe", 4200), start: CaptureTimelineTestData.Origin.AddMilliseconds(5));
                Add(controller, game, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                Border bar = Assert.Single(Bars(timeline), item => ReferenceEquals(item.Tag, game));

                Invoke(Open(bar), "Hide \"Game.exe\"");

                Assert.Equal(Items(Spec(null, new[] { "Game.exe" }).Exclude), Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { tool }, Visible(grid));
                Assert.Equal(new[] { tool }, BarModels(timeline));
            });
        }

        // The timeline follows the process filter only; the grid's status, host and method filters leave it unchanged.
        [Fact]
        public void Timeline_FollowsTheProcessFilterButNotTheOtherFilters()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100), start: CaptureTimelineTestData.Origin);
                ProxyConnectionModel failedGame = Row(
                    2, Local("Game.exe", 4100), status: "503", start: CaptureTimelineTestData.Origin.AddMilliseconds(5));
                ProxyConnectionModel tool = Row(
                    3, Local("Tool.exe", 4200), start: CaptureTimelineTestData.Origin.AddMilliseconds(10));
                Add(controller, game, failedGame, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                HideStatus(view, controller, "503");

                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();

                Assert.Equal(new[] { game }, Visible(grid));
                Assert.Equal(new[] { game, failedGame }, BarModels(timeline));
            });
        }

        // D11's gate: 100,000 rows are filtered once, and a late resolution then reveals its row with one Add.
        [Fact]
        public void HundredThousandRows_AreFilteredOnceAndALateResolutionRevealsOneRow()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ClientProcessInfo game = Local("Game.exe", 4100);
                ClientProcessInfo tool = Local("Tool.exe", 4200);
                ProxyConnectionModel pending = null;
                for (var index = 1; index <= 100_000; index++)
                {
                    var model = new ProxyConnectionModel(index, index, 1000);
                    if (index == 50_000)
                    {
                        pending = WithProcess(model, ClientProcessInfo.NotRecorded, resolving: true);
                    }
                    else
                    {
                        WithProcess(model, index % 2 == 0 ? game : tool);
                    }
                    controller.ProxyConnections.Add(model);
                }

                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                Assert.Equal(49_999, grid.Items.Count);
                List<NotifyCollectionChangedAction> changes = RecordChanges(grid);

                ProxyConnectionModel resolved = Assert.IsType<ProxyConnectionModel>(pending);
                resolved.SetClientProcess(game);
                WpfTestHost.Pump();

                Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, changes);
                Assert.Equal(50_000, grid.Items.Count);
            });
        }

        // Sol 1: a new process filter selects and counts the rows already in the tab through the shared operation, over
        // the controller's session snapshot; the grid shows exactly the operation's keys.
        [Fact]
        public void NewProcessFilter_SelectsAndCountsTheExistingRowsThroughTheSharedOperation()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel unknown = Row(3, ClientProcessInfo.NotRecorded);
                ProxyConnectionModel service = Row(4, Local("svchost.exe", 900));
                ProxyConnectionModel imported =
                    Row(5, new ClientProcessInfo("game2.exe", 0, null, ClientProcessStatus.Imported));
                Add(controller, game, tool, unknown, service, imported);
                var selection = new RecordingProcessSelection();
                CaptureConnectionView view = View(controller);
                view.ProcessSelection = selection.Select;
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                int before = selection.Requests.Count;
                ProcessFilterSpec spec = Spec(new[] { "game*.exe", "4200" }, new[] { "svchost.exe" });

                controller.SetProcessFilter(spec);
                WpfTestHost.Pump();

                Assert.Equal(before + 1, selection.Requests.Count);
                FilterCaptureSessionsRequest request = selection.Requests[^1];
                Assert.Equal(Items(spec.Include), Items(request.Filter.Include));
                Assert.Equal(Items(spec.Exclude), Items(request.Filter.Exclude));
                IReadOnlyList<CaptureSessionRef> snapshot = controller.SnapshotProcessSessions("d1");
                Assert.Equal(snapshot.Select(session => session.Key), request.Sessions.Select(session => session.Key));
                Assert.Equal(
                    snapshot.Select(session => session.Process),
                    request.Sessions.Select(session => session.Process));
                FilterCaptureSessionsResult selected = selection.Results[^1];
                AssertSameKeys(selected.IncludedKeys, Visible(grid).Select(model => model.RowKey));
                Assert.Equal(new[] { game, tool, imported }, Visible(grid));
                Assert.Equal(IndicatorText(selected.Scope.Counts), Indicator(view).Text);
            });
        }

        // Sol 1: the operation, not the view's matcher, decides the first selection; a process resolved later is placed
        // by the incremental matcher.
        [Fact]
        public void FirstSelectionFollowsTheSharedOperation_AndALaterResolutionUsesTheIncrementalMatcher()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel otherGame = Row(2, Local("Game.exe", 4100));
                ProxyConnectionModel pending = Row(3, ClientProcessInfo.NotRecorded, resolving: true);
                Add(controller, game, otherGame, pending);
                _ = controller.SnapshotProcessSessions("d1");
                CaptureRowKey dropped = otherGame.RowKey;
                var selection = new RecordingProcessSelection(result => Without(result, dropped));
                CaptureConnectionView view = View(controller);
                view.ProcessSelection = selection.Select;
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);

                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();

                Assert.Equal(new[] { game }, Visible(grid));
                Assert.Equal("Process filter: 1/3; 2 hidden; 1 unknown", Indicator(view).Text);
                List<NotifyCollectionChangedAction> changes = RecordChanges(grid);

                pending.SetClientProcess(Local("Game.exe", 4100));
                WpfTestHost.Pump();

                Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, changes);
                Assert.Equal(new[] { game, pending }, Visible(grid));
                Assert.Equal("Process filter: 2/3; 1 hidden; 0 unknown", Indicator(view).Text);
            });
        }

        // s1: the controller keeps an equal specification, so reapplying the same selectors notifies nothing; neither
        // that nor a notification that does not change the specification makes the view select again or refresh the
        // grid.
        [Fact]
        public void ReapplyingEquivalentSelectors_NotifiesNothingAndDoesNotRecount()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                Add(controller, game, tool);
                var selection = new RecordingProcessSelection();
                CaptureConnectionView view = View(controller);
                view.ProcessSelection = selection.Select;
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                ProcessFilterSpec applied = controller.ProcessFilter;
                int selections = selection.Requests.Count;
                var notifications = new List<string>();
                controller.PropertyChanged += (sender, args) => notifications.Add(args.PropertyName);
                List<NotifyCollectionChangedAction> changes = RecordChanges(grid);

                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                Assert.Same(applied, controller.ProcessFilter);
                Assert.DoesNotContain(nameof(WebServiceDeviceCaptureController.ProcessFilter), notifications);

                controller.RaisePropertyChange(nameof(WebServiceDeviceCaptureController.ProcessFilter));
                WpfTestHost.Pump();

                Assert.Equal(selections, selection.Requests.Count);
                Assert.Empty(changes);
                Assert.Equal(new[] { game }, Visible(grid));
            });
        }

        // s3: the grid filters its own view; other views of the capture stay unfiltered.
        [Fact]
        public void ProcessFilter_LeavesOtherViewsOfTheCaptureUnfiltered()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                Add(controller, game, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                ICollectionView shared = CollectionViewSource.GetDefaultView(controller.ProxyConnections);

                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();

                Assert.Equal(new[] { game }, Visible(grid));
                Assert.Null(shared.Filter);
                Assert.NotEqual(true, ((ICollectionViewLiveShaping)shared).IsLiveFiltering);
                Assert.Equal(new[] { game, tool }, shared.Cast<ProxyConnectionModel>());
            });
        }

        // s3: detaching turns off the grid's own live shaping and leaves shaping that others set on shared views.
        [Fact]
        public void Detach_ReleasesOnlyTheGridsOwnLiveShaping()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                Add(controller, game, tool);
                var shared = (ICollectionViewLiveShaping)CollectionViewSource.GetDefaultView(controller.ProxyConnections);
                shared.LiveFilteringProperties.Add(nameof(ProxyConnectionModel.Status));
                shared.IsLiveFiltering = true;
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                var owned = Assert.IsAssignableFrom<ICollectionViewLiveShaping>(grid.ItemsSource);
                Assert.NotSame(shared, owned);
                Assert.True(owned.IsLiveFiltering);

                view.DataContext = null;
                WpfTestHost.Pump();

                Assert.NotEqual(true, owned.IsLiveFiltering);
                Assert.True(shared.IsLiveFiltering);
                Assert.Equal(new[] { nameof(ProxyConnectionModel.Status) }, shared.LiveFilteringProperties);
            });
        }

        // REG1: views that a capture's notification retires still handle that notification. The grid shows the final
        // capture at once, and afterwards only the grid's current view observes a capture.
        [Fact]
        public void RapidRebindDuringACollectionNotification_LeavesOnlyTheFinalViewObservingItsSource()
        {
            _application.Run(window =>
            {
                var first = Controller();
                var second = Controller();
                ProxyConnectionModel kept = Row(1, Local("Tool.exe", 4200));
                Add(second, kept);
                CaptureConnectionView view = View(first);
                DataGrid grid = ConnectionList(view);
                object shownDuringTheNotification = null;
                var rebound = false;
                first.ProxyConnections.CollectionChanged += (sender, args) =>
                {
                    if (rebound)
                    {
                        return;
                    }
                    rebound = true;
                    view.DataContext = Context(second);
                    view.DataContext = null;
                    view.DataContext = Context(first);
                    view.DataContext = Context(second);
                    shownDuringTheNotification = (grid.ItemsSource as CollectionView)?.SourceCollection;
                };
                ShowCaptureView(window, view);

                first.ProxyConnections.Add(Row(2, Local("Game.exe", 4100)));
                WpfTestHost.Pump();

                Assert.Same(second.ProxyConnections, shownDuringTheNotification);
                Assert.Equal(new[] { kept }, Visible(grid));
                Assert.Equal(0, ObservingViews(first.ProxyConnections));
                Assert.Equal(1, ObservingViews(second.ProxyConnections));
                ProxyConnectionModel added = Row(3, Local("Game.exe", 4100));
                second.ProxyConnections.Add(added);
                WpfTestHost.Pump();
                Assert.Equal(new[] { kept, added }, Visible(grid));
            });
        }

        // REG1: a view that loses its capture while unloaded gives up the grid's view at once; that view stops
        // observing the capture once the notification that retired it has finished.
        [Fact]
        public void LosingTheContextWhileUnloadedDuringACollectionNotification_ReleasesTheViewAfterwards()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                CaptureConnectionView view = View(controller);
                DataGrid grid = ConnectionList(view);
                object shownDuringTheNotification = grid;
                var cleared = false;
                controller.ProxyConnections.CollectionChanged += (sender, args) =>
                {
                    if (cleared)
                    {
                        return;
                    }
                    cleared = true;
                    view.DataContext = null;
                    shownDuringTheNotification = grid.ItemsSource;
                };
                ShowCaptureView(window, view);
                window.Content = null;
                WpfTestHost.Pump();
                Assert.False(view.IsLoaded);
                Assert.Equal(1, ObservingViews(controller.ProxyConnections));

                controller.ProxyConnections.Add(Row(1, Local("Game.exe", 4100)));
                WpfTestHost.Pump();

                Assert.Null(shownDuringTheNotification);
                Assert.Null(grid.ItemsSource);
                Assert.Equal(0, ObservingViews(controller.ProxyConnections));
            });
        }

        // REG1: once the dispatcher has run, the views that the grid gave up no longer observe their captures.
        [Fact]
        public void RetiredViews_StopObservingTheirSourcesOnceTheDispatcherDrains()
        {
            _application.Run(window =>
            {
                var first = Controller();
                var second = Controller();
                Add(first, Row(1, Local("Game.exe", 4100)));
                Add(second, Row(2, Local("Tool.exe", 4200)));
                CaptureConnectionView view = View(first);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                Assert.Equal(1, ObservingViews(first.ProxyConnections));

                view.DataContext = Context(second);
                WpfTestHost.Pump();
                Assert.Equal(0, ObservingViews(first.ProxyConnections));
                Assert.Equal(1, ObservingViews(second.ProxyConnections));

                ProxyConnectionsCollection replaced = second.ProxyConnections;
                ProxyConnectionModel replacement = Row(3, Local("Game.exe", 4100));
                second.ProxyConnections = new ProxyConnectionsCollection { replacement };
                second.RaisePropertyChange(nameof(WebServiceDeviceCaptureController.ProxyConnections));
                WpfTestHost.Pump();
                Assert.Equal(0, ObservingViews(replaced));
                Assert.Equal(1, ObservingViews(second.ProxyConnections));
                Assert.Equal(new[] { replacement }, Visible(grid));

                view.DataContext = null;
                WpfTestHost.Pump();
                Assert.Equal(0, ObservingViews(second.ProxyConnections));
                Assert.Null(grid.ItemsSource);
            });
        }

        // A user's sort stays while the process filter is edited, and the grid keeps the shown rows in that order.
        [Fact]
        public void UserSort_IsKeptWhileTheProcessFilterIsEdited()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel otherGame = Row(3, Local("game2.exe", 4300));
                ProxyConnectionModel service = Row(4, Local("svchost.exe", 900));
                Add(controller, game, tool, otherGame, service);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                DataGridColumn number = grid.Columns[0];
                ClickHeader(grid, number);
                ClickHeader(grid, number);
                Assert.Equal(new[] { service, otherGame, tool, game }, Visible(grid));
                var include = Assert.IsType<TextBox>(view.FindName("ProcessIncludeText"));
                var apply = Assert.IsType<Button>(view.FindName("ApplyProcessFilterButton"));

                include.Text = "game*.exe";
                Click(apply);
                Assert.Equal(new[] { otherGame, game }, Visible(grid));

                include.Text = "game*.exe; Tool.exe";
                Click(apply);
                Assert.Equal(new[] { otherGame, tool, game }, Visible(grid));

                Click(Assert.IsType<Button>(view.FindName("ShowAllProcessesButton")));
                Assert.Equal(new[] { service, otherGame, tool, game }, Visible(grid));
                Assert.Equal(ListSortDirection.Descending, number.SortDirection);
                Assert.Equal(
                    new SortDescription(number.SortMemberPath, ListSortDirection.Descending),
                    Assert.Single(grid.Items.SortDescriptions));
            });
        }

        // A row whose process resolves after the user sorted appears where the sort puts it.
        [Fact]
        public void UserSort_PlacesALateResolvedRowWhereTheSortPutsIt()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel pending = Row(3, ClientProcessInfo.NotRecorded, resolving: true);
                ProxyConnectionModel otherGame = Row(4, Local("Game.exe", 4100));
                Add(controller, game, tool, pending, otherGame);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                DataGridColumn number = grid.Columns[0];
                ClickHeader(grid, number);
                ClickHeader(grid, number);
                controller.SetProcessFilter(Spec(new[] { "Game.exe" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { otherGame, game }, Visible(grid));

                pending.SetClientProcess(Local("Game.exe", 4100));
                WpfTestHost.Pump();

                Assert.Equal(new[] { otherGame, pending, game }, Visible(grid));
                Assert.Equal(ListSortDirection.Descending, number.SortDirection);
                Assert.Equal(
                    new SortDescription(number.SortMemberPath, ListSortDirection.Descending),
                    Assert.Single(grid.Items.SortDescriptions));
            });
        }

        // Sol 2: "Show only" replaces the selectors and clears every exclusion, including wildcards and process ids
        // that would hide the process it shows.
        [Fact]
        public void ShowOnly_ReplacesTheSelectorsAndClearsOverlappingExclusions()
        {
            ProcessFilterSpec current = Spec(new[] { "tool.exe" }, new[] { "game*.exe", "4100", "svchost.exe" });

            OperationResult<ProcessFilterSpec> only = CaptureProcessViewAdapter.WithOnly(current, "Game.exe");

            Assert.True(only.Succeeded);
            Assert.Equal(Items(Spec(new[] { "Game.exe" }).Include), Items(only.Value.Include));
            Assert.Empty(Items(only.Value.Exclude));
        }

        // Sol 2: a process-id exclusion that hides some of a process's rows no longer hides them after "Show only".
        [Fact]
        public void RowMenuShowOnly_RevealsEveryRowOfTheProcessDespiteAnOverlappingProcessIdExclusion()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel hiddenGame = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel game = Row(2, Local("Game.exe", 4200));
                ProxyConnectionModel tool = Row(3, Local("Tool.exe", 4300));
                Add(controller, hiddenGame, game, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                controller.SetProcessFilter(Spec(null, new[] { "4100" }));
                WpfTestHost.Pump();
                Assert.Equal(new[] { game, tool }, Visible(grid));

                Invoke(Open(Container(grid, game)), "Show only \"Game.exe\"");

                Assert.Equal(Items(Spec(new[] { "Game.exe" }).Include), Items(controller.ProcessFilter.Include));
                Assert.Empty(Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { hiddenGame, game }, Visible(grid));
            });
        }

        // s2: the preset first takes the selectors typed but not applied yet, even before the editor's delay ends.
        [Fact]
        public void HideSvchostPreset_AppliesTheEditorsPendingSelectorsBeforeTheDelayEnds()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel tool = Row(2, Local("Tool.exe", 4200));
                ProxyConnectionModel service = Row(3, Local("svchost.exe", 900));
                Add(controller, game, tool, service);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var include = Assert.IsType<TextBox>(view.FindName("ProcessIncludeText"));
                var exclude = Assert.IsType<TextBox>(view.FindName("ProcessExcludeText"));

                include.Text = "game*.exe";
                Click(Assert.IsType<Button>(view.FindName("HideSvchostButton")));

                Assert.Equal(Items(Spec(new[] { "game*.exe" }).Include), Items(controller.ProcessFilter.Include));
                Assert.Equal(Items(Spec(null, new[] { "svchost.exe" }).Exclude), Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game }, Visible(grid));
                Assert.Equal("game*.exe", include.Text);
                Assert.Equal("svchost.exe", exclude.Text);
            });
        }

        // s2: invalid typed selectors stay in the editor with the error, and the preset changes nothing.
        [Fact]
        public void HideSvchostPreset_WithInvalidPendingSelectors_KeepsTheTextAndChangesNothing()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100));
                ProxyConnectionModel service = Row(2, Local("svchost.exe", 900));
                Add(controller, game, service);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var include = Assert.IsType<TextBox>(view.FindName("ProcessIncludeText"));
                var error = Assert.IsType<TextBlock>(view.FindName("ProcessFilterError"));

                include.Text = @"C:\Games\Game.exe";
                Click(Assert.IsType<Button>(view.FindName("HideSvchostButton")));

                Assert.Equal(Visibility.Visible, error.Visibility);
                Assert.Equal(@"C:\Games\Game.exe", include.Text);
                Assert.Empty(Items(controller.ProcessFilter.Include));
                Assert.Empty(Items(controller.ProcessFilter.Exclude));
                Assert.Equal(new[] { game, service }, Visible(grid));
            });
        }

        // Sol 6: the row and timeline menus run the proxy commands that the capability registry classifies.
        [Fact]
        public void RowAndTimelineMenus_RunTheClassifiedProxyCommands()
        {
            _application.Run(window =>
            {
                var controller = Controller();
                ProxyConnectionModel game = Row(1, Local("Game.exe", 4100), start: CaptureTimelineTestData.Origin);
                ProxyConnectionModel tool = Row(
                    2, Local("Tool.exe", 4200), start: CaptureTimelineTestData.Origin.AddMilliseconds(5));
                Add(controller, game, tool);
                CaptureConnectionView view = View(controller);
                ShowCaptureView(window, view);
                DataGrid grid = ConnectionList(view);
                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                controller.SetProcessFilter(Spec(null, new[] { "svchost.exe" }));
                WpfTestHost.Pump();

                AssertProcessCommands(Open(Container(grid, game)));
                AssertProcessCommands(Open(Assert.Single(Bars(timeline), item => ReferenceEquals(item.Tag, game))));
            });
        }

        private static WebServiceDeviceCaptureController Controller() =>
            new("Test device", DeviceType.Unknown, true);

        private static ProxyConnectionModel Row(
            int number,
            ClientProcessInfo process,
            string status = "200",
            bool resolving = false,
            DateTime? start = null) =>
            WithProcess(CaptureTimelineTestData.Create(number, start: start, status: status), process, resolving);

        private static void Add(WebServiceDeviceCaptureController controller, params ProxyConnectionModel[] rows)
        {
            foreach (ProxyConnectionModel row in rows)
            {
                controller.ProxyConnections.Add(row);
            }
        }

        internal static CaptureConnectionView View(WebServiceDeviceCaptureController controller)
        {
            var view = new CaptureConnectionView { AutoScroll = false };
            ConnectionList(view).IsSynchronizedWithCurrentItem = false;
            view.DataContext = Context(controller);
            return view;
        }

        // The designed size: in the small default test window the grid has no row viewport.
        internal static void ShowCaptureView(Window window, CaptureConnectionView view)
        {
            window.Width = 1280;
            window.Height = 720;
            WpfTestHost.Show(window, view);
        }

        private static DataGrid ConnectionList(CaptureConnectionView view) => (DataGrid)view.FindName("ConnectionList");

        private static ProxyConnectionModel[] Visible(DataGrid grid) =>
            grid.Items.Cast<ProxyConnectionModel>().ToArray();

        private static TextBlock Indicator(CaptureConnectionView view) =>
            Assert.IsType<TextBlock>(view.FindName("ProcessFilterIndicator"));

        // Unchecks a status value, then enables the status filter so the grid refreshes.
        private static void HideStatus(CaptureConnectionView view, WebServiceDeviceCaptureController controller, string status)
        {
            controller.StatusFilterList.Single(item => item.Text == status).IsChecked = false;
            var filters = (CheckBoxFilters)view.FindName("StatusFilters");
            ((CheckBox)filters.FindName("EnableFilters")).IsChecked = true;
            WpfTestHost.Pump();
        }

        private static List<NotifyCollectionChangedAction> RecordChanges(DataGrid grid)
        {
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)grid.Items).CollectionChanged += (sender, args) => changes.Add(args.Action);
            return changes;
        }

        internal static DataGridRow Container(DataGrid grid, object item)
        {
            grid.ScrollIntoView(item);
            WpfTestHost.Pump();
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
            Assert.NotNull(row);
            return row;
        }

        private static string CellText(DataGrid grid, DataGridColumn column, ProxyConnectionModel item) =>
            string.Concat(Descendants(column.GetCellContent(Container(grid, item))).OfType<TextBlock>().Select(text => text.Text));

        private static IEnumerable<Border> Bars(CaptureTimelineView timeline) =>
            ((Canvas)timeline.FindName("TimelineCanvas")).Children.OfType<Border>();

        private static ProxyConnectionModel[] BarModels(CaptureTimelineView timeline) =>
            Bars(timeline).Select(bar => bar.Tag).OfType<ProxyConnectionModel>().OrderBy(model => model.RequestNumber).ToArray();

        private static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WpfTestHost.Pump();
        }

        // Sorts the grid by a column as a click on that column's header does. The header's own peer has no Invoke
        // pattern; the column's item under the grid's header row has it, and invoking it clicks the header. That row
        // lists the columns in their order, and each item stands for its column's header value.
        private static void ClickHeader(DataGrid grid, DataGridColumn column)
        {
            DataGridColumnHeaderItemAutomationPeer[] headers = UIElementAutomationPeer.CreatePeerForElement(grid)
                .GetChildren()
                .OfType<DataGridColumnHeadersPresenterAutomationPeer>()
                .Single()
                .GetChildren()
                .OfType<DataGridColumnHeaderItemAutomationPeer>()
                .ToArray();
            Assert.Equal(grid.Columns.Count, headers.Length);
            DataGridColumnHeaderItemAutomationPeer header = headers[grid.Columns.IndexOf(column)];
            Assert.Same(column.Header, header.Item);
            IInvokeProvider click = Assert.IsAssignableFrom<IInvokeProvider>(header.GetPattern(PatternInterface.Invoke));
            click.Invoke();
            WpfTestHost.Pump();
        }

        // The collection views that observe a capture's rows; in these tests only the grid creates views.
        private static int ObservingViews(ProxyConnectionsCollection source)
        {
            FieldInfo handlers = null;
            for (Type type = source.GetType(); handlers == null && type != null; type = type.BaseType)
            {
                handlers = type.GetField(
                    nameof(INotifyCollectionChanged.CollectionChanged),
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
            Assert.NotNull(handlers);
            return ((Delegate)handlers.GetValue(source))?.GetInvocationList()
                .Count(handler => handler.Target is CollectionView) ?? 0;
        }

        // Runs the menu item with this header, as a click does, and closes the menu.
        private static void Invoke(ContextMenu menu, string header)
        {
            try
            {
                MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, header));
                Assert.True(item.IsEnabled);
                if (item.Command is RoutedCommand routed)
                {
                    Assert.True(routed.CanExecute(item.CommandParameter, item.CommandTarget));
                    routed.Execute(item.CommandParameter, item.CommandTarget);
                }
                else if (item.Command != null)
                {
                    Assert.True(item.Command.CanExecute(item.CommandParameter));
                    item.Command.Execute(item.CommandParameter);
                }
                else
                {
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
            }
            finally
            {
                menu.IsOpen = false;
                WpfTestHost.Pump();
            }
        }

        // The process items run the proxy commands, which the capability registry classifies.
        private static void AssertProcessCommands(ContextMenu menu)
        {
            try
            {
                Assert.Equal("ShowOnlyProcess", ProxyCommandName(menu, "Show only \"Game.exe\""));
                Assert.Equal("HideProcess", ProxyCommandName(menu, "Hide \"Game.exe\""));
                Assert.Equal("ShowAllProcesses", ProxyCommandName(menu, "Show all processes"));
            }
            finally
            {
                menu.IsOpen = false;
                WpfTestHost.Pump();
            }
        }

        private static string ProxyCommandName(ContextMenu menu, string header)
        {
            MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, header));
            var command = Assert.IsAssignableFrom<RoutedCommand>(item.Command);
            Assert.Equal(typeof(WebServiceProxyCommands), command.OwnerType);
            Assert.True(command.CanExecute(item.CommandParameter, item.CommandTarget));
            return command.Name;
        }

        // The grid shows each selected key once and nothing else.
        private static void AssertSameKeys(IEnumerable<CaptureRowKey> expected, IEnumerable<CaptureRowKey> actual)
        {
            CaptureRowKey[] expectedKeys = expected.ToArray();
            CaptureRowKey[] actualKeys = actual.ToArray();
            Assert.Equal(expectedKeys.Length, actualKeys.Length);
            Assert.True(
                new HashSet<CaptureRowKey>(expectedKeys).SetEquals(actualKeys),
                "The grid must show exactly the keys that the shared operation selected.");
        }

        private static string IndicatorText(ProcessFilterCounts counts) =>
            $"Process filter: {counts.Included}/{counts.Total}; {counts.Hidden} hidden; {counts.Unknown} unknown";

        // The shared result without one selected key, counted as hidden instead.
        private static FilterCaptureSessionsResult Without(FilterCaptureSessionsResult result, CaptureRowKey dropped)
        {
            Assert.Contains(dropped, result.IncludedKeys);
            ProcessFilterCounts counts = result.Scope.Counts;
            return result with
            {
                IncludedKeys = result.IncludedKeys.Where(key => key != dropped).ToList().AsReadOnly(),
                Scope = result.Scope with
                {
                    Counts = counts with { Included = counts.Included - 1, Hidden = counts.Hidden + 1 },
                },
            };
        }

        // A row without process information offers no process action.
        private static void AssertNoProcessActions(ContextMenu menu)
        {
            try
            {
                Assert.DoesNotContain(
                    menu.Items.OfType<MenuItem>(),
                    item => item.IsEnabled && item.Header is string header &&
                        (header.StartsWith("Show only ", StringComparison.Ordinal) ||
                            header.StartsWith("Hide ", StringComparison.Ordinal)));
            }
            finally
            {
                menu.IsOpen = false;
                WpfTestHost.Pump();
            }
        }

        // The right-click route, as HostListUiTests opens it: ContextMenuOpening bubbles from the clicked element, then
        // the menu of the first element on the route that owns one opens for that owner.
        internal static ContextMenu Open(FrameworkElement clicked)
        {
            ConstructorInfo constructor = typeof(ContextMenuEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(object), typeof(bool), typeof(double), typeof(double) },
                modifiers: null);
            Assert.NotNull(constructor);
            var args = (ContextMenuEventArgs)constructor.Invoke(new object[] { clicked, true, 4d, 4d });
            clicked.RaiseEvent(args);

            FrameworkElement owner = ContextMenuOwner(clicked);
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

        private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
        {
            if (source == null)
            {
                yield break;
            }

            yield return source;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
            {
                foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(source, index)))
                {
                    yield return child;
                }
            }
        }

        private static TestCaptureContext Context(IDeviceCaptureController controller) =>
            new(controller, "Test device", readOnly: true, DeviceType.Unknown);
    }
}
