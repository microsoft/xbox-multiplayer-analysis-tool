// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT
{
    // Lays a tab control's tabs out in one row that never scrolls or wraps. When the tabs don't fit, they first become
    // compact (IsCompact, which a tab template can use to set a smaller label). Tabs that still don't fit leave the row
    // and are listed by a TabOverflowButton placed at OverflowButtonOffset. The selected tab always stays in the row.
    // Wraps instead lays the tabs out in rows exactly as WPF's TabPanel does, for a look that keeps wrapping rows.
    public sealed class OverflowTabPanel : Panel
    {
        private const double Epsilon = 0.01;

        public static readonly DependencyProperty WrapsProperty = DependencyProperty.Register(
            nameof(Wraps), typeof(bool), typeof(OverflowTabPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
            "IsCompact", typeof(bool), typeof(OverflowTabPanel), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsOverflowItemPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
            "IsOverflowItem", typeof(bool), typeof(OverflowTabPanel), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty IsOverflowItemProperty = IsOverflowItemPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey HasOverflowPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(HasOverflow), typeof(bool), typeof(OverflowTabPanel), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty HasOverflowProperty = HasOverflowPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey OverflowButtonOffsetPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(OverflowButtonOffset), typeof(double), typeof(OverflowTabPanel), new FrameworkPropertyMetadata(0.0));

        public static readonly DependencyProperty OverflowButtonOffsetProperty =
            OverflowButtonOffsetPropertyKey.DependencyProperty;

        public static readonly DependencyProperty OverflowButtonWidthProperty = DependencyProperty.Register(
            nameof(OverflowButtonWidth), typeof(double), typeof(OverflowTabPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private readonly List<UIElement> _overflowItems = new();

        // The rows that Wraps lays out, and the height they share.
        private int _rowCount = 1;
        private double _rowHeight;

        static OverflowTabPanel()
        {
            // As in TabPanel, Tab enters the row once, at the selected tab.
            KeyboardNavigation.TabNavigationProperty.OverrideMetadata(
                typeof(OverflowTabPanel), new FrameworkPropertyMetadata(KeyboardNavigationMode.Once));
            KeyboardNavigation.DirectionalNavigationProperty.OverrideMetadata(
                typeof(OverflowTabPanel), new FrameworkPropertyMetadata(KeyboardNavigationMode.Cycle));
        }

        public OverflowTabPanel()
        {
            ClipToBounds = true;
            AddHandler(Selector.SelectedEvent, new RoutedEventHandler(OnTabSelectionChanged), true);
            AddHandler(Selector.UnselectedEvent, new RoutedEventHandler(OnTabSelectionChanged), true);
        }

        // True lays the tabs out in as many rows as they need, every row but a lone one stretched to the full width
        // and the selected tab's row next to the content, as TabPanel does. No tab is then compact or listed by More,
        // the row doesn't clip, and the arrow keys move between tabs as they do in a TabPanel.
        public bool Wraps
        {
            get => (bool)GetValue(WrapsProperty);
            set => SetValue(WrapsProperty, value);
        }

        public bool HasOverflow => (bool)GetValue(HasOverflowProperty);

        // Where the More button goes: just after the last tab in the row.
        public double OverflowButtonOffset => (double)GetValue(OverflowButtonOffsetProperty);

        // The width to keep free for the More button when some tabs don't fit.
        public double OverflowButtonWidth
        {
            get => (double)GetValue(OverflowButtonWidthProperty);
            set => SetValue(OverflowButtonWidthProperty, value);
        }

        // The tabs that are out of the row, in their tab order.
        public IReadOnlyList<UIElement> OverflowItems => _overflowItems;

        public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

        public static bool GetIsOverflowItem(DependencyObject element) => (bool)element.GetValue(IsOverflowItemProperty);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Wraps)
            {
                PutEveryTabInARow();
                return MeasureRows(availableSize);
            }

            var tabs = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
            {
                if (child != null && child.Visibility != Visibility.Collapsed)
                {
                    tabs.Add(child);
                }
            }

            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
            double width = MeasureTabs(tabs, false, unbounded);
            if (width > availableSize.Width + Epsilon)
            {
                width = MeasureTabs(tabs, true, unbounded);
            }

            _overflowItems.Clear();
            if (width > availableSize.Width + Epsilon)
            {
                // Even when the selected tab alone is too wide, More stays in the row and the tab is clipped.
                double room = Math.Max(0, availableSize.Width - OverflowButtonWidth);
                width = Math.Min(KeepTabsThatFit(tabs, room), room);
            }

            foreach (UIElement child in InternalChildren)
            {
                child?.SetValue(IsOverflowItemPropertyKey, _overflowItems.Contains(child));
            }

            bool hasOverflow = _overflowItems.Count > 0;
            SetValue(HasOverflowPropertyKey, hasOverflow);
            SetValue(OverflowButtonOffsetPropertyKey, width);

            double height = 0;
            foreach (UIElement tab in tabs)
            {
                height = Math.Max(height, tab.DesiredSize.Height);
            }

            return new Size(hasOverflow ? width + OverflowButtonWidth : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Wraps)
            {
                ArrangeRows(finalSize);
                return finalSize;
            }

            double end = HasOverflow ? OverflowButtonOffset : finalSize.Width;
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                {
                    continue;
                }

                if (GetIsOverflowItem(child))
                {
                    // Out of view, but still in the tree, so the keyboard and accessibility tools can select it.
                    child.Arrange(new Rect(x, finalSize.Height / 2, 0, 0));
                    continue;
                }

                double width = HasOverflow ? Math.Min(child.DesiredSize.Width, Math.Max(0, end - x)) : child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }

        // Rows never clip their tabs, as in TabPanel.
        protected override Geometry GetLayoutClip(Size layoutSlotSize) => Wraps ? null : base.GetLayoutClip(layoutSlotSize);

        // Left and Right move through every tab in order, including tabs out of the row: the tab they select joins it.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || Wraps || e.KeyboardDevice.Modifiers != ModifierKeys.None)
            {
                return;
            }

            UIElement tab = TabContaining(e.OriginalSource as DependencyObject);
            if (tab == null)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Left:
                case Key.Right:
                    bool forward = (e.Key == Key.Right) == (FlowDirection == FlowDirection.LeftToRight);
                    SelectNextTab(tab, forward ? 1 : -1);
                    e.Handled = true;
                    break;

                case Key.Up:
                case Key.Down:
                    // The row has no other line to move to.
                    e.Handled = true;
                    break;
            }
        }

        private static double MeasureTabs(List<UIElement> tabs, bool compact, Size constraint)
        {
            double width = 0;
            foreach (UIElement tab in tabs)
            {
                if (GetIsCompact(tab) != compact)
                {
                    tab.SetValue(IsCompactPropertyKey, compact);

                    // The tab's template reacts below the tab itself, which would otherwise keep its earlier size.
                    InvalidateMeasureTree(tab);
                }

                tab.Measure(constraint);
                width += tab.DesiredSize.Width;
            }

            return width;
        }

        private static void InvalidateMeasureTree(DependencyObject element)
        {
            if (element is UIElement uiElement)
            {
                uiElement.InvalidateMeasure();
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                InvalidateMeasureTree(VisualTreeHelper.GetChild(element, i));
            }
        }

        // Keeps the selected tab, then as many tabs in order as fit; the rest are listed by the More button.
        private double KeepTabsThatFit(List<UIElement> tabs, double available)
        {
            UIElement selected = tabs.Find(tab => Selector.GetIsSelected(tab));
            double width = selected?.DesiredSize.Width ?? 0;
            bool full = false;
            foreach (UIElement tab in tabs)
            {
                if (tab == selected)
                {
                    continue;
                }

                if (!full && width + tab.DesiredSize.Width <= available + Epsilon)
                {
                    width += tab.DesiredSize.Width;
                }
                else
                {
                    full = true;
                    _overflowItems.Add(tab);
                }
            }

            return width;
        }

        // In rows every tab is in a row at its full size, so none is compact or listed by More.
        private void PutEveryTabInARow()
        {
            _overflowItems.Clear();
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                {
                    continue;
                }

                if (GetIsCompact(child))
                {
                    child.SetValue(IsCompactPropertyKey, false);
                    InvalidateMeasureTree(child);
                }

                child.SetValue(IsOverflowItemPropertyKey, false);
            }

            SetValue(HasOverflowPropertyKey, false);
            SetValue(OverflowButtonOffsetPropertyKey, 0.0);
        }

        // The tabs that rows lay out: as in TabPanel, every tab that isn't collapsed.
        private List<UIElement> RowTabs()
        {
            var tabs = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
            {
                if (child != null && child.Visibility != Visibility.Collapsed)
                {
                    tabs.Add(child);
                }
            }

            return tabs;
        }

        // A tab's size in a row leaves out its margin, as in TabPanel.
        private static Size SizeInRow(UIElement tab)
        {
            Thickness margin = (Thickness)tab.GetValue(MarginProperty);
            return new Size(
                Math.Max(0, tab.DesiredSize.Width - margin.Left - margin.Right),
                Math.Max(0, tab.DesiredSize.Height - margin.Top - margin.Bottom));
        }

        // Tabs flow into rows as wide as the panel: a tab starts a new row when it would make its row too wide.
        private Size MeasureRows(Size constraint)
        {
            _rowCount = 1;
            _rowHeight = 0;
            double widestRow = 0;
            double rowWidth = 0;
            int tabsInRow = 0;
            foreach (UIElement tab in RowTabs())
            {
                tab.Measure(constraint);
                Size size = SizeInRow(tab);
                _rowHeight = Math.Max(_rowHeight, size.Height);
                if (tabsInRow > 0 && rowWidth + size.Width > constraint.Width)
                {
                    widestRow = Math.Max(widestRow, rowWidth);
                    _rowCount++;
                    rowWidth = 0;
                    tabsInRow = 0;
                }

                rowWidth += size.Width;
                tabsInRow++;
            }

            widestRow = Math.Max(widestRow, rowWidth);
            return new Size(Math.Min(widestRow, constraint.Width), _rowHeight * _rowCount);
        }

        // When there are several rows, each is stretched to the full width, its tabs sharing the spare width, and the
        // row with the selected tab sits next to the content: last for tabs above it, first for tabs below it.
        private void ArrangeRows(Size finalSize)
        {
            List<UIElement> tabs = RowTabs();
            var widths = new double[tabs.Count];
            for (int i = 0; i < tabs.Count; i++)
            {
                widths[i] = SizeInRow(tabs[i]).Width;
            }

            Dock placement = (TemplatedParent as TabControl)?.TabStripPlacement ?? Dock.Top;
            bool severalRows = _rowCount > 1;
            int[] rowEnds = severalRows ? BreakRows(finalSize.Width, widths) : Array.Empty<int>();
            int selectedRow = severalRows ? SelectedRow(tabs, rowEnds, placement) : 0;
            double y = 0;
            if (severalRows && placement == Dock.Top)
            {
                y = (_rowCount - 1 - selectedRow) * _rowHeight;
            }
            else if (severalRows && placement == Dock.Bottom && selectedRow != 0)
            {
                y = (_rowCount - selectedRow) * _rowHeight;
            }

            double x = 0;
            int row = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                bool endsRow = severalRows && ((row < rowEnds.Length && rowEnds[row] == i) || i == tabs.Count - 1);
                double width = endsRow ? Math.Max(0, finalSize.Width - x) : widths[i];
                tabs[i].Arrange(new Rect(x, y, width, _rowHeight));
                x += width;
                if (endsRow)
                {
                    // The rows after the selected tab's row continue from the far edge of the strip.
                    bool restart = (placement == Dock.Top && row == selectedRow) ||
                        (placement == Dock.Bottom && row == selectedRow - 1);
                    y = restart ? 0 : y + _rowHeight;
                    x = 0;
                    row++;
                }
            }
        }

        // Where the rows break, as TabPanel finds it: the tabs first flow into rows; then, while that lowers the largest
        // share of spare width that any row's tabs get, the row with the largest share takes the last tab of the row
        // before it. Each tab then grows by its row's share. Returns the index of the last tab of every row but the last.
        private int[] BreakRows(double limit, double[] widths)
        {
            int rows = _rowCount;
            var ends = new int[rows - 1];
            var rowWidths = new double[rows];
            var counts = new int[rows];
            var shares = new double[rows];
            double bestLargestShare = 0;
            int row = 0;
            double rowWidth = 0;
            int tabsInRow = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                if (tabsInRow > 0 && rowWidth + widths[i] > limit && row < rows - 1)
                {
                    rowWidths[row] = rowWidth;
                    counts[row] = tabsInRow;
                    shares[row] = Math.Max(0, (limit - rowWidth) / tabsInRow);
                    ends[row] = i - 1;
                    bestLargestShare = Larger(bestLargestShare, shares[row]);
                    row++;
                    rowWidth = widths[i];
                    tabsInRow = 1;
                }
                else
                {
                    rowWidth += widths[i];

                    // A tab that takes no width gets no share.
                    if (widths[i] != 0)
                    {
                        tabsInRow++;
                    }
                }
            }

            if (row == 0)
            {
                return Array.Empty<int>();
            }

            rowWidths[row] = rowWidth;
            counts[row] = tabsInRow;
            shares[row] = tabsInRow > 0 ? (limit - rowWidth) / tabsInRow : 0;
            bestLargestShare = Larger(bestLargestShare, shares[row]);
            var bestEnds = (int[])ends.Clone();
            var bestShares = (double[])shares.Clone();
            while (true)
            {
                int to = LargestShare(shares).Row;
                if (to == 0 || ends[to - 1] < 0)
                {
                    break;
                }

                int from = to - 1;
                double moved = widths[ends[from]];
                rowWidths[to] += moved;
                if (rowWidths[to] > limit)
                {
                    break;
                }

                ends[from]--;
                counts[to]++;
                rowWidths[from] -= moved;
                counts[from]--;
                shares[from] = (limit - rowWidths[from]) / counts[from];
                shares[to] = (limit - rowWidths[to]) / counts[to];
                double largest = LargestShare(shares).Share;
                if (largest < bestLargestShare)
                {
                    bestLargestShare = largest;
                    ends.CopyTo(bestEnds, 0);
                    shares.CopyTo(bestShares, 0);
                }
            }

            row = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] += bestShares[row];
                if (row < bestEnds.Length && bestEnds[row] == i)
                {
                    row++;
                }
            }

            return bestEnds;
        }

        // The first row with the largest share above zero, or the first row and zero when no row has one.
        private static (int Row, double Share) LargestShare(double[] shares)
        {
            (int Row, double Share) largest = (0, 0);
            for (int i = 0; i < shares.Length; i++)
            {
                if (largest.Share < shares[i])
                {
                    largest = (i, shares[i]);
                }
            }

            return largest;
        }

        private static double Larger(double current, double candidate) => current < candidate ? candidate : current;

        // The row with the selected tab; with no tab selected, the row nearest the content for tabs above it.
        private int SelectedRow(List<UIElement> tabs, int[] rowEnds, Dock placement)
        {
            int row = 0;
            if (rowEnds.Length > 0)
            {
                for (int i = 0; i < tabs.Count; i++)
                {
                    if (Selector.GetIsSelected(tabs[i]))
                    {
                        return row;
                    }

                    if (row < rowEnds.Length && rowEnds[row] == i)
                    {
                        row++;
                    }
                }
            }

            return placement == Dock.Top ? _rowCount - 1 : row;
        }

        private void SelectNextTab(UIElement from, int step)
        {
            int count = InternalChildren.Count;
            int index = InternalChildren.IndexOf(from);
            for (int i = 1; i < count; i++)
            {
                UIElement candidate = InternalChildren[(((index + (step * i)) % count) + count) % count];
                if (candidate != null && candidate.Visibility == Visibility.Visible && candidate.IsEnabled)
                {
                    candidate.SetCurrentValue(Selector.IsSelectedProperty, true);
                    candidate.Focus();
                    return;
                }
            }
        }

        private UIElement TabContaining(DependencyObject element)
        {
            while (element != null)
            {
                DependencyObject parent = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
                if (parent == this)
                {
                    return element as UIElement;
                }

                element = parent;
            }

            return null;
        }

        private void OnTabSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is UIElement tab && VisualTreeHelper.GetParent(tab) == this)
            {
                InvalidateMeasure();
            }
        }
    }

    // The More button at the end of a tab row. It lists the tabs that an OverflowTabPanel moved out of the row;
    // choosing one selects it, and the selected tab then takes a place in the row.
    public sealed class TabOverflowButton : ToggleButton
    {
        public static readonly DependencyProperty OverflowPanelProperty = DependencyProperty.Register(
            nameof(OverflowPanel), typeof(OverflowTabPanel), typeof(TabOverflowButton), new FrameworkPropertyMetadata(null));

        public TabOverflowButton()
        {
            Content = Localization.GetLocalizedString("TAB_OVERFLOW_MORE");
            AutomationProperties.SetName(this, Localization.GetLocalizedString("TAB_OVERFLOW_NAME"));
            IsVisibleChanged += (sender, e) =>
            {
                if (!IsVisible)
                {
                    SetCurrentValue(IsCheckedProperty, false);
                }
            };
        }

        public OverflowTabPanel OverflowPanel
        {
            get => (OverflowTabPanel)GetValue(OverflowPanelProperty);
            set => SetValue(OverflowPanelProperty, value);
        }

        // The open menu, or null.
        internal ContextMenu Menu { get; private set; }

        // Set while assistive technology, such as a screen reader, opens or closes the menu.
        private bool _openingFromAutomation;

        // The text that names TAB in the menu: its header text, or all the text its header shows, so a label in parts,
        // such as a loaded capture's "[Read Only]", is listed in full.
        internal static string TabLabel(UIElement tab)
        {
            if (tab is HeaderedContentControl { Header: string header })
            {
                return header;
            }

            string name = AutomationProperties.GetName(tab);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var shown = new StringBuilder();
            AppendShownText(tab, shown);
            string text = shown.ToString().Trim();
            return text.Length > 0 ? text : (tab as HeaderedContentControl)?.Header?.ToString() ?? string.Empty;
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            if (!OpenMenu())
            {
                SetCurrentValue(IsCheckedProperty, false);
                return;
            }

            RaiseExpandCollapseChanged(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded);
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            CloseMenu();
            RaiseExpandCollapseChanged(ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed);
        }

        // Screen readers announce More as a button that opens a menu, expanded while the menu is open, rather than as
        // a toggle that is on or off.
        protected override AutomationPeer OnCreateAutomationPeer() => new TabOverflowButtonAutomationPeer(this);

        private void RaiseExpandCollapseChanged(ExpandCollapseState oldState, ExpandCollapseState newState)
        {
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) &&
                UIElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, oldState, newState);
            }
        }

        // A press on More while its menu is open closes the menu. The menu lets go of the mouse before it reports that
        // it closed, so the press reaches this button first and would otherwise toggle it on again and reopen the menu.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Menu != null)
            {
                CloseMenu();
                SetCurrentValue(IsCheckedProperty, false);
                e.Handled = true;
                return;
            }

            base.OnMouseLeftButtonDown(e);
        }

        // Adds the text of each label that ELEMENT shows, in order. Buttons in a header, such as a close button, and
        // labels that are hidden are skipped.
        private static void AppendShownText(DependencyObject element, StringBuilder text)
        {
            if (element is ButtonBase || element is UIElement { Visibility: not Visibility.Visible })
            {
                return;
            }

            if (element is TextBlock block)
            {
                // A label made of runs, as a capture tab's is, has no Text of its own.
                text.Append(string.IsNullOrEmpty(block.Text) ? new TextRange(block.ContentStart, block.ContentEnd).Text : block.Text);
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                AppendShownText(VisualTreeHelper.GetChild(element, i), text);
            }
        }

        // The red dot that ELEMENT shows while its capture runs (TabRecordingDotStyle), or null when it shows none.
        private static RecordingIndicator ShownRecordingDot(DependencyObject element)
        {
            if (element is UIElement { Visibility: not Visibility.Visible })
            {
                return null;
            }

            if (element is RecordingIndicator { IsActive: true } dot)
            {
                return dot;
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                if (ShownRecordingDot(VisualTreeHelper.GetChild(element, i)) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        private void SelectTab(UIElement tab)
        {
            SetCurrentValue(IsCheckedProperty, false);
            tab.SetCurrentValue(Selector.IsSelectedProperty, true);
            tab.Focus();
        }

        private bool OpenMenu()
        {
            IReadOnlyList<UIElement> tabs = OverflowPanel?.OverflowItems;
            if (tabs == null || tabs.Count == 0)
            {
                return false;
            }

            var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
            menu.SetResourceReference(StyleProperty, "TabOverflowMenuStyle");
            AutomationProperties.SetName(menu, Localization.GetLocalizedString("TAB_OVERFLOW_NAME"));
            foreach (UIElement tab in tabs)
            {
                // Underscores would otherwise mark access keys.
                var item = new MenuItem { Header = TabLabel(tab).Replace("_", "__"), IsEnabled = tab.IsEnabled };
                // A tab that shows a capture's red dot shows it here too, so the menu also shows which captures run.
                if (ShownRecordingDot(tab) != null)
                {
                    var dot = new RecordingIndicator { IsActive = true, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
                    dot.SetResourceReference(StyleProperty, "TabRecordingDotStyle");
                    item.Icon = dot;
                }

                item.Click += (sender, e) => SelectTab(tab);
                menu.Items.Add(item);
            }

            if (_openingFromAutomation || InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            {
                // Opened from the keyboard or by assistive technology, the menu starts on its first tab, as menus
                // opened with a key do. The menu focuses itself as it opens, so this waits for that.
                menu.Opened += (sender, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsEnabled)?.Focus()));
            }

            menu.Closed += Menu_Closed;
            Menu = menu;
            menu.IsOpen = true;
            return true;
        }

        private void CloseMenu()
        {
            ContextMenu menu = Menu;
            Menu = null;
            if (menu != null)
            {
                menu.IsOpen = false;
            }
        }

        // Escape or a click elsewhere closes the menu; Closed can follow after its closing animation.
        private void Menu_Closed(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            menu.Closed -= Menu_Closed;
            if (ReferenceEquals(menu, Menu))
            {
                Menu = null;
                SetCurrentValue(IsCheckedProperty, false);
            }
        }

        private sealed class TabOverflowButtonAutomationPeer : ButtonBaseAutomationPeer, IExpandCollapseProvider, IInvokeProvider
        {
            internal TabOverflowButtonAutomationPeer(TabOverflowButton owner) : base(owner)
            {
            }

            private TabOverflowButton Button => (TabOverflowButton)Owner;

            public ExpandCollapseState ExpandCollapseState =>
                Button.IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

            protected override string GetClassNameCore() => nameof(TabOverflowButton);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface is PatternInterface.ExpandCollapse or PatternInterface.Invoke
                    ? this
                    : base.GetPattern(patternInterface);

            public void Expand() => SetOpen(true);

            public void Collapse() => SetOpen(false);

            void IInvokeProvider.Invoke() => SetOpen(Button.IsChecked != true);

            private void SetOpen(bool open)
            {
                if (!IsEnabled())
                {
                    throw new ElementNotEnabledException();
                }

                Button._openingFromAutomation = true;
                try
                {
                    Button.SetCurrentValue(IsCheckedProperty, open);
                }
                finally
                {
                    Button._openingFromAutomation = false;
                }
            }
        }
    }
}
