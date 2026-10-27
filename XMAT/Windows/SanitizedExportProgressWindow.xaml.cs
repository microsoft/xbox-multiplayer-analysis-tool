// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace XMAT
{
    public partial class SanitizedExportProgressWindow : Window
    {
        private readonly Task _operation;
        private readonly Action _cancel;
        private bool _cancellationRequested;
        private bool _canClose;

        internal SanitizedExportProgressWindow(Task operation, Action cancel)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
            InitializeComponent();
            Title = Localization.GetLocalizedString("FILE_EXPORT_SANITIZED");
            StatusText.Text = Localization.GetLocalizedString("SANITIZED_EXPORT_SCANNING");
            DetailsText.Text = Localization.GetLocalizedString("SANITIZED_EXPORT_PROGRESS_DETAILS");
            CancelExport.Content = Localization.GetLocalizedString("CANCEL");
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
            if (!_canClose)
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
