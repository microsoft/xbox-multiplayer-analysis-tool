// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XMAT.Models;
using XMAT.SqlQueries;

namespace XMAT
{
    public partial class SqlQueryWorkspaceView : UserControl
    {
        private readonly Dictionary<SqlQueryDocumentModel, TabItem> _tabs = [];
        private bool _updatingTabs;
        internal SqlQueryWorkspaceModel Workspace => DataContext as SqlQueryWorkspaceModel;
        public SqlQueryWorkspaceView()
        {
            InitializeComponent();
            CommandBindings.Add(new CommandBinding(AppCommands.AnalyzeCaptures, IgnoreCaptureCommand, DenyCaptureCommand));
            CommandBindings.Add(new CommandBinding(AppCommands.ClearAllCaptures, IgnoreCaptureCommand, DenyCaptureCommand));
        }

        private static void IgnoreCaptureCommand(object sender, ExecutedRoutedEventArgs e) => e.Handled = true;
        private static void DenyCaptureCommand(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = false;
            e.Handled = true;
        }

        internal void AttachWorkspace(SqlQueryWorkspaceModel workspace)
        {
            if (ReferenceEquals(DataContext, workspace))
                return;
            if (Workspace != null)
            {
                Workspace.Documents.CollectionChanged -= Documents_CollectionChanged;
                Workspace.PropertyChanged -= Workspace_PropertyChanged;
            }
            _updatingTabs = true;
            try
            {
                _tabs.Clear();
                DocumentTabs.Items.Clear();
                DataContext = workspace;
                if (workspace != null)
                {
                    workspace.Documents.CollectionChanged += Documents_CollectionChanged;
                    workspace.PropertyChanged += Workspace_PropertyChanged;
                }
                SynchronizeTabs();
            }
            finally { _updatingTabs = false; }
        }

        private void Documents_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => SynchronizeTabs();
        private void Workspace_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SqlQueryWorkspaceModel.SelectedDocument))
                SynchronizeTabs();
        }

        private void SynchronizeTabs()
        {
            var wasUpdating = _updatingTabs;
            _updatingTabs = true;
            try
            {
                foreach (var removed in _tabs.Keys.Where(document => Workspace?.Documents.Contains(document) != true).ToArray())
                {
                    DocumentTabs.Items.Remove(_tabs[removed]);
                    _tabs.Remove(removed);
                }
                if (Workspace == null)
                    return;
                foreach (var document in Workspace.Documents.Where(document => !_tabs.ContainsKey(document)))
                {
                    // Explicit tab content keeps each editor and its undo stack alive when WPF detaches a tab.
                    var tab = new TabItem
                    {
                        Header = document,
                        HeaderTemplate = (DataTemplate)Resources["SqlDocumentHeader"],
                        Content = new SqlQueryDocumentView { DataContext = document },
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Stretch
                    };
                    _tabs.Add(document, tab);
                    DocumentTabs.Items.Add(tab);
                }
                DocumentTabs.SelectedItem = Workspace.SelectedDocument == null ? null :
                    _tabs.GetValueOrDefault(Workspace.SelectedDocument);
            }
            finally { _updatingTabs = wasUpdating; }
        }

        private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingTabs && ReferenceEquals(e.OriginalSource, DocumentTabs) && Workspace != null)
            {
                Workspace.SelectedDocument = (DocumentTabs.SelectedItem as TabItem)?.Header as SqlQueryDocumentModel;
            }
        }

        internal async Task InitializeAsync()
        {
            if (Workspace == null)
                return;
            await Workspace.RefreshSourcesAsync();
            if (Workspace.Documents.Count == 0)
                Workspace.NewDocument();
        }

        internal Task<bool> ConfirmWorkspaceCloseAsync() => Workspace == null ? Task.FromResult(true) :
            Workspace.RequestCloseAsync(document => SqlQueryDocumentDialogs.ConfirmUnsavedAsync(Window.GetWindow(this), document));

        private async void NewQuery_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace == null)
                return;
            await Workspace.RefreshSourcesAsync();
            Workspace.NewDocument();
        }

        private async void RefreshSources_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace != null)
                await Workspace.RefreshSourcesAsync();
        }

        private async void OpenQuery_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace?.CanCreateDocument != true)
                return;
            var path = SqlQueryDocumentDialogs.PickSqlFile(Window.GetWindow(this));
            if (path == null)
                return;
            await Workspace.RefreshSourcesAsync();
            var document = Workspace.NewDocument();
            if (document != null)
                await document.OpenFileAsync(path);
        }

        private async void CloseDocument_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace != null && sender is Button { DataContext: SqlQueryDocumentModel document })
            {
                await Workspace.CloseDocumentAsync(document,
                    item => SqlQueryDocumentDialogs.ConfirmUnsavedAsync(Window.GetWindow(this), item));
            }
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            if (Workspace == null)
                return;
            var menu = new ContextMenu { PlacementTarget = HistoryButton, Placement = PlacementMode.Bottom };
            foreach (var entry in Workspace.History)
            {
                var item = new MenuItem { Header = entry.DisplayName, ToolTip = entry.Sql };
                item.Click += (_, _) => Workspace.Reopen(entry);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Localization.GetLocalizedString("SQL_HISTORY_EMPTY"), IsEnabled = false });
            }
            else
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = Localization.GetLocalizedString("SQL_HISTORY_CLEAR") };
                clear.Click += (_, _) => Workspace.History.Clear();
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
        }

        private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            if (e.Key == Key.N)
            {
                e.Handled = true;
                Workspace?.NewDocument();
            }
            else if (e.Key == Key.W && Workspace?.SelectedDocument is SqlQueryDocumentModel document)
            {
                e.Handled = true;
                await Workspace.CloseDocumentAsync(document,
                    item => SqlQueryDocumentDialogs.ConfirmUnsavedAsync(Window.GetWindow(this), item));
            }
            else if (e.Key is Key.A or Key.X && e.OriginalSource is TextBox text)
            {
                if (e.Key == Key.A)
                    text.SelectAll();
                else
                    text.Cut();
                e.Handled = true;
            }
        }
    }
}
