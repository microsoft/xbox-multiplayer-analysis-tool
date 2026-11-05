// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.Tests.Cli;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // Delete and Shift+Delete in a capture list (P2-8 to P2-11): only the grid's keys and row menu delete, and only the
    // visible selected or unselected rows, after a confirmation that states the counts and defaults to Cancel. Hidden rows,
    // rows that arrive or become hidden meanwhile and read-only tabs are never touched; nothing keeps a deleted session alive.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureDeleteUiTests
    {
        private static readonly int[] Visible = { 1, 2, 6, 7 };
        private readonly CaptureUiApplicationFixture _application;

        public CaptureDeleteUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        // Rows 1, 2, 6 and 7 are visible, and 6 has no response; filters hide 3 (host c.test), 4 (svchost.exe) and 5 (host
        // b.test). Delete selects the next surviving visible row, else the previous one, else none; Shift+Delete keeps the
        // selection; Cancel deletes nothing; the filters keep their choices. Delete in the process filter editor never deletes.
        [Theory]
        [InlineData(false, "2,6", true, "1,3,4,5,7", "7")]
        [InlineData(false, "7", true, "1,2,3,4,5,6", "6")]
        [InlineData(false, "1,2,6,7", true, "3,4,5", "")]
        [InlineData(true, "2,7", true, "2,3,4,5,7", "2,7")]
        [InlineData(false, "2,6", false, "1,2,3,4,5,6,7", "2,6")]
        public void DeleteKeys_DeleteOnlyVisibleRows_ThenRestoreTheSelection(
            bool unselected, string selected, bool confirm, string remaining, string selection)
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                foreach (int number in Enumerable.Range(1, 7))
                {
                    tab.Add(number, number == 4 ? "svchost.exe" : "Game.exe",
                        number switch { 3 => "c.test", 5 => "b.test", 7 => "d.test", _ => "a.test" }, number == 6 ? null : "200");
                }

                tab.Controller.SetProcessFilter(Spec(null, new[] { "svchost.exe" }));
                (CaptureConnectionView view, DataGrid grid, RecordingDeleteDialogs dialogs) = Show(window, tab);
                Uncheck(view, "HostFilters", tab.Controller.HostFilterList, "b.test", "c.test");
                dialogs.Answer = confirm;
                int[] chosen = Numbers(selected);
                Select(grid, chosen.Select(number => Row(tab, number)).ToArray());
                KeyDown((TextBox)view.FindName("ProcessIncludeText"), grid, Key.Delete);
                Assert.Empty(dialogs.Prompts);

                Press(grid, Key.Delete, unselected ? ModifierKeys.Shift : ModifierKeys.None);

                int[] victims = unselected ? Visible.Except(chosen).ToArray() : chosen;
                CaptureDeletePrompt prompt = Assert.Single(dialogs.Prompts);
                Assert.Equal(new CaptureDeletePrompt(unselected, victims.Length, 7 - victims.Length, chosen.Length, 3,
                    victims.Count(number => number == 6)), prompt);
                AssertConfirmation(prompt);
                Assert.Equal(Numbers(remaining), tab.Controller.ProxyConnections.Select(row => (int)row.RequestNumber));
                Assert.Equal(Numbers(selection),
                    grid.SelectedItems.Cast<ProxyConnectionModel>().Select(row => (int)row.RequestNumber).OrderBy(number => number));
                ProxyConnectionModel current = selection.Length == 0 ? null : Row(tab, Numbers(selection)[0]);
                Assert.Same(current, grid.SelectedItem);
                Assert.Same(current, grid.Items.CurrentItem);
                Assert.Same(current, tab.Controller.SelectedConnectionModel);
                Assert.Equal(new[] { ("a.test", true), ("c.test", false), ("b.test", false), ("d.test", true) },
                    tab.Controller.HostFilterList.Select(item => (item.Text, item.IsChecked)));
            });
        }

        // Contract amendment 1: one deleted row reaches the grid as a Remove and 17 as one Reset. On both paths, with the grid
        // sorted by descending request number, the surviving rows keep their objects and order; Delete selects the next
        // surviving visible row in that order and Shift+Delete keeps the selection and current row; the details pane and the
        // indicator follow; Auto Scroll never moves the grid during the deletion; and the timeline draws no deleted row.
        [Theory]
        [InlineData(false, 1)]
        [InlineData(false, 17)]
        [InlineData(true, 1)]
        [InlineData(true, 17)]
        public void FewDeletedRowsArriveAsRemoves_ManyAsOneReset_AndTheViewIsRestoredOnce(bool unselected, int count)
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                foreach (int number in Enumerable.Range(1, 30))
                {
                    tab.Add(number, number % 5 == 0 ? "svchost.exe" : "Game.exe");
                }

                tab.Controller.SetProcessFilter(Spec(null, new[] { "svchost.exe" }));
                (CaptureConnectionView view, DataGrid grid, RecordingDeleteDialogs dialogs) = Show(window, tab);
                grid.Items.SortDescriptions.Add(
                    new SortDescription(nameof(ProxyConnectionModel.RequestNumber), ListSortDirection.Descending));
                WpfTestHost.Pump();
                ProxyConnectionModel[] visible = grid.Items.Cast<ProxyConnectionModel>().ToArray();
                ProxyConnectionModel[] victims = visible.Skip(2).Take(count).ToArray();
                ProxyConnectionModel[] selected = unselected ? visible.Except(victims).ToArray() : victims;
                ProxyConnectionModel current = unselected ? visible[0] : visible[2 + count];
                Select(grid, selected);
                view.AutoScroll = true;
                grid.ScrollIntoView(visible[^1]);
                WpfTestHost.Pump();
                var changes = new List<NotifyCollectionChangedAction>();
                ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, args) => changes.Add(args.Action);
                var broughtIntoView = new List<object>();
                grid.AddHandler(
                    FrameworkElement.RequestBringIntoViewEvent,
                    new RequestBringIntoViewEventHandler(
                        (_, args) => broughtIntoView.Add((args.TargetObject as FrameworkElement)?.DataContext)),
                    handledEventsToo: true);

                Press(grid, Key.Delete, unselected ? ModifierKeys.Shift : ModifierKeys.None);

                Assert.Equal(
                    new[] { count == 1 ? NotifyCollectionChangedAction.Remove : NotifyCollectionChangedAction.Reset }, changes);
                Assert.Empty(dialogs.Notices);
                Assert.Equal(30 - count, tab.Controller.ProxyConnections.Count);
                Assert.Equal(visible.Except(victims), grid.Items.Cast<ProxyConnectionModel>());
                Assert.Equal(
                    (unselected ? selected : new[] { current }).OrderBy(row => row.RequestNumber),
                    grid.SelectedItems.Cast<ProxyConnectionModel>().OrderBy(row => row.RequestNumber));
                Assert.Same(current, grid.SelectedItem);
                Assert.Same(current, grid.Items.CurrentItem);
                Assert.Same(current, tab.Controller.SelectedConnectionModel);
                Assert.Same(current.ResponseBody, ((RequestResponseView)view.FindName("ResponseViewControl")).BodyBytes);
                Assert.Equal(
                    Indicator(included: 24 - count, total: 30 - count), ((TextBlock)view.FindName("ProcessFilterIndicator")).Text);
                Assert.All(broughtIntoView.OfType<ProxyConnectionModel>(), row => Assert.Same(current, row));
                Assert.DoesNotContain(
                    Bars((CaptureTimelineView)view.FindName("TimelineView")),
                    bar => victims.Contains(bar.Tag as ProxyConnectionModel));
            });
        }

        // The rows were frozen before the confirmation opened: a row that arrives meanwhile is not part of the command, and a
        // frozen row that a filter hides meanwhile is kept, which a notice reports.
        [Fact]
        public void RowsThatArriveOrBecomeHiddenWhileConfirming_AreKept_AndTheNoticeCountsThem()
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                ProxyConnectionModel selected = tab.Add(1);
                tab.Add(2);
                ProxyConnectionModel tool = tab.Add(3, process: "Tool.exe");
                (_, DataGrid grid, RecordingDeleteDialogs dialogs) = Show(window, tab);
                ProxyConnectionModel arrived = null;
                dialogs.WhileConfirming = () =>
                {
                    arrived = tab.Add(4);
                    tab.Controller.SetProcessFilter(Spec(null, new[] { "Tool.exe" }));
                    WpfTestHost.Pump();
                };

                Select(grid, selected);
                Press(grid, Key.Delete, ModifierKeys.Shift);

                Assert.Equal(new CaptureDeletePrompt(true, 2, 1, 1, 0, 0), Assert.Single(dialogs.Prompts));
                Assert.Equal(new[] { selected, tool, arrived }, tab.Controller.ProxyConnections);
                Assert.Equal(Localization.GetLocalizedString("CAPTURE_DELETE_RETAINED", 1, 2, 1, 0), Assert.Single(dialogs.Notices));
            });
        }

        // B1: a view that throws on any change of the rows after the deletion committed, on the small Remove path (1 row)
        // and the bulk Reset path (17 rows). The notice says the sessions were deleted, never that nothing was; the row and
        // timeline menus opened on victims let go of them; the capture data operation ends; and the victims are collectable.
        [Theory]
        [InlineData(1)]
        [InlineData(17)]
        public void AViewFailingAfterTheCommit_IsReportedAsADeletion_AndReleasesTheMenusAndTheVictims(int count)
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                WeakReference[] victims = DeleteWhileAViewFails(window, tab, count);
                WpfTestHost.Pump();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.All(victims, victim => Assert.False(victim.IsAlive));
            });
        }

        // Deletes rows 1 to COUNT through the Delete key while both menus target victims and an observer subscribed last
        // throws on every change of the rows; no strong reference to a victim outlives this method.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] DeleteWhileAViewFails(Window window, LiveCaptureTab tab, int count)
        {
            ProxyConnectionModel[] victims = Enumerable.Range(1, count).Select(number => tab.Add(number)).ToArray();
            tab.Add(count + 1);
            (CaptureConnectionView view, DataGrid grid, RecordingDeleteDialogs dialogs) = Show(window, tab);
            Select(grid, victims);
            ContextMenu rowMenu = CaptureProcessFilterViewTests.Open(CaptureProcessFilterViewTests.Container(grid, victims[0]));
            rowMenu.IsOpen = false;
            ContextMenu timelineMenu = CaptureProcessFilterViewTests.Open(
                Bars((CaptureTimelineView)view.FindName("TimelineView")).First(bar => victims.Contains(bar.Tag as ProxyConnectionModel)));
            timelineMenu.IsOpen = false;
            var failures = 0;
            NotifyCollectionChangedEventHandler fail = (_, _) =>
            {
                failures++;
                throw new InvalidOperationException("A view failed.");
            };
            tab.Controller.ProxyConnections.CollectionChanged += fail;
            try
            {
                Press(grid, Key.Delete);
            }
            finally
            {
                tab.Controller.ProxyConnections.CollectionChanged -= fail;
            }

            Assert.NotEqual(0, failures);
            Assert.Equal(Localization.GetLocalizedString("CAPTURE_DELETE_VIEW_FAILED"), Assert.Single(dialogs.Notices));
            Assert.Equal(new long[] { count + 1 }, tab.Controller.ProxyConnections.Select(row => row.RequestNumber));
            Assert.True(XMAT.Models.CaptureAppModel.AppModel.CanModifyCaptureData);
            Assert.All(new[] { rowMenu, timelineMenu }, menu =>
            {
                Assert.Empty(menu.Items);
                Assert.Null(menu.PlacementTarget);
                Assert.False(menu.IsOpen);
            });
            return victims.Select(victim => new WeakReference(victim)).ToArray();
        }

        // An opened capture file and an imported SAZ are read-only: the row menu offers export, its delete items are
        // disabled and say why, and the Delete key does nothing.
        [Fact]
        public void ReadOnlyTabs_OfferExportButNotDeletion()
        {
            using var directory = new CliTestDirectory();
            using var file = new SqLtReadonlyDatabase(
                ProcessCaptures.WriteXmatcap(directory.Root, "opened.xmatcap", ProcessCaptures.MixedSessions));
            _application.Run(window =>
            {
                var opened = new WebServiceDeviceCaptureController(ProcessCaptures.Device, DeviceType.LocalPC, true);
                opened.LoadCaptureDataset(file.TableByName(WebServiceCaptureMethod.TableName).Dataset);
                using var imported = new LiveCaptureTab(readOnly: true);
                imported.Add(1);
                foreach (ICaptureDeviceContext tab in new ICaptureDeviceContext[] { new OpenedTab(opened), imported })
                {
                    (_, DataGrid grid, RecordingDeleteDialogs dialogs) = Show(window, tab);
                    var row = grid.Items.Cast<ProxyConnectionModel>().First();
                    Select(grid, row);
                    ContextMenu menu = CaptureProcessFilterViewTests.Open(CaptureProcessFilterViewTests.Container(grid, row));
                    MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
                    menu.IsOpen = false;
                    foreach (string name in new[] { "DeleteSelectedSessions", "DeleteUnselectedSessions" })
                    {
                        MenuItem item = Assert.Single(items, candidate => CommandName(candidate.Command) == name);
                        Assert.False(item.IsEnabled);
                        Assert.True(ToolTipService.GetShowOnDisabled(item));
                        Assert.Equal(Localization.GetLocalizedString("CAPTURE_DELETE_READ_ONLY"), item.ToolTip);
                    }

                    Assert.Contains(items, item => CommandName(item.Command) == "ExportSelectedSessions" && item.IsEnabled);
                    Press(grid, Key.Delete);
                    Assert.Empty(dialogs.Prompts);
                }
            });
        }

        [Fact]
        public void Delete_ReleasesTheSession_AndUpdatesTheIndicatorAndTimeline()
        {
            _application.Run(window =>
            {
                using var tab = new LiveCaptureTab();
                ProxyConnectionModel survivor = tab.Add(1);
                tab.Add(2, process: "svchost.exe");
                WeakReference[] released = DeleteWhileEveryViewHoldsIt(window, tab, survivor);
                WpfTestHost.Pump();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.All(released, reference => Assert.False(reference.IsAlive));
            });
        }

        // Deletes session 3 while the grid selects it, the details pane shows its body, the row menu targets it, a timeline
        // bar draws it and its WebSocket frame is selected; no strong reference to it outlives this method.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] DeleteWhileEveryViewHoldsIt(Window window, LiveCaptureTab tab, ProxyConnectionModel survivor)
        {
            ProxyConnectionModel victim = tab.Add(3);
            tab.Frame(3, "victim frame");
            WebSocketMessageModel frame = Assert.Single(tab.Controller.WebSocketMessages);
            tab.Controller.SetProcessFilter(Spec(null, new[] { "svchost.exe" }));
            (CaptureConnectionView view, DataGrid grid, _) = Show(window, tab);
            var timeline = (CaptureTimelineView)view.FindName("TimelineView");
            var indicator = (TextBlock)view.FindName("ProcessFilterIndicator");
            tab.Controller.SelectedWebSocketMessage = frame;
            Select(grid, victim);
            CaptureProcessFilterViewTests.Open(CaptureProcessFilterViewTests.Container(grid, victim)).IsOpen = false;
            Assert.Contains(Bars(timeline), bar => ReferenceEquals(bar.Tag, victim));
            Assert.Equal(Indicator(included: 2, total: 3), indicator.Text);

            Press(grid, Key.Delete);

            Assert.Equal(new[] { survivor }, grid.Items.Cast<ProxyConnectionModel>());
            Assert.Same(survivor, tab.Controller.SelectedConnectionModel);
            Assert.Empty(tab.Controller.WebSocketMessages);
            Assert.Null(tab.Controller.SelectedWebSocketMessage);
            Assert.Equal(new object[] { survivor }, Bars(timeline).Select(bar => bar.Tag).Where(tag => tag != null));
            Assert.Equal(Indicator(included: 1, total: 2), indicator.Text);
            return new[] { new WeakReference(victim), new WeakReference(victim.ResponseBody), new WeakReference(frame) };
        }

        private static (CaptureConnectionView View, DataGrid Grid, RecordingDeleteDialogs Dialogs) Show(Window window, ICaptureDeviceContext tab)
        {
            var dialogs = new RecordingDeleteDialogs();
            var view = new CaptureConnectionView { AutoScroll = false, DataContext = tab, DeleteDialogs = dialogs };
            CaptureProcessFilterViewTests.ShowCaptureView(window, view);
            return (view, (DataGrid)view.FindName("ConnectionList"), dialogs);
        }

        private static void Select(DataGrid grid, params ProxyConnectionModel[] rows)
        {
            grid.SelectedItems.Clear();
            Array.ForEach(rows, row => grid.SelectedItems.Add(row));
            WpfTestHost.Pump();
        }

        // Presses KEY in the grid, which must bind it: Delete as the keyboard raises it; Shift cannot be held in a test, so
        // Shift+Delete runs the grid's binding for that gesture.
        private static void Press(DataGrid grid, Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            KeyBinding binding = Assert.Single(grid.InputBindings.OfType<KeyBinding>(), item => item.Key == key && item.Modifiers == modifiers);
            var command = Assert.IsAssignableFrom<RoutedCommand>(binding.Command);
            Assert.Equal(typeof(WebServiceProxyCommands), command.OwnerType);
            if (modifiers == ModifierKeys.None)
            {
                KeyDown(grid, grid, key);
                return;
            }

            command.Execute(binding.CommandParameter, grid);
            WpfTestHost.Pump();
        }

        // A key without modifiers as the keyboard raises it on TARGET: the input bindings on the target's route handle it.
        private static void KeyDown(UIElement target, Visual shown, Key key)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shown), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            WpfTestHost.Pump();
        }

        // Unchecks values of a checkbox filter, then enables the filter.
        private static void Uncheck(CaptureConnectionView view, string filter, IEnumerable<CheckedListItem> items, params string[] values)
        {
            foreach (CheckedListItem item in items.Where(item => values.Contains(item.Text)))
            {
                item.IsChecked = false;
            }

            ((CheckBox)((CheckBoxFilters)view.FindName(filter)).FindName("EnableFilters")).IsChecked = true;
            WpfTestHost.Pump();
        }

        // The confirmation states the prompt's counts and, only when there are some, the sessions without a complete
        // response; Cancel is its default answer.
        private static void AssertConfirmation(CaptureDeletePrompt prompt)
        {
            var dialog = MainWindow.DeleteConfirmation(prompt);
            string counts = prompt.Unselected
                ? Localization.GetLocalizedString("CAPTURE_DELETE_CONFIRM_UNSELECTED", prompt.Sessions, prompt.Selected, prompt.Hidden)
                : Localization.GetLocalizedString("CAPTURE_DELETE_CONFIRM_SELECTED", prompt.Sessions, prompt.Kept, prompt.Hidden);
            string incomplete = Localization.GetLocalizedString("CAPTURE_DELETE_INCOMPLETE", prompt.Incomplete);
            Assert.Equal(
                (MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel),
                (dialog.Buttons, dialog.Icon, dialog.DefaultResult));
            Assert.Contains(counts, dialog.Text);
            Assert.Equal(prompt.Incomplete > 0, dialog.Text.Contains(incomplete, StringComparison.Ordinal));
            Assert.DoesNotContain("NO_IMPL_", dialog.Text + dialog.Caption, StringComparison.Ordinal);
        }

        private static int[] Numbers(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

        private static ProxyConnectionModel Row(LiveCaptureTab tab, int number) =>
            tab.Controller.ProxyConnections.Single(row => row.RequestNumber == number);

        private static string CommandName(ICommand command) => (command as RoutedCommand)?.Name;

        private static IEnumerable<Border> Bars(CaptureTimelineView timeline) =>
            ((Canvas)timeline.FindName("TimelineCanvas")).Children.OfType<Border>();

        private static string Indicator(int included, int total) =>
            Localization.GetLocalizedString("PROCESS_FILTER_INDICATOR", included, total, total - included, 0);

        private sealed class RecordingDeleteDialogs : ICaptureDeleteDialogService
        {
            internal bool Answer { get; set; } = true;
            internal Action WhileConfirming { get; set; }
            internal List<CaptureDeletePrompt> Prompts { get; } = new();
            internal List<string> Notices { get; } = new();

            public Task<bool> ConfirmDeleteAsync(CaptureDeletePrompt prompt)
            {
                Prompts.Add(prompt);
                WhileConfirming?.Invoke();
                return Task.FromResult(Answer);
            }

            public Task ShowDeleteNoticeAsync(string message) { Notices.Add(message); return Task.CompletedTask; }
        }

        private sealed class OpenedTab(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => ProcessCaptures.Device;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; } = controller;
        }
    }
}
