// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT
{
    // Shows a row of filters, the children of Filters, while the row has room for them at their natural widths. When it
    // doesn't, a Filters button takes the row's place. The button opens a popup that lists the same filters one above
    // another, and they return to the row when the popup closes, so each filter keeps its state, bindings and name.
    // Children marked IsInlineOnly, such as the row's label, stay in the row. Children marked IsSummary, such as a
    // filter's summary and the button that clears it, sit on a line of their own under the row or the Filters button:
    // they never fold, never count toward folding and never join the popup. While IsOverflowEnabled is false the row
    // never folds and keeps its summaries.
    [ContentProperty(nameof(Filters))]
    public sealed class FilterOverflowHost : FrameworkElement
    {
        private const double Epsilon = 0.01;

        // The gap between the row and the summary line under it.
        private const double SummaryGap = 4;

        public static readonly DependencyProperty FiltersProperty = DependencyProperty.Register(
            nameof(Filters), typeof(Panel), typeof(FilterOverflowHost),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnFiltersChanged));

        public static readonly DependencyProperty IsOverflowEnabledProperty = DependencyProperty.Register(
            nameof(IsOverflowEnabled), typeof(bool), typeof(FilterOverflowHost),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure, OnIsOverflowEnabledChanged));

        public static readonly DependencyProperty ActiveFilterCountProperty = DependencyProperty.Register(
            nameof(ActiveFilterCount), typeof(int), typeof(FilterOverflowHost),
            new FrameworkPropertyMetadata(0, OnActiveFilterCountChanged));

        private static readonly DependencyPropertyKey IsOverflowingPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsOverflowing), typeof(bool), typeof(FilterOverflowHost), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty IsOverflowingProperty = IsOverflowingPropertyKey.DependencyProperty;

        public static readonly DependencyProperty IsInlineOnlyProperty = DependencyProperty.RegisterAttached(
            "IsInlineOnly", typeof(bool), typeof(FilterOverflowHost), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty IsSummaryProperty = DependencyProperty.RegisterAttached(
            "IsSummary", typeof(bool), typeof(FilterOverflowHost), new FrameworkPropertyMetadata(false));

        private readonly FilterOverflowButton _button;
        private readonly Popup _popup;
        private readonly Border _surface;
        private readonly FilterOverflowList _list;
        private readonly WrapPanel _summaryLine = new();

        // The filters that the open popup lists, in row order, with the place in the row that each returns to.
        private readonly List<(UIElement Child, int Index)> _lent = new();
        private Panel _lentFrom;

        // The same for the summaries on the line under the row.
        private readonly List<(UIElement Child, int Index)> _summaries = new();
        private Panel _summariesFrom;

        public FilterOverflowHost()
        {
            _list = new FilterOverflowList();
            _surface = new Border { Child = _list };
            _surface.SetResourceReference(StyleProperty, "FilterOverflowPopupStyle");
            // Tab stays in the open popup, and Esc closes it.
            KeyboardNavigation.SetTabNavigation(_surface, KeyboardNavigationMode.Cycle);
            _surface.KeyDown += Surface_KeyDown;

            _button = new FilterOverflowButton { Visibility = Visibility.Collapsed };
            _button.SetResourceReference(StyleProperty, "FilterOverflowButtonStyle");
            _button.Checked += (sender, e) => OpenPopup();
            _button.Unchecked += (sender, e) => _popup.IsOpen = false;

            _popup = new Popup
            {
                Child = _surface,
                PlacementTarget = _button,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                StaysOpen = false,
                AllowsTransparency = true
            };
            _popup.SetResourceReference(Popup.PopupAnimationProperty, SystemParameters.ComboBoxPopupAnimationKey);
            _popup.Opened += Popup_Opened;
            _popup.Closed += Popup_Closed;

            AddLogicalChild(_button);
            AddVisualChild(_button);
            AddLogicalChild(_summaryLine);
            AddVisualChild(_summaryLine);
            AddLogicalChild(_popup);
            Unloaded += (sender, e) => _button.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        }

        // The row of filters.
        public Panel Filters
        {
            get => (Panel)GetValue(FiltersProperty);
            set => SetValue(FiltersProperty, value);
        }

        // Whether the row folds into the Filters button when it doesn't fit.
        public bool IsOverflowEnabled
        {
            get => (bool)GetValue(IsOverflowEnabledProperty);
            set => SetValue(IsOverflowEnabledProperty, value);
        }

        // How many filters are on, which the Filters button shows while it stands in for the row.
        public int ActiveFilterCount
        {
            get => (int)GetValue(ActiveFilterCountProperty);
            set => SetValue(ActiveFilterCountProperty, value);
        }

        // Whether the Filters button stands in for the row.
        public bool IsOverflowing => (bool)GetValue(IsOverflowingProperty);

        // The width the host asks for while its row doesn't fold: the row at its natural width, or the summary line under
        // it if that's wider. Known once the host has been measured.
        internal double NaturalWidth => Math.Max(Filters?.DesiredSize.Width ?? 0, _summaryLine.DesiredSize.Width);

        internal ToggleButton OverflowButton => _button;

        internal Popup OverflowPopup => _popup;

        // The line under the row that holds the summaries.
        internal Panel SummaryLine => _summaryLine;

        protected override int VisualChildrenCount => Filters == null ? 2 : 3;

        protected override IEnumerator LogicalChildren
        {
            get
            {
                var children = new List<object>(4);
                if (Filters != null)
                {
                    children.Add(Filters);
                }

                children.Add(_button);
                children.Add(_summaryLine);
                children.Add(_popup);
                return children.GetEnumerator();
            }
        }

        public static bool GetIsInlineOnly(DependencyObject element) => (bool)element.GetValue(IsInlineOnlyProperty);

        public static void SetIsInlineOnly(DependencyObject element, bool value) => element.SetValue(IsInlineOnlyProperty, value);

        public static bool GetIsSummary(DependencyObject element) => (bool)element.GetValue(IsSummaryProperty);

        public static void SetIsSummary(DependencyObject element, bool value) => element.SetValue(IsSummaryProperty, value);

        protected override Visual GetVisualChild(int index)
        {
            Panel filters = Filters;
            int first = filters == null ? 0 : 1;
            if (index == 0 && filters != null)
            {
                return filters;
            }

            if (index == first)
            {
                return _button;
            }

            if (index == first + 1)
            {
                return _summaryLine;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // XAML can fill in the row after the host initializes, so the summaries find their line here, once the row
            // is complete and before anything is measured.
            PlaceSummaries();
            _summaryLine.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            double summaryHeight = SummaryHeight();
            var rowSize = new Size(availableSize.Width, Math.Max(0, availableSize.Height - summaryHeight));
            Panel filters = Filters;
            if (filters != null)
            {
                // The row at its natural width; arranged wider, it can share the extra room among its filters.
                filters.Measure(new Size(double.PositiveInfinity, rowSize.Height));

                // While the open popup lists the filters, the row stays folded.
                if (_lent.Count == 0)
                {
                    SetIsOverflowing(IsOverflowEnabled && filters.DesiredSize.Width > availableSize.Width + Epsilon);
                }
            }

            _button.Measure(rowSize);
            Size row = IsOverflowing || filters == null ? _button.DesiredSize : filters.DesiredSize;
            return new Size(Math.Max(row.Width, _summaryLine.DesiredSize.Width), row.Height + summaryHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // While the button stands in for the row, the row is hidden but keeps its layout, so the host still learns
            // when it would fit again.
            double summaryHeight = SummaryHeight();
            double rowHeight = Math.Max(0, finalSize.Height - summaryHeight);
            Filters?.Arrange(new Rect(0, 0, finalSize.Width, rowHeight));
            _button.Arrange(new Rect(0, 0, Math.Min(_button.DesiredSize.Width, finalSize.Width), rowHeight));
            double lineHeight = _summaryLine.DesiredSize.Height;
            _summaryLine.Arrange(new Rect(0, Math.Max(0, finalSize.Height - lineHeight), finalSize.Width, lineHeight));
            return finalSize;
        }

        private static void OnFiltersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = (FilterOverflowHost)d;
            host._button.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            host.ReturnSummaries();
            if (e.OldValue is Panel old)
            {
                old.SetCurrentValue(VisibilityProperty, Visibility.Visible);
                host.RemoveVisualChild(old);
                host.RemoveLogicalChild(old);
            }

            host.SetValue(IsOverflowingPropertyKey, false);
            host._button.Visibility = Visibility.Collapsed;
            if (e.NewValue is Panel panel)
            {
                host.AddLogicalChild(panel);
                host.AddVisualChild(panel);
            }
        }

        private static void OnIsOverflowEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = (FilterOverflowHost)d;
            if (e.NewValue is false)
            {
                host._button.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            }

            host.PlaceSummaries();
        }

        private static void OnActiveFilterCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((FilterOverflowHost)d)._button.ShowActiveFilters((int)e.NewValue);

        // Moves the children of FROM that MATCH into TO, in order, recording the place in FROM that each returns to.
        private static void Move(Panel from, Panel to, Func<UIElement, bool> match, List<(UIElement Child, int Index)> moved)
        {
            var moving = from.Children.Cast<UIElement>()
                .Select((child, index) => (Child: child, Index: index))
                .Where(entry => entry.Child != null && match(entry.Child))
                .ToList();
            foreach ((UIElement child, int index) in moving)
            {
                from.Children.Remove(child);
                to.Children.Add(child);
                moved.Add((child, index));
            }
        }

        // Returns the children that Move took out of TO, each to its recorded place.
        private static void Return(List<(UIElement Child, int Index)> moved, Panel holder, Panel to)
        {
            foreach ((UIElement child, int index) in moved)
            {
                holder.Children.Remove(child);
                to?.Children.Insert(Math.Min(index, to.Children.Count), child);
            }

            moved.Clear();
        }

        // Focuses the first control under ROOT that Tab stops at. Returns whether one took focus.
        private static bool FocusFirst(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element &&
                    KeyboardNavigation.GetIsTabStop(element) && element.Focus())
                {
                    return true;
                }

                if (FocusFirst(child))
                {
                    return true;
                }
            }

            return false;
        }

        private void SetIsOverflowing(bool overflowing)
        {
            if (overflowing == IsOverflowing)
            {
                return;
            }

            // Keyboard focus in the part that goes away moves to the part that takes its place.
            bool moveFocus = overflowing ? Filters.IsKeyboardFocusWithin : _button.IsKeyboardFocused;
            SetValue(IsOverflowingPropertyKey, overflowing);
            Filters.SetCurrentValue(VisibilityProperty, overflowing ? Visibility.Hidden : Visibility.Visible);
            _button.Visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            if (moveFocus)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (IsOverflowing)
                    {
                        _button.Focus();
                    }
                    else if (Filters != null)
                    {
                        FocusFirst(Filters);
                    }
                }));
            }
        }

        // Lends the filters to the popup and opens it. Only a folded row opens one.
        private void OpenPopup()
        {
            Panel filters = Filters;
            if (!IsOverflowing || filters == null)
            {
                _button.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                return;
            }

            if (_lent.Count == 0)
            {
                _lentFrom = filters;
                Move(filters, _list, child => !GetIsInlineOnly(child) && !GetIsSummary(child), _lent);
            }

            _popup.IsOpen = true;
        }

        // While the row can fold, its summaries sit on the line under it, where they stay in view; otherwise they go
        // back to their places in the row.
        private void PlaceSummaries()
        {
            Panel filters = Filters;
            if (!IsOverflowEnabled || filters == null)
            {
                ReturnSummaries();
            }
            else if (_summaries.Count == 0)
            {
                _summariesFrom = filters;
                Move(filters, _summaryLine, GetIsSummary, _summaries);
            }
        }

        private void ReturnSummaries()
        {
            Return(_summaries, _summaryLine, _summariesFrom);
            _summariesFrom = null;
        }

        // The room that the summary line takes under the row, with its gap; none while it shows nothing.
        private double SummaryHeight() =>
            _summaryLine.DesiredSize.Height > 0 ? SummaryGap + _summaryLine.DesiredSize.Height : 0;

        private void Popup_Opened(object sender, EventArgs e)
        {
            // The open popup closes at a press outside it, so the button ignores presses meanwhile: a press on it
            // closes the popup without opening it again.
            _button.IsHitTestVisible = false;
            // Focus moves to the first filter once the popup shows.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_popup.IsOpen)
                {
                    FocusFirst(_list);
                }
            }));
        }

        private void Popup_Closed(object sender, EventArgs e)
        {
            _button.IsHitTestVisible = true;
            foreach ((UIElement filter, _) in _lent)
            {
                // A filter's own popup, such as an editor, closes with the list.
                if (filter is Popup popup)
                {
                    popup.SetCurrentValue(Popup.IsOpenProperty, false);
                }
            }

            // Focus left in the closed popup would be lost, so it returns to the button. A press elsewhere keeps the
            // focus it moved.
            if (Keyboard.FocusedElement == null || _surface.IsKeyboardFocusWithin)
            {
                _button.Focus();
            }

            Return(_lent, _list, _lentFrom);
            _lentFrom = null;
            _button.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            InvalidateMeasure();
        }

        // Esc closes the popup and returns to the button. Controls in the popup that use Esc themselves, such as an open
        // dropdown, handle it first.
        private void Surface_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _popup.IsOpen)
            {
                e.Handled = true;
                _popup.IsOpen = false;
            }
        }

        // The filters in the popup, one above another with Spacing between them, each as wide as the list. Margins
        // that space the filters along the row are set aside, so every filter lines up with the list's edges.
        private sealed class FilterOverflowList : Panel
        {
            private const double Spacing = 8;

            protected override Size MeasureOverride(Size availableSize)
            {
                var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
                double width = 0;
                double height = 0;
                bool first = true;
                foreach (UIElement child in InternalChildren)
                {
                    child.Measure(unbounded);
                    if (child.DesiredSize.Height <= 0)
                    {
                        continue;
                    }

                    width = Math.Max(width, child.DesiredSize.Width - HorizontalMargin(child));
                    height += child.DesiredSize.Height + (first ? 0 : Spacing);
                    first = false;
                }

                return new Size(width, height);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                double y = 0;
                bool first = true;
                foreach (UIElement child in InternalChildren)
                {
                    double height = child.DesiredSize.Height;
                    if (height <= 0)
                    {
                        // Collapsed filters and a filter's own popup take no room and no gap.
                        child.Arrange(new Rect(0, y, finalSize.Width, 0));
                        continue;
                    }

                    if (!first)
                    {
                        y += Spacing;
                    }

                    Thickness margin = child is FrameworkElement element ? element.Margin : default;
                    child.Arrange(new Rect(-margin.Left, y, finalSize.Width + margin.Left + margin.Right, height));
                    y += height;
                    first = false;
                }

                return finalSize;
            }

            private static double HorizontalMargin(UIElement child) =>
                child is FrameworkElement element ? element.Margin.Left + element.Margin.Right : 0;
        }
    }

    // The Filters button that takes the place of a folded filter row: a funnel icon that opens the list of filters.
    // Screen readers get a button that expands and collapses, named for how many filters are on.
    public sealed class FilterOverflowButton : ToggleButton
    {
        private static readonly DependencyPropertyKey HasActiveFiltersPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(HasActiveFilters), typeof(bool), typeof(FilterOverflowButton), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty HasActiveFiltersProperty = HasActiveFiltersPropertyKey.DependencyProperty;

        internal FilterOverflowButton()
        {
            ShowActiveFilters(0);
            IsVisibleChanged += (sender, e) =>
            {
                if (!IsVisible)
                {
                    SetCurrentValue(IsCheckedProperty, false);
                }
            };
        }

        // Whether any filter is on; the button's style marks it.
        public bool HasActiveFilters => (bool)GetValue(HasActiveFiltersProperty);

        internal void ShowActiveFilters(int count)
        {
            SetValue(HasActiveFiltersPropertyKey, count > 0);
            string name = count > 0
                ? Localization.GetLocalizedString("FILTER_OVERFLOW_ACTIVE_NAME", count)
                : Localization.GetLocalizedString("GENERIC_FILTERS");
            AutomationProperties.SetName(this, name);
            ToolTip = name;
        }

        // Alt+Down opens the list, as it opens a dropdown; Enter and Space press the button as they press any other.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!e.Handled && IsChecked != true && e.Key == Key.System && e.SystemKey == Key.Down &&
                (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0)
            {
                e.Handled = true;
                SetCurrentValue(IsCheckedProperty, true);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            RaiseExpandCollapseChanged(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded);
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            RaiseExpandCollapseChanged(ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new FilterOverflowButtonAutomationPeer(this);

        private void RaiseExpandCollapseChanged(ExpandCollapseState oldState, ExpandCollapseState newState)
        {
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) &&
                UIElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, oldState, newState);
            }
        }

        private sealed class FilterOverflowButtonAutomationPeer : ButtonBaseAutomationPeer, IExpandCollapseProvider, IInvokeProvider
        {
            internal FilterOverflowButtonAutomationPeer(FilterOverflowButton owner) : base(owner)
            {
            }

            public ExpandCollapseState ExpandCollapseState =>
                ((FilterOverflowButton)Owner).IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

            protected override string GetClassNameCore() => nameof(FilterOverflowButton);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface is PatternInterface.ExpandCollapse or PatternInterface.Invoke
                    ? this
                    : base.GetPattern(patternInterface);

            public void Expand() => SetOpen(true);

            public void Collapse() => SetOpen(false);

            void IInvokeProvider.Invoke() => SetOpen(((FilterOverflowButton)Owner).IsChecked != true);

            private void SetOpen(bool open)
            {
                if (!IsEnabled())
                {
                    throw new ElementNotEnabledException();
                }

                Owner.SetCurrentValue(IsCheckedProperty, open);
            }
        }
    }
}
