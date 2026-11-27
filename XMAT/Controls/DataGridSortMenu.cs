// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace XMAT
{
    // Lets the keyboard sort a table, since column headers can't take focus: the table's context menu (Shift+F10 or
    // the Menu key) lists its sortable columns under Sort by, and choosing one sorts as a click on its header does.
    public static class DataGridSortMenu
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(DataGridSortMenu), new PropertyMetadata(false, OnIsEnabledChanged));

        // Marks the items this class adds, so each opening replaces them.
        private static readonly object AddedItemTag = new();

        // Marks the empty menu this class gives a table that has none. Only that menu is kept from opening when it has
        // nothing to show; a menu that belongs to a row or to the view is filled by its own handlers.
        private static readonly object SortOnlyMenuTag = new();

        public static bool GetIsEnabled(DependencyObject grid) => (bool)grid.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject grid, bool value) => grid.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not DataGrid grid)
            {
                return;
            }

            grid.ContextMenuOpening -= Grid_ContextMenuOpening;
            if (!(bool)e.NewValue)
            {
                return;
            }

            grid.ContextMenuOpening += Grid_ContextMenuOpening;
            if (grid.ContextMenu == null)
            {
                // A menu only opens for an element that has one, so a table without a menu gets an empty one here.
                grid.SetCurrentValue(FrameworkElement.ContextMenuProperty, new ContextMenu { Tag = SortOnlyMenuTag });
            }
        }

        private static void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!PrepareMenu((DataGrid)sender, e.OriginalSource as DependencyObject))
            {
                // Nothing to show: don't open an empty menu.
                e.Handled = true;
            }
        }

        // Adds Sort by to the menu that opens from SOURCE in GRID. Returns false only when that menu is the empty one
        // this class gave the table and there is still nothing to show.
        internal static bool PrepareMenu(DataGrid grid, DependencyObject source)
        {
            ContextMenu menu = MenuFor(source, grid);
            return menu == null || AddSortItems(grid, menu) || !ReferenceEquals(menu.Tag, SortOnlyMenuTag) ||
                menu.Items.Count > 0;
        }

        // Replaces MENU's Sort by item with one for GRID's sortable columns. Returns whether there was any to list.
        internal static bool AddSortItems(DataGrid grid, ContextMenu menu)
        {
            if (menu.ItemsSource != null)
            {
                return false;
            }

            // A table's own menu, such as an analysis table's, takes the look's menu surface too.
            GroupedMenu.ApplyLook(menu);
            foreach (object added in menu.Items.OfType<FrameworkElement>().Where(item => item.Tag == AddedItemTag).ToList())
            {
                menu.Items.Remove(added);
            }

            List<DataGridColumn> columns = SortableColumns(grid);
            if (columns.Count == 0)
            {
                return false;
            }

            var sortBy = new MenuItem { Header = Localization.GetLocalizedString("GRID_SORT_BY"), Tag = AddedItemTag };
            if (LookState.Current.IsNewLook)
            {
                MenuRow.SetGlyph(sortBy, MenuGlyphs.Sort);
            }
            foreach (DataGridColumn column in columns)
            {
                var item = new MenuItem
                {
                    Header = ColumnName(column).Replace("_", "__"),
                    IsCheckable = true,
                    IsChecked = column.SortDirection != null,
                };
                item.Click += (_, _) => Sort(grid, column);
                sortBy.Items.Add(item);
            }

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator { Tag = AddedItemTag });
            }

            menu.Items.Add(sortBy);
            return true;
        }

        // Columns the keyboard can sort by: those a header click sorts, while the headers show and the column has a
        // name to list.
        internal static List<DataGridColumn> SortableColumns(DataGrid grid) =>
            grid.CanUserSortColumns && (grid.HeadersVisibility & DataGridHeadersVisibility.Column) != 0
                ? grid.Columns.Where(column => column.CanUserSort && column.Visibility == Visibility.Visible &&
                    !string.IsNullOrEmpty(column.SortMemberPath) && !string.IsNullOrWhiteSpace(ColumnName(column))).ToList()
                : new List<DataGridColumn>();

        // Sorts by COLUMN through its header, so the table's own sort handling and direction cycling apply. Header peers
        // stand for the columns' header content, so columns that share a header are told apart by their order.
        internal static void Sort(DataGrid grid, DataGridColumn column)
        {
            DataGridColumnHeadersPresenter headers = Descendants(grid).OfType<DataGridColumnHeadersPresenter>().FirstOrDefault();
            if (headers == null)
            {
                return;
            }

            List<ItemAutomationPeer> peers = UIElementAutomationPeer.CreatePeerForElement(headers)?.GetChildren()?
                .OfType<ItemAutomationPeer>().Where(peer => ReferenceEquals(peer.Item, column.Header)).ToList();
            int index = grid.Columns
                .Where(other => other.Visibility == Visibility.Visible && ReferenceEquals(other.Header, column.Header))
                .OrderBy(other => other.DisplayIndex).ToList().IndexOf(column);
            if (peers != null && index >= 0 && index < peers.Count)
            {
                (peers[index].GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
            }
        }

        private static string ColumnName(DataGridColumn column) =>
            DataGridColumnInfo.GetDescription(column) ??
            (new TitleCaseConverter().Convert(column.Header, typeof(string), null, CultureInfo.CurrentCulture) as string) ??
            column.Header?.ToString() ??
            string.Empty;

        // The menu that opens: the closest one from where the menu was asked for, up to the table.
        private static ContextMenu MenuFor(DependencyObject source, DataGrid grid)
        {
            for (DependencyObject element = source; element != null; element = ParentOf(element))
            {
                if (element is FrameworkElement { ContextMenu: { } menu })
                {
                    return menu;
                }

                if (ReferenceEquals(element, grid))
                {
                    break;
                }
            }

            return grid.ContextMenu;
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
