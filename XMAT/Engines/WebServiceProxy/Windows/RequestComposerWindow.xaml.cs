// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Edits an isolated request draft and sends it, or one request per number when its URL has a '#', and displays the
    /// latest response separately from capture. Every send is also recorded in the Replays tab.
    /// </summary>
    public partial class RequestComposerWindow : Window
    {
        private readonly RequestReplayClient _client;
        private readonly bool _ownsClient;
        private readonly Func<string, bool> _confirm;
        private readonly ReplayRecordingTarget _recording;
        private RequestReplayDraft _draft;
        private ReplayProvenance _provenance;
        private ComposerState _state;
        private CancellationTokenSource _sendingCancellation;
        private CancellationTokenSource _readingCancellation;
        private bool _closed;
        private bool _updatingEditors;
        private string _bodyTextInEditor;

        // The cancellation of the run that the user stopped with Cancel request, as opposed to a run that XMAT stopped, as
        // when it closes. It is compared with each run's own cancellation, so an earlier run's cancel never counts.
        private CancellationTokenSource _cancelledByUser;

        // An uploaded body, the exact bytes read, and the name of its file; null while the editor holds the body.
        private byte[] _fileBody;
        private string _fileName;

        // The editors as the request was loaded, to tell whether the user has edited it since.
        private EditorState _loaded;

        // What the window is doing; it starts nothing else meanwhile. Preparing covers checking a send and asking the user
        // about it, or asking which file to upload or whether to replace an edited request.
        private enum ComposerState
        {
            Idle,
            Preparing,
            Sending,
            ReadingFile,
        }

        // The draft is built before the base Window constructor registers this window with the application: a capture
        // that cannot be replayed must not leave a hidden window behind that keeps XMAT running after its last close.
        // recording is where sends are recorded, as edited replays of the capture in the tab named sourceName; without it
        // nothing is recorded, which only tests do. confirm asks the user a question with the message, Yes or No.
        internal RequestComposerWindow(
            ProxyConnectionModel capture,
            RequestReplayClient client = null,
            Func<string, bool> confirm = null,
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
            Func<string, bool> confirm = null,
            ReplayRecordingTarget recording = null) =>
            new(new RequestReplayDraft(), client, confirm, recording, ReplayProvenance.Composed);

        private RequestComposerWindow(
            RequestReplayDraft draft,
            RequestReplayClient client,
            Func<string, bool> confirm,
            ReplayRecordingTarget recording,
            ReplayProvenance provenance)
        {
            InitializeComponent();
            _recording = recording;
            _ownsClient = client == null;
            _client = client ?? new RequestReplayClient();
            _confirm = confirm ?? (message =>
                MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) ==
                    MessageBoxResult.Yes);

            Title = Localization.GetLocalizedString("COMPOSER_TITLE");
            ReplayNotice.Text = Localization.GetLocalizedString("COMPOSER_NOTICE");
            MethodLabel.Content = Localization.GetLocalizedString("COMPOSER_METHOD");
            UrlLabel.Content = Localization.GetLocalizedString("COMPOSER_URL");
            UrlBox.ToolTip = Localization.GetLocalizedString("COMPOSER_URL_HINT");
            HeadersLabel.Content = Localization.GetLocalizedString("COMPOSER_HEADERS");
            BodyLabel.Content = Localization.GetLocalizedString("COMPOSER_BODY");
            BodyBox.ToolTip = Localization.GetLocalizedString("COMPOSER_BODY_HINT");
            Base64Box.Content = Localization.GetLocalizedString("COMPOSER_BASE64");
            UploadFileButton.Content = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILE");
            UploadFileButton.ToolTip = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILE_HINT");
            ClearBodyButton.Content = Localization.GetLocalizedString("COMPOSER_CLEAR_BODY");
            FollowRedirectsBox.Content = Localization.GetLocalizedString("COMPOSER_FOLLOW_REDIRECTS");
            FollowRedirectsBox.ToolTip = Localization.GetLocalizedString("COMPOSER_FOLLOW_REDIRECTS_HINT");
            RedirectLimitBox.ToolTip = Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT");
            AutomationProperties.SetName(RedirectLimitBox, Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT"));
            InspectSessionBox.Content = Localization.GetLocalizedString("COMPOSER_INSPECT_SESSION");
            InspectSessionBox.ToolTip = Localization.GetLocalizedString("COMPOSER_INSPECT_SESSION_HINT");
            HttpVersionLabel.Content = Localization.GetLocalizedString("COMPOSER_HTTP_VERSION");
            HttpVersionBox.ToolTip = Localization.GetLocalizedString("COMPOSER_HTTP_VERSION_HINT");
            ConnectToBox.Content = Localization.GetLocalizedString("COMPOSER_CONNECT_TO");
            ConnectToBox.ToolTip = Localization.GetLocalizedString("COMPOSER_CONNECT_TO_HINT");
            ConnectToText.ToolTip = Localization.GetLocalizedString("COMPOSER_CONNECT_TO_HINT");
            AutomationProperties.SetName(ConnectToText, Localization.GetLocalizedString("COMPOSER_CONNECT_TO_ENDPOINT"));
            ResponseView.Title = Localization.GetLocalizedString("COMPOSER_RESPONSE");
            CopyCurlButton.Content = Localization.GetLocalizedString("COMPOSER_COPY_CURL");
            SendButton.Content = Localization.GetLocalizedString("COMPOSER_SEND");
            CancelSendButton.Content = Localization.GetLocalizedString("COMPOSER_CANCEL_SEND");
            CloseButton.Content = Localization.GetLocalizedString("COMPOSER_CLOSE");
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_READY");

            // Follow same-origin redirects is off; when selected, it follows at most 10 by default.
            for (int limit = 1; limit <= ReplayOptions.MaxRedirectLimit; limit++)
                RedirectLimitBox.Items.Add(limit);
            RedirectLimitBox.SelectedItem = ReplayOptions.MaxRedirectLimit;

            // The HTTP version is chosen automatically, as before, unless the user asks for exactly one; Connect to another
            // endpoint is off.
            HttpVersionBox.Items.Add(VersionItem(ReplayHttpVersion.Auto, "COMPOSER_HTTP_VERSION_AUTO"));
            HttpVersionBox.Items.Add(VersionItem(ReplayHttpVersion.Http11, "COMPOSER_HTTP_VERSION_11"));
            HttpVersionBox.Items.Add(VersionItem(ReplayHttpVersion.Http2, "COMPOSER_HTTP_VERSION_2"));
            HttpVersionBox.SelectedIndex = 0;

            LoadDraft(draft, provenance);
        }

        internal RequestReplayResult LastResult { get; private set; }

        internal async Task SendAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            // Everything that the run sends is fixed here, before the user is asked anything: the request, as a copy that
            // later edits do not change, the options, the transport and where the request comes from.
            UpdateDraft();
            int maxRedirects = MaxRedirects;
            bool inspect = InspectSessionBox.IsChecked == true;
            ReplayProvenance provenance = _provenance;
            if (!TryReadTransport(out ReplayTransportOptions transport))
            {
                StatusText.Text = ErrorText(Localization.GetLocalizedString("COMPOSER_CONNECT_TO_INVALID"));
                return;
            }

            ReplayPlanPreview plan;
            bool confirmed;
            _state = ComposerState.Preparing;
            try
            {
                plan = Prepare(maxRedirects, transport, out confirmed);
            }
            finally
            {
                EndState();
            }

            if (plan == null || _closed)
                return;

            using var cancellation = new CancellationTokenSource();
            // The recording keeps the Replays tab from being closed or replaced until the run ends; closing XMAT cancels
            // the run through cancellation and waits for it. While XMAT is busy or exiting nothing is sent, so nothing goes
            // unrecorded, and the permission that the user gave is not kept for another try.
            using ReplayRecording recording = _recording?.TryBegin(cancellation, _ => provenance);
            if (_recording != null && recording == null)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_BUSY");
                return;
            }

            _sendingCancellation = cancellation;
            _state = ComposerState.Sending;
            // A new run must not leave the previous response looking like its result.
            ClearResponse();
            SetBusy(true);
            StatusText.Text = plan.Sequence == null
                ? Localization.GetLocalizedString("COMPOSER_SENDING")
                : Localization.GetLocalizedString("COMPOSER_SENDING_SEQUENCE", plan.Sends, 0);
            IProgress<int> progress = plan.Sequence == null
                ? null
                : new Progress<int>(done =>
                {
                    if (ReferenceEquals(_sendingCancellation, cancellation))
                        StatusText.Text = Localization.GetLocalizedString("COMPOSER_SENDING_SEQUENCE", plan.Sends, done);
                });
            RequestReplayBatchResult sent = null;
            try
            {
                sent = await _client.SendPlanAsync(
                    plan, maxRedirects, confirmed, cancellation.Token, recording?.Recorder, progress);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // The Replays tab could not take a send, which ended the run.
                PublicUtilities.AppLog(LogLevel.ERROR, $"Composer send stopped: {exception.GetType().FullName}");
            }
            finally
            {
                _sendingCancellation = null;
                EndState();
            }

            // Closing cancels the run, but it may have ended first: a closed window shows and selects nothing.
            if (_closed)
                return;

            bool cancelled = sent?.WasCancelled ?? cancellation.IsCancellationRequested;
            ShowOutcome(plan, sent, recording, maxRedirects, cancelled);
            // Inspect session selects the last row that this run recorded, whatever the run's outcome: also when it failed
            // or the user cancelled it after some of its requests were recorded. A run that XMAT stopped, because it is
            // closing or exiting, or because the run's tab closed, selects nothing, also when the user had cancelled it
            // first (TrySelectLastRecorded refuses a revoked run); so does a run that recorded no row.
            if (inspect && recording != null && (!cancelled || ReferenceEquals(_cancelledByUser, cancellation)))
                _recording.TrySelectLastRecorded(recording);
        }

        // Test seams: the range of numbers for a URL with '#', as typed ("START-END"), or null to send nothing; and the file
        // to upload, or null for none. By default the user chooses them in dialogs.
        internal Func<string> ChooseSequence { get; set; }

        internal Func<string> ChooseBodyFile { get; set; }

        // The client that sends. The window disposes the client that it created once it is closed and no run uses it.
        internal RequestReplayClient Client => _client;

        // The request to send, checked as sending it would check it and frozen with its transport, and whether the user gave
        // the permission that it needs; null when nothing is to be sent: the input is invalid, the user cancelled or
        // declined, or the window closed meanwhile. A '#' in the URL asks for a range and sends one request per number.
        private ReplayPlanPreview Prepare(int maxRedirects, ReplayTransportOptions transport, out bool confirmed)
        {
            confirmed = false;
            try
            {
                ReplaySequence sequence = null;
                if (_draft.Url.Contains(ReplaySequence.Placeholder))
                {
                    if (!ReplaySequence.IsPathAndQueryTemplate(_draft.Url))
                    {
                        StatusText.Text = Localization.GetLocalizedString("COMPOSER_SEQUENCE_INVALID");
                        return null;
                    }

                    string range = (ChooseSequence ?? (() => SequenceRangeWindow.Ask(this)))();
                    if (range == null || _closed)
                        return null;
                    if (!ReplaySequence.TryParse(range, out sequence))
                    {
                        StatusText.Text = Localization.GetLocalizedString("COMPOSER_SEQUENCE_RANGE_INVALID");
                        return null;
                    }
                }
                else
                {
                    // Invalid input is reported, with what is wrong, before the user is asked anything.
                    using var validated = _draft.CreateRequestToSend();
                }

                // The size of the run is checked before any number is put in the URL, and the transport with the request.
                OperationResult<ReplayPlanPreview> preview = _client.Preview(
                    sequence == null ? ReplaySource.FromDraft(_draft) : ReplaySource.FromTemplate(_draft, sequence),
                    maxRedirects,
                    transport);
                if (!preview.Succeeded)
                {
                    StatusText.Text = ErrorText(PlanError(
                        preview.Diagnostics.Count > 0 ? preview.Diagnostics[0] : null, _draft, sequence));
                    return null;
                }

                // One question for the whole run, about what was checked: what is typed or chosen afterwards changes
                // nothing in it.
                ReplayPlanPreview plan = preview.Value;
                if (plan.UsesUnsafeMethod || plan.ChangesOrigin || plan.UsesConnectTo)
                {
                    if (!_confirm(ConfirmationMessage(plan, maxRedirects)) || _closed)
                        return null;
                    confirmed = true;
                }

                return plan;
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                ShowError(exception);
                return null;
            }
        }

        // Why the operation refused a run: what the composer says itself about Fiddler's control headers and the transport
        // options; for a sequence whose request cannot be built, what building it with the widest number says, as a single
        // send says it; otherwise the operation's message, such as a run that is too large.
        private static string PlanError(OperationDiagnostic diagnostic, RequestReplayDraft draft, ReplaySequence sequence)
        {
            string explained = diagnostic?.Code switch
            {
                ReplayOperation.ControlHeaderCode => "COMPOSER_CONTROL_HEADER",
                ReplayOperation.Http2HttpsCode => "COMPOSER_HTTP_VERSION_HTTPS",
                ReplayOperation.ConnectToHttpsCode => "COMPOSER_CONNECT_TO_HTTPS",
                ReplayOperation.ConnectToDeniedCode => "COMPOSER_CONNECT_TO_DENIED",
                _ => null,
            };
            if (explained != null)
            {
                return Localization.GetLocalizedString(explained);
            }

            if (diagnostic?.Code == ReplayOperation.RequestInvalidCode && sequence != null)
            {
                try
                {
                    using var widest = draft.WithUrl(sequence.Expand(draft.Url, sequence.End)).CreateRequestToSend();
                }
                catch (Exception exception) when (IsInputError(exception))
                {
                    return exception.Message;
                }
            }

            return diagnostic?.Message ?? Localization.GetLocalizedString("COMPOSER_FAILED");
        }

        // The one confirmation of a run: how many requests, with which method and to where; that they may change server
        // data or send captured credentials to another origin; that they go to another endpoint than the address of the
        // URL's host, which it names with the URL's origin; and that redirects, when followed, are more requests.
        internal static string ConfirmationMessage(ReplayPlanPreview plan, int maxRedirects)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ReplaySequence sequence = plan.Sequence;
            bool risky = plan.UsesUnsafeMethod || plan.ChangesOrigin;
            string message = sequence == null
                ? Localization.GetLocalizedString(
                    risky ? "COMPOSER_CONFIRM_MESSAGE" : "COMPOSER_CONFIRM_SEND", plan.Template.Method, plan.Template.Url)
                : Localization.GetLocalizedString(
                    risky ? "COMPOSER_CONFIRM_SEQUENCE" : "COMPOSER_CONFIRM_SEND_SEQUENCE",
                    plan.Sends,
                    plan.Template.Method,
                    sequence.Format(sequence.Start),
                    sequence.Format(sequence.End),
                    plan.Template.Url);
            if (plan.UsesConnectTo)
            {
                message += "\n\n" + Localization.GetLocalizedString(
                    "COMPOSER_CONFIRM_CONNECT_TO", plan.Transport.ConnectTo.ToString(), Origin(plan.Template.Url));
            }

            return maxRedirects == 0
                ? message
                : message + "\n\n" + Localization.GetLocalizedString("COMPOSER_CONFIRM_REDIRECTS", maxRedirects);
        }

        // A URL's scheme, host and port, as the confirmation names the origin of a request that goes to another endpoint.
        private static string Origin(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.GetLeftPart(UriPartial.Authority) : url;

        // Says how the run went. One request shows its response and status, or why it has none; a sequence counts its
        // requests and shows the last response. Then what the Replays tab did with the sends.
        private void ShowOutcome(
            ReplayPlanPreview plan, RequestReplayBatchResult sent, ReplayRecording recording, int maxRedirects, bool cancelled)
        {
            if (sent == null)
            {
                StatusText.Text = WithRecording(ErrorText(Localization.GetLocalizedString("COMPOSER_FAILED")), recording);
                return;
            }

            if (cancelled)
            {
                // A cancelled send usually has no row in the Replays tab, though it may have reached the server.
                StatusText.Text = plan.Sequence == null
                    ? CancelledText(recording)
                    : WithRecording(
                        Localization.GetLocalizedString("COMPOSER_SEQUENCE_CANCELLED", sent.Sends, plan.Sends), recording);
                return;
            }

            if (!sent.Result.Succeeded)
            {
                IReadOnlyList<OperationDiagnostic> diagnostics = sent.Result.Diagnostics;
                StatusText.Text = WithRecording(
                    ErrorText(PlanError(diagnostics.Count > 0 ? diagnostics[0] : null, plan.Template, plan.Sequence)),
                    recording);
                return;
            }

            ReplayExchange last = sent.LastExchange;
            if (last?.Outcome == ReplayOutcome.Completed)
                ShowResponse(RequestReplayClient.ToResult(last));

            var status = new List<string>();
            if (plan.Sequence == null)
            {
                status.Add(SendStatus(last));
            }
            else
            {
                ReplayResult result = sent.Result.Value;
                status.Add(Localization.GetLocalizedString(
                    "COMPOSER_SEQUENCE_COMPLETED", result.Items.Count, result.Completed, result.Failed));
                int notFollowed = result.Items.Count(item => RedirectStopText(item.Code, maxRedirects) != null);
                if (notFollowed > 0)
                    status.Add(Localization.GetLocalizedString("COMPOSER_SEQUENCE_REDIRECTS_STOPPED", notFollowed));
                if (last?.Outcome == ReplayOutcome.Completed)
                    status.Add(Localization.GetLocalizedString("COMPOSER_SEQUENCE_LAST_SHOWN", last.StatusCode));
            }

            // Redirects that the last request followed, or why it did not follow one, explain the response shown.
            if (last?.Outcome == ReplayOutcome.Completed && last.Redirects > 0)
                status.Add(Localization.GetLocalizedString("COMPOSER_REDIRECTS_FOLLOWED", last.Redirects));
            if (last?.Outcome == ReplayOutcome.Completed && RedirectStopText(last.Code, maxRedirects) is string stop)
                status.Add(stop);
            StatusText.Text = WithRecording(string.Join(" ", status), recording);
        }

        // The status of one send: its response, or why it got none.
        private static string SendStatus(ReplayExchange exchange) => exchange?.Outcome switch
        {
            ReplayOutcome.Completed => Localization.GetLocalizedString(
                "COMPOSER_COMPLETED",
                exchange.StatusCode,
                exchange.Duration.TotalMilliseconds.ToString("F0"),
                exchange.Response.Body.Length),
            ReplayOutcome.TimedOut => Localization.GetLocalizedString("COMPOSER_TIMEOUT"),
            ReplayOutcome.TooLarge => ErrorText(Localization.GetLocalizedString("COMPOSER_TOO_LARGE")),
            _ => ErrorText(exchange?.Error?.Message ?? Localization.GetLocalizedString("COMPOSER_FAILED")),
        };

        // Why a redirect response is the result, when following redirects stopped at it; null otherwise.
        private static string RedirectStopText(string code, int maxRedirects) => code switch
        {
            ReplaySender.RedirectOriginChangeCode => Localization.GetLocalizedString("COMPOSER_REDIRECT_ORIGIN"),
            ReplaySender.RedirectInsecureCode => Localization.GetLocalizedString("COMPOSER_REDIRECT_INSECURE"),
            ReplaySender.RedirectUnsupportedCode => Localization.GetLocalizedString("COMPOSER_REDIRECT_UNSUPPORTED"),
            ReplaySender.RedirectLimitCode => Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT_REACHED", maxRedirects),
            _ => null,
        };

        private void ShowResponse(RequestReplayResult result)
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
        }

        private void ClearResponse()
        {
            LastResult = null;
            ResponseView.FirstLineAndHeaders = "";
            ResponseView.HeaderDictionary = null;
            ResponseView.BodyBytes = null;
            ResponseView.BodyText = null;
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

        // Cancel request: the user stops the run in progress before its next request, and the file being read.
        internal void CancelSend()
        {
            _cancelledByUser = _sendingCancellation;
            StopWork();
        }

        private void StopWork()
        {
            _sendingCancellation?.Cancel();
            _readingCancellation?.Cancel();
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            StopWork();
            // A run or read in progress releases the client once it has ended (EndState): it may still be using it.
            ReleaseClient();
            base.OnClosed(e);
        }

        // Back to idle after preparing, sending or reading a file. A window closed meanwhile releases its client now that
        // nothing uses it, and leaves its controls as they are.
        private void EndState()
        {
            _state = ComposerState.Idle;
            if (_closed)
                ReleaseClient();
            else
                SetBusy(false);
        }

        private void ReleaseClient()
        {
            if (_ownsClient && _state == ComposerState.Idle && !_client.IsDisposed)
                _client.Dispose();
        }

        // 0 unless Follow same-origin redirects is selected; then the most redirects chosen, 1 to 10.
        private int MaxRedirects =>
            FollowRedirectsBox.IsChecked != true ? 0 :
            RedirectLimitBox.SelectedItem is int limit ? limit : ReplayOptions.MaxRedirectLimit;

        private void UpdateDraft()
        {
            _draft.Method = MethodBox.Text;
            _draft.Url = UrlBox.Text;
            _draft.HeadersText = HeadersBox.Text;
            // An uploaded body is sent as it was read. An edited body replaces the draft's; an unedited one keeps the
            // original bytes, since TextBox can normalize captured line endings.
            if (_fileBody == null && BodyBox.Text != _bodyTextInEditor)
            {
                _draft.BodyText = BodyBox.Text;
                _bodyTextInEditor = BodyBox.Text;
            }
        }

        // Shows a request in the editors as loaded: the one that the window opened with, or one dropped on it.
        private void LoadDraft(RequestReplayDraft draft, ReplayProvenance provenance)
        {
            _draft = draft;
            _provenance = provenance;
            _fileBody = null;
            _fileName = null;
            MethodBox.Text = draft.Method;
            UrlBox.Text = draft.Url;
            HeadersBox.Text = draft.HeadersText;
            ShowBody();
            _loaded = CurrentEditors();
        }

        // The body editor; or, for an uploaded file, its name and size, since the editor would show the Base64 of a body
        // that can be 64 MiB.
        private void ShowBody()
        {
            bool fromFile = _fileBody != null;
            _updatingEditors = true;
            try
            {
                Base64Box.IsChecked = _draft.BodyIsBase64;
                BodyBox.Text = fromFile ? "" : _draft.BodyText;
            }
            finally
            {
                _updatingEditors = false;
            }

            _bodyTextInEditor = BodyBox.Text;
            BodyBox.Visibility = fromFile ? Visibility.Collapsed : Visibility.Visible;
            BodyFileText.Visibility = fromFile ? Visibility.Visible : Visibility.Collapsed;
            BodyFileText.Text = fromFile
                ? Localization.GetLocalizedString("COMPOSER_BODY_FILE", _fileName, _fileBody.LongLength)
                : null;
            Base64Box.IsEnabled = !fromFile;
        }

        private void SetBusy(bool busy)
        {
            RequestEditor.IsEnabled = !busy;
            SendButton.IsEnabled = !busy;
            CancelSendButton.IsEnabled = busy;
            UpdateCurlAvailability();
        }

        // Copy as cURL copies one request as it is: not a URL with '#', which sends one request per number; not with
        // redirects followed, which curl would follow its own way; and only with the default transport, since the command
        // would not choose the same HTTP version or connect to the same endpoint. The button says why it is unavailable.
        private void UpdateCurlAvailability()
        {
            string unavailable = CurlUnavailableText();
            CopyCurlButton.IsEnabled = _state == ComposerState.Idle && unavailable == null;
            CopyCurlButton.ToolTip = unavailable;
        }

        private string CurlUnavailableText() =>
            UrlBox.Text.Contains(ReplaySequence.Placeholder) || FollowRedirectsBox.IsChecked == true ||
                SelectedHttpVersion != ReplayHttpVersion.Auto || ConnectToBox.IsChecked == true
                ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_UNAVAILABLE")
                : null;

        // Automatic unless HTTP/1.1 only or HTTP/2 only is chosen.
        private ReplayHttpVersion SelectedHttpVersion =>
            HttpVersionBox.SelectedItem is ComboBoxItem { Tag: ReplayHttpVersion version } ? version : ReplayHttpVersion.Auto;

        // The transport that the controls choose now, or false when Connect to another endpoint is on with text that is not
        // an IP address and port. A run sends with the transport that its preview checked, never the controls afterwards.
        private bool TryReadTransport(out ReplayTransportOptions transport)
        {
            ReplayConnectTo target = null;
            if (ConnectToBox.IsChecked == true && !ReplayConnectTo.TryParse(ConnectToText.Text.Trim(), out target))
            {
                transport = null;
                return false;
            }

            transport = new ReplayTransportOptions(SelectedHttpVersion, target);
            return true;
        }

        private static ComboBoxItem VersionItem(ReplayHttpVersion version, string key) =>
            new() { Content = Localization.GetLocalizedString(key), Tag = version };

        private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) =>
            UpdateCurlAvailability();

        private void HttpVersion_Changed(object sender, SelectionChangedEventArgs e) => UpdateCurlAvailability();

        private void ConnectTo_Changed(object sender, RoutedEventArgs e)
        {
            ConnectToText.IsEnabled = ConnectToBox.IsChecked == true;
            UpdateCurlAvailability();
        }

        private void FollowRedirects_Changed(object sender, RoutedEventArgs e)
        {
            RedirectLimitBox.IsEnabled = FollowRedirectsBox.IsChecked == true;
            UpdateCurlAvailability();
        }

        private void BodyFormat_Changed(object sender, RoutedEventArgs e)
        {
            if (_draft == null || _updatingEditors || _fileBody != null)
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

        // Upload File: the whole body becomes the bytes of a file that the user chooses, read once, now, and at most 64
        // MiB. The window keeps those bytes and the file's name, never its path, so later changes to the file are not
        // sent; the headers stay as they are. A file that cannot be read, or a read that is cancelled, changes nothing.
        internal async Task UploadBodyAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            string path;
            _state = ComposerState.Preparing;
            try
            {
                path = (ChooseBodyFile ?? ChooseBodyFileInDialog)();
            }
            finally
            {
                EndState();
            }

            // The file dialog runs a message loop, so the window can close while it is open (with its owner, say).
            if (string.IsNullOrEmpty(path) || _closed)
                return;

            string name = Path.GetFileName(path);
            using var cancellation = new CancellationTokenSource();
            _readingCancellation = cancellation;
            _state = ComposerState.ReadingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOADING", name);
            ReplayBodyRead read = null;
            try
            {
                read = await ReplayBodyFile.ReadAsync(path, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the body stays as it was.
            }
            finally
            {
                _readingCancellation = null;
                EndState();
            }

            if (_closed)
                return;

            if (read == null)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOAD_CANCELLED");
            }
            else if (!read.Succeeded)
            {
                StatusText.Text = Localization.GetLocalizedString(read.Code switch
                {
                    ReplayBodyFile.TooLargeCode => "COMPOSER_UPLOAD_TOO_LARGE",
                    ReplayBodyFile.NotFoundCode => "COMPOSER_UPLOAD_NOT_FOUND",
                    _ => "COMPOSER_UPLOAD_UNREADABLE",
                });
            }
            else
            {
                // The bytes as read, shared by every request of a sequence and never changed.
                _draft.SetBodyBytes(read.Body);
                _fileBody = read.Body;
                _fileName = name;
                ShowBody();
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOADED", name, read.Body.LongLength);
            }
        }

        // Clear body: an empty text body, in place of an uploaded file or the text in the editor.
        internal void ClearBody()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            _fileBody = null;
            _fileName = null;
            _draft.BodyText = "";
            _draft.BodyIsBase64 = false;
            ShowBody();
        }

        private string ChooseBodyFileInDialog()
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.GetLocalizedString("COMPOSER_UPLOAD_TITLE"),
                Filter = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILTER"),
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        // Whether a drag over the window lends a request that the window can take now (ComposerSessionDrag).
        internal bool CanAcceptSession(IDataObject data) =>
            !_closed && _state == ComposerState.Idle && ComposerSessionDrag.IsLending(data);

        // Drag a session: loads a request dragged from a connections list, in place of the request in the editors, after
        // asking when the user has edited that one. What is loaded is the copy made when the drag began, built as Edit and
        // resend builds it; nothing else that is dropped is used. Nothing is sent or read, and the previous response is
        // cleared, so it cannot pass for the new request's. Refused while the window sends, reads a file or asks something.
        internal bool DropSession(IDataObject data)
        {
            if (_closed)
                return false;
            if (_state != ComposerState.Idle)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_DROP_BUSY");
                return false;
            }

            ComposerSession session = ComposerSessionDrag.Take(data);
            if (session == null)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_DROP_REFUSED");
                return false;
            }

            ReplayProvenance provenance = session.Provenance;
            if (!RequestReplayDraft.CanReplay(session.Capture))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_DROP_UNSUPPORTED");
                return false;
            }

            RequestReplayDraft draft;
            try
            {
                draft = RequestReplayDraft.FromCapture(session.Capture);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_DROP_FAILED", exception.Message);
                return false;
            }

            if (CurrentEditors() != _loaded)
            {
                bool replace;
                _state = ComposerState.Preparing;
                try
                {
                    replace = _confirm(Localization.GetLocalizedString(
                        "COMPOSER_REPLACE_EDITED", provenance.SourceName, provenance.RequestNumber));
                }
                finally
                {
                    EndState();
                }

                if (!replace || _closed)
                    return false;
            }

            LoadDraft(draft, provenance);
            ClearResponse();
            StatusText.Text = Localization.GetLocalizedString(
                "COMPOSER_DROPPED", provenance.SourceName, provenance.RequestNumber);
            return true;
        }

        // Only a request dragged from a connections list can be dropped, anywhere on the window: text, links and files are
        // refused, on the editors too.
        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = CanAcceptSession(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Effects = DropSession(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private EditorState CurrentEditors() =>
            new(MethodBox.Text, UrlBox.Text, HeadersBox.Text, BodyBox.Text, Base64Box.IsChecked == true, _fileBody);

        private void CopyCurl_Click(object sender, RoutedEventArgs e)
        {
            if (_state != ComposerState.Idle)
                return;
            string unavailable = CurlUnavailableText();
            if (unavailable != null)
            {
                StatusText.Text = unavailable;
                return;
            }

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
        private async void UploadFile_Click(object sender, RoutedEventArgs e) => await UploadBodyAsync();
        private void ClearBody_Click(object sender, RoutedEventArgs e) => ClearBody();
        private void CancelSend_Click(object sender, RoutedEventArgs e) => CancelSend();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowError(Exception exception) => StatusText.Text = ErrorText(exception.Message);

        private static string ErrorText(string message) => Localization.GetLocalizedString("COMPOSER_ERROR", message);

        private static bool IsInputError(Exception exception) =>
            exception is ArgumentException or FormatException or InvalidOperationException or NotSupportedException;

        // What the editors hold, to tell whether the user has edited the request since it was loaded. An uploaded body is
        // compared by reference: it is the same bytes until another upload or Clear body.
        private sealed record EditorState(
            string Method, string Url, string Headers, string Body, bool Base64, byte[] FileBody);
    }
}
