// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Edits an isolated request draft and displays its latest replay response separately from capture. Each send is also
    /// recorded in the Replays tab.
    /// </summary>
    public partial class RequestComposerWindow : Window
    {
        private readonly RequestReplayDraft _draft;
        private readonly RequestReplayClient _client;
        private readonly bool _ownsClient;
        private readonly Func<string, string, bool> _confirm;
        private readonly ReplayRecordingTarget _recording;
        private readonly ReplayProvenance _provenance;
        private CancellationTokenSource _sendingCancellation;
        private bool _closed;
        private string _bodyTextInEditor;

        // The draft is built before the base Window constructor registers this window with the application: a capture
        // that cannot be replayed must not leave a hidden window behind that keeps XMAT running after its last close.
        // recording is where sends are recorded, as edited replays of the capture in the tab named sourceName; without it
        // nothing is recorded, which only tests do.
        internal RequestComposerWindow(
            ProxyConnectionModel capture,
            RequestReplayClient client = null,
            Func<string, string, bool> confirm = null,
            ReplayRecordingTarget recording = null,
            string sourceName = null)
            : this(
                RequestReplayDraft.FromCapture(capture),
                client,
                confirm,
                recording,
                ReplayProvenance.Of(ReplaySourceKind.Edited, sourceName, capture))
        {
        }

        // Compose a new request: an empty GET with nothing captured behind it.
        internal static RequestComposerWindow ForNewRequest(
            RequestReplayClient client = null,
            Func<string, string, bool> confirm = null,
            ReplayRecordingTarget recording = null) =>
            new(new RequestReplayDraft(), client, confirm, recording, ReplayProvenance.Composed);

        private RequestComposerWindow(
            RequestReplayDraft draft,
            RequestReplayClient client,
            Func<string, string, bool> confirm,
            ReplayRecordingTarget recording,
            ReplayProvenance provenance)
        {
            InitializeComponent();
            _draft = draft;
            _recording = recording;
            _provenance = provenance;
            _ownsClient = client == null;
            _client = client ?? new RequestReplayClient();
            _confirm = confirm ?? ((method, url) =>
                MessageBox.Show(this, Localization.GetLocalizedString("COMPOSER_CONFIRM_MESSAGE", method, url),
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

            Title = Localization.GetLocalizedString("COMPOSER_TITLE");
            ReplayNotice.Text = Localization.GetLocalizedString("COMPOSER_NOTICE");
            MethodLabel.Content = Localization.GetLocalizedString("COMPOSER_METHOD");
            UrlLabel.Content = Localization.GetLocalizedString("COMPOSER_URL");
            HeadersLabel.Content = Localization.GetLocalizedString("COMPOSER_HEADERS");
            BodyLabel.Content = Localization.GetLocalizedString("COMPOSER_BODY");
            BodyBox.ToolTip = Localization.GetLocalizedString("COMPOSER_BODY_HINT");
            Base64Box.Content = Localization.GetLocalizedString("COMPOSER_BASE64");
            ResponseView.Title = Localization.GetLocalizedString("COMPOSER_RESPONSE");
            CopyCurlButton.Content = Localization.GetLocalizedString("COMPOSER_COPY_CURL");
            SendButton.Content = Localization.GetLocalizedString("COMPOSER_SEND");
            CancelSendButton.Content = Localization.GetLocalizedString("COMPOSER_CANCEL_SEND");
            CloseButton.Content = Localization.GetLocalizedString("COMPOSER_CLOSE");
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_READY");

            MethodBox.Text = _draft.Method;
            UrlBox.Text = _draft.Url;
            HeadersBox.Text = _draft.HeadersText;
            BodyBox.Text = _draft.BodyText;
            _bodyTextInEditor = BodyBox.Text;
            Base64Box.IsChecked = _draft.BodyIsBase64;
        }

        internal RequestReplayResult LastResult { get; private set; }

        internal async Task SendAsync()
        {
            if (_sendingCancellation != null || _closed)
                return;

            UpdateDraft();
            try
            {
                // Reject invalid input before asking permission or entering the sending state.
                using var validated = _draft.CreateRequestToSend();
                if (_draft.RequiresConfirmation && !_confirm(_draft.Method, _draft.Url))
                    return;
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                ShowError(exception);
                return;
            }

            using var cancellation = new CancellationTokenSource();
            // The recording keeps the Replays tab from being closed or replaced until the send ends; closing XMAT cancels
            // the send through cancellation and waits for it. While XMAT is busy nothing is sent, so nothing goes unrecorded.
            using ReplayRecording recording = _recording?.TryBegin(cancellation, _ => _provenance);
            if (_recording != null && recording == null)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_BUSY");
                return;
            }

            _sendingCancellation = cancellation;
            // A failed new send must not leave the previous response looking like its result.
            LastResult = null;
            ResponseView.FirstLineAndHeaders = "";
            ResponseView.HeaderDictionary = null;
            ResponseView.BodyBytes = null;
            ResponseView.BodyText = null;
            SetSending(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SENDING");
            try
            {
                var result = await _client.SendAsync(_draft, confirmed: true, cancellation.Token, recording?.Recorder);
                // Closing cancels the request, but completion may race with that cancellation.
                if (!_closed)
                {
                    var response = result.Response;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in response.Headers.Concat(response.ContentHeaders))
                        headers[header.Key] = string.Join(", ", header.Value);
                    ResponseView.FirstLineAndHeaders = response.FirstLineAndHeaders;
                    ResponseView.HeaderDictionary = headers;
                    ResponseView.BodyBytes = response.BodyBytes;
                    ResponseView.BodyText = (string)new BodyConverter().Convert(
                        new object[] { headers, response.BodyBytes }, typeof(string), null, CultureInfo.CurrentCulture);
                    LastResult = result;
                    StatusText.Text = WithRecording(
                        Localization.GetLocalizedString("COMPOSER_COMPLETED",
                            response.Status, result.Duration.TotalMilliseconds.ToString("F0"), response.BodyBytes.Length),
                        recording);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                {
                    // A timeout is a send without a response, which the Replays tab records; a cancelled send usually is
                    // not, though it may have reached the server.
                    StatusText.Text = cancellation.IsCancellationRequested
                        ? CancelledText(recording)
                        : WithRecording(Localization.GetLocalizedString("COMPOSER_TIMEOUT"), recording);
                }
            }
            catch (Exception exception) when (IsInputError(exception) || exception is HttpRequestException or IOException)
            {
                if (!_closed)
                    StatusText.Text = WithRecording(ErrorText(exception), recording);
            }
            finally
            {
                _sendingCancellation = null;
                if (!_closed)
                    SetSending(false);
            }
        }

        // The status followed by what the Replays tab did with the send: recorded, recorded without bodies that did not
        // fit, or not recorded because the tab was closed.
        private static string WithRecording(string status, ReplayRecording recording)
        {
            ReplayRecorder recorder = recording?.Recorder;
            string note = recorder switch
            {
                null => null,
                { NotRecorded: > 0 } => Localization.GetLocalizedString("COMPOSER_NOT_RECORDED", recorder.Destination),
                { OmittedBodies: > 0 } => Localization.GetLocalizedString(
                    "COMPOSER_RECORDED_WITHOUT_BODIES", recorder.Destination, recorder.BodyBudgetBytes / (1024 * 1024)),
                { Recorded: > 0 } => Localization.GetLocalizedString("COMPOSER_RECORDED", recorder.Destination),
                _ => null,
            };
            return note == null ? status : status + " " + note;
        }

        // A cancelled send that the Replays tab has no row for: it may still have reached the server.
        private static string CancelledText(ReplayRecording recording) =>
            recording?.Recorder is { Recorded: 0, NotRecorded: 0 } recorder
                ? Localization.GetLocalizedString("COMPOSER_CANCELLED_NOT_RECORDED", recorder.Destination)
                : WithRecording(Localization.GetLocalizedString("COMPOSER_CANCELLED"), recording);

        internal void CancelSend()
        {
            _sendingCancellation?.Cancel();
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            CancelSend();
            if (_ownsClient)
                _client.Dispose();
            base.OnClosed(e);
        }

        private void UpdateDraft()
        {
            _draft.Method = MethodBox.Text;
            _draft.Url = UrlBox.Text;
            _draft.HeadersText = HeadersBox.Text;
            // Preserve the original bytes unless edited: TextBox can normalize captured line endings.
            if (BodyBox.Text != _bodyTextInEditor)
            {
                _draft.BodyText = BodyBox.Text;
                _bodyTextInEditor = BodyBox.Text;
            }
        }

        private void SetSending(bool sending)
        {
            RequestEditor.IsEnabled = !sending;
            SendButton.IsEnabled = !sending;
            CopyCurlButton.IsEnabled = !sending;
            CancelSendButton.IsEnabled = sending;
        }

        private void BodyFormat_Changed(object sender, RoutedEventArgs e)
        {
            if (_draft == null)
                return;
            UpdateDraft();
            try
            {
                _draft.SetBodyFormat(Base64Box.IsChecked == true);
                BodyBox.Text = _draft.BodyText;
                _bodyTextInEditor = BodyBox.Text;
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                Base64Box.IsChecked = _draft.BodyIsBase64;
                ShowError(exception);
            }
        }

        private void CopyCurl_Click(object sender, RoutedEventArgs e)
        {
            UpdateDraft();
            try
            {
                Clipboard.SetText(_draft.ToCurlCommand());
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_COPIED");
            }
            catch (Exception exception) when (IsInputError(exception) || exception is ExternalException)
            {
                ShowError(exception);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
        private void CancelSend_Click(object sender, RoutedEventArgs e) => CancelSend();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowError(Exception exception) => StatusText.Text = ErrorText(exception);

        private static string ErrorText(Exception exception) =>
            Localization.GetLocalizedString("COMPOSER_ERROR", exception.Message);

        private static bool IsInputError(Exception exception) =>
            exception is ArgumentException or FormatException or InvalidOperationException or NotSupportedException;
    }
}
