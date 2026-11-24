// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XMAT
{
    // A table's column summaries, in the new look: the info icon at the right end of a column header
    // (ColumnSummaryButton) shows how that column's values are spread over the rows that the table shows, the most
    // common first, and choosing a value can filter the table to it (see ColumnSummaryPopup). IsEnabled turns them on
    // for a DataGrid. Source describes each column; without one, every column with a sort member is summarized by that
    // member's text and has no filter.
    public static class ColumnSummary
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ColumnSummary), new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(IColumnSummarySource), typeof(ColumnSummary), new PropertyMetadata(null, OnSourceChanged));

        private static readonly DependencyProperty PopupProperty = DependencyProperty.RegisterAttached(
            "Popup", typeof(ColumnSummaryPopup), typeof(ColumnSummary), new PropertyMetadata(null));

        public static bool GetIsEnabled(DataGrid grid) => (bool)grid.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

        public static IColumnSummarySource GetSource(DataGrid grid) => (IColumnSummarySource)grid.GetValue(SourceProperty);

        public static void SetSource(DataGrid grid, IColumnSummarySource value) => grid.SetValue(SourceProperty, value);

        // The summary popup of GRID, while its summaries are on.
        internal static ColumnSummaryPopup PopupOf(DataGrid grid) => (ColumnSummaryPopup)grid.GetValue(PopupProperty);

        // How GRID summarizes COLUMN, or null when the column has no summary.
        internal static ColumnSummaryColumn Describe(DataGrid grid, DataGridColumn column)
        {
            if (column == null)
            {
                return null;
            }

            ColumnSummaryColumn description = GetSource(grid) is { } source
                ? source.Describe(column)
                : ColumnSummaryColumn.FromSortMember(column);
            return description?.ValueOf == null ? null : description;
        }

        // A column's name as its header shows it, or its full name for an abbreviated header.
        internal static string Title(DataGridColumn column) =>
            DataGridColumnInfo.GetDescription(column) ??
            new TitleCaseConverter().Convert(column.Header, typeof(string), null, CultureInfo.CurrentCulture) as string ??
            column.Header?.ToString() ??
            string.Empty;

        private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not DataGrid grid)
            {
                return;
            }

            PopupOf(grid)?.Detach();
            grid.ClearValue(PopupProperty);
            if ((bool)e.NewValue)
            {
                grid.SetValue(PopupProperty, new ColumnSummaryPopup(grid));
            }

            RefreshButtons(grid);
        }

        private static void OnSourceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is DataGrid grid)
            {
                RefreshButtons(grid);
            }
        }

        // The info icons already in GRID's headers follow a change to its summaries.
        private static void RefreshButtons(DataGrid grid)
        {
            foreach (ColumnSummaryButton button in Descendants(grid).OfType<ColumnSummaryButton>().ToList())
            {
                button.Refresh();
            }
        }

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
