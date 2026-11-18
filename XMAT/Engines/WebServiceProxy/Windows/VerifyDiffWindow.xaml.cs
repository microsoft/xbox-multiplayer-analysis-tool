// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // View verification differences (WebServiceProxyCommands.ViewVerifyDiff): the verdict and the verification differences
    // that the Replays tab keeps with one row (ProxyConnectionModel.Verification), as Reissue and verify recorded them for
    // that row's send. It shows the record of the very row object that it was opened for, never one found again by its
    // tab's name or its number, and it sends and compares nothing. Its text is prepared off the UI thread
    // (VerifyDiffPresentation) and shown only while the window still shows that record. Once the window closes, or the
    // row leaves its tab (the tab is cleared or closed, the row is removed or replaced, or the tab's rows are replaced),
    // the window lets go of the record, which holds captured content, of the row and of the tab's rows and controller,
    // stops preparing the text and clears what it showed, and says why.
    public partial class VerifyDiffWindow : Window
    {
        private readonly string _tabName;
        private readonly long _number;

        // Cancelled once the window lets go of its record (Forget): the text being prepared for it is no longer needed.
        private readonly CancellationTokenSource _formatting = new();
        private WebServiceDeviceCaptureController _controller;
        private ProxyConnectionsCollection _rows;
        private ProxyConnectionModel _row;
        private RecordedVerification _shown;

        // row is a row of controller's tab with a recorded verification; format prepares the text off the UI thread, and
        // stops once its token is cancelled; tests replace it to choose when it completes.
        internal VerifyDiffWindow(
            ProxyConnectionModel row,
            WebServiceDeviceCaptureController controller,
            Func<RecordedVerification, CancellationToken, VerifyDiffPresentation> format = null)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(controller);
            _shown = row.Verification ??
                throw new ArgumentException("The row was not recorded from a verified send.", nameof(row));
            _row = row;
            _controller = controller;
            _rows = controller.ProxyConnections;
            _tabName = controller.DeviceName;
            _number = row.RequestNumber;

            InitializeComponent();
            Title = Localization.GetLocalizedString("VERIFY_DIFF_TITLE", _tabName, _number);
            IdentityText.Text = Localization.GetLocalizedString("VERIFY_DIFF_IDENTITY", _number, _tabName, row.ReplaySource);
            BodyTab.Header = Localization.GetLocalizedString("VERIFY_DIFF_BODY_TAB");
            JsonTab.Header = Localization.GetLocalizedString("VERIFY_DIFF_JSON_TAB");
            // Each box of differences has its own name for automation, which begins as its tab's header reads: a TextBox
            // takes no name from the header of the tab that holds it, nor from its text, which is empty while the
            // differences are being prepared.
            AutomationProperties.SetName(BodyDiffText, Localization.GetLocalizedString("VERIFY_DIFF_BODY_FIELD"));
            AutomationProperties.SetName(JsonDiffText, Localization.GetLocalizedString("VERIFY_DIFF_JSON_FIELD"));
            CloseButton.Content = Localization.GetLocalizedString("VERIFY_DIFF_CLOSE");
            ShowMessage(Localization.GetLocalizedString("VERIFY_DIFF_PENDING"));

            _rows.CollectionChanged += Rows_CollectionChanged;
            _controller.PropertyChanged += Controller_PropertyChanged;
            Presented = PresentAsync(_shown, format ?? VerifyDiffPresentation.Of, _formatting.Token);
        }

        // The record that the window shows: the row's own, until the window closes or the row leaves its tab, then null.
        internal RecordedVerification Shown => _shown;

        // Completes once the prepared text was shown, or dropped because the window no longer shows its record.
        internal Task Presented { get; }

        protected override void OnClosed(EventArgs e)
        {
            Forget();
            base.OnClosed(e);
        }

        private async Task PresentAsync(
            RecordedVerification recorded,
            Func<RecordedVerification, CancellationToken, VerifyDiffPresentation> format,
            CancellationToken cancellationToken)
        {
            VerifyDiffPresentation presentation;
            try
            {
                presentation = await Task.Run(() => format(recorded, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The window let go of the record (Forget): nothing is shown, and nothing went wrong.
                return;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // The type only: the record holds captured content, which is never logged.
                PublicUtilities.AppLog(
                    LogLevel.ERROR, $"Verification differences not shown: {exception.GetType().FullName}");
                presentation = null;
            }

            try
            {
                await Dispatcher.InvokeAsync(() => Publish(recorded, presentation));
            }
            catch (OperationCanceledException)
            {
                // XMAT is exiting: its dispatcher runs nothing more, and the window goes with it.
            }
        }

        // On the UI thread: the text is shown only while the window still shows the record that it was prepared from. Text
        // that could not be prepared leaves the verdict, worded from its codes alone, and says so.
        private void Publish(RecordedVerification recorded, VerifyDiffPresentation presentation)
        {
            if (!ReferenceEquals(_shown, recorded))
            {
                return;
            }

            if (presentation == null)
            {
                SummaryText.Text = VerifyDiffPresentation.VerdictOf(recorded.Verdict);
                ShowMessage(Localization.GetLocalizedString("VERIFY_DIFF_FAILED"));
                return;
            }

            SummaryText.Text = presentation.Summary;
            DetailText.Text = presentation.Detail;
            BodyDiffText.Text = presentation.Body;
            JsonDiffText.Text = presentation.Json;
            ShowMessage(presentation.Message);
        }

        private void Rows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move))
            {
                CheckRow();
            }
        }

        private void Controller_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(WebServiceDeviceCaptureController.ProxyConnections))
            {
                CheckRow();
            }
        }

        // After a change to the tab's rows, which the UI thread checks: the window lets go of a row that left the tab.
        private void CheckRow()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(CheckRow));
                return;
            }

            if (_shown != null &&
                !(ReferenceEquals(_controller.ProxyConnections, _rows) && _rows.Any(row => ReferenceEquals(row, _row))))
            {
                Forget();
                ShowMessage(Localization.GetLocalizedString("VERIFY_DIFF_STALE", _number, _tabName));
            }
        }

        // Lets go of the row and its record, which holds captured content, and of the tab's rows and controller once it
        // stops listening to them; stops preparing the text; and clears the text that showed the record.
        private void Forget()
        {
            if (_rows != null)
            {
                _rows.CollectionChanged -= Rows_CollectionChanged;
            }

            if (_controller != null)
            {
                _controller.PropertyChanged -= Controller_PropertyChanged;
            }

            _rows = null;
            _controller = null;
            _shown = null;
            _row = null;
            _formatting.Cancel();
            SummaryText.Text = string.Empty;
            DetailText.Text = string.Empty;
            BodyDiffText.Text = string.Empty;
            JsonDiffText.Text = string.Empty;
            ShowMessage(null);
        }

        private void ShowMessage(string message)
        {
            MessageText.Text = message ?? string.Empty;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
