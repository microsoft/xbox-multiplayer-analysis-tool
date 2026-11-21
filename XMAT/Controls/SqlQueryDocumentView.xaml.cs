// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XMAT.Models;
using XMAT.SqlQueries;

namespace XMAT
{
    public partial class SqlQueryDocumentView : UserControl
    {
        private SqlQueryDocumentModel Document => DataContext as SqlQueryDocumentModel;
        public SqlQueryDocumentView() => InitializeComponent();

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (Document is { Schema.Count: 0, CanRefreshSchema: true } document)
                await document.RefreshSchemaAsync();
        }

        private async void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Document is { CanRefreshSchema: true } document)
                await document.RefreshSchemaAsync();
        }

        private async Task RunAsync()
        {
            var document = Document;
            if (document == null)
                return;
            if (Validation.GetHasError(TimeoutInput) || Validation.GetHasError(QuotaInput))
            {
                document.ReportMessage(Localization.GetLocalizedString("SQL_INVALID_OPTIONS"));
                return;
            }
            await document.RunAsync(EditorView.Editor.SelectionStart, EditorView.Editor.SelectionLength);
        }

        private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();
        private async void Editor_RunRequested(object sender, EventArgs e) => await RunAsync();
        private void Cancel_Click(object sender, RoutedEventArgs e) => Document?.Cancel();
        private async void RefreshSchema_Click(object sender, RoutedEventArgs e)
        {
            if (Document != null)
                await Document.RefreshSchemaAsync();
        }

        private async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();
        private async Task OpenAsync()
        {
            var document = Document;
            if (document == null || !document.CanEditFiles ||
                !await SqlQueryDocumentDialogs.ConfirmUnsavedAsync(Window.GetWindow(this), document))
                return;
            var path = SqlQueryDocumentDialogs.PickSqlFile(Window.GetWindow(this));
            if (path != null)
                await document.OpenFileAsync(path);
        }
        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Document != null)
                await SqlQueryDocumentDialogs.SaveAsync(Window.GetWindow(this), Document);
        }
        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (Document != null)
                await SqlQueryDocumentDialogs.SaveAsync(Window.GetWindow(this), Document, saveAs: true);
        }

        private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5 || e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await RunAsync();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
            {
                e.Handled = true;
                await OpenAsync();
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                if (Document != null && Document.CanEditFiles)
                {
                    await SqlQueryDocumentDialogs.SaveAsync(Window.GetWindow(this), Document,
                        saveAs: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                }
            }
        }

        private void Examples_Click(object sender, RoutedEventArgs e)
        {
            var document = Document;
            if (document == null)
                return;
            var menu = new ContextMenu { PlacementTarget = ExamplesButton, Placement = PlacementMode.Bottom };
            foreach (var example in document.Examples)
            {
                var item = new MenuItem { Header = example.Title, ToolTip = example.Sql };
                item.Click += (_, _) => EditorView.ReplaceSql(example.Sql);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Localization.GetLocalizedString("SQL_NO_EXAMPLES"), IsEnabled = false });
            }
            menu.IsOpen = true;
        }

        private void InsertSchema_Click(object sender, RoutedEventArgs e) => InsertSchema();
        private void SchemaTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SchemaTree.SelectedItem is SqlQuerySchemaItem)
                InsertSchema();
        }
        private void InsertSchema()
        {
            if (SchemaTree.SelectedItem is SqlQuerySchemaItem item)
                EditorView.InsertSql(item.Insertion);
        }
        private void GenerateSelect_Click(object sender, RoutedEventArgs e) => GenerateSelect(false);
        private void PreviewTable_Click(object sender, RoutedEventArgs e) => GenerateSelect(true);
        private void GenerateSelect(bool preview)
        {
            if (SchemaTree.SelectedItem is SqlQuerySchemaItem item)
            {
                EditorView.ReplaceSql(SqlQueryExamples.Select(item.Table, preview));
                Document?.ReportMessage(Localization.GetLocalizedString("SQL_GENERATED_NOT_RUN"));
            }
        }
    }
}
