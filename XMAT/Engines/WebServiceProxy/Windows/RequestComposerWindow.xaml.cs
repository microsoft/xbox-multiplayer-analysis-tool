// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
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
using XMAT.Operations.Storage;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Edits an isolated request draft and sends it, or one request per number when its URL has a '#', and displays the
    /// latest response separately from capture. Every send is also recorded in the Replays tab. A request can also be opened
    /// from a request file and saved as one, without sending it.
    /// </summary>
    public partial class RequestComposerWindow : Window
    {
        // The name that Save request as suggests: the extension says what the file is, and its last part what it holds.
        private const string SuggestedRequestFileName = "request.xmat-request.json";

        // The most characters of a file's name that the window shows (UntrustedText), as long as names can be.
        private const int MaxShownFileNameChars = 260;

        // The largest body of a request file that the editor shows. A larger one stays its exact bytes, shown by their size
        // (ShowBody): the editor would hold its text, or its Base64, a third larger, made on the UI thread.
        private const int MaxShownBodyBytes = 64 * 1024;

        private readonly RequestReplayClient _client;
        private readonly bool _ownsClient;
        private readonly Func<string, bool> _confirm;
        private readonly ReplayRecordingTarget _recording;
        private RequestReplayDraft _draft;
        private ReplayProvenance _provenance;
        private ComposerState _state;
        private CancellationTokenSource _sendingCancellation;
        private CancellationTokenSource _readingCancellation;
        private CancellationTokenSource _checkingCancellation;
        private CancellationTokenSource _writingCancellation;
        private bool _closed;
        private bool _updatingEditors;
        private string _bodyTextInEditor;

        // The cancellation of the run that the user stopped with Cancel request, as opposed to a run that XMAT stopped, as
        // when it closes. It is compared with each run's own cancellation, so an earlier run's cancel never counts.
        private CancellationTokenSource _cancelledByUser;

        // A body that the editor does not hold, as its exact bytes, and the name of its file as the window shows it: a file
        // uploaded for the body, or the body of a request file that is larger than the editor shows, which
        // _fileBodyFromRequestFile tells apart. Null while the editor holds the body.
        private byte[] _fileBody;
        private string _fileName;
        private bool _fileBodyFromRequestFile;

        // The editors as the request was loaded, to tell whether the user has edited it since.
        private EditorState _loaded;

        // What the window is doing; it starts nothing else meanwhile. Preparing covers checking a send and asking the user
        // about it, or asking which file to upload or open, whether to replace an edited request, or where and how to save
        // it. ReadingFile covers reading a body or request file, Checking making the editor's body into bytes for Use this
        // body or checking a request before saving it, and WritingFile saving a request file.
        private enum ComposerState
        {
            Idle,
            Preparing,
            Sending,
            ReadingFile,
            Checking,
            WritingFile,
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
            // The response came through the HTTP stack, which already removed its transfer framing: its preview never
            // decodes chunked framing again.
            ResponseView.BodyIsDecoded = true;
            CopyCurlButton.Content = Localization.GetLocalizedString("COMPOSER_COPY_CURL");
            SendButton.Content = Localization.GetLocalizedString("COMPOSER_SEND");
            CancelSendButton.Content = Localization.GetLocalizedString("COMPOSER_CANCEL_SEND");
            CloseButton.Content = Localization.GetLocalizedString("COMPOSER_CLOSE");
            OpenRequestButton.Content = Localization.GetLocalizedString("COMPOSER_OPEN_REQUEST");
            OpenRequestButton.ToolTip = Localization.GetLocalizedString("COMPOSER_OPEN_REQUEST_HINT");
            SaveRequestButton.Content = Localization.GetLocalizedString("COMPOSER_SAVE_REQUEST");
            SaveRequestButton.ToolTip = Localization.GetLocalizedString("COMPOSER_SAVE_REQUEST_HINT");
            CompleteMethodButton.Content = Localization.GetLocalizedString("COMPOSER_USE_METHOD");
            CompleteMethodButton.ToolTip = Localization.GetLocalizedString("COMPOSER_USE_METHOD_HINT");
            CompleteUrlButton.Content = Localization.GetLocalizedString("COMPOSER_USE_URL");
            CompleteUrlButton.ToolTip = Localization.GetLocalizedString("COMPOSER_USE_URL_HINT");
            CompleteHeadersButton.Content = Localization.GetLocalizedString("COMPOSER_USE_HEADERS");
            CompleteHeadersButton.ToolTip = Localization.GetLocalizedString("COMPOSER_USE_HEADERS_HINT");
            CompleteNoHeadersButton.Content = Localization.GetLocalizedString("COMPOSER_NO_HEADERS");
            CompleteNoHeadersButton.ToolTip = Localization.GetLocalizedString("COMPOSER_NO_HEADERS_HINT");
            CompleteBodyButton.Content = Localization.GetLocalizedString("COMPOSER_USE_BODY");
            CompleteBodyButton.ToolTip = Localization.GetLocalizedString("COMPOSER_USE_BODY_HINT");
            CompleteEmptyBodyButton.Content = Localization.GetLocalizedString("COMPOSER_EMPTY_BODY");
            CompleteEmptyBodyButton.ToolTip = Localization.GetLocalizedString("COMPOSER_EMPTY_BODY_HINT");
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

        // Test seam: how a file uploaded for the body is read, by default ReplayBodyFile.ReadAsync.
        internal Func<string, CancellationToken, Task<ReplayBodyRead>> ReadBodyFile { get; set; }

        // Test seams for request files: the file to open, or null for none; how it is read, by default
        // RequestFileReader.ReadFileAsync; where to save a request, or null for nowhere; how to save it, or null to save
        // nothing, by default asked in SaveRequestWindow; and how the file is published, by default
        // PrivateFilePublisher.Publish. Also where Copy as cURL puts its command, by default the clipboard. And the work
        // that the window runs off the UI thread: checking a request before saving it, by default InspectForSaving, and
        // making the editor's body into bytes for Use this body, by default BodyBytesFor.
        internal Func<string> ChooseRequestFile { get; set; }

        internal Func<string, CancellationToken, Task<RequestFileReadResult>> ReadRequestFile { get; set; }

        internal Func<string> ChooseSavePath { get; set; }

        internal Func<RequestSaveQuestion, RequestSaveChoice> ChooseSaveContent { get; set; }

        internal Func<string, Action<Stream>, CancellationToken, PrivateFilePublication> PublishRequestFile { get; set; }

        internal Action<string> CopyText { get; set; }

        internal Func<RequestReplayDraft, CancellationToken, RequestFileRecognized> InspectRequest { get; set; }

        internal Func<RequestReplayDraft, CancellationToken, byte[]> BodyBytesOf { get; set; }

        // The client that sends. The window disposes the client that it created once it is closed and no run uses it.
        internal RequestReplayClient Client => _client;

        // The request to send, checked as sending it would check it and frozen with its transport, and whether the user gave
        // the permission that it needs; null when nothing is to be sent: the input is invalid or still leaves out a part of
        // its request file, the user cancelled or declined, or the window closed meanwhile. A '#' in the URL asks for a range
        // and sends one request per number.
        private ReplayPlanPreview Prepare(int maxRedirects, ReplayTransportOptions transport, out bool confirmed)
        {
            confirmed = false;
            // A part that a request file left out is never sent as the placeholder that the editors show for it.
            if (_draft.Omissions.Any)
            {
                StatusText.Text = ErrorText(IncompleteText(_draft.Omissions));
                return null;
            }

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

        // Why the operation refused a run: what the composer says itself about Fiddler's control headers, the transport
        // options and a part that a request file left out; for a sequence whose request cannot be built, what building it
        // with the widest number says, as a single send says it; otherwise the operation's message, such as a run that is
        // too large.
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

            if (diagnostic?.Code == RequestFileCodes.RequestIncomplete)
            {
                return IncompleteText(draft.Omissions);
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
            LastResult = result;
        }

        private void ClearResponse()
        {
            LastResult = null;
            ResponseView.FirstLineAndHeaders = "";
            ResponseView.HeaderDictionary = null;
            ResponseView.BodyBytes = null;
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

        // Cancel request: the user stops the run in progress before its next request, the file being read or saved, and a
        // request or body being checked.
        internal void CancelSend()
        {
            _cancelledByUser = _sendingCancellation;
            StopWork();
        }

        private void StopWork()
        {
            _sendingCancellation?.Cancel();
            _readingCancellation?.Cancel();
            _checkingCancellation?.Cancel();
            _writingCancellation?.Cancel();
        }

        // Whether a run of work off the UI thread, which has just ended, was stopped: Cancel request or closing the window
        // cancelled it, which can come after the work last checked its token, or another run has taken its place. What the
        // work returned is then not used: it applies nothing and asks nothing, as if the work had been cancelled.
        private static bool Stopped(CancellationTokenSource run, CancellationTokenSource current) =>
            run.IsCancellationRequested || !ReferenceEquals(run, current);

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            StopWork();
            // The response's preview stops now, not when the view is unloaded later: a closed window shows nothing.
            ResponseView.StopPreview();
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
            // A body held as bytes, uploaded or from a request file, is sent as it was read. An edited body replaces the
            // draft's; an unedited one keeps the original bytes, since TextBox can normalize captured line endings.
            if (_fileBody == null && BodyBox.Text != _bodyTextInEditor)
            {
                _draft.BodyText = BodyBox.Text;
                _bodyTextInEditor = BodyBox.Text;
            }
        }

        // Shows a request in the editors as loaded: the one that the window opened with, or one dropped on it or opened from
        // a request file, with what that file left out. heldBody is the body of a request file that is larger than the
        // editor shows (HeldBody), which the draft keeps as these exact bytes, and source that file's name as shown.
        private void LoadDraft(
            RequestReplayDraft draft, ReplayProvenance provenance, byte[] heldBody = null, string source = null)
        {
            if (heldBody != null)
                draft.SetBodyBytes(heldBody);
            _draft = draft;
            _provenance = provenance;
            _fileBody = heldBody;
            _fileName = heldBody == null ? null : source;
            _fileBodyFromRequestFile = heldBody != null;
            MethodBox.Text = draft.Method;
            UrlBox.Text = draft.Url;
            HeadersBox.Text = draft.HeadersText;
            ShowBody();
            ShowOmissions();
            _loaded = CurrentEditors();
        }

        // The body editor; or, for an uploaded file or a request file's body larger than the editor shows, the file's name
        // and the body's size, since the editor would show the text or Base64 of a body that can be 64 MiB, made on the UI
        // thread. Such a body is never shown otherwise.
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
                ? Localization.GetLocalizedString(
                    _fileBodyFromRequestFile ? "COMPOSER_BODY_REQUEST_FILE" : "COMPOSER_BODY_FILE",
                    _fileName,
                    _fileBody.LongLength)
                : null;
            Base64Box.IsEnabled = !fromFile;
            UpdateCurlAvailability();
        }

        private void SetBusy(bool busy)
        {
            RequestEditor.IsEnabled = !busy;
            OmissionsPanel.IsEnabled = !busy;
            SendButton.IsEnabled = !busy;
            OpenRequestButton.IsEnabled = !busy;
            SaveRequestButton.IsEnabled = !busy;
            CancelSendButton.IsEnabled = busy;
            UpdateCurlAvailability();
        }

        // Copy as cURL copies one request as it is: not a URL with '#', which sends one request per number; not with
        // redirects followed, which curl would follow its own way; and only with the default transport, since the command
        // would not choose the same HTTP version or connect to the same endpoint. Nor a request that still leaves out a part
        // of its request file, which the command would send as its placeholder, nor a body that the window holds as bytes,
        // from an uploaded file or a request file, larger than 64 KiB (MaxShownBodyBytes): the command would hold all of it
        // as Base64, up to 64 MiB, made on the UI thread. The button says why it is unavailable.
        private void UpdateCurlAvailability()
        {
            string unavailable = CurlUnavailableText();
            CopyCurlButton.IsEnabled = _state == ComposerState.Idle && unavailable == null;
            CopyCurlButton.ToolTip = unavailable;
        }

        private string CurlUnavailableText() =>
            _draft?.Omissions.Any == true
                ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_INCOMPLETE", PendingParts(_draft.Omissions))
                : UrlBox.Text.Contains(ReplaySequence.Placeholder) || FollowRedirectsBox.IsChecked == true ||
                    SelectedHttpVersion != ReplayHttpVersion.Auto || ConnectToBox.IsChecked == true
                    ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_UNAVAILABLE")
                    : _fileBody?.LongLength > MaxShownBodyBytes
                        ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY")
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
        // sent; the headers stay as they are. A file that cannot be read, or a read that is cancelled, even once the file
        // was read by then, changes nothing. An upload does not supply a body that a request file left out: Use this body
        // does.
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

            if (string.IsNullOrEmpty(path) || _closed)
                return;

            // The file's name as the window shows it, never its path; a name can hold characters that hide or reorder text.
            string name = UntrustedText.Show(Path.GetFileName(path), MaxShownFileNameChars);
            Func<string, CancellationToken, Task<ReplayBodyRead>> readBody = ReadBodyFile ?? ReplayBodyFile.ReadAsync;
            using var cancellation = new CancellationTokenSource();
            _readingCancellation = cancellation;
            _state = ComposerState.ReadingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOADING", name);
            ReplayBodyRead read = null;
            bool stopped;
            try
            {
                read = await readBody(path, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the body stays as it was.
            }
            finally
            {
                // A file read by the time that the user cancelled is not used either.
                stopped = Stopped(cancellation, _readingCancellation);
                _readingCancellation = null;
                EndState();
            }

            if (_closed)
                return;

            if (stopped || read == null)
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
                _fileBodyFromRequestFile = false;
                ShowBody();
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOADED", name, read.Body.LongLength);
            }
        }

        // Clear body: an empty text body, in place of a body held as bytes, from an uploaded file or a request file, or of
        // the text in the editor. It does not supply a body that a request file left out: Empty body does.
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

        // Open request: loads a request file in place of the request in the editors, after asking when the user has edited
        // that one. The file is read and made into a request off the UI thread, and the read stops when the window closes or
        // the user cancels it; a file that cannot be read changes nothing, nor does a file read by the time of the cancel.
        // What the file left out stays to be supplied (ShowOmissions). A body larger than the editor shows is kept as its
        // exact bytes and shown by its size (HeldBody). A file carries no transport but its HTTP version and grants no
        // permission: Connect to another endpoint and Follow same-origin redirects go back to off, and a send asks for every
        // permission that it needs, as always. Only the file's name is shown or kept, never its path. Nothing is sent, and
        // the previous response is cleared, so it cannot pass for the new request's.
        internal async Task OpenRequestAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            string path;
            _state = ComposerState.Preparing;
            try
            {
                path = (ChooseRequestFile ?? ChooseRequestFileInDialog)();
            }
            finally
            {
                EndState();
            }

            if (string.IsNullOrEmpty(path) || _closed)
                return;

            string name = UntrustedText.Show(Path.GetFileName(path), MaxShownFileNameChars);
            Func<string, CancellationToken, Task<RequestFileReadResult>> read =
                ReadRequestFile ?? RequestFileReader.ReadFileAsync;
            using var cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            // The file is read, checked and made into a request off the UI thread.
            Func<Task<(RequestFileReadResult, RequestReplayDraft)>> open = async () =>
            {
                RequestFileReadResult result = await read(path, token);
                return (result, result.Content == null ? null : RequestReplayDraft.FromRequestFile(result.Content));
            };
            _readingCancellation = cancellation;
            _state = ComposerState.ReadingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_OPENING", name);
            RequestFileReadResult opened = null;
            RequestReplayDraft draft = null;
            bool stopped;
            try
            {
                (opened, draft) = await Task.Run(open, token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the request stays as it was.
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                // A file that the reader accepted but that makes no request changes nothing either.
                opened = RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }
            finally
            {
                // A file read by the time that the user cancelled is not opened either, and nothing is asked about it.
                stopped = Stopped(cancellation, _readingCancellation);
                _readingCancellation = null;
                EndState();
            }

            if (_closed)
                return;

            if (stopped || draft == null)
            {
                StatusText.Text = Localization.GetLocalizedString(
                    stopped || opened == null ? "COMPOSER_OPEN_CANCELLED" : OpenFailureKey(opened.Code));
                return;
            }

            if (_loaded != CurrentEditors())
            {
                bool replace;
                _state = ComposerState.Preparing;
                try
                {
                    replace = _confirm(Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_FILE", name));
                }
                finally
                {
                    EndState();
                }

                if (_closed)
                    return;
                if (!replace)
                {
                    StatusText.Text = Localization.GetLocalizedString("COMPOSER_OPEN_CANCELLED");
                    return;
                }
            }

            ResetTransport(opened.Content.HttpVersion);
            // A body larger than the editor shows stays the file's exact bytes, which the window shows by their size.
            LoadDraft(draft, ReplayProvenance.FromRequestFile(name), HeldBody(opened.Content), name);
            ClearResponse();
            StatusText.Text = draft.Omissions.Any
                ? Localization.GetLocalizedString("COMPOSER_OPENED_INCOMPLETE", name, PendingParts(draft.Omissions))
                : Localization.GetLocalizedString("COMPOSER_OPENED", name);
        }

        // A request file carries the HTTP version and no other transport: Connect to another endpoint goes back to off,
        // without an endpoint, and Follow same-origin redirects to off, with at most 10 when it is selected again.
        private void ResetTransport(ReplayHttpVersion httpVersion)
        {
            HttpVersionBox.SelectedItem = HttpVersionBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is ReplayHttpVersion version && version == httpVersion) ??
                HttpVersionBox.Items[0];
            ConnectToBox.IsChecked = false;
            ConnectToText.Text = "";
            FollowRedirectsBox.IsChecked = false;
            RedirectLimitBox.SelectedItem = ReplayOptions.MaxRedirectLimit;
        }

        // The body of a request file that is larger than the editor shows (MaxShownBodyBytes); null for a smaller body, or
        // none, which the editor shows. The window keeps it as its exact bytes, sent and saved as they are, and shows only
        // its size: it never makes its text or Base64, which could be 64 MiB, on the UI thread.
        private static byte[] HeldBody(RequestFileContent content) =>
            content.Body is { LongLength: > MaxShownBodyBytes } body ? body : null;

        private string ChooseRequestFileInDialog()
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization.GetLocalizedString("COMPOSER_OPEN_TITLE"),
                Filter = Localization.GetLocalizedString("COMPOSER_OPEN_FILTER"),
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        // Why a request file was not opened; whatever the reason, the request in the editors is unchanged.
        private static string OpenFailureKey(string code) => code switch
        {
            RequestFileCodes.NotFound => "COMPOSER_OPEN_NOT_FOUND",
            RequestFileCodes.TooLarge => "COMPOSER_OPEN_TOO_LARGE",
            RequestFileCodes.Unreadable => "COMPOSER_OPEN_UNREADABLE",
            RequestFileCodes.Invalid => "COMPOSER_OPEN_INVALID",
            RequestFileCodes.Unsupported => "COMPOSER_OPEN_UNSUPPORTED",
            _ => "COMPOSER_OPEN_FAILED",
        };

        // What the request file left out and is still to be supplied, each part with the buttons that supply it; nothing
        // for any other request.
        private void ShowOmissions()
        {
            RequestOmissions pending = _draft.Omissions;
            OmissionsPanel.Visibility = pending.Any ? Visibility.Visible : Visibility.Collapsed;
            OmissionsText.Text = pending.Any
                ? Localization.GetLocalizedString("COMPOSER_OMITTED", PendingParts(pending))
                : null;
            CompleteMethodButton.Visibility = pending.Method ? Visibility.Visible : Visibility.Collapsed;
            CompleteUrlButton.Visibility = pending.Url ? Visibility.Visible : Visibility.Collapsed;
            CompleteHeadersButton.Visibility = pending.Headers ? Visibility.Visible : Visibility.Collapsed;
            CompleteNoHeadersButton.Visibility = CompleteHeadersButton.Visibility;
            CompleteBodyButton.Visibility = pending.Body ? Visibility.Visible : Visibility.Collapsed;
            CompleteEmptyBodyButton.Visibility = CompleteBodyButton.Visibility;
            UpdateCurlAvailability();
        }

        // The parts that a request file left out and that are not supplied yet, as the composer names them.
        internal static string PendingParts(RequestOmissions pending)
        {
            ArgumentNullException.ThrowIfNull(pending);
            var parts = new List<string>(4);
            if (pending.Method)
                parts.Add(Localization.GetLocalizedString("COMPOSER_PART_METHOD"));
            if (pending.Url)
                parts.Add(Localization.GetLocalizedString("COMPOSER_PART_URL"));
            if (pending.Headers)
                parts.Add(Localization.GetLocalizedString("COMPOSER_PART_HEADERS"));
            if (pending.Body)
                parts.Add(Localization.GetLocalizedString("COMPOSER_PART_BODY"));
            return string.Join(", ", parts);
        }

        private static string IncompleteText(RequestOmissions pending) =>
            Localization.GetLocalizedString("COMPOSER_INCOMPLETE", PendingParts(pending));

        // The buttons that supply a part that the request file left out: each supplies its own part, explicitly, as the
        // editors show it now (RequestReplayDraft.CompleteMethod, CompleteUrl, ReplaceHeaders and CompleteBody). Nothing
        // else supplies one: typing, Upload File and Clear body change only what the editors hold. A part that is not valid
        // is not supplied, and the status says why.
        internal void SupplyMethod()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            string method = MethodBox.Text;
            if (!RequestReplayDraft.IsValidMethod(method) || method.Length > RequestFileLimits.MaxMethodChars)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SUPPLY_METHOD_INVALID");
                return;
            }

            _draft.CompleteMethod(method);
            Supplied("COMPOSER_PART_METHOD");
        }

        internal void SupplyUrl()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            string url = UrlBox.Text;
            if (!RequestReplayDraft.IsValidRequestFileUrl(url))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SUPPLY_URL_INVALID");
                return;
            }

            _draft.CompleteUrl(url);
            Supplied("COMPOSER_PART_URL");
        }

        // none: No headers, which supplies a request without headers and clears the editor; otherwise the editor's lines.
        internal void SupplyHeaders(bool none)
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            try
            {
                _draft.ReplaceHeaders(none ? "" : HeadersBox.Text);
            }
            catch (FormatException exception)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SUPPLY_FAILED", exception.Message);
                return;
            }

            if (none)
                HeadersBox.Text = "";
            Supplied("COMPOSER_PART_HEADERS");
        }

        // empty: Empty body, which supplies an empty body in place of the editor's text or an uploaded file; otherwise the
        // body that an uploaded file or the editor holds, as the editor shows it. The editor's text is made into the bytes
        // to send (from Base64, or as UTF-8) off the UI thread, since it can be a body of 64 MiB, from a copy of the draft
        // that nothing changes meanwhile; the window is busy until then, and Cancel request or closing the window stops it
        // and supplies nothing, even once the bytes are made.
        internal async Task SupplyBodyAsync(bool empty)
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            if (empty)
            {
                _fileBody = null;
                _fileName = null;
                _draft.CompleteBody(Array.Empty<byte>());
                _draft.SetBodyFormat(false);
                ShowBody();
                Supplied("COMPOSER_PART_BODY");
                return;
            }

            if (_fileBody != null)
            {
                // The exact bytes that the window holds for the body, which it shows by the file's name and size.
                _draft.CompleteBody(_fileBody);
                Supplied("COMPOSER_PART_BODY");
                return;
            }

            UpdateDraft();
            RequestReplayDraft draft = _draft;
            RequestReplayDraft copy = draft.WithUrl(draft.Url);
            // Text that is not Base64 is kept as the editor shows it, so it is never decoded again on the UI thread.
            string text = copy.BodyIsBase64 ? null : copy.BodyText;
            Func<RequestReplayDraft, CancellationToken, byte[]> decode = BodyBytesOf ?? BodyBytesFor;
            using var cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            _checkingCancellation = cancellation;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SUPPLYING_BODY");
            byte[] body = null;
            string invalid = null;
            bool stopped;
            try
            {
                body = await Task.Run(() => decode(copy, token), token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the body stays left out.
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                invalid = exception.Message;
            }
            finally
            {
                // Bytes made by the time that the user cancelled supply nothing either.
                stopped = Stopped(cancellation, _checkingCancellation);
                _checkingCancellation = null;
                EndState();
            }

            if (_closed || !ReferenceEquals(draft, _draft))
                return;
            if (stopped || body == null)
            {
                StatusText.Text = stopped || invalid == null
                    ? Localization.GetLocalizedString("COMPOSER_SUPPLY_CANCELLED")
                    : Localization.GetLocalizedString("COMPOSER_SUPPLY_FAILED", invalid);
                return;
            }

            _draft.CompleteBody(body);
            if (text != null)
            {
                // Shown as the text that the editor holds again, not as the Base64 of its bytes.
                _draft.BodyText = text;
                _draft.BodyIsBase64 = false;
            }

            Supplied("COMPOSER_PART_BODY");
        }

        // The bytes that a draft's body stands for, from its Base64 or as its text's UTF-8: up to 64 MiB of work, which
        // SupplyBodyAsync runs off the UI thread on a copy of the draft that nothing changes meanwhile.
        internal static byte[] BodyBytesFor(RequestReplayDraft draft, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            cancellationToken.ThrowIfCancellationRequested();
            return draft.GetBodyBytes();
        }

        // Says which part is now supplied, and what is still left out, if anything.
        private void Supplied(string part)
        {
            ShowOmissions();
            string supplied = Localization.GetLocalizedString("COMPOSER_SUPPLIED", Localization.GetLocalizedString(part));
            StatusText.Text = supplied + " " + (_draft.Omissions.Any
                ? Localization.GetLocalizedString("COMPOSER_STILL_OMITTED", PendingParts(_draft.Omissions))
                : Localization.GetLocalizedString("COMPOSER_ALL_SUPPLIED"));
        }

        private async void CompletePart_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, CompleteMethodButton))
                SupplyMethod();
            else if (ReferenceEquals(sender, CompleteUrlButton))
                SupplyUrl();
            else if (ReferenceEquals(sender, CompleteHeadersButton) || ReferenceEquals(sender, CompleteNoHeadersButton))
                SupplyHeaders(ReferenceEquals(sender, CompleteNoHeadersButton));
            else if (ReferenceEquals(sender, CompleteBodyButton) || ReferenceEquals(sender, CompleteEmptyBodyButton))
                await SupplyBodyAsync(ReferenceEquals(sender, CompleteEmptyBodyButton));
        }

        // Save request as: saves the request as a request file, without sending it. What is saved is fixed first, on the UI
        // thread: a copy of the draft that later edits do not change, which shares the body's text or bytes, never changed
        // in place, with the HTTP version chosen now. Checking that copy and recognizing what it holds can read a body of
        // 64 MiB, so that runs off the UI thread (InspectForSaving). The window then asks where, then how: as a template,
        // which keeps only the request's shape, unless the user includes the captured content for this save. The content
        // needs every part that a request file left out, and its own permission when the URL goes to another origin than
        // the request was captured or loaded for. The file is written off the UI thread too, privately and never in place
        // of another file. The window is busy while it checks or writes, and closing it or Cancel request stops either: a
        // check stopped even once it ended asks nothing and saves nothing. The request in the editors is never changed,
        // whatever happens.
        internal async Task SaveRequestAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            UpdateDraft();
            RequestReplayDraft draft = _draft.WithUrl(_draft.Url);
            ReplayHttpVersion httpVersion = SelectedHttpVersion;
            Func<RequestReplayDraft, CancellationToken, RequestFileRecognized> inspect =
                InspectRequest ?? InspectForSaving;
            using var checking = new CancellationTokenSource();
            CancellationToken checkingToken = checking.Token;
            _checkingCancellation = checking;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_CHECKING");
            RequestFileRecognized recognized = null;
            string invalid = null;
            bool stopped;
            try
            {
                recognized = await Task.Run(() => inspect(draft, checkingToken), checkingToken);
            }
            catch (OperationCanceledException) when (checking.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is saved.
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or RequestFileException)
            {
                invalid = exception.Message;
            }
            finally
            {
                // A check that ended by the time that the user cancelled saves nothing either, and asks nothing.
                stopped = Stopped(checking, _checkingCancellation);
                _checkingCancellation = null;
                EndState();
            }

            if (_closed)
                return;
            if (stopped || recognized == null)
            {
                StatusText.Text = stopped || invalid == null
                    ? Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED")
                    : Localization.GetLocalizedString("COMPOSER_SAVE_INVALID", invalid);
                return;
            }

            string path;
            RequestSaveChoice choice = null;
            _state = ComposerState.Preparing;
            try
            {
                path = (ChooseSavePath ?? ChooseSavePathInDialog)();
                if (!string.IsNullOrEmpty(path) && !_closed)
                {
                    var question = new RequestSaveQuestion(
                        draft.Omissions, recognized, draft.ChangesOrigin ? Origin(draft.Url) : null);
                    choice = (ChooseSaveContent ?? (asked => SaveRequestWindow.Ask(this, asked)))(question);
                }
            }
            finally
            {
                EndState();
            }

            if (string.IsNullOrEmpty(path) || _closed)
                return;
            if (choice == null)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED");
                return;
            }

            // The content is checked again here, so that no answer saves what the window would not let through.
            RequestFileProfile profile = choice.Profile;
            if (profile == RequestFileProfile.Content && draft.Omissions.Any)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_INCOMPLETE", PendingParts(draft.Omissions));
                return;
            }

            if (profile == RequestFileProfile.Content && draft.ChangesOrigin && !choice.OriginChangeAllowed)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_ORIGIN_REFUSED", Origin(draft.Url));
                return;
            }

            string name = UntrustedText.Show(Path.GetFileName(path), MaxShownFileNameChars);
            Func<string, Action<Stream>, CancellationToken, PrivateFilePublication> publish =
                PublishRequestFile ?? PrivateFilePublisher.Publish;
            using var cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            RequestFileException refused = null;
            // The file is written and published off the UI thread.
            Func<PrivateFilePublication> save = () => publish(
                path,
                stream =>
                {
                    try
                    {
                        RequestFileWriter.Write(draft, httpVersion, profile, stream, token);
                    }
                    catch (RequestFileException exception)
                    {
                        // The publisher says only Failed; the code says why.
                        refused = exception;
                        throw;
                    }
                },
                token);
            PrivateFilePublication published = PrivateFilePublication.Cancelled;
            _writingCancellation = cancellation;
            _state = ComposerState.WritingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVING", name);
            try
            {
                // Unlike a read or a check, a save is reported as it ended, also after Cancel request: a file that was
                // published by then exists, and the status says so.
                published = await Task.Run(save, token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Stopped before the file was begun: nothing was written.
            }
            finally
            {
                _writingCancellation = null;
                EndState();
            }

            if (_closed)
                return;

            StatusText.Text = SaveResultText(published, profile, name, refused, draft.Omissions);
        }

        // Checks a request as a request file holds it (RequestReplayDraft.ValidateStructure) and recognizes what its content
        // would include (RequestFileWriter.Recognize): work that can read a body of 64 MiB, which SaveRequestAsync runs off
        // the UI thread on a copy of the draft that nothing changes meanwhile. It stops between its steps once cancelled.
        internal static RequestFileRecognized InspectForSaving(RequestReplayDraft draft, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            cancellationToken.ThrowIfCancellationRequested();
            draft.ValidateStructure();
            cancellationToken.ThrowIfCancellationRequested();
            return RequestFileWriter.Recognize(draft);
        }

        // Where to save a request file. The dialog never offers to replace a file, and the publisher never does (Exists).
        private string ChooseSavePathInDialog()
        {
            var dialog = new SaveFileDialog
            {
                Title = Localization.GetLocalizedString("COMPOSER_SAVE_TITLE"),
                Filter = Localization.GetLocalizedString("COMPOSER_SAVE_FILTER"),
                FileName = SuggestedRequestFileName,
                DefaultExt = "json",
                AddExtension = true,
                OverwritePrompt = false,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        // What became of a save, by the file's name only: only Published wrote the file, and Exists replaced nothing. A
        // request that the writer refused (refused) says why, by its code.
        private static string SaveResultText(
            PrivateFilePublication published,
            RequestFileProfile profile,
            string name,
            RequestFileException refused,
            RequestOmissions pending) =>
            published switch
            {
                PrivateFilePublication.Published => Localization.GetLocalizedString(
                    profile == RequestFileProfile.Content ? "COMPOSER_SAVED_CONTENT" : "COMPOSER_SAVED_TEMPLATE", name),
                PrivateFilePublication.Exists => Localization.GetLocalizedString("COMPOSER_SAVE_EXISTS", name),
                PrivateFilePublication.Unsupported => Localization.GetLocalizedString("COMPOSER_SAVE_UNSUPPORTED"),
                PrivateFilePublication.Cancelled => Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED"),
                PrivateFilePublication.CleanupFailed => Localization.GetLocalizedString("COMPOSER_SAVE_CLEANUP_FAILED"),
                _ => refused?.Code switch
                {
                    null => Localization.GetLocalizedString("COMPOSER_SAVE_FAILED"),
                    RequestFileCodes.Incomplete =>
                        Localization.GetLocalizedString("COMPOSER_SAVE_INCOMPLETE", PendingParts(pending)),
                    _ => Localization.GetLocalizedString("COMPOSER_SAVE_INVALID", refused.Message),
                },
            };

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

        // Copy as cURL: the command that sends the request as the editors hold it, to the clipboard. A request that still
        // leaves out a part of its request file is never copied (RequestIncompleteException).
        internal void CopyAsCurl()
        {
            if (_state != ComposerState.Idle || _closed)
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
                (CopyText ?? Clipboard.SetText)(_draft.ToCurlCommand());
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_COPIED");
            }
            catch (RequestIncompleteException)
            {
                StatusText.Text = Localization.GetLocalizedString(
                    "COMPOSER_COPY_CURL_INCOMPLETE", PendingParts(_draft.Omissions));
            }
            catch (Exception exception) when (IsInputError(exception) || exception is ExternalException)
            {
                ShowError(exception);
            }
        }

        private void CopyCurl_Click(object sender, RoutedEventArgs e) => CopyAsCurl();

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
        private async void UploadFile_Click(object sender, RoutedEventArgs e) => await UploadBodyAsync();
        private async void OpenRequest_Click(object sender, RoutedEventArgs e) => await OpenRequestAsync();
        private async void SaveRequest_Click(object sender, RoutedEventArgs e) => await SaveRequestAsync();
        private void ClearBody_Click(object sender, RoutedEventArgs e) => ClearBody();
        private void CancelSend_Click(object sender, RoutedEventArgs e) => CancelSend();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowError(Exception exception) => StatusText.Text = ErrorText(exception.Message);

        private static string ErrorText(string message) => Localization.GetLocalizedString("COMPOSER_ERROR", message);

        private static bool IsInputError(Exception exception) =>
            exception is ArgumentException or FormatException or InvalidOperationException or NotSupportedException;

        // What the editors hold, to tell whether the user has edited the request since it was loaded. A body that the window
        // holds as bytes, an uploaded file's or a large one from a request file, is compared by reference: it is the same
        // bytes until another upload or Clear body.
        private sealed record EditorState(
            string Method, string Url, string Headers, string Body, bool Base64, byte[] FileBody);
    }
}
