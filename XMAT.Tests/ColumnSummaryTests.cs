// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ColumnSummaryTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        // Layout positions compared across elements can differ by rounding.
        private const double Tolerance = 0.01;
        private readonly CaptureUiApplicationFixture _application;

        public ColumnSummaryTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public void Counts_ListTheSixMostCommonValuesLargestFirstThenTheRestInOneRow()
        {
            InEnglish(() =>
            {
                string[] rows = Rows(
                    ("sessiondirectory.xboxlive.com", 412), ("userpresence.xboxlive.com", 268), ("profile.xboxlive.com", 187),
                    ("social.xboxlive.com", 121), ("achievements.xboxlive.com", 96), ("title.mgt.xboxlive.com", 88),
                    ("privacy.xboxlive.com", 24), ("userstats.xboxlive.com", 20), ("xsts.auth.xboxlive.com", 18),
                    ("inventory.xboxlive.com", 14), ("leaderboards.xboxlive.com", 12), ("clubhub.xboxlive.com", 10),
                    ("gameclips.xboxlive.com", 8), ("screenshots.xboxlive.com", 6));
                var hosts = new ColumnSummaryColumn(row => (string)row)
                {
                    RowCountText = Requests,
                    ValueCountText = count => ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_HOST", "COLUMN_SUMMARY_HOSTS"),
                    MoreValuesText = count => Localization.GetLocalizedString("COLUMN_SUMMARY_MORE_HOSTS", count),
                    Select = _ => { },
                };

                ColumnSummaryModel model = ColumnSummaryModel.Create("Host", hosts, rows, "privacy.xboxlive.com");

                Assert.Equal("1,284 requests \u00B7 14 hosts", model.Totals);
                Assert.Equal(
                    new[]
                    {
                        "sessiondirectory.xboxlive.com", "userpresence.xboxlive.com", "profile.xboxlive.com",
                        "social.xboxlive.com", "achievements.xboxlive.com", "title.mgt.xboxlive.com", "8 more hosts",
                    },
                    model.Rows.Select(row => row.Label));
                Assert.Equal(new[] { "412", "268", "187", "121", "96", "88", "112" }, model.Rows.Select(row => row.CountText));
                Assert.Equal(new[] { "32%", "21%", "15%", "9%", "7%", "7%", "9%" }, model.Rows.Select(row => row.PercentText));
                // Bars are scaled to the largest row. The rest can't be chosen, and it has the selected row's value here.
                Assert.Equal(1, model.Rows[0].Fraction);
                Assert.Equal(112.0 / 412, model.Rows[6].Fraction, 6);
                Assert.Equal(new[] { true, true, true, true, true, true, false }, model.Rows.Select(row => row.CanSelect));
                Assert.Equal(6, model.Rows.ToList().FindIndex(row => row.IsCurrent));
                Assert.Equal("8 more hosts, 112 requests, 9 percent", model.Rows[6].AccessibleName);
            });
        }

        [Fact]
        public void Counts_ShowEveryValueWhenThereAreFew_AndMarkTheSelectedRowsValue()
        {
            InEnglish(() =>
            {
                string[] rows = Rows(("200", 1102), ("304", 98), ("429", 41), ("404", 22), ("500", 14), (string.Empty, 7));
                var statuses = new ColumnSummaryColumn(row => (string)row)
                {
                    EmptyValueText = "No status",
                    RowCountText = Requests,
                    BarOf = ColumnSummaryBars.ForHttpStatus,
                };

                ColumnSummaryModel model = ColumnSummaryModel.Create("Status", statuses, rows, "429");

                Assert.Equal("1,284 requests", model.Totals);
                Assert.Equal(new[] { "200", "304", "429", "404", "500", "No status" }, model.Rows.Select(row => row.Label));
                Assert.Equal(new[] { "86%", "8%", "3%", "2%", "1%", "<1%" }, model.Rows.Select(row => row.PercentText));
                Assert.Equal(
                    new[]
                    {
                        ColumnSummaryBar.Success, ColumnSummaryBar.Redirect, ColumnSummaryBar.ClientError,
                        ColumnSummaryBar.ClientError, ColumnSummaryBar.ServerError, ColumnSummaryBar.NoStatus,
                    },
                    model.Rows.Select(row => row.Bar));
                Assert.Equal("429, 41 requests, 3 percent", model.Rows[2].AccessibleName);
                Assert.Equal("No status, 7 requests, less than 1 percent", model.Rows[5].AccessibleName);
                ColumnSummaryRow current = Assert.Single(model.Rows, row => row.IsCurrent);
                Assert.Equal("429", current.Label);
                Assert.Equal(Localization.GetLocalizedString("COLUMN_SUMMARY_SELECTED_ROW"), current.ItemStatus);
                // Without a filter nothing can be chosen, and the footer says so.
                Assert.DoesNotContain(model.Rows, row => row.CanSelect);
                Assert.Equal(Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_NO_FILTER"), model.Hint);

                // Seven values are all shown; equal counts go by value, with the empty value last.
                ColumnSummaryModel seven = ColumnSummaryModel.Create(
                    "Status", statuses, Rows(("g", 1), ("f", 1), (string.Empty, 1), ("d", 1), ("c", 1), ("b", 1), ("a", 1)), null);
                Assert.Equal(new[] { "a", "b", "c", "d", "f", "g", "No status" }, seven.Rows.Select(row => row.Label));
                Assert.DoesNotContain(seven.Rows, row => row.IsMore || row.IsCurrent);
            });
        }

        [Theory]
        [InlineData(1102, 1284, 86, false)]
        [InlineData(14, 1284, 1, false)]
        [InlineData(7, 1284, 0, true)]
        [InlineData(1, 8, 13, false)]
        [InlineData(1283, 1284, 99, false)]
        [InlineData(1284, 1284, 100, false)]
        public void Percent_IsWholeAndNeverHidesASmallShareOrRoundsAPartOneUpToAll(
            int count, int total, int percent, bool underOne) =>
            Assert.Equal((percent, underOne), ColumnSummaryCounts.Percent(count, total));

        [Theory]
        [InlineData("200", ColumnSummaryBar.Success)]
        [InlineData("304", ColumnSummaryBar.Redirect)]
        [InlineData("429", ColumnSummaryBar.ClientError)]
        [InlineData("503", ColumnSummaryBar.ServerError)]
        [InlineData("", ColumnSummaryBar.NoStatus)]
        [InlineData("101", ColumnSummaryBar.NoStatus)]
        public void StatusBars_TakeTheTimelinesStatusColors(string status, ColumnSummaryBar bar) =>
            Assert.Equal(bar, ColumnSummaryBars.ForHttpStatus(status));

        // Each column but # has an info icon: a button named for its column, also in its tooltip, that tells screen
        // readers whether its summary is open.
        [Fact]
        public void InfoIcons_ShowOnEveryColumnButTheNumberColumn_AsButtonsNamedForTheirColumn()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                ColumnSummaryButton[] icons = grid.Columns.Select(column => Icon(grid, column)).ToArray();
                Assert.Equal(Visibility.Collapsed, icons[0].Visibility);
                Assert.All(icons.Skip(1), icon => Assert.Equal(Visibility.Visible, icon.Visibility));
                // The filler header after the last column has none either.
                Assert.Equal(icons.Skip(1), popup.Buttons);
                Assert.Equal(
                    new[] { "Status summary", "Method summary", "Scheme summary", "Host summary", "Process summary", "Path summary" },
                    popup.Buttons.Select(AutomationProperties.GetName));
                Assert.All(popup.Buttons, icon => Assert.Equal(AutomationProperties.GetName(icon), icon.ToolTip));

                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(icons[4]);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Equal("Host summary", peer.GetName());
                Assert.Empty(peer.GetChildren() ?? new List<AutomationPeer>());
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                var summary = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
                Assert.Equal(ExpandCollapseState.Collapsed, summary.ExpandCollapseState);
                summary.Expand();
                WpfTestHost.Pump();
                Assert.True(popup.IsOpen);
                Assert.Same(grid.Columns[4], popup.Column);
                Assert.Equal(ExpandCollapseState.Expanded, summary.ExpandCollapseState);
                summary.Collapse();
                WpfTestHost.Pump();
                Assert.False(popup.IsOpen);
                Assert.Equal(ExpandCollapseState.Collapsed, summary.ExpandCollapseState);
            });
        }

        // The icon's 20px hit area ends 4px before the column's divider, above the gripper that resizes the column, in
        // both spacings. Sorted, Status, Method and Scheme show their whole name with the sort arrow just before the
        // icon at their default widths, and a narrow column shortens its name, not the icon.
        [Fact]
        public void InfoIcons_End4pxBeforeTheDivider_FollowTheSortArrow_AndKeepTheirSizeInNarrowColumns()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                Window window = Window.GetWindow(grid);
                int[] sortedColumns = { 1, 2, 3 };
                foreach (int column in sortedColumns)
                {
                    grid.Columns[column].SortDirection = ListSortDirection.Ascending;
                }

                grid.Columns[4].MinWidth = 40;
                grid.Columns[4].Width = new DataGridLength(48);
                try
                {
                    foreach (bool comfort in new[] { false, true })
                    {
                        ComfortView.Apply(window.Resources, comfort);
                        WpfTestHost.Pump();
                        Assert.Equal(comfort ? 36 : 28, Header(grid, 1).ActualHeight);
                        for (int column = 1; column < grid.Columns.Count; column++)
                        {
                            DataGridColumnHeader header = Header(grid, column);
                            ColumnSummaryButton icon = Icon(grid, grid.Columns[column]);
                            Rect area = Bounds(icon, header);
                            Assert.Equal(new Size(20, 20), area.Size);
                            Assert.Equal(4, Bounds(Part(header, "Divider"), header).Left - area.Right, 3);
                            Assert.True(area.Top >= 0 && area.Bottom <= header.ActualHeight, "The icon fits the header.");
                            Rect label = VisibleBounds(Label(header), header);
                            Assert.True(label.Right <= area.Left + Tolerance, $"The {Label(header).Text} name ends at {label.Right}, after the icon's {area.Left}.");
                            foreach (Point point in new[]
                            {
                                new Point(area.Left + 0.5, area.Top + 0.5), new Point(area.Right - 0.5, area.Top + 0.5),
                                new Point(area.Left + 0.5, area.Bottom - 0.5), new Point(area.Right - 0.5, area.Bottom - 0.5),
                            })
                            {
                                Assert.True(
                                    header.InputHitTest(point) is DependencyObject hit && (hit == icon || icon.IsAncestorOf(hit)),
                                    $"The icon takes a click at {point} in the {grid.Columns[column].Header} header.");
                            }
                        }

                        foreach (int column in sortedColumns)
                        {
                            DataGridColumnHeader sorted = Header(grid, column);
                            Rect arrow = Bounds(Part(sorted, "SortArrow"), sorted);
                            TextBlock name = Label(sorted);
                            Assert.Equal(Visibility.Visible, Part(sorted, "SortArrow").Visibility);
                            Assert.InRange(Bounds(Icon(grid, grid.Columns[column]), sorted).Left - arrow.Right, 0, 4);
                            Assert.True(VisibleBounds(name, sorted).Right <= arrow.Left + Tolerance, $"The {name.Text} name ends before the sort arrow.");
                            // A shortened name loses at least a letter to the ellipsis; layout rounding moves a whole one by
                            // less than a pixel.
                            Assert.True(
                                name.ActualWidth >= FullWidth(name) - 1,
                                $"Sorted, {name.Text} shows its whole name: {name.ActualWidth} of {FullWidth(name)} (Comfort view {(comfort ? "on" : "off")}).");
                        }

                        TextBlock narrow = Label(Header(grid, 4));
                        Assert.True(narrow.ActualWidth < FullWidth(narrow), "A narrow column shortens its name.");
                    }
                }
                finally
                {
                    ComfortView.Apply(window.Resources, on: true);
                }
            });
        }

        // A click, Enter, Space or Alt+Down on the icon opens the summary under its column with focus on its first value;
        // Esc closes it and returns focus to the icon, and so does a click on the icon. The icon never sorts its column.
        [Fact]
        public void TheIcon_OpensTheSummaryWithFocusOnItsFirstValue_FromAClickEnterSpaceOrAltDown_AndNeverSorts()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                long[] order = Visible(grid);
                ColumnSummaryButton host = Icon(grid, grid.Columns[4]);
                Assert.Equal(ColorOf(host, "TextSecondaryBrush"), GlyphColor(host));

                // A press and release on the icon are the icon's alone: they are handled before they reach its header,
                // which would otherwise sort, resize or move the column.
                var handled = new List<bool>();
                var aboveIcon = (UIElement)VisualTreeHelper.GetParent(host);
                aboveIcon.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => handled.Add(e.Handled)), true);
                aboveIcon.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => handled.Add(e.Handled)), true);
                foreach (RoutedEvent button in new[] { Mouse.MouseDownEvent, Mouse.MouseUpEvent })
                {
                    host.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = button });
                }

                WpfTestHost.Pump();
                Assert.Equal(new[] { true, true }, handled);
                Assert.True(host.IsKeyboardFocused);

                Activate(host);
                Assert.True(popup.IsOpen);
                Assert.Same(grid.Columns[4], popup.Column);
                Assert.True(host.IsSummaryOpen);
                Assert.Equal(ColorOf(host, "AccentBrush"), GlyphColor(host));
                Assert.Equal("a.test", Assert.IsType<ColumnSummaryRow>(Assert.IsType<ListBoxItem>(Keyboard.FocusedElement).DataContext).Label);
                // Pressing the icon again takes focus back to it and leaves the summary open, so that the click closes it.
                host.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                WpfTestHost.Pump();
                Assert.True(host.IsKeyboardFocused);
                Assert.True(popup.IsOpen);
                Activate(host);
                Assert.False(popup.IsOpen);
                Assert.False(host.IsSummaryOpen);
                Assert.True(host.IsKeyboardFocused);

                foreach (Action<UIElement> open in new Action<UIElement>[]
                {
                    target => Press(target, Key.Enter),
                    PressSpace,
                    target => Press(target, Key.Down, system: true),
                })
                {
                    open(host);
                    Assert.True(popup.IsOpen);
                    Assert.Same(grid.Columns[4], popup.Column);
                    ListBoxItem first = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                    Assert.Equal("a.test, 3 requests, 50 percent", AutomationProperties.GetName(first));
                    Press(first, Key.Escape);
                    Assert.False(popup.IsOpen);
                    Assert.True(host.IsKeyboardFocused);
                }

                Assert.Null(grid.Columns[4].SortDirection);
                Assert.Equal(order, Visible(grid));

                // The rest of the header still sorts the column.
                ClickHeader(grid, grid.Columns[4]);
                Assert.Equal(ListSortDirection.Ascending, grid.Columns[4].SortDirection);
            });
        }

        // The icons are one tab stop, the icon used last or else the first, with a 1px accent ring for keyboard focus;
        // Left, Right, Home and End move between them, and Tab moves on to the rows.
        [Fact]
        public void InfoIcons_AreOneTabStop_TheLastUsedOrTheFirst_AndLeftAndRightMoveBetweenThem()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                IReadOnlyList<ColumnSummaryButton> icons = popup.Buttons;
                bool[] Stops() => icons.Select(icon => icon.IsTabStop).ToArray();
                var before = (GridSplitter)view.FindName("TimelineSplitter");

                Assert.Equal(new[] { true, false, false, false, false, false }, Stops());
                Assert.True(before.Focus());
                Assert.True(before.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(icons[0].IsKeyboardFocused);
                Assert.Equal(Visibility.Visible, Part(icons[0], "FocusRing").Visibility);
                Assert.Equal(ColorOf(icons[0], "TextPrimaryBrush"), GlyphColor(icons[0]));

                Press(icons[0], Key.Left);
                Assert.True(icons[0].IsKeyboardFocused);
                Press(icons[0], Key.Right);
                Press(icons[1], Key.Right);
                Assert.True(icons[2].IsKeyboardFocused);
                Press(icons[2], Key.End);
                Assert.True(icons[5].IsKeyboardFocused);
                Press(icons[5], Key.Right);
                Assert.True(icons[5].IsKeyboardFocused);
                Press(icons[5], Key.Home);
                Press(icons[0], Key.Right);
                Assert.True(icons[1].IsKeyboardFocused);
                Assert.Equal(Visibility.Collapsed, Part(icons[0], "FocusRing").Visibility);
                Assert.Equal(new[] { false, true, false, false, false, false }, Stops());
                Assert.False(popup.IsOpen);

                // Back in the table, Tab lands on the icon used last.
                Assert.True(before.Focus());
                Assert.True(before.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(icons[1].IsKeyboardFocused);

                Press(icons[1], Key.Tab);
                DataGridCell cell = Assert.IsType<DataGridCell>(Keyboard.FocusedElement);
                Assert.Same(grid.Items[0], cell.DataContext);
                Assert.Same(grid.Columns[0], cell.Column);
            });
        }

        // Only the icon opens a summary: resting the pointer on a header or on the icon does nothing.
        [Fact]
        public void RestingOnAHeaderOrItsIcon_OpensNothing()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                foreach (UIElement target in new UIElement[] { Header(grid, 1), Icon(grid, grid.Columns[1]) })
                {
                    target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                    foreach (RoutedEvent move in new[] { UIElement.PreviewMouseMoveEvent, UIElement.MouseMoveEvent })
                    {
                        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = move });
                    }

                    PumpFor(TimeSpan.FromSeconds(1));
                    Assert.False(popup.IsOpen);
                }
            });
        }

        [Fact]
        public void ChoosingAValue_SetsThatFilterToIt_AndColumnsWithoutAFilterChooseNothing()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                Assert.True(popup.Open(grid.Columns[1]));
                // Its status bars take the timeline's colors, and No status has no value in the Status filter.
                var bar = (Border)Item(popup, "404").Template.FindName("Bar", Item(popup, "404"));
                Assert.Equal(
                    ((SolidColorBrush)Application.Current.FindResource("WarningFillBrush")).Color,
                    ((SolidColorBrush)bar.Background).Color);
                Assert.False(Row(popup, "No status").CanSelect);
                Click(Item(popup, "404"));
                Assert.False(popup.IsOpen);
                Assert.True(Icon(grid, grid.Columns[1]).IsKeyboardFocused);
                Assert.True(((CheckBoxFilters)view.FindName("StatusFilters")).IsFilterEnabled);
                Assert.Equal(new[] { "404" }, Checked(controller.StatusFilterList));
                WpfTestHost.Pump();
                Assert.Equal(new long[] { 4, 5 }, Visible(grid));

                // Counts cover the rows that the filters show.
                Assert.True(popup.Open(grid.Columns[4]));
                Assert.Equal("2 requests \u00B7 2 hosts", popup.View.Model.Totals);
                Click(Item(popup, "b.test"));
                Assert.True(((CheckBoxFilters)view.FindName("HostFilters")).IsFilterEnabled);
                Assert.Equal(new[] { "b.test" }, Checked(controller.HostFilterList));
                WpfTestHost.Pump();
                Assert.Equal(new long[] { 4 }, Visible(grid));

                // A process is shown alone, as Show only does.
                Assert.True(popup.Open(grid.Columns[5]));
                Click(Item(popup, "Game.exe"));
                Assert.Equal(Items(Spec(new[] { "Game.exe" }).Include), Items(controller.ProcessFilter.Include));

                // Scheme has no filter: its values can't be chosen, and the footer says so.
                Assert.True(popup.Open(grid.Columns[3]));
                Assert.Equal(Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_NO_FILTER"), popup.View.Model.Hint);
                Click(Item(popup, "https"));
                Assert.True(popup.IsOpen);
            });
        }

        // In an open summary Up and Down move through the values and Enter filters the table to one; a click outside
        // the summary closes it, and so does the original look.
        [Fact]
        public void InTheSummary_UpAndDownMove_EnterFilters_AndAClickOutsideCloses()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                ColumnSummaryButton host = Icon(grid, grid.Columns[4]);
                Press(host, Key.Down, system: true);
                var first = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                Press(first, Key.Down);
                var second = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                Assert.Equal("b.test", ((ColumnSummaryRow)second.DataContext).Label);
                Press(second, Key.Up);
                Assert.True(first.IsKeyboardFocused);
                Press(first, Key.Down);
                Press(second, Key.Enter);
                Assert.False(popup.IsOpen);
                Assert.True(host.IsKeyboardFocused);
                Assert.Equal(new[] { "b.test" }, Checked(controller.HostFilterList));

                Press(host, Key.Enter);
                Assert.True(popup.IsOpen);
                Cell(grid, grid.Items[0], grid.Columns[2]).RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                WpfTestHost.Pump();
                Assert.False(popup.IsOpen);

                Press(host, Key.Enter);
                Assert.True(popup.IsOpen);
                LookState.Current.IsNewLook = false;
                try
                {
                    WpfTestHost.Pump();
                    Assert.False(popup.IsOpen);
                }
                finally
                {
                    LookState.Current.IsNewLook = true;
                }
            });
        }

        // An open summary holds still while focus is in it, as it is from the moment it opens, and stays open as rows
        // arrive and the table grows; with focus back on its icon, it follows the table at most once a second.
        [Fact]
        public void OpenSummary_HoldsStillWhileFocusIsInIt_AndFollowsTheTableOnceASecondOtherwise()
        {
            RunWithSummaries((view, controller, grid, popup) =>
            {
                Assert.True(popup.Open(grid.Columns[1]));
                Assert.True(PumpUntil(() => popup.View.IsKeyboardFocusWithin, Timeout), "Focus is in the summary.");
                controller.ProxyConnections.Add(TestRow(7, "429", "a.test", "Game.exe"));
                PumpFor(ColumnSummaryPopup.RefreshInterval + TimeSpan.FromMilliseconds(500));
                Assert.True(popup.IsOpen, "The summary stays open as rows arrive.");
                Assert.DoesNotContain(popup.View.Model.Rows, row => row.Label == "429");

                Assert.True(Icon(grid, grid.Columns[1]).Focus(), "The icon takes focus.");
                Assert.True(popup.IsOpen, "The summary stays open with focus on its icon.");
                Assert.True(
                    PumpUntil(() => popup.View.Model.Rows.Any(row => row.Label == "429"), Timeout),
                    $"The summary follows the table: open {popup.IsOpen}, pointer over it {popup.View.IsMouseOver}.");
            });
        }

        // The original look keeps its own column headers: no info icons, and so no summaries.
        [Fact]
        public void OriginalLook_HasNoInfoIconsOrSummaries()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/OriginalLook.xaml"),
                });
                LookState.Current.IsNewLook = false;
                try
                {
                    CaptureConnectionView view = CaptureProcessFilterViewTests.View(Controller(TestRows()));
                    CaptureProcessFilterViewTests.ShowCaptureView(window, view);
                    var grid = (DataGrid)view.FindName("ConnectionList");
                    Assert.Equal(grid.Columns.Count, Descendants(grid).OfType<DataGridColumnHeader>().Count(header => header.Column != null));
                    Assert.Empty(Descendants(grid).OfType<ColumnSummaryButton>());
                    Assert.False(ColumnSummary.PopupOf(grid).Open(grid.Columns[1]));
                }
                finally
                {
                    LookState.Current.IsNewLook = true;
                }
            });
        }

        // The traffic table with six requests: Status 200 three times, 404 twice and one without a status, over hosts
        // a.test (3), b.test (2) and c.test (1), from Game.exe and Tool.exe. Its headers are the new look's, from App.xaml.
        private void RunWithSummaries(
            Action<CaptureConnectionView, WebServiceDeviceCaptureController, DataGrid, ColumnSummaryPopup> test)
        {
            _application.Run(window =>
            {
                // The popup isn't in the window's tree, so its style comes from the application, as in XMAT.
                var styles = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/DataViz.xaml"),
                };
                Application.Current.Resources.MergedDictionaries.Add(styles);
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources(
                    "DataGrid", "DataGridColumnHeader", "ColumnHeaderGripperStyle", "TitleCaseConverter", "TrafficGridHeaderStyle"));
                ColumnSummaryPopup popup = null;
                try
                {
                    WebServiceDeviceCaptureController controller = Controller(TestRows());
                    CaptureConnectionView view = CaptureProcessFilterViewTests.View(controller);
                    CaptureProcessFilterViewTests.ShowCaptureView(window, view);
                    var grid = (DataGrid)view.FindName("ConnectionList");
                    popup = ColumnSummary.PopupOf(grid);
                    Assert.NotNull(popup);
                    test(view, controller, grid, popup);
                }
                finally
                {
                    popup?.Close();
                    Application.Current.Resources.MergedDictionaries.Remove(styles);
                }
            });
        }

        private static WebServiceDeviceCaptureController Controller(IEnumerable<ProxyConnectionModel> rows)
        {
            var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true);
            foreach (ProxyConnectionModel row in rows)
            {
                controller.ProxyConnections.Add(row);
            }

            return controller;
        }

        private static ProxyConnectionModel[] TestRows() => new[]
        {
            TestRow(1, "200", "a.test", "Game.exe"),
            TestRow(2, "200", "a.test", "Tool.exe"),
            TestRow(3, "200", "b.test", "Game.exe"),
            TestRow(4, "404", "b.test", "Game.exe"),
            TestRow(5, "404", "c.test", "Tool.exe"),
            TestRow(6, string.Empty, "a.test", "Game.exe"),
        };

        private static ProxyConnectionModel TestRow(int number, string status, string host, string process) =>
            WithProcess(CaptureTimelineTestData.Create(number, host: host, status: status), Local(process, 0));

        private static string[] Rows(params (string Value, int Count)[] values) =>
            values.SelectMany(value => Enumerable.Repeat(value.Value, value.Count)).ToArray();

        private static string Requests(int count) =>
            ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_REQUEST", "COLUMN_SUMMARY_REQUESTS");

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

        private static string[] Checked(IEnumerable<CheckedListItem> items) =>
            items.Where(item => item.IsChecked).Select(item => item.Text).ToArray();

        private static long[] Visible(DataGrid grid) =>
            grid.Items.Cast<ProxyConnectionModel>().Select(row => row.RequestNumber).ToArray();

        private static DataGridColumnHeader Header(DataGrid grid, int column) =>
            Descendants(grid).OfType<DataGridColumnHeader>().Single(header => ReferenceEquals(header.Column, grid.Columns[column]));

        private static ColumnSummaryButton Icon(DataGrid grid, DataGridColumn column) =>
            Descendants(Header(grid, grid.Columns.IndexOf(column))).OfType<ColumnSummaryButton>().Single();

        // A header's column name.
        private static TextBlock Label(DataGridColumnHeader header) =>
            Descendants(header).OfType<TextBlock>().First();

        private static FrameworkElement Part(Control control, string name) =>
            Assert.IsAssignableFrom<FrameworkElement>(control.Template.FindName(name, control));

        private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

        // An element's bounds as it shows, inside the clip of a layout slot too small for it.
        private static Rect VisibleBounds(FrameworkElement element, Visual ancestor)
        {
            var bounds = new Rect(element.RenderSize);
            if (LayoutInformation.GetLayoutClip(element) is { } clip)
            {
                bounds.Intersect(clip.Bounds);
            }

            return element.TransformToAncestor(ancestor).TransformBounds(bounds);
        }

        private static double FullWidth(TextBlock text) =>
            new FormattedText(
                text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

        private static Color ColorOf(FrameworkElement element, string key) =>
            ((SolidColorBrush)element.FindResource(key)).Color;

        private static Color GlyphColor(ColumnSummaryButton icon) =>
            Assert.IsType<SolidColorBrush>(((TextBlock)Part(icon, "Glyph")).Foreground).Color;

        private static DataGridCell Cell(DataGrid grid, object item, DataGridColumn column) =>
            Descendants(CaptureProcessFilterViewTests.Container(grid, item)).OfType<DataGridCell>()
                .Single(cell => ReferenceEquals(cell.Column, column));

        private static ColumnSummaryRow Row(ColumnSummaryPopup popup, string label) =>
            popup.View.Model.Rows.Single(row => row.Label == label);

        private static ListBoxItem Item(ColumnSummaryPopup popup, string label)
        {
            WpfTestHost.Pump();
            return Assert.IsType<ListBoxItem>(popup.View.Values.ItemContainerGenerator.ContainerFromItem(Row(popup, label)));
        }

        // A click on the icon, as screen readers and other automation give it.
        private static void Activate(ColumnSummaryButton icon)
        {
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(icon).GetPattern(PatternInterface.Invoke)).Invoke();
            WpfTestHost.Pump();
        }

        // Sorts the table by COLUMN as a click on its header does: the column's item under the table's header row clicks it.
        private static void ClickHeader(DataGrid grid, DataGridColumn column)
        {
            DataGridColumnHeaderItemAutomationPeer header = UIElementAutomationPeer.CreatePeerForElement(grid)
                .GetChildren()
                .OfType<DataGridColumnHeadersPresenterAutomationPeer>()
                .Single()
                .GetChildren()
                .OfType<DataGridColumnHeaderItemAutomationPeer>()
                .Single(item => ReferenceEquals(item.Item, column.Header));
            ((IInvokeProvider)header.GetPattern(PatternInterface.Invoke)).Invoke();
            WpfTestHost.Pump();
        }

        // A press and release of the left button, which each element on the way re-raises as a left button event.
        private static void Click(UIElement target)
        {
            foreach (RoutedEvent routed in new[] { UIElement.PreviewMouseDownEvent, UIElement.PreviewMouseUpEvent })
            {
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed });
            }

            WpfTestHost.Pump();
        }

        // A key press as Windows reports it, down to the focused element and back up, unless an element handles it on
        // the way down; SYSTEM gives the key with Alt, as Alt+Down.
        private static void Press(UIElement target, Key key, bool system = false)
        {
            foreach (RoutedEvent routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent })
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = routed };
                if (system)
                {
                    MethodInfo markSystem = typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(markSystem);
                    markSystem.Invoke(args, null);
                }

                target.RaiseEvent(args);
                if (args.Handled)
                {
                    break;
                }
            }

            WpfTestHost.Pump();
        }

        // Space clicks a button as it is released.
        private static void PressSpace(UIElement target)
        {
            foreach (RoutedEvent routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            {
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Space)
                {
                    RoutedEvent = routed,
                });
            }

            WpfTestHost.Pump();
        }

        private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < timeout)
            {
                WpfTestHost.Pump();
                Thread.Sleep(10);
            }

            return condition();
        }

        private static void PumpFor(TimeSpan duration) => PumpUntil(() => false, duration);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
