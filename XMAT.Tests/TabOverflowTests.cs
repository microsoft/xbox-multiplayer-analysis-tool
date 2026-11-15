// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class TabOverflowTests
    {
        private static readonly string[] Views = { "Request/Response", "WebSockets", "Script Editor", "Block List", "Bypass List" };
        private readonly CaptureUiApplicationFixture _application;

        public TabOverflowTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void InspectorTabs_KeepTheToolbarTextSizeAndMoveTabsToMoreInsteadOfScrolling()
        {
            _application.Run(window =>
            {
                var (tabs, host) = ShowInspector(window, 1000);
                var panel = HeaderPanel(tabs);
                var more = MoreButton(tabs);
                // Tab labels match the capture toolbar's buttons.
                double buttonSize = (double)tabs.FindResource("BaseFontSize");
                Assert.Equal(12, buttonSize);
                Assert.False(panel.HasOverflow);
                Assert.Equal(Visibility.Hidden, more.Visibility);
                Assert.Equal(buttonSize, LabelSize(Items(tabs)[1]));
                Assert.Equal(buttonSize, LabelSize(Items(tabs)[0]));
                Assert.Equal(buttonSize, more.FontSize);
                double natural = Items(tabs).Sum(tab => tab.ActualWidth);

                // Just too narrow: the labels keep their size, and the last tab moves to More.
                host.Width = natural - 8;
                WpfTestHost.Pump();
                Assert.True(panel.HasOverflow);
                Assert.True(OverflowTabPanel.GetIsOverflowItem(Items(tabs).Last()));
                Assert.Equal(buttonSize, LabelSize(Items(tabs)[1]));
                Assert.Equal(buttonSize, LabelSize(Items(tabs)[0]));

                // Narrower still: tabs that don't fit move to More, which follows the last tab in the row.
                host.Width = 300;
                WpfTestHost.Pump();
                Assert.True(panel.HasOverflow);
                Assert.Equal(Visibility.Visible, more.Visibility);
                var inRow = Items(tabs).Where(tab => !OverflowTabPanel.GetIsOverflowItem(tab)).ToList();
                var outOfRow = Items(tabs).Where(tab => OverflowTabPanel.GetIsOverflowItem(tab)).ToList();
                Assert.Contains(Items(tabs)[0], inRow);
                Assert.NotEmpty(outOfRow);
                Assert.Equal(outOfRow, panel.OverflowItems);
                Assert.All(outOfRow, tab => Assert.Equal(0, Slot(tab).Width));
                double rowEnd = Slot(inRow.Last()).Right;
                double moreStart = more.TranslatePoint(new Point(0, 0), panel).X;
                double moreEnd = more.TranslatePoint(new Point(more.ActualWidth, 0), panel).X;
                Assert.InRange(moreStart - rowEnd, -0.5, 0.5);
                Assert.True(moreEnd <= panel.ActualWidth + 0.5, "More must fit in the row.");
                Assert.Equal(panel.ActualHeight, more.ActualHeight);

                // Wide again: every tab in the row and no More.
                host.Width = 1000;
                WpfTestHost.Pump();
                Assert.False(panel.HasOverflow);
                Assert.Equal(Visibility.Hidden, more.Visibility);
                Assert.Equal(buttonSize, LabelSize(Items(tabs)[1]));
                Assert.All(Items(tabs), tab => Assert.True(Slot(tab).Width > 0));
            });
        }

        [Fact]
        public void ChoosingATabFromMore_SelectsItAndBringsItIntoTheRow()
        {
            _application.Run(window =>
            {
                var (tabs, _) = ShowInspector(window, 120);
                var panel = HeaderPanel(tabs);
                var more = MoreButton(tabs);
                Assert.Equal(Items(tabs).Skip(1), panel.OverflowItems);
                // The selected tab alone is wider than the room left, so it is clipped and More stays in the row.
                Assert.True(more.TranslatePoint(new Point(more.ActualWidth, 0), panel).X <= panel.ActualWidth + 0.5);
                var chosen = (TabItem)panel.OverflowItems.Last();
                var listed = panel.OverflowItems.Select(tab => (string)((TabItem)tab).Header).ToList();

                more.IsChecked = true;
                WpfTestHost.Pump();
                var menu = more.Menu;
                Assert.NotNull(menu);
                Assert.True(menu.IsOpen);
                Assert.Same(window.FindResource("TabOverflowMenuStyle"), menu.Style);
                Assert.Equal(listed, menu.Items.OfType<MenuItem>().Select(item => (string)item.Header));
                menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                WpfTestHost.Pump();

                Assert.True(chosen.IsSelected);
                Assert.False(OverflowTabPanel.GetIsOverflowItem(chosen));
                Assert.True(Slot(chosen).Width > 0);
                Assert.Contains(Items(tabs)[0], panel.OverflowItems);
                Assert.False(more.IsChecked);
                Assert.Null(more.Menu);
            });
        }

        [Fact]
        public void ArrowKeys_MoveThroughEveryTabInOrder()
        {
            _application.Run(window =>
            {
                var (tabs, _) = ShowInspector(window, 120);
                var items = Items(tabs);
                var first = items[0];
                var next = items[1];
                Assert.True(OverflowTabPanel.GetIsOverflowItem(next));

                Press(first, Key.Right);
                WpfTestHost.Pump();
                Assert.True(next.IsSelected);
                Assert.False(OverflowTabPanel.GetIsOverflowItem(next));

                Press(next, Key.Left);
                WpfTestHost.Pump();
                Assert.True(first.IsSelected);

                Press(first, Key.Left);
                WpfTestHost.Pump();
                Assert.True(items[^1].IsSelected);
                Assert.False(OverflowTabPanel.GetIsOverflowItem(items[^1]));
            });
        }

        [Fact]
        public void PressingMoreWhileItsMenuIsOpen_ClosesTheMenuWithoutReopeningIt()
        {
            _application.Run(window =>
            {
                var (tabs, _) = ShowInspector(window, 120);
                var more = MoreButton(tabs);
                more.IsChecked = true;
                WpfTestHost.Pump();
                var menu = more.Menu;
                Assert.True(menu.IsOpen);

                // A press outside an open menu dismisses it at once, but the menu reports that it closed only later,
                // after the same press has reached the More button.
                menu.IsOpen = false;
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                };
                more.RaiseEvent(press);

                Assert.True(press.Handled);
                Assert.False(more.IsChecked);
                Assert.False(more.IsPressed);
                Assert.Null(more.Menu);
                WpfTestHost.Pump();
                Assert.False(more.IsChecked);
                Assert.Null(more.Menu);
            });
        }

        [Fact]
        public void SwitchingTabs_MovesNoTabAndNoLabel()
        {
            _application.Run(window =>
            {
                var (tabs, _) = ShowInspector(window, 1000);
                AssertTabsHoldStillAsSelectionMoves(tabs);

                // So do headers built from a template, with a close button beside the label.
                var analysis = AnalysisTabs(window);
                AssertTabsHoldStillAsSelectionMoves(analysis);
            });
        }

        [Fact]
        public void TemplatedHeaders_AreListedByTheirTextNotTheirButtons()
        {
            _application.Run(window =>
            {
                var tabs = AnalysisTabs(window);
                var tab = (UIElement)tabs.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.Equal("Analysis 2", TabOverflowButton.TabLabel(tab));
            });
        }

        // Tabs like the analysis results: each header shows its label and a close button.
        private static TabControl AnalysisTabs(Window window)
        {
            var header = new FrameworkElementFactory(typeof(StackPanel));
            header.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding());
            var close = new FrameworkElementFactory(typeof(Button));
            close.SetValue(ContentControl.ContentProperty, "x");
            header.AppendChild(label);
            header.AppendChild(close);
            var tabs = new TabControl
            {
                Style = (Style)window.FindResource("InspectorTabControlStyle"),
                ItemTemplate = new DataTemplate { VisualTree = header },
                ItemsSource = new[] { "Analysis 1", "Analysis 2", "Analysis 3" }
            };
            WpfTestHost.Show(window, tabs);
            return tabs;
        }

        // Selecting each tab in turn changes no tab's width, and every label keeps its size and its place: it stays
        // centered at the same height, so only its weight changes.
        private static void AssertTabsHoldStillAsSelectionMoves(TabControl tabs)
        {
            var containers = Enumerable.Range(0, tabs.Items.Count)
                .Select(index => (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(index)).ToList();
            var widths = containers.Select(tab => tab.ActualWidth).ToList();
            var labels = containers.Select(LabelPlacement).ToList();
            Assert.All(widths, width => Assert.True(width > 0));
            foreach (var tab in containers.Skip(1).Append(containers[0]))
            {
                tab.IsSelected = true;
                WpfTestHost.Pump();
                Assert.Equal(widths, containers.Select(item => item.ActualWidth));
                Assert.Single(containers.Select(LabelSize).Distinct());
                foreach (var (item, placement) in containers.Zip(labels))
                {
                    Point now = LabelPlacement(item);
                    Assert.InRange(Math.Abs(now.X - placement.X), 0, 0.5);
                    Assert.InRange(Math.Abs(now.Y - placement.Y), 0, 0.5);
                }
            }
        }

        // The middle of a tab's label across, at the label's top, relative to the tab.
        [Fact]
        public void More_IsAMenuButtonThatAssistiveTechnologyOpensOnItsFirstTab()
        {
            _application.Run(window =>
            {
                var (tabs, _) = ShowInspector(window, 120);
                var more = MoreButton(tabs);
                var peer = UIElementAutomationPeer.CreatePeerForElement(more);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Null(peer.GetPattern(PatternInterface.Toggle));
                var expander = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
                Assert.Equal(ExpandCollapseState.Collapsed, expander.ExpandCollapseState);

                more.Focus();
                expander.Expand();
                WpfTestHost.Pump();
                Assert.Equal(ExpandCollapseState.Expanded, expander.ExpandCollapseState);
                Assert.Equal(Localization.GetLocalizedString("TAB_OVERFLOW_NAME"), AutomationProperties.GetName(more.Menu));
                Assert.Same(more.Menu.Items.OfType<MenuItem>().First(), Keyboard.FocusedElement);

                expander.Collapse();
                WpfTestHost.Pump();
                Assert.Null(more.Menu);
                Assert.Equal(ExpandCollapseState.Collapsed, expander.ExpandCollapseState);
            });
        }

        private static Point LabelPlacement(TabItem tab)
        {
            var label = (ContentPresenter)tab.Template.FindName("HeaderSite", tab);
            return label.TranslatePoint(new Point(label.ActualWidth / 2, 0), tab);
        }

        private static (TabControl Tabs, Border Host) ShowInspector(Window window, double width)
        {
            window.Width = 1200;
            var tabs = new TabControl { Style = (Style)window.FindResource("InspectorTabControlStyle") };
            foreach (var view in Views)
            {
                tabs.Items.Add(new TabItem { Header = view, Content = new TextBlock { Text = view } });
            }
            var host = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Child = tabs };
            WpfTestHost.Show(window, host);
            return (tabs, host);
        }

        private static List<TabItem> Items(TabControl tabs) => tabs.Items.Cast<TabItem>().ToList();

        private static Rect Slot(UIElement tab) => LayoutInformation.GetLayoutSlot((FrameworkElement)tab);

        private static OverflowTabPanel HeaderPanel(TabControl tabs) =>
            (OverflowTabPanel)tabs.Template.FindName("HeaderPanel", tabs);

        private static TabOverflowButton MoreButton(TabControl tabs) =>
            (TabOverflowButton)tabs.Template.FindName("MoreButton", tabs);

        private static double LabelSize(TabItem tab) =>
            TextElement.GetFontSize((ContentPresenter)tab.Template.FindName("HeaderSite", tab));

        private static void Press(UIElement target, Key key) =>
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
    }
}
