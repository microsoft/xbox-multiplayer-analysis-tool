// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace XMAT
{
    // Makes a table one tab stop, with KeyboardNavigation.TabNavigation="Once" on the table: Tab passes over its cells,
    // which the arrow keys move between, and leaves the table. Tab still stops at controls inside the current row's
    // cells, such as the analysis rule buttons, and Shift+Tab goes back to a tab stop in the column headers, such as the
    // info icons of column summaries. Without this, Tab visits every cell of the current row, since each row's cells
    // form a tab group of their own.
    public static class DataGridTabStop
    {
        public static readonly DependencyProperty IsSingleProperty = DependencyProperty.RegisterAttached(
            "IsSingle", typeof(bool), typeof(DataGridTabStop), new PropertyMetadata(false, OnIsSingleChanged));

        public static bool GetIsSingle(DataGrid grid) => (bool)grid.GetValue(IsSingleProperty);

        public static void SetIsSingle(DataGrid grid, bool value) => grid.SetValue(IsSingleProperty, value);

        private static void OnIsSingleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DataGrid grid)
            {
                grid.PreviewKeyDown -= Grid_PreviewKeyDown;
                if ((bool)e.NewValue)
                {
                    grid.PreviewKeyDown += Grid_PreviewKeyDown;
                }
            }
        }

        private static void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // A cell being edited keeps Tab, which moves the edit on to the next cell.
            if (e.Key != Key.Tab || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 ||
                e.OriginalSource is not DependencyObject focused ||
                Ancestor<DataGridCell>(focused) is not { IsEditing: false } cell ||
                Ancestor<DataGridRow>(cell) is not { } row ||
                !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), sender))
            {
                return;
            }

            bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            UIElement control = NextControl(row, cell, focused, back);
            // Back from the row's first control, a tab stop in the column headers, such as the info icons of column
            // summaries, comes before the rows.
            if (control == null && back)
            {
                control = HeaderTabStop((DataGrid)sender);
            }

            // Navigation from a row leaves the table, since the row is not a tab group of its own.
            e.Handled = (control != null && control.Focus()) ||
                row.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }

        // The tab stop in GRID's column headers, or null when they have none.
        private static UIElement HeaderTabStop(DataGrid grid) =>
            Descendant<DataGridColumnHeadersPresenter>(grid) is { } headers ? Controls(headers).FirstOrDefault() : null;

        // The control Tab moves to from FOCUSED, in CELL of ROW, or null when there are no more in that direction. A
        // cell's own controls come after the cell.
        private static UIElement NextControl(DataGridRow row, DataGridCell cell, DependencyObject focused, bool back)
        {
            List<(int Column, UIElement Control)> controls = Cells(row)
                .OrderBy(rowCell => rowCell.Column?.DisplayIndex ?? int.MaxValue)
                .SelectMany(rowCell => Controls(rowCell).Select(control => (rowCell.Column?.DisplayIndex ?? int.MaxValue, control)))
                .ToList();
            int index = controls.FindIndex(entry => entry.Control == focused || entry.Control.IsAncestorOf(focused));
            if (index >= 0)
            {
                index += back ? -1 : 1;
                return index >= 0 && index < controls.Count ? controls[index].Control : null;
            }

            int column = cell.Column?.DisplayIndex ?? 0;
            return back
                ? controls.LastOrDefault(entry => entry.Column < column).Control
                : controls.FirstOrDefault(entry => entry.Column >= column).Control;
        }

        private static IEnumerable<DataGridCell> Cells(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is DataGridCell cell)
                {
                    yield return cell;
                    continue;
                }

                foreach (DataGridCell descendant in Cells(child))
                {
                    yield return descendant;
                }
            }
        }

        // The tab stops inside PARENT, in tab order, without looking inside them.
        private static IEnumerable<UIElement> Controls(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } control and not DataGridCell &&
                    KeyboardNavigation.GetIsTabStop(control))
                {
                    yield return control;
                    continue;
                }

                foreach (UIElement descendant in Controls(child))
                {
                    yield return descendant;
                }
            }
        }

        private static T Descendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if ((child as T ?? Descendant<T>(child)) is { } match)
                {
                    return match;
                }
            }

            return null;
        }

        private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
        {
            for (; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            {
                if (element is T match)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
