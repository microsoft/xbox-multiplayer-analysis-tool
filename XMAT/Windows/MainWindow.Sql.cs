// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XMAT.Models;

namespace XMAT
{
    public partial class MainWindow
    {
        private void SqlWorkspace_Initialized(object sender, System.EventArgs e) =>
            WorkspaceTabs.SelectionChanged += SqlWorkspaceTab_Selected;

        private void NewSqlQuery_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            var application = CaptureAppModel.AppModel;
            e.CanExecute = !_isCloseInProgress && !_closeConfirmed &&
                (!application.HasSqlQueryWorkspace || application.SqlQueryWorkspace.CanCreateDocument);
            e.Handled = true;
        }

        private async void NewSqlQuery_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            var workspace = CaptureAppModel.AppModel.SqlQueryWorkspace;
            SqlWorkspaceView.AttachWorkspace(workspace);
            await workspace.RefreshSourcesAsync();
            workspace.NewDocument();
            WorkspaceTabs.SelectedItem = SqlWorkspaceTab;
        }

        private async void SqlWorkspaceTab_Selected(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs) || !SqlWorkspaceTab.IsSelected || _isCloseInProgress)
                return;
            SqlWorkspaceView.AttachWorkspace(CaptureAppModel.AppModel.SqlQueryWorkspace);
            await SqlWorkspaceView.InitializeAsync();
        }

        private Task<bool> CanCloseSqlWorkspaceAsync()
        {
            var application = CaptureAppModel.AppModel;
            if (!application.HasSqlQueryWorkspace)
                return Task.FromResult(true);
            SqlWorkspaceView.AttachWorkspace(application.SqlQueryWorkspace);
            return SqlWorkspaceView.ConfirmWorkspaceCloseAsync();
        }

        private static void CancelSqlWorkspaceClosing() => CaptureAppModel.AppModel.CancelSqlWorkspaceClosing();
    }
}
