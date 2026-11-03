// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    internal partial class CaptureAppModel
    {
        private SqlQueryWorkspaceModel _sqlQueryWorkspace;

        internal bool HasSqlQueryWorkspace => _sqlQueryWorkspace != null;

        public SqlQueryWorkspaceModel SqlQueryWorkspace
        {
            get
            {
                _dispatcher.VerifyAccess();
                if (_sqlQueryWorkspace == null)
                {
                    _sqlQueryWorkspace = new(CaptureQueries, _dispatcher,
                        () => CanBeginCaptureDataOperation && !IsCaptureFileLoadRunning,
                        source => source.Kind == QuerySourceKind.Loaded ? LoadedCaptureFilePath : null,
                        () => [LoadedCaptureFilePath]);
                    PropertyChanged += SqlWorkspace_CapturePropertyChanged;
                }
                return _sqlQueryWorkspace;
            }
        }

        private void SqlWorkspace_CapturePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_dispatcher.CheckAccess())
            {
                if (!_dispatcher.HasShutdownStarted)
                {
                    _dispatcher.BeginInvoke(new Action(() => SqlWorkspace_CapturePropertyChanged(sender, e)));
                }
                return;
            }
            _sqlQueryWorkspace.NotifySnapshotAvailability();
            if (e.PropertyName is nameof(ActiveDatabase) or nameof(LoadedDatabase) or
                nameof(LoadedCaptureFilePath) or nameof(LoadedCaptureDisplayName))
            {
                _ = _sqlQueryWorkspace.RefreshSourcesAsync();
            }
        }

        internal Task PauseAndDrainSqlWorkspaceAsync() =>
            _sqlQueryWorkspace?.PauseAndDrainAsync() ?? Task.CompletedTask;

        internal void CancelSqlWorkspaceClosing() => _sqlQueryWorkspace?.CancelClosing();

        internal Task DisposeSqlWorkspaceAsync()
        {
            if (!_dispatcher.CheckAccess())
            {
                return _dispatcher.InvokeAsync(DisposeSqlWorkspaceAsync).Task.Unwrap();
            }
            PropertyChanged -= SqlWorkspace_CapturePropertyChanged;
            return _sqlQueryWorkspace?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }
    }
}
