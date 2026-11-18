// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Xml.Linq;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // The new look's context menus: labeled groups of short rows on one rounded surface, which the keyboard and screen
    // readers use in full, and the original look's flat menus as they were.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ContextMenuTests
    {
        private const string Host = "telemetry.enterprise.githubcopilot.com";
        private const string Process = "copilot-runtime.exe";
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private readonly CaptureUiApplicationFixture _application;

        public ContextMenuTests(CaptureUiApplicationFixture application) => _application = application;

        // Host, Request, Process and Sessions, each under a header that names its subject once, then Sort by; the
        // timeline's bars have the Host and Process groups only.
        [Fact]
        public void RowAndTimelineMenus_GroupTheirItemsUnderHeaders()
        {
            _application.Run(window =>
            {
                (CaptureConnectionView view, DataGrid grid, ProxyConnectionModel row) =
                    ShowCapture(window, new HostListPolicy(), Host, Local(Process, 4100), Spec(null, new[] { "Other.exe" }));
                ContextMenu menu = Open(grid, row);
                try
                {
                    Assert.Equal(new[]
                    {
                        "[Host telemetry.enterprise.githubcopilot.com]",
                        "Add to Block List", "Add to Bypass List", "Manage host lists...", "-",
                        "[Request]",
                        "Edit and resend", "Replay", "-",
                        "[Process copilot-runtime.exe]",
                        "Show only this process", "Hide this process", "Show all processes", "-",
                        "[Sessions]",
                        "Export selected...", "Export visible...", "Delete selected", "Delete unselected", "-",
                        "Sort by",
                    }, Structure(menu));
                    Assert.Same(menu.FindResource("ContextMenuSurfaceStyle"), menu.Style);
                    Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.False(string.IsNullOrEmpty(MenuRow.GetGlyph(item))));
                    Assert.Equal(
                        new[] { null, "Skips HTTPS decryption for new connections" },
                        menu.Items.OfType<MenuItem>().Take(2).Select(MenuRow.GetDescription));
                }
                finally
                {
                    Close(menu);
                }

                var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                ContextMenu barMenu = CaptureProcessFilterViewTests.Open(
                    ((Canvas)timeline.FindName("TimelineCanvas")).Children.OfType<Border>().Single(bar => bar.Tag == row));
                try
                {
                    Assert.Equal(new[]
                    {
                        "[Host telemetry.enterprise.githubcopilot.com]",
                        "Add to Block List", "Add to Bypass List", "Manage host lists...", "-",
                        "[Process copilot-runtime.exe]",
                        "Show only this process", "Hide this process", "Show all processes",
                    }, Structure(barMenu));
                }
                finally
                {
                    Close(barMenu);
                }
            });
        }

        // Without a single host or process the headers stay, without a subject, and a note says why; an analysis table
        // shows Sort by alone on the same surface.
        [Fact]
        public void MenusWithoutAHostOrProcess_ExplainWhy_AndTablesShowSortByAlone()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, policy, "", ClientProcessInfo.NotRecorded);
                ContextMenu menu = Open(grid, row);
                try
                {
                    Assert.Equal(new[]
                    {
                        "[Host]", "(No host is available for this item)", "Manage host lists...", "-",
                        "[Request]", "Edit and resend", "Replay", "-",
                        "[Process]", "(No process information for this session)", "-",
                        "[Sessions]", "Export selected...", "Export visible...", "Delete selected", "Delete unselected", "-",
                        "Sort by",
                    }, Structure(menu));
                }
                finally
                {
                    Close(menu);
                }

                var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a" } };
                table.Columns.Add(new DataGridTextColumn { Header = "Endpoint", Binding = new Binding(".") });
                DataGridSortMenu.SetIsEnabled(table, true);
                WpfTestHost.Show(window, table);
                ContextMenu sortMenu = CaptureProcessFilterViewTests.Open(Descendants(table).OfType<DataGridCell>().First());
                try
                {
                    Assert.Equal(new[] { "Sort by" }, Structure(sortMenu));
                    Assert.Same(sortMenu.FindResource("ContextMenuSurfaceStyle"), sortMenu.Style);
                    Assert.Equal(MenuGlyphs.Sort, MenuRow.GetGlyph(sortMenu.Items.OfType<MenuItem>().Single()));
                }
                finally
                {
                    Close(sortMenu);
                }
            });
        }

        // Up and Down move between the actions only: headers and notes can't take focus.
        [Fact]
        public void ArrowKeys_PassOverHeadersAndNotes()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                policy.BlockList.Add("*.githubcopilot.com");
                policy.BypassList.Add(Host);
                (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, policy, Host, Local(Process, 4100));
                ContextMenu menu = Open(grid, row);
                try
                {
                    Assert.Equal(2, menu.Items.OfType<MenuNote>().Count());
                    Assert.Equal(4, menu.Items.OfType<MenuGroupHeader>().Count());
                    Assert.All(menu.Items.OfType<MenuLabel>(), label => Assert.False(label.Focusable));
                    MenuItem[] actions = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToArray();

                    Press(menu, Key.Down);
                    var visited = new List<object> { Keyboard.FocusedElement };
                    for (int step = 0; step < actions.Length; step++)
                    {
                        Press((UIElement)Keyboard.FocusedElement, Key.Down);
                        visited.Add(Keyboard.FocusedElement);
                    }
                    Assert.Equal<object>(actions.Append(actions[0]), visited);

                    Press((UIElement)Keyboard.FocusedElement, Key.Up);
                    Assert.Same(actions[^1], Keyboard.FocusedElement);
                }
                finally
                {
                    Close(menu);
                }
            });
        }

        // A long host ends in an ellipsis in the header; the menu stays within 320px and the row names keep the host.
        [Fact]
        public void LongHost_IsTrimmedInTheHeader_AndTheMenuStaysWithin320Pixels()
        {
            _application.Run(window =>
            {
                const string longHost = "build-agent-telemetry-collector-0123456789.westus3.prod.enterprise.githubcopilot.com";
                (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, new HostListPolicy(), longHost, Local(Process, 4100));
                ContextMenu menu = Open(grid, row);
                try
                {
                    var surface = (FrameworkElement)menu.Template.FindName("Surface", menu);
                    Assert.InRange(surface.ActualWidth, 220, 320);
                    var header = Assert.IsType<MenuGroupHeader>(menu.Items[0]);
                    var subject = (TextBlock)header.Template.FindName("Subject", header);
                    Assert.Equal(longHost, subject.Text);
                    var natural = new TextBlock { Text = subject.Text, FontFamily = subject.FontFamily, FontSize = subject.FontSize };
                    natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(subject.ActualWidth < natural.DesiredSize.Width, "The subject must be trimmed to fit.");
                    Assert.Equal(TextTrimming.CharacterEllipsis, subject.TextTrimming);
                    Assert.Equal(
                        Localization.GetLocalizedString("MENU_ADD_TO_BLOCK_LIST_NAME", longHost),
                        Name(menu.Items.OfType<MenuItem>().First()));
                }
                finally
                {
                    Close(menu);
                }
            });
        }

        // Rows are 32px, or 28px with Comfort view off; headers and notes are 28px and separators 9px.
        [Fact]
        public void Rows_Are32PixelsTall_Or28WithComfortViewOff()
        {
            _application.Run(window =>
            {
                var policy = new HostListPolicy();
                policy.BlockList.Add("*.githubcopilot.com");
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources("{x:Static MenuItem.SeparatorStyleKey}"));
                (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, policy, Host, Local(Process, 4100));
                try
                {
                    foreach ((bool comfort, double height) in new[] { (true, 32.0), (false, 28.0), (true, 32.0) })
                    {
                        ComfortView.Apply(window.Resources, comfort);
                        ContextMenu menu = Open(grid, row);
                        try
                        {
                            Assert.All(
                                menu.Items.OfType<MenuItem>().Where(item => MenuRow.GetDescription(item) == null),
                                item => Assert.Equal(height, item.ActualHeight));
                            Assert.All(menu.Items.OfType<MenuLabel>(), label => Assert.Equal(28, label.ActualHeight));
                            Assert.All(
                                menu.Items.OfType<Separator>().Where(separator => separator is not MenuLabel),
                                separator => Assert.Equal(9, separator.ActualHeight));
                        }
                        finally
                        {
                            Close(menu);
                        }
                    }
                }
                finally
                {
                    ComfortView.Apply(window.Resources, on: true);
                }
            });
        }

        // The labels leave the host and the process to the headers, but each row's accessible name says it in full.
        [Fact]
        public void ScreenReaders_HearEachRowsFullMeaning()
        {
            _application.Run(window =>
            {
                (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, new HostListPolicy(), Host, Local(Process, 4100));
                ContextMenu menu = Open(grid, row);
                try
                {
                    MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
                    Assert.Equal(new[]
                    {
                        "Add telemetry.enterprise.githubcopilot.com to Block List",
                        "Add telemetry.enterprise.githubcopilot.com to Bypass List",
                        "Manage host lists...",
                        "Edit and resend",
                        "Replay",
                        "Show only copilot-runtime.exe",
                        "Hide copilot-runtime.exe",
                        "Export selected sessions...",
                        "Export visible sessions...",
                        "Delete selected sessions",
                        "Delete unselected sessions",
                        "Sort by",
                    }, items.Select(Name));
                    Assert.Equal("Skips HTTPS decryption for new connections", Peer(items[1]).GetHelpText());
                    Assert.Equal(
                        new[] { ModifierKeys.None, ModifierKeys.Shift }.Select(modifiers =>
                            new KeyGesture(Key.Delete, modifiers).GetDisplayStringForCulture(CultureInfo.CurrentUICulture)),
                        items.Skip(9).Take(2).Select(item => item.InputGestureText));
                    Assert.All(menu.Items.OfType<MenuLabel>(),
                        label => Assert.Equal(AutomationControlType.Text, Peer(label).GetAutomationControlType()));
                    Assert.Equal("Host " + Host, Peer((UIElement)menu.Items[0]).GetName());
                }
                finally
                {
                    Close(menu);
                }
            });
        }

        // The original look lists today's items in one flat menu. Menus are filled as they open, so a look chosen while a
        // menu is closed shows the next time it opens.
        [Fact]
        public void OriginalLook_KeepsTheFlatMenu_AndALookChosenWhileClosedShowsWhenItOpens()
        {
            _application.Run(window =>
            {
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    (_, DataGrid grid, ProxyConnectionModel row) = ShowCapture(window, new HostListPolicy(), Host, Local(Process, 4100));
                    ContextMenu menu = Open(grid, row);
                    Assert.IsType<MenuGroupHeader>(menu.Items[0]);
                    Assert.NotNull(menu.Style);
                    Close(menu);

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    menu = Open(grid, row);
                    Assert.Equal(new[]
                    {
                        L("HOST_LIST_ADD_BLOCK", Host), L("HOST_LIST_ADD_BYPASS", Host), "-", L("HOST_LISTS_MANAGE_MENU"), "-",
                        L("COMPOSER_TITLE"), L("REPLAY_MENU"), "-",
                        L("PROCESS_FILTER_SHOW_ONLY", Process), L("PROCESS_FILTER_HIDE", Process), "-",
                        L("WEBCAP_EXPORT_SELECTED"), L("WEBCAP_EXPORT_VISIBLE"), "-",
                        L("WEBCAP_DELETE_SELECTED"), L("WEBCAP_DELETE_UNSELECTED"), "-",
                        L("GRID_SORT_BY"),
                    }, Structure(menu));
                    Assert.Null(menu.Style);
                    Assert.All(menu.Items.OfType<MenuItem>(), item =>
                    {
                        Assert.Null(MenuRow.GetGlyph(item));
                        Assert.Equal(string.Empty, AutomationProperties.GetName(item));
                    });
                    Close(menu);

                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                    menu = Open(grid, row);
                    Assert.IsType<MenuGroupHeader>(menu.Items[0]);
                    Assert.Same(menu.FindResource("ContextMenuSurfaceStyle"), menu.Style);
                    Close(menu);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        // The shadow is 0 8 24 in black, at 14% under the light theme's menus and 50% under the dark theme's; High Contrast
        // has none.
        [Theory]
        [InlineData("Light", 0.14)]
        [InlineData("Dark", 0.5)]
        public void MenuShadow_FollowsTheTheme_AndHighContrastHasNone(string theme, double opacity)
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{theme}Theme.xaml")
                });
                var shadow = new MenuShadow();
                shadow.SetResourceReference(Border.BackgroundProperty, "SurfaceCanvasBrush");
                WpfTestHost.Show(window, shadow);

                var effect = Assert.IsType<DropShadowEffect>(shadow.Effect);
                Assert.Equal((Colors.Black, opacity, 8.0, 24.0, 270.0),
                    (effect.Color, effect.Opacity, effect.ShadowDepth, effect.BlurRadius, effect.Direction));

                window.Resources[SystemParameters.HighContrastKey] = true;
                WpfTestHost.Pump();
                Assert.Null(shadow.Effect);

                window.Resources[SystemParameters.HighContrastKey] = false;
                WpfTestHost.Pump();
                Assert.Same(effect, shadow.Effect);
            });
        }

        // The main menu's dropdowns open on the surface just below their header, aligned as Windows aligns menus, and a
        // submenu opens beside its row with its first row level with it. A context menu's surface opens where it is
        // placed, its shadow's room around it.
        [Fact]
        public void Dropdowns_SubmenusAndContextMenus_OpenWhereTheyArePlaced()
        {
            _application.Run(window =>
            {
                Rect area = SystemParameters.WorkArea;
                window.Left = area.Left + (area.Width / 2) - 300;
                window.Top = area.Top + 100;
                window.Width = 600;
                window.Height = 400;
                var menuBar = CaptureChromeTests.Parse<Menu>(
                    CaptureChromeTests.ProductMarkup("MainWindow.xaml").Descendants(Presentation + "Menu").First());
                var target = new Button { Content = "Target", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
                WpfTestHost.Show(window, new StackPanel { Children = { menuBar, target } });
                double scale = VisualTreeHelper.GetDpi(menuBar).DpiScaleY;
                const double Tolerance = 1.5;

                var edit = (MenuItem)menuBar.FindName("EditMenu");
                edit.IsSubmenuOpen = true;
                WpfTestHost.Pump();
                var surface = (Border)edit.Template.FindName("SubMenuBorder", edit);
                Assert.Equal((new CornerRadius(8), 220.0, 320.0), (surface.CornerRadius, surface.MinWidth, surface.MaxWidth));
                Point header = edit.PointToScreen(new Point(0, 0));
                Point headerEnd = edit.PointToScreen(new Point(edit.ActualWidth, edit.ActualHeight));
                Point top = surface.PointToScreen(new Point(0, 0));
                Point topEnd = surface.PointToScreen(new Point(surface.ActualWidth, 0));
                Assert.Equal(headerEnd.Y + (3 * scale), top.Y, Tolerance);
                if (SystemParameters.MenuDropAlignment)
                {
                    Assert.Equal(headerEnd.X, topEnd.X, Tolerance);
                }
                else
                {
                    Assert.Equal(header.X, top.X, Tolerance);
                }

                var appearance = (MenuItem)menuBar.FindName("AppearanceMenu");
                appearance.IsSubmenuOpen = true;
                WpfTestHost.Pump();
                var submenu = (Border)appearance.Template.FindName("SubMenuBorder", appearance);
                Point row = appearance.PointToScreen(new Point(0, 0));
                Point rowEnd = appearance.PointToScreen(new Point(appearance.ActualWidth, 0));
                Point subTop = submenu.PointToScreen(new Point(0, 0));
                Point subTopEnd = submenu.PointToScreen(new Point(submenu.ActualWidth, 0));
                Assert.Equal(row.Y, appearance.Items.OfType<MenuItem>().First().PointToScreen(new Point(0, 0)).Y, Tolerance);
                Assert.True(
                    Math.Abs(subTop.X - (rowEnd.X + scale)) <= Tolerance || Math.Abs(subTopEnd.X - (row.X + scale)) <= Tolerance,
                    $"The submenu ({subTop.X}-{subTopEnd.X}) must open beside its row ({row.X}-{rowEnd.X}).");
                appearance.IsSubmenuOpen = false;
                edit.IsSubmenuOpen = false;
                WpfTestHost.Pump();

                var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
                menu.Items.Add(new MenuItem { Header = "Item" });
                GroupedMenu.ApplyLook(menu);
                menu.IsOpen = true;
                WpfTestHost.Pump();
                try
                {
                    var menuSurface = (FrameworkElement)menu.Template.FindName("Surface", menu);
                    Point targetBottom = target.PointToScreen(new Point(0, target.ActualHeight));
                    Point targetEnd = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
                    Point menuTop = menuSurface.PointToScreen(new Point(0, 0));
                    Point menuTopEnd = menuSurface.PointToScreen(new Point(menuSurface.ActualWidth, 0));
                    Assert.Equal(targetBottom.Y, menuTop.Y, Tolerance);
                    Assert.True(
                        Math.Abs(menuTop.X - targetBottom.X) <= Tolerance || Math.Abs(menuTopEnd.X - targetEnd.X) <= Tolerance,
                        $"The menu ({menuTop.X}-{menuTopEnd.X}) must line up with its target ({targetBottom.X}-{targetEnd.X}).");
                }
                finally
                {
                    Close(menu);
                }
            });
        }

        // A capture tab's view with a row for HOST from PROCESS, selected, and a second row from another process.
        private static (CaptureConnectionView View, DataGrid Grid, ProxyConnectionModel Row) ShowCapture(
            Window window, HostListPolicy policy, string host, ClientProcessInfo process, ProcessFilterSpec filter = null)
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device", DeviceType.Unknown, false, new ProxyPortPool(), hostListPolicy: policy);
            ProxyConnectionModel row = WithProcess(CaptureTimelineTestData.Create(1, host: host), process);
            controller.ProxyConnections.Add(row);
            controller.ProxyConnections.Add(WithProcess(
                CaptureTimelineTestData.Create(2, CaptureTimelineTestData.Origin.AddMilliseconds(8), "other.example.com"),
                Local("Other.exe", 4200)));
            if (filter != null)
            {
                controller.SetProcessFilter(filter);
            }

            // MainWindow handles Manage host lists.
            window.CommandBindings.Add(new CommandBinding(AppCommands.ManageHostLists, (_, _) => { }));
            var view = new CaptureConnectionView { AutoScroll = false };
            var grid = (DataGrid)view.FindName("ConnectionList");
            grid.IsSynchronizedWithCurrentItem = false;
            // As the new look's table style does.
            DataGridSortMenu.SetIsEnabled(grid, true);
            view.DataContext = new TestCaptureContext(controller, "Test device", readOnly: false, DeviceType.Unknown);
            CaptureProcessFilterViewTests.ShowCaptureView(window, view);
            grid.SelectedItem = row;
            WpfTestHost.Pump();
            return (view, grid, row);
        }

        private static ContextMenu Open(DataGrid grid, ProxyConnectionModel row) =>
            CaptureProcessFilterViewTests.Open(CaptureProcessFilterViewTests.Container(grid, row));

        private static void Close(ContextMenu menu)
        {
            menu.IsOpen = false;
            WpfTestHost.Pump();
        }

        // The menu as a list: [header and subject], (note), row label, and - for a separator.
        private static string[] Structure(ContextMenu menu) => menu.Items.Cast<object>().Select(item => item switch
        {
            MenuGroupHeader header => "[" + string.Join(" ", new[] { header.Text, header.Subject }.Where(text => text != null)) + "]",
            MenuNote note => "(" + note.Text + ")",
            MenuItem row => (string)row.Header,
            Separator => "-",
            _ => item.ToString(),
        }).ToArray();

        private static AutomationPeer Peer(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element);

        private static string Name(MenuItem item) => Peer(item).GetName();

        private static string L(string key, params object[] values) => Localization.GetLocalizedString(key, values);

        private static void Press(UIElement target, Key key) =>
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
