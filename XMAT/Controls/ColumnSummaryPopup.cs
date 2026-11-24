// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XMAT
{
    // The column summary of one table (see ColumnSummary), and the info icons in its headers that open it
    // (ColumnSummaryButton). A summary opens 4px below its column's header, left-aligned with it, with focus on its
    // first value: Up and Down move, Enter or a click chooses a value, and Esc closes the summary and returns focus to
    // its icon. A click on its icon or outside it, or focus moving anywhere but the summary and its icon, closes it.
    // The counts are those of the rows the table shows as it opens, and they hold still while the pointer or the focus
    // is in it; otherwise they follow the table at most once a second. The icons are one tab stop: the one used last,
    // or else the first.
    internal sealed class ColumnSummaryPopup : Popup
    {
        internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
        private const double HeaderGap = 4;

        private readonly DataGrid _grid;
        private readonly ColumnSummaryView _view = new();
        private readonly DispatcherTimer _refreshTimer;
        private readonly List<ColumnSummaryButton> _buttons = new();
        // The column whose icon is the icons' tab stop: the icon that had focus last.
        private DataGridColumn _tabStopColumn;
        // The open summary's description and icon, and where its header's left edge was in the table as it opened.
        private ColumnSummaryColumn _description;
        private ColumnSummaryButton _opener;
        private double _headerLeft;
        private Window _window;
        private ColumnSummaryRow _pressedRow;
        private bool _closing;
        // Counts the summaries opened, so a focus check that waited knows whether its summary is still the open one.
        private int _opened;

        internal ColumnSummaryPopup(DataGrid grid)
        {
            _grid = grid;
            Child = _view;
            AllowsTransparency = true;
            Placement = PlacementMode.Bottom;
            Focusable = false;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Normal, grid.Dispatcher) { Interval = RefreshInterval };
            _refreshTimer.Tick += RefreshTimer_Tick;

            grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Grid_ScrollChanged));
            grid.SizeChanged += Grid_SizeChanged;
            grid.IsVisibleChanged += Grid_IsVisibleChanged;
            grid.Unloaded += Grid_Unloaded;
            _view.PreviewKeyDown += View_PreviewKeyDown;
            _view.PreviewMouseLeftButtonDown += View_PreviewMouseLeftButtonDown;
            _view.PreviewMouseLeftButtonUp += View_PreviewMouseLeftButtonUp;
            _view.LostKeyboardFocus += Summary_LostKeyboardFocus;
        }

        private enum FocusAfterClose
        {
            // Focus in the summary returns to its icon.
            ReturnIfInSummary,
            ReturnToIcon,
            // Focus is on its way somewhere else.
            Stay,
        }

        // The column whose summary is open, or null.
        internal DataGridColumn Column { get; private set; }

        internal ColumnSummaryView View => _view;

        // The info icons that show, in column order.
        internal IReadOnlyList<ColumnSummaryButton> Buttons => Shown();

        // Stops following the table, for a table that no longer has column summaries.
        internal void Detach()
        {
            Close();
            _grid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Grid_ScrollChanged));
            _grid.SizeChanged -= Grid_SizeChanged;
            _grid.IsVisibleChanged -= Grid_IsVisibleChanged;
            _grid.Unloaded -= Grid_Unloaded;
            _buttons.Clear();
        }

        // Whether COLUMN has a summary, and so an info icon.
        internal bool HasSummary(DataGridColumn column) => ColumnSummary.Describe(_grid, column) != null;

        internal void Add(ColumnSummaryButton button)
        {
            if (!_buttons.Contains(button))
            {
                _buttons.Add(button);
            }
        }

        internal void Remove(ColumnSummaryButton button)
        {
            if (ReferenceEquals(button, _opener))
            {
                Close(FocusAfterClose.Stay);
            }

            _buttons.Remove(button);
            UpdateTabStops();
        }

        // The icon of COLUMN, while it shows.
        internal ColumnSummaryButton ButtonOf(DataGridColumn column) =>
            Shown().FirstOrDefault(button => ReferenceEquals(button.Column, column));

        // Makes BUTTON the icons' tab stop, as when it gets focus.
        internal void SetTabStop(ColumnSummaryButton button)
        {
            _tabStopColumn = button.Column;
            UpdateTabStops();
        }

        // Exactly one icon that shows is a tab stop: the one that had focus last, or else the first.
        internal void UpdateTabStops()
        {
            List<ColumnSummaryButton> shown = Shown();
            ColumnSummaryButton stop =
                shown.FirstOrDefault(button => ReferenceEquals(button.Column, _tabStopColumn)) ?? shown.FirstOrDefault();
            foreach (ColumnSummaryButton button in _buttons)
            {
                button.IsTabStop = ReferenceEquals(button, stop);
            }
        }

        // Moves focus from FROM to the icon STEP icons along, or to the first or last icon for int.MinValue or
        // int.MaxValue. Focus stays at either end.
        internal void MoveFocus(ColumnSummaryButton from, int step)
        {
            List<ColumnSummaryButton> shown = Shown();
            int index = shown.IndexOf(from);
            int target = step switch
            {
                int.MinValue => 0,
                int.MaxValue => shown.Count - 1,
                _ => index + step,
            };
            if (index >= 0 && target >= 0 && target < shown.Count && target != index)
            {
                shown[target].Focus();
            }
        }

        // Moves focus from the icons to the table's rows, as Tab does: to the current cell, or else to the selected row
        // or the first row in view. Returns false when no row shows.
        internal bool FocusRows()
        {
            DataGridColumn first = _grid.Columns
                .Where(column => column.Visibility == Visibility.Visible)
                .OrderBy(column => column.DisplayIndex)
                .FirstOrDefault();
            DataGridCell cell = CellOf(_grid.CurrentCell.Item, _grid.CurrentCell.Column ?? first) ??
                CellOf(_grid.SelectedItem, first) ??
                CellOf(FirstItemInView(), first);
            return cell != null && cell.Focus();
        }

        // Opens COLUMN's summary from its icon, with focus on its first value. Returns false in the original look, or when
        // the column has no summary or its icon doesn't show.
        internal bool Open(DataGridColumn column)
        {
            ColumnSummaryButton button = LookState.Current.IsNewLook && _grid.IsVisible ? ButtonOf(column) : null;
            ColumnSummaryColumn description = button == null ? null : ColumnSummary.Describe(_grid, column);
            DataGridColumnHeader header = button?.Header;
            if (description == null || header is not { IsVisible: true })
            {
                return false;
            }

            if (IsOpen && ReferenceEquals(Column, column))
            {
                FocusFirstValue();
                return true;
            }

            Close();
            _opened++;
            Column = column;
            _description = description;
            _opener = button;
            _headerLeft = HeaderLeft();
            Refresh(force: true);

            // The surface sits inside a margin for its shadow; the popup is placed so that the surface lines up with the
            // header's left edge, the gap below it.
            _view.ApplyTemplate();
            Thickness inset = _view.Surface?.Margin ?? default;
            PlacementTarget = header;
            HorizontalOffset = -inset.Left;
            VerticalOffset = HeaderGap - inset.Top;
            Watch();
            if (SystemParameters.ClientAreaAnimation)
            {
                _view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
            }

            IsOpen = true;
            button.IsSummaryOpen = true;
            _refreshTimer.Start();
            FocusFirstValue();
            return true;
        }

        // Closes the summary. Focus that was in it returns to its icon, and so does any focus when RETURNFOCUS is set.
        internal void Close(bool returnFocus = false) =>
            Close(returnFocus ? FocusAfterClose.ReturnToIcon : FocusAfterClose.ReturnIfInSummary);

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Close();
        }

        private void Close(FocusAfterClose focus)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            try
            {
                bool focusInside = _view.IsKeyboardFocusWithin;
                ColumnSummaryButton opener = _opener;
                _refreshTimer.Stop();
                Unwatch();
                IsOpen = false;
                _view.BeginAnimation(OpacityProperty, null);
                if (opener != null)
                {
                    opener.IsSummaryOpen = false;
                }

                if (Column != null &&
                    (focus == FocusAfterClose.ReturnToIcon || (focus == FocusAfterClose.ReturnIfInSummary && focusInside)))
                {
                    ReturnFocus(opener);
                }

                Column = null;
                _description = null;
                _opener = null;
                _pressedRow = null;
            }
            finally
            {
                _closing = false;
            }
        }

        // Counts the rows that the table shows now. Unless FORCE is set, the summary holds still while the pointer or
        // focus is in it, and keeps its rows when nothing changed.
        private void Refresh(bool force)
        {
            if (Column == null || (!force && (_view.IsMouseOver || _view.IsKeyboardFocusWithin)))
            {
                return;
            }

            ColumnSummaryModel model = ColumnSummaryModel.Create(
                ColumnSummary.Title(Column), _description, _grid.Items, _grid.SelectedItem);
            if (force || !model.HasSameContent(_view.Model))
            {
                _view.Model = model;
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e) => Refresh(force: false);

        private void FocusFirstValue()
        {
            if (!_view.FocusFirstValue())
            {
                // The values are laid out once the popup has its window.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (IsOpen)
                    {
                        _view.FocusFirstValue();
                    }
                }));
            }
        }

        private void ReturnFocus(ColumnSummaryButton opener)
        {
            if (!(opener is { IsVisible: true } && opener.Focus()))
            {
                _grid.Focus();
            }
        }

        // While open, the summary closes when the window moves or loses activation, on a click in the window other than
        // on an info icon, which handles its own click, when its icon loses focus to anything but the summary, and when
        // the original look is shown.
        private void Watch()
        {
            _window = Window.GetWindow(_grid);
            if (_window != null)
            {
                _window.Deactivated += Window_Changed;
                _window.LocationChanged += Window_Changed;
                _window.PreviewMouseDown += Window_PreviewMouseDown;
            }

            _opener.LostKeyboardFocus += Summary_LostKeyboardFocus;
            LookState.Current.PropertyChanged += Look_PropertyChanged;
        }

        private void Unwatch()
        {
            if (_window != null)
            {
                _window.Deactivated -= Window_Changed;
                _window.LocationChanged -= Window_Changed;
                _window.PreviewMouseDown -= Window_PreviewMouseDown;
                _window = null;
            }

            if (_opener != null)
            {
                _opener.LostKeyboardFocus -= Summary_LostKeyboardFocus;
            }

            LookState.Current.PropertyChanged -= Look_PropertyChanged;
        }

        private void Window_Changed(object sender, EventArgs e) => Close();

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ButtonAt(e.OriginalSource as DependencyObject) == null)
            {
                Close();
            }
        }

        private void Look_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!LookState.Current.IsNewLook)
            {
                Close();
            }
        }

        // Focus that leaves the summary and its icon, as for a click on another icon or Tab from the icon, closes it.
        // Moving between the summary's window and the table's, focus can pass through other elements on its way, so the
        // summary waits to see where it lands.
        private void Summary_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (Column == null || _closing)
            {
                return;
            }

            int opened = _opened;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (opened == _opened && Column != null && !(Keyboard.FocusedElement is DependencyObject focus &&
                    (ReferenceEquals(focus, _opener) || ReferenceEquals(focus, _view) || _view.IsAncestorOf(focus))))
                {
                    Close(FocusAfterClose.Stay);
                }
            }));
        }

        private void Grid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Rows scroll under a summary, as Auto Scroll does while capturing, and as rows arrive the table can grow
            // wider; the summary closes only when its header moves sideways.
            if (Column != null && (e.HorizontalChange != 0 || Math.Abs(HeaderLeft() - _headerLeft) > 0.5))
            {
                Close();
            }
        }

        private void Grid_SizeChanged(object sender, SizeChangedEventArgs e) => Close();

        private void Grid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_grid.IsVisible)
            {
                Close(FocusAfterClose.Stay);
            }
        }

        private void Grid_Unloaded(object sender, RoutedEventArgs e) => Close(FocusAfterClose.Stay);

        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.Tab:
                case Key.System when e.SystemKey == Key.Up:
                    Close(returnFocus: true);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (RowAt(Keyboard.FocusedElement as DependencyObject) is { CanSelect: true } row)
                    {
                        Choose(row);
                    }

                    e.Handled = true;
                    break;
            }
        }

        // A click chooses a value when it presses and releases on the same row; it never moves focus.
        private void View_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressedRow = RowAt(e.OriginalSource as DependencyObject);
            e.Handled = true;
        }

        private void View_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ColumnSummaryRow row = RowAt(e.OriginalSource as DependencyObject);
            bool chosen = row is { CanSelect: true } && ReferenceEquals(row, _pressedRow);
            _pressedRow = null;
            e.Handled = true;
            if (chosen)
            {
                Choose(row);
            }
        }

        private void Choose(ColumnSummaryRow row)
        {
            ColumnSummaryColumn description = _description;
            Close();
            description?.Select?.Invoke(row.Entry);
        }

        private List<ColumnSummaryButton> Shown() =>
            _buttons
                .Where(button => button.Visibility == Visibility.Visible && button.Column is { Visibility: Visibility.Visible })
                .OrderBy(button => button.Column.DisplayIndex)
                .ToList();

        // The left edge of the open summary's header in the table.
        private double HeaderLeft() =>
            _opener?.Header is { } header && _grid.IsAncestorOf(header) ? header.TranslatePoint(default, _grid).X : 0;

        // The icon of this table that SOURCE is in, or null.
        private ColumnSummaryButton ButtonAt(DependencyObject source)
        {
            for (DependencyObject element = source; element != null; element = ParentOf(element))
            {
                if (element is ColumnSummaryButton button)
                {
                    return _buttons.Contains(button) ? button : null;
                }
            }

            return null;
        }

        private DataGridCell CellOf(object item, DataGridColumn column)
        {
            if (item == null || column == null ||
                _grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow { IsVisible: true } row)
            {
                return null;
            }

            return Descendants(row).OfType<DataGridCell>().FirstOrDefault(cell => ReferenceEquals(cell.Column, column));
        }

        // The table's top row in view. Tables scroll by rows, so the vertical offset is that row's index.
        private object FirstItemInView()
        {
            if (_grid.Items.Count == 0)
            {
                return null;
            }

            ScrollViewer viewer = Descendants(_grid).OfType<ScrollViewer>().FirstOrDefault();
            int index = viewer is { CanContentScroll: true } ? (int)viewer.VerticalOffset : 0;
            return _grid.Items[Math.Clamp(index, 0, _grid.Items.Count - 1)];
        }

        private static ColumnSummaryRow RowAt(DependencyObject source)
        {
            for (DependencyObject element = source; element != null; element = ParentOf(element))
            {
                if (element is ListBoxItem item)
                {
                    return item.DataContext as ColumnSummaryRow;
                }
            }

            return null;
        }

        private static DependencyObject ParentOf(DependencyObject element) =>
            (element is Visual ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);

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
