// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XMAT.Operations;
using XMAT.Operations.Export;

namespace XMAT
{
    public partial class SanitizedExportProgressWindow : Window, IProgress<OperationProgress>
    {
        private readonly Task _operation;
        private readonly Action _cancel;
        private string _phase;
        private bool _cancellationRequested;
        private bool _canClose;

        internal SanitizedExportProgressWindow(Task operation, Action cancel)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
            InitializeComponent();
            Title = Localization.GetLocalizedString("EXPORT_TITLE");
            StatusText.Text = Localization.GetLocalizedString("SANITIZED_EXPORT_SCANNING");
            DetailsText.Text = Localization.GetLocalizedString("SANITIZED_EXPORT_PROGRESS_DETAILS");
            CancelExport.Content = Localization.GetLocalizedString("CANCEL");
        }

        // Shows the export's current phase, also one reported before the window appeared. Only a change of phase reaches
        // the dispatcher, and once Cancel is asked for the window keeps saying so.
        void IProgress<OperationProgress>.Report(OperationProgress value)
        {
            string key = value?.Phase switch
            {
                ExportCaptureSessionsOperation.SelectPhase => "EXPORT_PHASE_SELECT",
                ExportCaptureSessionsOperation.ReadPhase => "EXPORT_PHASE_READ",
                ExportCaptureSessionsOperation.WritePhase => "EXPORT_PHASE_WRITE",
                ExportCaptureSessionsOperation.PublishPhase => "EXPORT_PHASE_PUBLISH",
                _ => null,
            };
            if (key != null && Interlocked.Exchange(ref _phase, key) != key)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!_cancellationRequested)
                    {
                        StatusText.Text = Localization.GetLocalizedString(key);
                    }
                });
            }
        }

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            try
            {
                await _operation;
            }
            catch
            {
                // The command handler awaits the same task and reports errors.
            }
            finally
            {
                _canClose = true;
                Close();
            }
        }

        private void RequestCancellation()
        {
            if (_cancellationRequested || _operation.IsCompleted)
            {
                return;
            }
            _cancellationRequested = true;
            CancelExport.IsEnabled = false;
            StatusText.Text = Localization.GetLocalizedString("SANITIZED_EXPORT_CANCELLING");
            _cancel();
        }

        private void CancelExport_Click(object sender, RoutedEventArgs e) => RequestCancellation();

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_canClose && !_operation.IsCompleted)
            {
                e.Cancel = true;
                RequestCancellation();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RequestCancellation();
                e.Handled = true;
            }
        }
    }
}
