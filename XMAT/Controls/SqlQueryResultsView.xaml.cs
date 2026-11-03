// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT
{
    public partial class SqlQueryResultsView : UserControl
    {
        private SqlQueryDocumentModel _document;
        private bool _restoringSelection;
        private int _pageGeneration;

        public SqlQueryResultsView() => InitializeComponent();

        private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_document != null)
                _document.PropertyChanged -= Document_PropertyChanged;
            _document = e.NewValue as SqlQueryDocumentModel;
            if (_document != null)
                _document.PropertyChanged += Document_PropertyChanged;
            RebuildColumns();
            RestoreSelection();
        }

        private void Document_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SqlQueryDocumentModel.ResultColumns))
                RebuildColumns();
            if (e.PropertyName is nameof(SqlQueryDocumentModel.PageRows) or nameof(SqlQueryDocumentModel.SelectedRows))
            {
                var generation = ++_pageGeneration;
                Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
                {
                    if (generation == _pageGeneration)
                        RestoreSelection();
                }));
            }
        }

        private void RebuildColumns()
        {
            ResultGrid.Columns.Clear();
            if (_document == null)
                return;
            var labels = Models.SqlQueryResultPresentation.ColumnLabels(_document.ResultColumns);
            for (var index = 0; index < _document.ResultColumns.Count; index++)
            {
                var metadata = _document.ResultColumns[index];
                var headerStyle = new Style(typeof(DataGridColumnHeader),
                    TryFindResource(typeof(DataGridColumnHeader)) as Style);
                headerStyle.Setters.Add(new Setter(ToolTipProperty,
                    Localization.GetLocalizedString("SQL_COLUMN_METADATA",
                        index + 1, metadata.Name, metadata.DeclaredType, metadata.TableName, metadata.OriginName)));
                var cellStyle = new Style(typeof(TextBlock));
                cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                cellStyle.Setters.Add(new Setter(ToolTipProperty, new Binding($"Cells[{index}].Description")));
                ResultGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = labels[index],
                    HeaderStyle = headerStyle,
                    Binding = new Binding($"Cells[{index}].Preview"),
                    ElementStyle = cellStyle,
                    CanUserSort = false,
                    MinWidth = 60,
                    MaxWidth = 420,
                    Width = DataGridLength.SizeToHeader
                });
            }
        }

        private void RestoreSelection()
        {
            if (_document == null)
                return;
            _restoringSelection = true;
            try
            {
                var selected = _document.SelectedRows.ToHashSet();
                ResultGrid.SelectedItems.Clear();
                foreach (var row in _document.PageRows.Where(row => selected.Contains(row.Ordinal)))
                {
                    ResultGrid.SelectedItems.Add(row);
                }
            }
            finally { _restoringSelection = false; }
        }

        private void ResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection || _document == null)
                return;
            _document.UpdateSelectedRows(e.AddedItems.OfType<SqlQueryResultRow>().Select(row => row.Ordinal),
                e.RemovedItems.OfType<SqlQueryResultRow>().Where(_document.PageRows.Contains).Select(row => row.Ordinal));
            UpdateInspector();
        }

        private void ResultGrid_CurrentCellChanged(object sender, EventArgs e) => UpdateInspector();

        private void UpdateInspector()
        {
            if (_document == null || _restoringSelection)
                return;
            var row = ResultGrid.CurrentItem as SqlQueryResultRow ?? ResultGrid.SelectedItem as SqlQueryResultRow;
            _document.SelectInspectorRow(row);
            var column = ResultGrid.CurrentCell.Column;
            var ordinal = column == null ? 0 : ResultGrid.Columns.IndexOf(column);
            if (row != null && ordinal >= 0 && ordinal < row.Cells.Count)
                FieldsGrid.SelectedItem = row.Cells[ordinal];
        }

        private void FieldsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            _document?.SelectInspectorCell(FieldsGrid.SelectedItem as SqlQueryCellModel);

        private void ResultGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                ResultGrid.SelectAll();
                e.Handled = true;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
                e.Handled = true;
        }

        private async void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_document != null)
                await _document.MovePageAsync(false);
        }
        private async void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_document != null)
                await _document.MovePageAsync(true);
        }
        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            _document?.ClearSelection();
            RestoreSelection();
        }
        private async void InspectRead_Click(object sender, RoutedEventArgs e)
        {
            if (_document != null)
                await _document.InspectValueAsync(FieldsGrid.SelectedItem as SqlQueryCellModel, true);
        }
        private async void InspectNext_Click(object sender, RoutedEventArgs e)
        {
            if (_document != null)
                await _document.InspectValueAsync(FieldsGrid.SelectedItem as SqlQueryCellModel, false);
        }

        private void ExportAll_Click(object sender, RoutedEventArgs e) => ShowExportMenu(ExportAllButton, false);
        private void ExportSelected_Click(object sender, RoutedEventArgs e) => ShowExportMenu(ExportSelectedButton, true);

        private void ShowExportMenu(Button target, bool selectedOnly)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            foreach (var format in new[] { QueryExportFormat.Csv, QueryExportFormat.Json })
            {
                var item = new MenuItem
                {
                    Header = Localization.GetLocalizedString(format == QueryExportFormat.Csv ? "SQL_EXPORT_CSV" : "SQL_EXPORT_JSON")
                };
                item.Click += async (_, _) => await ExportAsync(format, selectedOnly);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async Task ExportAsync(QueryExportFormat format, bool selectedOnly)
        {
            var document = _document;
            if (document == null || !document.CanReadResult)
                return;
            var count = selectedOnly ? document.SelectedRows.Count : document.RowCount;
            if (MessageBox.Show(Window.GetWindow(this),
                Localization.GetLocalizedString("SQL_EXPORT_WARNING", count,
                    Localization.GetLocalizedString(selectedOnly ? "SQL_SELECTED_ROWS" : "SQL_ALL_ROWS")),
                Localization.GetLocalizedString("SQL_EXPORT_TITLE"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            var extension = format == QueryExportFormat.Csv ? ".csv" : ".json";
            var dialog = new SaveFileDialog
            {
                Title = Localization.GetLocalizedString("SQL_EXPORT_TITLE"),
                DefaultExt = extension,
                Filter = format == QueryExportFormat.Csv
                    ? Localization.GetLocalizedString("SQL_CSV_FILTER") : Localization.GetLocalizedString("SQL_JSON_FILTER"),
                FileName = "query-results" + extension,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            if (!await document.ExportAsync(dialog.FileName, format, selectedOnly, File.Exists(dialog.FileName)))
            {
                ResultTabs.SelectedIndex = 1;
            }
        }
    }
}
