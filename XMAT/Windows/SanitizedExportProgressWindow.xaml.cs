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
    // The progress window of both exports (CaptureExportWorkflow): its status, the copy kind's pre-copy notice, the count
    // of rows processed, and Cancel, which Escape and closing the window also press. It stays until the operation ends,
    // and Cancel is off once the publication has started.
    public partial class SanitizedExportProgressWindow : Window
    {
        private readonly Task _operation;
        private readonly Action _cancel;
        private bool _cancellationRequested;
        private bool _cancellationEnded;
        private bool _canClose;

        // title, status, details and notice are shown as given; notice may be null.
        internal SanitizedExportProgressWindow(
            Task operation,
            Action cancel,
            string title,
            string status,
            string details,
            string notice = null)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
            InitializeComponent();
            Title = title;
            StatusText.Text = status;
            DetailsText.Text = details;
            if (!string.IsNullOrEmpty(notice))
            {
                NoticeText.Text = notice;
                NoticeText.Visibility = Visibility.Visible;
            }
            CancelExport.Content = Localization.GetLocalizedString("CANCEL");
        }

        // The count line: rows processed, rows left out included, of the rows planned.
        internal void ShowCount(int processed, int total)
        {
            CountText.Text = Localization.GetLocalizedString("EXPORT_PROGRESS_COUNT", processed, total);
            CountText.Visibility = Visibility.Visible;
        }

        // The publication has started, and nothing can cancel it any more.
        internal void EndCancellation()
        {
            _cancellationEnded = true;
            CancelExport.IsEnabled = false;
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
            if (_cancellationRequested || _cancellationEnded || _operation.IsCompleted)
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