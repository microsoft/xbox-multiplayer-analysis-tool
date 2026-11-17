// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.Operations.Storage;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Edits an isolated request draft and sends it, or one request per number when its URL has a '#', and displays the
    /// latest response separately from capture. Every send is also recorded in the Replays tab. A request can also be opened
    /// from a request file and saved as one, without sending it, and exported for curl, without sending it: copied as a
    /// command or saved as a script. A request that the Replays tab recorded in this session can be loaded again from the
    /// history. Its body can be built as a multipart form, from text and file parts (MultipartFormWindow).
    /// </summary>
    public partial class RequestComposerWindow : Window
    {
        // The name that Save request as suggests: the extension says what the file is, and its last part what it holds.
        private const string SuggestedRequestFileName = "request.xmat-request.json";

        // The name that Save as cURL script suggests.
        private const string SuggestedCurlScriptName = "request.ps1";

        // The most characters of a file's name that the window shows (UntrustedText), as long as names can be.
        private const int MaxShownFileNameChars = 260;

        // The most characters of an origin that a question or the status shows (UntrustedText).
        private const int MaxShownOriginChars = 300;

        // The largest body of a request file that the editor shows. A larger one stays its exact bytes, shown by their size
        // (ShowBody): the editor would hold its text, or its Base64, a third larger, made on the UI thread.
        private const int MaxShownBodyBytes = 64 * 1024;

        // The command that Copy as cURL renders is UTF-8 text; bytes that are not are never copied.
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        // A body that the editor does not hold, as its exact bytes; where it comes from as the window shows it; and the key
        // of the text that shows it (ShowBody): a file uploaded for the body, by its name, a form that Build form made, by its
        // number of parts, or a body that is larger than the editor shows, of a request file, by its name, or of a row of the
        // Replays tab that Load took it from, by the tab's name and the row's number. Null while the editor holds the body.
        private byte[] _fileBody;
        private string _fileName;
        private string _fileBodyKey;

        // The parts that Build form made the body from, to build the form from again, kept only while the body is still that
        // form: every other body forgets them, and so does closing the window. They hold no file's path. And the Build form
        // dialog while it is open.
        private IReadOnlyList<MultipartPart> _formSeed;
        private MultipartFormWindow _formDialog;

        // The request as it was loaded, with the transport options as the window, the last request file or the last Load
        // set them (EditorState), to tell whether the user has edited them since.
        private EditorState _loaded;

        // The history view while its menu is open (OpenHistoryView): its one subscription to the history, the rebuild that a
        // change queued, if any, and the number of the opening, which closing advances, so that work queued for an earlier
        // opening does nothing.
        private IDisposable _historyObservation;
        private DispatcherOperation _historyRebuild;
        private int _historyGeneration;
        private bool _historyOpen;

        // What the window is doing; it starts nothing else meanwhile. Preparing covers asking the user about a send, which
        // numbers to send and whether to send it, or asking which file to upload or open, whether to replace an edited
        // request, or where and how to save it or a cURL script, and whether to export it, and the Build form dialog while it
        // is open. ReadingFile covers reading a body or request file, or preparing a request that Load takes from the
        // history, Checking checking a send, making the editor's body into bytes for Use this body, checking a request before
        // saving it, or preparing a cURL command or script, and WritingFile saving a request file or a cURL script.
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
        // nothing is recorded, which only tests do. confirm asks the user a question with the message, Yes or No; without
        // it, the window asks in a message box whose default answer is No (ShowQuestion).
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
                (ShowQuestion ?? ShowMessageBox)(
                    message, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

            Title = Localization.GetLocalizedString("COMPOSER_TITLE");
            ReplayNotice.Text = Localization.GetLocalizedString("COMPOSER_NOTICE");
            MethodLabel.Content = Localization.GetLocalizedString("COMPOSER_METHOD");
            UrlLabel.Content = Localization.GetLocalizedString("COMPOSER_URL");
            UrlBox.ToolTip = Localization.GetLocalizedString("COMPOSER_URL_HINT");
            HeadersLabel.Content = Localization.GetLocalizedString("COMPOSER_HEADERS");
            BodyLabel.Content = Localization.GetLocalizedString("COMPOSER_BODY");
            BodyBox.ToolTip = Localization.GetLocalizedString("COMPOSER_BODY_HINT");
            // Each field of the request, and the HTTP version, has its own name for automation, as its label reads without
            // the access key: a TextBox or ComboBox takes no name from a Label's Target, and the name that LabeledBy gives
            // exists only once something has made the label's automation peer, which a screen reader that starts at the
            // focused field has not.
            AutomationProperties.SetName(MethodBox, Localization.GetLocalizedString("COMPOSER_METHOD_FIELD"));
            AutomationProperties.SetName(UrlBox, Localization.GetLocalizedString("COMPOSER_URL_FIELD"));
            AutomationProperties.SetName(HeadersBox, Localization.GetLocalizedString("COMPOSER_HEADERS_FIELD"));
            AutomationProperties.SetName(BodyBox, Localization.GetLocalizedString("COMPOSER_BODY_FIELD"));
            Base64Box.Content = Localization.GetLocalizedString("COMPOSER_BASE64");
            UploadFileButton.Content = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILE");
            UploadFileButton.ToolTip = Localization.GetLocalizedString("COMPOSER_UPLOAD_FILE_HINT");
            BuildFormButton.Content = Localization.GetLocalizedString("COMPOSER_BUILD_FORM");
            BuildFormButton.ToolTip = Localization.GetLocalizedString("COMPOSER_BUILD_FORM_HINT");
            ClearBodyButton.Content = Localization.GetLocalizedString("COMPOSER_CLEAR_BODY");
            FollowRedirectsBox.Content = Localization.GetLocalizedString("COMPOSER_FOLLOW_REDIRECTS");
            FollowRedirectsBox.ToolTip = Localization.GetLocalizedString("COMPOSER_FOLLOW_REDIRECTS_HINT");
            RedirectLimitBox.ToolTip = Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT");
            AutomationProperties.SetName(RedirectLimitBox, Localization.GetLocalizedString("COMPOSER_REDIRECT_LIMIT"));
            InspectSessionBox.Content = Localization.GetLocalizedString("COMPOSER_INSPECT_SESSION");
            InspectSessionBox.ToolTip = Localization.GetLocalizedString("COMPOSER_INSPECT_SESSION_HINT");
            HttpVersionLabel.Content = Localization.GetLocalizedString("COMPOSER_HTTP_VERSION");
            HttpVersionBox.ToolTip = Localization.GetLocalizedString("COMPOSER_HTTP_VERSION_HINT");
            AutomationProperties.SetName(HttpVersionBox, Localization.GetLocalizedString("COMPOSER_HTTP_VERSION_FIELD"));
            ConnectToBox.Content = Localization.GetLocalizedString("COMPOSER_CONNECT_TO");
            ConnectToBox.ToolTip = Localization.GetLocalizedString("COMPOSER_CONNECT_TO_HINT");
            ConnectToText.ToolTip = Localization.GetLocalizedString("COMPOSER_CONNECT_TO_HINT");
            AutomationProperties.SetName(ConnectToText, Localization.GetLocalizedString("COMPOSER_CONNECT_TO_ENDPOINT"));
            ResponseView.Title = Localization.GetLocalizedString("COMPOSER_RESPONSE");
            // The response came through the HTTP stack, which already removed its transfer framing: its preview never
            // decodes chunked framing again.
            ResponseView.BodyIsDecoded = true;
            CopyCurlButton.Content = Localization.GetLocalizedString("COMPOSER_COPY_CURL");
            SaveCurlButton.Content = Localization.GetLocalizedString("COMPOSER_SAVE_CURL");
            // The warning is shown before either export is used, and is read with each button.
            CurlWarningText.Text = Localization.GetLocalizedString("COMPOSER_CURL_WARNING");
            AutomationProperties.SetHelpText(CopyCurlButton, CurlWarningText.Text);
            AutomationProperties.SetHelpText(SaveCurlButton, CurlWarningText.Text);
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
            HistoryButton.Content = Localization.GetLocalizedString("COMPOSER_HISTORY");
            HistoryButton.ToolTip = Localization.GetLocalizedString("COMPOSER_HISTORY_HINT");
            AutomationProperties.SetName(HistoryMenu, Localization.GetLocalizedString("COMPOSER_HISTORY_MENU"));
            // The history is of what the Replays tab recorded: a window that records nothing, which only tests make, has none.
            HistoryButton.Visibility = _recording == null ? Visibility.Collapsed : Visibility.Visible;
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

        // Send. Everything that the run sends is fixed at its entry, on the UI thread and without parsing anything, before
        // the user is asked anything (FrozenSend): the request, as a copy that later edits do not change, whether its body
        // is a form that Build form made, the options, the transport as the controls hold it and where the request comes
        // from. Checking it, which parses the headers, the body, the URL and the endpoint, runs off the UI thread
        // (CheckSend); for a URL with '#', the user then chooses the numbers on the UI thread, and the run with them is
        // checked off the UI thread too (CheckSequence), over the same request and the transport that the first check
        // parsed, never the controls again. Cancel request or closing the window stops either check (RunSendCheckAsync).
        // The one question for the whole run is worded from what the checks found; then the run is recorded and sent, and
        // why the operation refused it, if it did, is worded off the UI thread too (RunRefusalText).
        internal async Task SendAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            UpdateDraft();
            RequestReplayDraft draft = _draft.WithUrl(_draft.Url);
            var send = new FrozenSend(
                draft,
                _formSeed != null,
                MaxRedirects,
                SelectedHttpVersion,
                ConnectToBox.IsChecked == true,
                ConnectToText.Text,
                draft.Url.Contains(ReplaySequence.Placeholder));
            bool inspect = InspectSessionBox.IsChecked == true;
            ReplayProvenance provenance = _provenance;
            string shown = StatusText.Text;
            RequestReplayClient client = _client;
            Func<FrozenSend, CancellationToken, SendCheck> check =
                PrepareSend ?? ((frozen, token) => CheckSend(frozen, client, token));
            SendCheck found = await RunSendCheckAsync(token => check(send, token), shown);
            if (found == null)
                return;

            if (send.Numbered)
            {
                ReplaySequence sequence = AskSequence();
                if (sequence == null)
                    return;
                ReplayTransportOptions transport = found.Transport;
                Func<FrozenSend, ReplaySequence, ReplayTransportOptions, CancellationToken, SendCheck> previewSequence =
                    PreviewSequence ?? ((frozen, numbers, checkedTransport, token) =>
                        CheckSequence(frozen, numbers, checkedTransport, client, token));
                found = await RunSendCheckAsync(token => previewSequence(send, sequence, transport, token), shown);
                if (found == null)
                    return;
            }

            // One question for the whole run, about what was checked: what is typed or chosen afterwards changes nothing in
            // it. The window parses nothing to ask it: the URL's origin that it names comes from the check.
            ReplayPlanPreview plan = found.Plan;
            int maxRedirects = send.MaxRedirects;
            bool confirmed = false;
            if (plan.UsesUnsafeMethod || plan.ChangesOrigin || plan.UsesConnectTo)
            {
                _state = ComposerState.Preparing;
                try
                {
                    confirmed = _confirm(ConfirmationMessage(plan, maxRedirects, found.ConnectToOrigin));
                }
                catch (Exception exception) when (IsInputError(exception))
                {
                    ShowError(exception);
                }
                finally
                {
                    EndState();
                }

                if (!confirmed || _closed)
                    return;
            }

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
            string refusal = null;
            Func<OperationDiagnostic, ReplayPlanPreview, string> explain = ExplainRunRefusal ?? RunRefusalText;
            try
            {
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

                // Why the operation refused the run once it was sent, worded off the UI thread before the window shows it:
                // for a sequence, building its widest request again says it (PlanError). The run has ended, so a cancel
                // meanwhile changes nothing: the window says what the run did.
                if (sent is { WasCancelled: false, Result.Succeeded: false })
                {
                    OperationDiagnostic diagnostic = FirstDiagnostic(sent.Result.Diagnostics);
                    refusal = await Task.Run(() => explain(diagnostic, plan));
                }
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
            ShowOutcome(plan, sent, refusal, recording, maxRedirects, cancelled);
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

        // Test seams for the cURL export: how a request is prepared, by default RequestCurlExporter.Prepare, and rendered,
        // by default RequestCurlExporter.Write, both off the UI thread; where to save a cURL script, or null for nowhere,
        // by default asked in a save dialog; and how the script is published, by default PrivateFilePublisher.Publish.
        internal Func<RequestReplayDraft, ReplayTransportOptions, int, CancellationToken, CurlRequest> PrepareCurl { get; set; }

        internal Action<CurlRequest, CurlScriptForm, string, Stream, CancellationToken> WriteCurlScript { get; set; }

        internal Func<string> ChooseCurlScriptPath { get; set; }

        internal Func<string, Action<Stream>, CancellationToken, PrivateFilePublication> PublishCurlScript { get; set; }

        // Test seam: how the window shows a question when it was given no confirm, by default in a message box that it
        // owns (ShowMessageBox), with the buttons, the icon and the answer that Enter gives: Yes or No, a warning, and No.
        internal Func<string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> ShowQuestion { get; set; }

        // Test seam: how Load makes a row of the Replays tab into a request, off the UI thread, by default
        // RequestReplayDraft.FromCapture, which keeps a body larger than the editor shows as its exact bytes.
        internal Func<ProxyConnectionModel, int, CancellationToken, RequestReplayDraft> PrepareHistoryDraft { get; set; }

        // Test seam: how Build form shows its dialog, by default modally, owned by the window (ShowOwnedFormDialog).
        internal Action<MultipartFormWindow> ShowFormDialog { get; set; }

        // Test seam: whether building a request would drop the Content-Type of a form that Build form made, which Save request
        // and both cURL exports check first, off the UI thread, over the request and the form flag that they froze; by default
        // FormContentTypeDropped.
        internal Func<bool, RequestReplayDraft, CancellationToken, bool> CheckFormContentType { get; set; }

        // Test seams for Send, null in production: its first check, off the UI thread, by default CheckSend with the window's
        // client; the check of a URL with '#' once the user chose its numbers, off the UI thread too, by default
        // CheckSequence; and how the window words why the operation refused a run once it was sent, off the UI thread, by
        // default RunRefusalText.
        internal Func<FrozenSend, CancellationToken, SendCheck> PrepareSend { get; set; }

        internal Func<FrozenSend, ReplaySequence, ReplayTransportOptions, CancellationToken, SendCheck> PreviewSequence
        {
            get;
            set;
        }

        internal Func<OperationDiagnostic, ReplayPlanPreview, string> ExplainRunRefusal { get; set; }

        // The client that sends. The window disposes the client that it created once it is closed and no run uses it.
        internal RequestReplayClient Client => _client;

        // What Send froze at its entry, on the UI thread, without parsing anything (SendAsync): the request, as a copy that
        // later edits do not change, which shares the body's text or bytes, never changed in place; whether its body is a
        // form that Build form made; the most redirects; the transport as the controls held it: the HTTP version, and
        // whether Connect to another endpoint is on, with the endpoint as typed; and whether the URL has a '#', which numbers
        // the requests. Both checks of Send read only this, and the client (CheckSend, CheckSequence).
        internal sealed record FrozenSend(
            RequestReplayDraft Draft,
            bool Form,
            int MaxRedirects,
            ReplayHttpVersion HttpVersion,
            bool ConnectTo,
            string Endpoint,
            bool Numbered);

        // What a check of Send found: why nothing is sent, as the status says it (Refusal); otherwise the transport that it
        // parsed and checked, which for a URL with '#' is the one that the check of its numbers takes (CheckSequence), and
        // the plan that it previewed, if any, with the URL's origin that the question names when the plan connects to
        // another endpoint (ConfirmationMessage).
        internal sealed record SendCheck(
            string Refusal, ReplayTransportOptions Transport, ReplayPlanPreview Plan, string ConnectToOrigin)
        {
            internal static SendCheck Refused(string refusal) => new(refusal, null, null, null);
        }

        // Runs a check of Send off the UI thread, as Checking: the window is busy and says that it checks the request, and
        // Cancel request or closing the window stops the check (_checkingCancellation). The window releases its client only
        // once the check has returned (EndState follows the await): the check may still be using it. A stopped check gives
        // no result, and one that it gives once it was stopped is not used (Stopped). Null when nothing is to be sent: the
        // check was stopped, which the status says unless the window closed, or it refused the request, which the status
        // says in the check's words. Otherwise what it found, with the status as it was before the check (shown).
        private async Task<SendCheck> RunSendCheckAsync(Func<CancellationToken, SendCheck> work, string shown)
        {
            using var checking = new CancellationTokenSource();
            CancellationToken token = checking.Token;
            _checkingCancellation = checking;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SEND_CHECKING");
            SendCheck found = null;
            bool stopped;
            try
            {
                found = await Task.Run(() => work(token), token);
            }
            catch (OperationCanceledException) when (checking.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is asked or sent.
            }
            finally
            {
                // A check that ended by the time that the user cancelled asks nothing and sends nothing either.
                stopped = Stopped(checking, _checkingCancellation);
                _checkingCancellation = null;
                EndState();
            }

            if (_closed)
                return null;
            if (stopped)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SEND_PREPARE_CANCELLED");
                return null;
            }

            StatusText.Text = found.Refusal ?? shown;
            return found.Refusal == null ? found : null;
        }

        // The numbers for a URL with '#', which the user chooses on the UI thread, as Preparing, in a dialog that keeps them
        // short; null when nothing is to be sent: the user cancelled, the window closed meanwhile, or the range is not one
        // that a sequence can have, which the status says.
        private ReplaySequence AskSequence()
        {
            string range = null;
            _state = ComposerState.Preparing;
            try
            {
                range = (ChooseSequence ?? (() => SequenceRangeWindow.Ask(this)))();
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                ShowError(exception);
            }
            finally
            {
                EndState();
            }

            if (range == null || _closed)
                return null;
            if (!ReplaySequence.TryParse(range, out ReplaySequence sequence))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SEQUENCE_RANGE_INVALID");
                return null;
            }

            return sequence;
        }

        // Send's first check, off the UI thread, over what Send froze, in the order that its refusals keep: a form whose
        // Content-Type the headers would drop (FormContentTypeDropped); the endpoint of Connect to another endpoint as typed
        // (TryParseTransport); a part that a request file left out, which is never sent as the placeholder that the editors
        // show for it; then, for a URL with '#', that only its path or query has one, with the transport that the check of
        // its numbers takes once the user chose them (CheckSequence); otherwise that the request can be built, with what is
        // wrong worded as building it says it, then the plan, as sending checks it (Previewed). Pure: it reads only what it
        // is given, and the client, which makes an operation of its own for each check. The token is checked before each
        // step and before the result is given, so a stopped check gives none. CreateRequestToSend and Preview take no
        // token: a cancel that comes while either runs waits for it to return, as long as the request makes it.
        internal static SendCheck CheckSend(FrozenSend send, RequestReplayClient client, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(send);
            ArgumentNullException.ThrowIfNull(client);
            if (FormContentTypeDropped(send.Form, send.Draft, cancellationToken))
                return SendCheck.Refused(Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"));
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseTransport(send.HttpVersion, send.ConnectTo, send.Endpoint, out ReplayTransportOptions transport))
                return SendCheck.Refused(ErrorText(Localization.GetLocalizedString("COMPOSER_CONNECT_TO_INVALID")));
            if (send.Draft.Omissions.Any)
                return SendCheck.Refused(ErrorText(IncompleteText(send.Draft.Omissions)));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (send.Numbered)
                {
                    if (!ReplaySequence.IsPathAndQueryTemplate(send.Draft.Url))
                        return SendCheck.Refused(Localization.GetLocalizedString("COMPOSER_SEQUENCE_INVALID"));
                    cancellationToken.ThrowIfCancellationRequested();
                    return new SendCheck(null, transport, null, null);
                }

                using (send.Draft.CreateRequestToSend())
                {
                    // Invalid input is reported, with what is wrong, before the user is asked anything.
                }

                cancellationToken.ThrowIfCancellationRequested();
                OperationResult<ReplayPlanPreview> preview =
                    client.Preview(ReplaySource.FromDraft(send.Draft), send.MaxRedirects, transport);
                return Previewed(preview, send.Draft, null, cancellationToken);
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SendCheck.Refused(ErrorText(exception.Message));
            }
        }

        // The run of a URL with '#' once the user chose its numbers, checked off the UI thread as sending checks it, over the
        // request that Send froze and the transport that its first check parsed (CheckSend), never the controls as they are
        // now: the size of the run before any number is put in the URL, then the widest request and the transport; what is
        // wrong is worded as building the widest request says it (Previewed). Pure, with the checkpoints of CheckSend, and
        // as it, no bound inside Preview.
        internal static SendCheck CheckSequence(
            FrozenSend send,
            ReplaySequence sequence,
            ReplayTransportOptions transport,
            RequestReplayClient client,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(send);
            ArgumentNullException.ThrowIfNull(sequence);
            ArgumentNullException.ThrowIfNull(client);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                OperationResult<ReplayPlanPreview> preview =
                    client.Preview(ReplaySource.FromTemplate(send.Draft, sequence), send.MaxRedirects, transport);
                return Previewed(preview, send.Draft, sequence, cancellationToken);
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SendCheck.Refused(ErrorText(exception.Message));
            }
        }

        // What a preview found, as Send uses it: why the plan is refused, worded here, off the UI thread, since for a sequence
        // that builds the widest request again (PlanError); or the plan, with the URL's origin that the question names when
        // it connects to another endpoint (Origin), so that the window parses nothing to ask.
        private static SendCheck Previewed(
            OperationResult<ReplayPlanPreview> preview,
            RequestReplayDraft draft,
            ReplaySequence sequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCheck found = preview.Succeeded
                ? Planned(preview.Value)
                : SendCheck.Refused(ErrorText(PlanError(FirstDiagnostic(preview.Diagnostics), draft, sequence)));
            cancellationToken.ThrowIfCancellationRequested();
            return found;
        }

        private static SendCheck Planned(ReplayPlanPreview plan) =>
            new(null, plan.Transport, plan, plan.UsesConnectTo ? Origin(plan.Template.Url) : null);

        private static OperationDiagnostic FirstDiagnostic(IReadOnlyList<OperationDiagnostic> diagnostics) =>
            diagnostics.Count > 0 ? diagnostics[0] : null;

        // Why the operation refused a run once it was sent, as the status says it: as PlanError words its first diagnostic,
        // which for a sequence builds the widest request again. SendAsync runs it off the UI thread.
        internal static string RunRefusalText(OperationDiagnostic diagnostic, ReplayPlanPreview plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ErrorText(PlanError(diagnostic, plan.Template, plan.Sequence));
        }

        // Why the operation refused a run: what the composer says itself about Fiddler's control headers, the transport
        // options and a part that a request file left out; for a sequence whose request cannot be built, what building it
        // with the widest number says, as a single send says it; otherwise the operation's message, such as a run that is
        // too large. Building the widest request parses it, so this runs off the UI thread only (Previewed,
        // RunRefusalText).
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
        // URL's host, which it names with the URL's origin as the check of the run found it (connectToOrigin), so that
        // nothing is parsed to ask; and that redirects, when followed, are more requests.
        internal static string ConfirmationMessage(ReplayPlanPreview plan, int maxRedirects, string connectToOrigin)
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
                    "COMPOSER_CONFIRM_CONNECT_TO", plan.Transport.ConnectTo.ToString(), connectToOrigin);
            }

            return maxRedirects == 0
                ? message
                : message + "\n\n" + Localization.GetLocalizedString("COMPOSER_CONFIRM_REDIRECTS", maxRedirects);
        }

        // A URL's scheme, host and port, as the confirmation names the origin of a request that goes to another endpoint,
        // found by the check of the run (Planned), and as Save request as names an origin that its content needs a
        // permission for.
        private static string Origin(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.GetLeftPart(UriPartial.Authority) : url;

        // Says how the run went, from what it did and the words that SendAsync found for them, never parsing anything. One
        // request shows its response and status, or why it has none; a sequence counts its requests and shows the last
        // response; a run that the operation refused says why (refusal, worded off the UI thread by RunRefusalText). Then
        // what the Replays tab did with the sends.
        private void ShowOutcome(
            ReplayPlanPreview plan,
            RequestReplayBatchResult sent,
            string refusal,
            ReplayRecording recording,
            int maxRedirects,
            bool cancelled)
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
                StatusText.Text = WithRecording(
                    refusal ?? ErrorText(Localization.GetLocalizedString("COMPOSER_FAILED")), recording);
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
            // Build form ends with the window: its dialog closes, which stops its work, and the form's parts are released.
            _formDialog?.Close();
            _formSeed = null;
            // The history view ends with the window: its subscription, its queued rebuild and its items do not outlive it.
            HistoryMenu.IsOpen = false;
            CloseHistoryView();
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

        // Shows a request in the editors as loaded: the one that the window opened with, or one dropped on it, opened from a
        // request file, with what that file left out, or loaded from the history; the parts of a form that the body was are
        // forgotten. heldBody is a body larger than the editor shows (MaxShownBodyBytes), which the draft keeps as these
        // exact bytes; source is where it comes from as the window shows it, and heldBodyKey the key of the text that shows
        // it (ShowBody).
        private void LoadDraft(
            RequestReplayDraft draft,
            ReplayProvenance provenance,
            byte[] heldBody = null,
            string source = null,
            string heldBodyKey = null)
        {
            if (heldBody != null)
                draft.SetBodyBytes(heldBody);
            _draft = draft;
            _provenance = provenance;
            _fileBody = heldBody;
            _fileName = heldBody == null ? null : source;
            _fileBodyKey = heldBody == null ? null : heldBodyKey;
            _formSeed = null;
            MethodBox.Text = draft.Method;
            UrlBox.Text = draft.Url;
            HeadersBox.Text = draft.HeadersText;
            ShowBody();
            ShowOmissions();
            _loaded = CurrentEditors();
        }

        // The body editor; or, for an uploaded file or a body larger than the editor shows, from a request file or the
        // Replays tab, where it comes from and the body's size, since the editor would show the text or Base64 of a body
        // that can be 64 MiB, made on the UI thread. Such a body is never shown otherwise.
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
                ? Localization.GetLocalizedString(_fileBodyKey, _fileName, _fileBody.LongLength)
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

        // Copy as cURL and Save as cURL script export one request as it is, for the inbox curl.exe, so neither is available,
        // and each button says why, for a request that still leaves out a part of its request file, which the command would
        // send as its placeholder, or with an option that curl would not apply as XMAT does (UnsupportedCurlOption). Copy as
        // cURL also copies a body of at most 64 KiB: a body that the window holds as bytes, from an uploaded file or a
        // request file, makes it unavailable by its length; any other body is measured when it is prepared, off the UI
        // thread. Availability costs nothing here: nothing is decoded on the UI thread, and RequestCurlExporter.Prepare makes
        // the exact checks, which give the same reasons.
        private void UpdateCurlAvailability()
        {
            bool idle = _state == ComposerState.Idle;
            string notCopied = CurlUnavailableText(copying: true);
            string notSaved = CurlUnavailableText(copying: false);
            CopyCurlButton.IsEnabled = idle && notCopied == null;
            CopyCurlButton.ToolTip = notCopied;
            SaveCurlButton.IsEnabled = idle && notSaved == null;
            SaveCurlButton.ToolTip = notSaved;
        }

        private string CurlUnavailableText(bool copying) =>
            _draft?.Omissions.Any == true
                ? Localization.GetLocalizedString("COMPOSER_CURL_INCOMPLETE", PendingParts(_draft.Omissions))
                : UnsupportedCurlOption() is CurlUnsupported unsupported
                    ? UnsupportedCurlText(unsupported)
                    : copying && _fileBody?.LongLength > RequestCurlExporter.MaxPasteableBodyBytes
                        ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY")
                        : null;

        // The option that makes a cURL export unsupported now, as RequestCurlExporter.Prepare names it: a URL with '#', which
        // sends one request per number; redirects followed, which curl would follow its own way; HTTP/2 only, which the
        // inbox curl.exe does not send; or Connect to another endpoint. Null when there is none: Automatic and HTTP/1.1 only
        // are exported.
        private CurlUnsupported? UnsupportedCurlOption()
        {
            if (UrlBox.Text.Contains(ReplaySequence.Placeholder))
                return CurlUnsupported.NumberedUrl;
            if (FollowRedirectsBox.IsChecked == true)
                return CurlUnsupported.Redirects;
            if (SelectedHttpVersion == ReplayHttpVersion.Http2)
                return CurlUnsupported.Http2Exact;
            return ConnectToBox.IsChecked == true ? CurlUnsupported.ConnectTo : null;
        }

        // Why an option makes the cURL exports unavailable: the same words before and after Prepare.
        private static string UnsupportedCurlText(CurlUnsupported unsupported) =>
            Localization.GetLocalizedString(unsupported switch
            {
                CurlUnsupported.NumberedUrl => "COMPOSER_CURL_NUMBERED_URL",
                CurlUnsupported.Redirects => "COMPOSER_CURL_REDIRECTS",
                CurlUnsupported.Http2Exact => "COMPOSER_CURL_HTTP2",
                CurlUnsupported.ConnectTo => "COMPOSER_CURL_CONNECT_TO",
                _ => "COMPOSER_CURL_UNSUPPORTED",
            });

        // Automatic unless HTTP/1.1 only or HTTP/2 only is chosen.
        private ReplayHttpVersion SelectedHttpVersion =>
            HttpVersionBox.SelectedItem is ComboBoxItem { Tag: ReplayHttpVersion version } ? version : ReplayHttpVersion.Auto;

        // The transport that the controls choose now, or false when Connect to another endpoint is on with text that is not
        // an IP address and port (TryParseTransport). The cURL exports read it so; Send freezes the controls as they are and
        // parses them off the UI thread (CheckSend). A run sends with the transport that its preview checked, never the
        // controls afterwards.
        private bool TryReadTransport(out ReplayTransportOptions transport) =>
            TryParseTransport(SelectedHttpVersion, ConnectToBox.IsChecked == true, ConnectToText.Text, out transport);

        // The transport from what the controls held: the HTTP version, and with Connect to another endpoint on, the endpoint
        // as typed, trimmed and parsed (ReplayConnectTo.TryParse); false when that text is not an IP address and port. Pure.
        private static bool TryParseTransport(
            ReplayHttpVersion httpVersion, bool connectTo, string endpoint, out ReplayTransportOptions transport)
        {
            ReplayConnectTo target = null;
            if (connectTo && !ReplayConnectTo.TryParse(endpoint?.Trim(), out target))
            {
                transport = null;
                return false;
            }

            transport = new ReplayTransportOptions(httpVersion, target);
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

            // The file dialog runs a message loop, so the window can close while it is open (with its owner, say).
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
                _fileBodyKey = "COMPOSER_BODY_FILE";
                _formSeed = null;
                ShowBody();
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_UPLOADED", name, read.Body.LongLength);
            }
        }

        // Clear body: an empty text body, in place of a body held as bytes, from an uploaded file, a form or a request file,
        // or of the text in the editor. It does not supply a body that a request file left out: Empty body does.
        internal void ClearBody()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            _fileBody = null;
            _fileName = null;
            _formSeed = null;
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

        // Build form: opens the dialog that builds a multipart form for the body (MultipartFormWindow), modal and owned by the
        // window, with the parts of the form that the body still is, if any, or none. There, Use form changes the header
        // text, the body and the parts in one step (CommitForm); nothing else that the dialog does changes the request. The
        // window does nothing else until the dialog closes, and closing the window closes it.
        internal void BuildForm()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            var dialog = new MultipartFormWindow(MethodBox.Text, _formSeed, () => HeadersBox.Text, CommitForm);
            _formDialog = dialog;
            _state = ComposerState.Preparing;
            dialog.Closed += FormDialog_Closed;
            (ShowFormDialog ?? ShowOwnedFormDialog)(dialog);
        }

        private void ShowOwnedFormDialog(MultipartFormWindow dialog)
        {
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private void FormDialog_Closed(object sender, EventArgs e)
        {
            _formDialog = null;
            EndState();
        }

        // Use form, in the dialog: the window takes the form in one step: the header text as the dialog prepared it, with the
        // form's Content-Type in place of every Content-Type line; the body, which the window holds as the form's exact bytes
        // and shows by its parts and size, as it does an uploaded file; and the parts, to build the form from again. It is an
        // edit, not a load (LoadDraft): the method, the URL, the transport options, Inspect session, the response, where the
        // request comes from and its origin stay as they are, and so do the parts that a request file left out, which only
        // their own buttons supply. Open request and Load then ask before they replace the request.
        private void CommitForm(MultipartFormBody form, string headersText)
        {
            HeadersBox.Text = headersText;
            _draft.SetBodyBytes(form.Body);
            _fileBody = form.Body;
            _fileName = form.Parts.Count == 1
                ? Localization.GetLocalizedString("COMPOSER_FORM_ONE_PART")
                : Localization.GetLocalizedString("COMPOSER_FORM_PARTS", form.Parts.Count);
            _fileBodyKey = "COMPOSER_BODY_FORM";
            _formSeed = form.Parts;
            ShowBody();
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_FORM_USED", _fileName, form.Body.LongLength);
        }

        // Whether building this request would drop the Content-Type of a form that Build form made (form): its Connection
        // header nominates Content-Type (RequestReplayDraft.IsNominatedByConnection). Header text that is not valid is not
        // this refusal: the action refuses it in its own words. Pure: it reads only the request and the form flag that it is
        // given, which nothing changes meanwhile, and changes nothing, so it can parse header text of any length off the UI
        // thread: Send's first check makes it first (CheckSend), and Save request and both cURL exports as the first step of
        // their work (RefuseDroppedFormContentType), each before any question or output. The token is checked before the
        // headers are parsed and again before the result is given: a cancelled check gives none.
        internal static bool FormContentTypeDropped(bool form, RequestReplayDraft draft, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            cancellationToken.ThrowIfCancellationRequested();
            bool dropped = form && NominatesContentType(draft);
            cancellationToken.ThrowIfCancellationRequested();
            return dropped;
        }

        // Whether the request's Connection header nominates Content-Type; false for header text that is not valid.
        private static bool NominatesContentType(RequestReplayDraft draft)
        {
            IReadOnlyList<KeyValuePair<string, string>> headers;
            try
            {
                headers = draft.GetHeaders();
            }
            catch (FormatException)
            {
                return false;
            }

            return RequestReplayDraft.IsNominatedByConnection(headers, "Content-Type");
        }

        // The first step of the work that Save request and both cURL exports run off the UI thread, over the request and the
        // form flag that they froze: a form's Content-Type that the headers would drop (CheckFormContentType, by default
        // FormContentTypeDropped) refuses the action, as content-type-dropped, before anything is inspected, prepared or
        // rendered for it, and so before any question or output; the window says so once the work has ended. It changes no
        // header.
        private static void RefuseDroppedFormContentType(
            bool form,
            RequestReplayDraft draft,
            Func<bool, RequestReplayDraft, CancellationToken, bool> dropsContentType,
            CancellationToken cancellationToken)
        {
            if (dropsContentType(form, draft, cancellationToken))
                throw new MultipartFormException(MultipartCodes.ContentTypeDropped);
        }

        // Open request: loads a request file in place of the request in the editors, after asking when the user has edited
        // that one or the transport options, which opening resets (EditorState); the answer is not a permission to send, and
        // declining changes nothing. The file is read and made into a request off the UI thread, and the read stops when the
        // window closes or the user cancels it; a file that cannot be read changes nothing, nor does a file read by the time
        // of the cancel. What the file left out stays to be supplied (ShowOmissions). A body larger than the editor shows is
        // kept as its exact bytes and shown by its size (HeldBody). A file carries no transport but its HTTP version and
        // grants no permission: Connect to another endpoint and Follow same-origin redirects go back to off, and a send asks
        // for every permission that it needs, as always. Only the file's name is shown or kept, never its path. Nothing is
        // sent, and the previous response is cleared, so it cannot pass for the new request's.
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
            LoadDraft(
                draft, ReplayProvenance.FromRequestFile(name), HeldBody(opened.Content), name, "COMPOSER_BODY_REQUEST_FILE");
            ClearResponse();
            StatusText.Text = draft.Omissions.Any
                ? Localization.GetLocalizedString("COMPOSER_OPENED_INCOMPLETE", name, PendingParts(draft.Omissions))
                : Localization.GetLocalizedString("COMPOSER_OPENED", name);
        }

        // A request file carries the HTTP version and no other transport, and Load from the history none at all, which is
        // Automatic: Connect to another endpoint goes back to off, without an endpoint, and Follow same-origin redirects to
        // off, with at most 10 when it is selected again. Inspect session stays as it is.
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
                _formSeed = null;
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
        // in place, with the HTTP version chosen now, and whether the body is a form that Build form made. Off the UI thread,
        // a form's Content-Type that the headers would drop refuses the save first, whether as a template or with the
        // content, before anything is inspected or asked (RefuseDroppedFormContentType); then checking that copy and
        // recognizing what it holds, which can read a body of 64 MiB (InspectForSaving). The window then asks where, then
        // how: as a template, which keeps only the request's shape, unless the user includes the captured content for this
        // save. The content needs every part that a request file left out, and its own permission when the URL goes to
        // another origin than the request was captured or loaded for. The file is written off the UI thread too, privately
        // and never in place of another file. The window is busy while it checks or writes, and closing it or Cancel request
        // stops either: a check stopped even once it ended asks nothing and saves nothing. The request in the editors is
        // never changed, whatever happens.
        internal async Task SaveRequestAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;

            UpdateDraft();
            RequestReplayDraft draft = _draft.WithUrl(_draft.Url);
            bool form = _formSeed != null;
            ReplayHttpVersion httpVersion = SelectedHttpVersion;
            Func<bool, RequestReplayDraft, CancellationToken, bool> dropsContentType =
                CheckFormContentType ?? FormContentTypeDropped;
            Func<RequestReplayDraft, CancellationToken, RequestFileRecognized> inspect =
                InspectRequest ?? InspectForSaving;
            using var checking = new CancellationTokenSource();
            CancellationToken checkingToken = checking.Token;
            Func<RequestFileRecognized> examine = () =>
            {
                RefuseDroppedFormContentType(form, draft, dropsContentType, checkingToken);
                return inspect(draft, checkingToken);
            };
            _checkingCancellation = checking;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_CHECKING");
            RequestFileRecognized recognized = null;
            string refusal = null;
            bool stopped;
            try
            {
                recognized = await Task.Run(examine, checkingToken);
            }
            catch (OperationCanceledException) when (checking.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is saved.
            }
            catch (MultipartFormException exception) when (exception.Code == MultipartCodes.ContentTypeDropped)
            {
                refusal = Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED");
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or RequestFileException)
            {
                refusal = Localization.GetLocalizedString("COMPOSER_SAVE_INVALID", exception.Message);
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
                StatusText.Text = stopped || refusal == null
                    ? Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED")
                    : refusal;
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

            // A drop keeps the transport options as they are, so changing them is not an edit that it loses. Nor does it set
            // them: they stay compared with those that the window, the last request file or the last Load set, which Open
            // request and Load reset.
            TransportState loadedTransport = _loaded.Transport;
            if (CurrentEditors() with { Transport = loadedTransport } != _loaded)
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
            _loaded = _loaded with { Transport = loadedTransport };
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

        // History: the requests that the Replays tab recorded in this session (ReplayHistory), newest first, in a menu that
        // lists them from the history as it is when the menu opens, and keeps them current while it is open. Each entry
        // shows its tab and number, time, method, URL and status as the history bounded and escaped them
        // (ReplayHistoryPreview), with Load, unavailable with the reason for a request that cannot be sent again, and Show
        // in Replays, offered for every entry; then Clear history. An item's tag is its entry and nothing else, and what it
        // shows, as its text, tooltip and automation name, is those previews or a localized reason, never the request
        // itself. Every action finds its entry's row again when it is taken (ReplayHistory.TryResolve), whatever the menu
        // still shows.
        internal void OpenHistoryView()
        {
            ReplayHistory history = _recording?.History;
            if (_closed || _historyOpen || history == null)
                return;
            _historyOpen = true;
            // Observed before it is read, so that no change in between goes unseen.
            _historyObservation = history.Observe(HistoryChanged);
            ShowHistory(history.Snapshot());
        }

        // Closing the menu, or the window, ends the view: it stops observing the history, aborts the rebuild that a change
        // queued, makes any work queued for this opening stale, and keeps none of its items, tags, tooltips or handlers.
        internal void CloseHistoryView()
        {
            StopHistoryRebuild();
            _historyObservation?.Dispose();
            _historyObservation = null;
            _historyOpen = false;
            HistoryMenu.Items.Clear();
        }

        private void StopHistoryRebuild()
        {
            _historyGeneration++;
            _historyRebuild?.Abort();
            _historyRebuild = null;
        }

        // What the history calls, on the UI thread, when it changes while the view is open. It only queues a rebuild, at
        // background priority, at most one however many changes come before it runs, and never rebuilds within the change.
        private void HistoryChanged()
        {
            if (!_historyOpen || _historyRebuild != null)
                return;
            int generation = _historyGeneration;
            Action rebuild = () => RebuildHistory(generation);
            _historyRebuild = Dispatcher.BeginInvoke(DispatcherPriority.Background, rebuild);
        }

        // A queued rebuild does nothing once the view that queued it has closed, even if it has opened again since; otherwise
        // it shows the history as it is when the rebuild runs, never as it was when the rebuild was queued.
        private void RebuildHistory(int generation)
        {
            if (generation != _historyGeneration || !_historyOpen)
                return;
            _historyRebuild = null;
            ShowHistory(_recording.History.Snapshot());
        }

        // The menu's items: the entries, newest first, then Clear history; or that the history is empty.
        private void ShowHistory(IReadOnlyList<ReplayHistoryPreview> previews)
        {
            ItemCollection items = HistoryMenu.Items;
            items.Clear();
            if (previews.Count == 0)
            {
                items.Add(new MenuItem
                {
                    Name = "HistoryEmpty",
                    Header = Localization.GetLocalizedString("COMPOSER_HISTORY_EMPTY"),
                    IsEnabled = false,
                });
                return;
            }

            foreach (ReplayHistoryPreview preview in previews)
                items.Add(HistoryItem(preview));
            items.Add(new Separator());
            items.Add(HistoryAction("HistoryClear", "COMPOSER_HISTORY_CLEAR", null, HistoryClear_Click));
        }

        // An entry, which shows its previews. Its Load is unavailable, and says why, for a request that cannot be sent again;
        // its Show in Replays is offered whatever Load can do.
        private MenuItem HistoryItem(ReplayHistoryPreview preview)
        {
            string shown = Localization.GetLocalizedString(
                "COMPOSER_HISTORY_ITEM",
                preview.TabNamePreview,
                preview.RequestNumber,
                preview.RequestTime,
                preview.MethodPreview,
                preview.UrlPreview,
                string.IsNullOrEmpty(preview.StatusPreview)
                    ? Localization.GetLocalizedString("COMPOSER_HISTORY_NO_RESPONSE")
                    : Localization.GetLocalizedString("COMPOSER_HISTORY_STATUS", preview.StatusPreview));
            // A text block as the header, not text, in which the menu would take an underscore for an access key; a long
            // preview is cut short there, and shown whole in the tooltip.
            var header = new TextBlock { Text = shown, MaxWidth = 640, TextTrimming = TextTrimming.CharacterEllipsis };
            var item = new MenuItem { Header = header, ToolTip = shown, Tag = preview.Entry };
            AutomationProperties.SetName(item, shown);
            MenuItem load = HistoryAction("HistoryLoad", "COMPOSER_HISTORY_LOAD", preview.Entry, HistoryLoad_Click);
            string loadHelp = Localization.GetLocalizedString(
                preview.CanLoad ? "COMPOSER_HISTORY_LOAD_HINT" : "COMPOSER_HISTORY_CANNOT_LOAD");
            load.IsEnabled = preview.CanLoad;
            load.ToolTip = loadHelp;
            ToolTipService.SetShowOnDisabled(load, true);
            AutomationProperties.SetHelpText(load, loadHelp);
            item.Items.Add(load);
            item.Items.Add(HistoryAction("HistoryShow", "COMPOSER_HISTORY_SHOW", preview.Entry, HistoryShow_Click));
            return item;
        }

        // An item that acts on an entry, or on the whole history (entry null); its tag is the entry and nothing else.
        private static MenuItem HistoryAction(string name, string key, ReplayHistoryEntry entry, RoutedEventHandler click)
        {
            var item = new MenuItem { Name = name, Header = Localization.GetLocalizedString(key), Tag = entry };
            // Handled by the item itself: the menu closes, and lets go of its items, before their Click arrives.
            item.Click += click;
            return item;
        }

        // Load: loads the request of a history entry in place of the request in the editors, as Edit and resend loads a
        // captured request: from the entry's row in the Replays tab, never from what the menu shows. Refused while the window
        // is busy, and for an entry that is no longer in the Replays tab or in the history, or a request that cannot be sent
        // again. The request is prepared off the UI thread (RequestReplayDraft.FromCapture) while the window is busy; Cancel
        // request or closing the window stops that, and a request prepared by then is neither asked about nor loaded. A body
        // larger than the editor shows stays its exact bytes, shown by their size (MaxShownBodyBytes). The entry's row is
        // found again once the request is prepared, and again just before it is loaded, after asking, when the user has
        // edited the request or the transport options (EditorState), whether to replace them: No by default, and no
        // permission to send. Loading resets the transport options before the request becomes the editors' baseline, keeps
        // Inspect session, clears the previous response and sends nothing: a send or an export then asks for every
        // permission that it needs, as always. A load that does not happen leaves the request, its transport options and the
        // response as they were.
        internal async Task LoadFromHistoryAsync(ReplayHistoryEntry entry)
        {
            if (_closed)
                return;
            if (_state is not ComposerState.Idle)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_BUSY");
                return;
            }

            ReplayHistory history = _recording?.History;
            if (history == null || !history.TryResolve(entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_GONE");
                return;
            }

            if (!RequestReplayDraft.CanReplay(row))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_UNSUPPORTED");
                return;
            }

            // The entry as the window names it: its tab's name and its request's number.
            string source = tab.DeviceName;
            long number = row.RequestNumber;
            Func<ProxyConnectionModel, int, CancellationToken, RequestReplayDraft> prepare =
                PrepareHistoryDraft ?? RequestReplayDraft.FromCapture;
            using var loading = new CancellationTokenSource();
            CancellationToken token = loading.Token;
            _readingCancellation = loading;
            _state = ComposerState.ReadingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_LOADING", source, number);
            RequestReplayDraft prepared = null;
            byte[] held = null;
            string failure = null;
            bool stopped;
            try
            {
                (prepared, held) = await Task.Run(() => PrepareFromHistory(prepare, row, token), token);
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested)
            {
                // Cancelled, or the window closed: the request stays as it was.
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                failure = exception.Message;
            }
            finally
            {
                // A request prepared by the time that the user cancelled is not loaded either, and nothing is asked about it.
                stopped = Stopped(loading, _readingCancellation);
                _readingCancellation = null;
                EndState();
            }

            if (_closed)
                return;
            if (stopped || prepared == null)
            {
                StatusText.Text = stopped || failure == null
                    ? Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_CANCELLED")
                    : Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_FAILED", failure);
                return;
            }

            // The entry can have left the Replays tab or the history while its request was prepared.
            if (!history.TryResolve(entry, out _, out _))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_GONE");
                return;
            }

            if (CurrentEditors() != _loaded)
            {
                bool replace;
                _state = ComposerState.Preparing;
                try
                {
                    replace = _confirm(Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_HISTORY", source, number));
                }
                finally
                {
                    EndState();
                }

                if (_closed)
                    return;
                if (!replace)
                {
                    StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_CANCELLED");
                    return;
                }
            }

            // Or while the user was asked.
            if (!history.TryResolve(entry, out tab, out row))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_GONE");
                return;
            }

            // The transport options are reset first, so that the request's baseline holds them as reset.
            ResetTransport(ReplayHttpVersion.Auto);
            LoadDraft(
                prepared,
                ReplayProvenance.Of(ReplaySourceKind.Edited, tab.DeviceName, row),
                held,
                Localization.GetLocalizedString("COMPOSER_HISTORY_ROW", source, number),
                "COMPOSER_BODY_REPLAYS_ROW");
            ClearResponse();
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_LOADED", source, number);
        }

        // Makes a row of the Replays tab into the request that Load loads, off the UI thread: a body larger than the editor
        // shows comes back as its exact bytes (held), of which nothing else is made.
        private static (RequestReplayDraft Draft, byte[] Held) PrepareFromHistory(
            Func<ProxyConnectionModel, int, CancellationToken, RequestReplayDraft> prepare,
            ProxyConnectionModel row,
            CancellationToken token)
        {
            RequestReplayDraft draft = prepare(row, MaxShownBodyBytes, token);
            return (draft, draft.GetBodyLength() > MaxShownBodyBytes ? draft.GetBodyBytes() : null);
        }

        // Show in Replays: selects the entry's request in its tab, by reference (ReplayRecordingTarget.TrySelect), whether or
        // not Load can load it. Refused, and said so, for an entry that is no longer in the Replays tab or in the history,
        // and once XMAT is shutting down.
        internal void ShowInReplays(ReplayHistoryEntry entry)
        {
            ReplayHistory history = _recording?.History;
            if (_closed || history == null)
                return;
            if (!history.TryResolve(entry, out ICaptureDeviceContext tab, out ProxyConnectionModel row) ||
                !_recording.TrySelect(tab, row))
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_SHOW_GONE");
                return;
            }

            StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_SHOWN", tab.DeviceName, row.RequestNumber);
        }

        // Clear history: forgets every entry, for every composer, whose open menus then say that the history is empty. The
        // Replays tab keeps its requests, and a request that Load loaded stays as it is.
        internal void ClearHistory()
        {
            ReplayHistory history = _recording?.History;
            if (_closed || history == null)
                return;
            history.Clear();
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_HISTORY_CLEARED");
        }

        private EditorState CurrentEditors() =>
            new(MethodBox.Text, UrlBox.Text, HeadersBox.Text, BodyBox.Text, Base64Box.IsChecked == true, _fileBody,
                _draft.Omissions,
                new TransportState(
                    HttpVersion: SelectedHttpVersion,
                    ConnectTo: ConnectToBox.IsChecked == true,
                    Endpoint: ConnectToText.Text,
                    FollowRedirects: FollowRedirectsBox.IsChecked == true,
                    RedirectLimit: RedirectLimitBox.SelectedItem as int?));

        // Copy as cURL: one click copies to the clipboard a command for PowerShell 7 that sends the request as the editors
        // hold it, with the inbox curl.exe (RequestCurlExporter, CurlScriptForm.Pasteable); the warning beside the button
        // says beforehand that it copies the full request, credentials and body included. The request and its options are
        // fixed first (TryFreezeForCurl). Off the UI thread, while the window is busy, a form's Content-Type that the headers
        // would drop refuses the copy first (RefuseDroppedFormContentType); then the request is prepared, its body held to 64
        // KiB, beyond which Save as cURL script saves it, and the command rendered. Cancel request or closing the window stops
        // that work, and a command made by then is not copied. Only then, for a URL that goes to another origin than the
        // request was captured or loaded for, the user is asked whether to export it for that origin (AllowsCurlOrigin).
        // Nothing ever clears the clipboard: a command that is not copied leaves it as it was.
        internal async Task CopyAsCurlAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            if (!TryFreezeForCurl(
                copying: true,
                out RequestReplayDraft draft,
                out ReplayTransportOptions transport,
                out int redirects,
                out bool form))
            {
                return;
            }

            Func<bool, RequestReplayDraft, CancellationToken, bool> dropsContentType =
                CheckFormContentType ?? FormContentTypeDropped;
            Func<RequestReplayDraft, ReplayTransportOptions, int, CancellationToken, CurlRequest> prepare =
                PrepareCurl ?? RequestCurlExporter.Prepare;
            Action<CurlRequest, CurlScriptForm, string, Stream, CancellationToken> write =
                WriteCurlScript ?? RequestCurlExporter.Write;
            using var rendering = new CancellationTokenSource();
            CancellationToken token = rendering.Token;
            // Checked for a form's Content-Type, prepared, held to what the clipboard takes and rendered off the UI thread: no
            // command for a larger body.
            Func<string> render = () =>
            {
                RefuseDroppedFormContentType(form, draft, dropsContentType, token);
                CurlRequest request = prepare(draft, transport, redirects, token);
                if (request.Body.Length > RequestCurlExporter.MaxPasteableBodyBytes)
                    return null;
                using var rendered = new MemoryStream();
                write(request, CurlScriptForm.Pasteable, null, rendered, token);
                return StrictUtf8.GetString(rendered.GetBuffer(), 0, checked((int)rendered.Length));
            };
            _checkingCancellation = rendering;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_CURL_PREPARING");
            string command = null;
            string refused = null;
            bool stopped;
            try
            {
                command = await Task.Run(render, token);
            }
            catch (OperationCanceledException) when (rendering.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is copied.
            }
            catch (Exception exception) when (CurlRefusalText(exception, draft.Omissions) is string reason)
            {
                refused = reason;
            }
            finally
            {
                // A command made by the time that the user cancelled is not copied either, and nothing is asked about it.
                stopped = Stopped(rendering, _checkingCancellation);
                _checkingCancellation = null;
                EndState();
            }

            if (_closed)
                return;
            if (stopped || command == null)
            {
                StatusText.Text = stopped
                    ? Localization.GetLocalizedString("COMPOSER_COPY_CURL_CANCELLED")
                    : refused ?? Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY");
                return;
            }

            bool allowed;
            _state = ComposerState.Preparing;
            try
            {
                allowed = AllowsCurlOrigin(draft);
            }
            finally
            {
                EndState();
            }

            if (_closed)
                return;
            if (!allowed)
            {
                StatusText.Text = Localization.GetLocalizedString(
                    "COMPOSER_COPY_CURL_ORIGIN_REFUSED", ShownOrigin(draft.Url));
                return;
            }

            try
            {
                (CopyText ?? Clipboard.SetText)(command);
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_COPIED");
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard, which keeps what it had.
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_COPY_CURL_FAILED");
            }
        }

        // Save as cURL script: saves, without sending it, a PowerShell 7 script that sends the request with the inbox
        // curl.exe, its body inline up to 64 MiB (RequestCurlExporter, CurlScriptForm.File). The request and its options are
        // fixed first (TryFreezeForCurl). Off the UI thread, while the window is busy, a form's Content-Type that the headers
        // would drop refuses the script first (RefuseDroppedFormContentType); then the request is prepared. Cancel request or
        // closing the window stops that work, and a request prepared by then is neither asked about nor saved. The window
        // then asks where; for a URL that goes to another origin than the request was captured or loaded for, whether to
        // export it for that origin (AllowsCurlOrigin); then whether to save the full request at all, No by default, for
        // every script. The script is rendered straight into the file and published off the UI thread, privately and never
        // in place of another file (PrivateFilePublisher). What the publication did is what the status says, also after
        // Cancel request: a script published by then stays published, and closing the window never deletes it or says
        // otherwise.
        internal async Task SaveCurlScriptAsync()
        {
            if (_state != ComposerState.Idle || _closed)
                return;
            if (!TryFreezeForCurl(
                copying: false,
                out RequestReplayDraft draft,
                out ReplayTransportOptions transport,
                out int redirects,
                out bool form))
            {
                return;
            }

            Func<bool, RequestReplayDraft, CancellationToken, bool> dropsContentType =
                CheckFormContentType ?? FormContentTypeDropped;
            Func<RequestReplayDraft, ReplayTransportOptions, int, CancellationToken, CurlRequest> prepare =
                PrepareCurl ?? RequestCurlExporter.Prepare;
            Action<CurlRequest, CurlScriptForm, string, Stream, CancellationToken> write =
                WriteCurlScript ?? RequestCurlExporter.Write;
            using var preparing = new CancellationTokenSource();
            CancellationToken preparingToken = preparing.Token;
            Func<CurlRequest> prepareChecked = () =>
            {
                RefuseDroppedFormContentType(form, draft, dropsContentType, preparingToken);
                return prepare(draft, transport, redirects, preparingToken);
            };
            _checkingCancellation = preparing;
            _state = ComposerState.Checking;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_CURL_PREPARING");
            CurlRequest request = null;
            string refused = null;
            bool stopped;
            try
            {
                request = await Task.Run(prepareChecked, preparingToken);
            }
            catch (OperationCanceledException) when (preparing.IsCancellationRequested)
            {
                // Cancelled, or the window closed: nothing is saved.
            }
            catch (Exception exception) when (CurlRefusalText(exception, draft.Omissions) is string reason)
            {
                refused = reason;
            }
            finally
            {
                // A request prepared by the time that the user cancelled is not saved either, and nothing is asked about it.
                stopped = Stopped(preparing, _checkingCancellation);
                _checkingCancellation = null;
                EndState();
            }

            if (_closed)
                return;
            if (stopped || request == null)
            {
                StatusText.Text = stopped || refused == null
                    ? Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED")
                    : refused;
                return;
            }

            string path;
            bool allowed = false;
            bool consented = false;
            _state = ComposerState.Preparing;
            try
            {
                path = (ChooseCurlScriptPath ?? ChooseCurlScriptPathInDialog)();
                if (!string.IsNullOrEmpty(path) && !_closed)
                {
                    allowed = AllowsCurlOrigin(draft);
                    consented = allowed && !_closed &&
                        _confirm(Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", request.Body.Length));
                }
            }
            finally
            {
                EndState();
            }

            if (string.IsNullOrEmpty(path) || _closed)
                return;
            if (!allowed)
            {
                StatusText.Text = Localization.GetLocalizedString(
                    "COMPOSER_SAVE_CURL_ORIGIN_REFUSED", ShownOrigin(draft.Url));
                return;
            }

            if (!consented)
            {
                StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED");
                return;
            }

            string name = UntrustedText.Show(Path.GetFileName(path), MaxShownFileNameChars);
            Func<string, Action<Stream>, CancellationToken, PrivateFilePublication> publish =
                PublishCurlScript ?? PrivateFilePublisher.Publish;
            using var publishing = new CancellationTokenSource();
            CancellationToken publishingToken = publishing.Token;
            // The script is rendered straight into the file, and published, off the UI thread.
            Func<PrivateFilePublication> save = () => publish(
                path, stream => write(request, CurlScriptForm.File, null, stream, publishingToken), publishingToken);
            PrivateFilePublication published = PrivateFilePublication.Cancelled;
            _writingCancellation = publishing;
            _state = ComposerState.WritingFile;
            SetBusy(true);
            StatusText.Text = Localization.GetLocalizedString("COMPOSER_SAVING", name);
            try
            {
                published = await Task.Run(save, publishingToken);
            }
            catch (OperationCanceledException) when (publishing.IsCancellationRequested)
            {
                // Stopped before the script was begun: nothing was written.
            }
            finally
            {
                _writingCancellation = null;
                EndState();
            }

            // A closed window says nothing more; whatever the publication did stands.
            if (_closed)
                return;
            StatusText.Text = CurlSaveResultText(published, name);
        }

        // Fixes what a cURL export exports, from the editors and options as they are now: a copy of the draft that later
        // edits do not change, which shares the body's text or bytes, never changed in place, with the transport, the most
        // redirects, and whether the body is a form that Build form made, whose Content-Type the export checks off the UI
        // thread first (RefuseDroppedFormContentType). False, with the reason in the status, when the export is not
        // available (CurlUnavailableText), which costs nothing to know.
        private bool TryFreezeForCurl(
            bool copying, out RequestReplayDraft draft, out ReplayTransportOptions transport, out int redirects, out bool form)
        {
            draft = null;
            redirects = 0;
            form = false;
            string unavailable = CurlUnavailableText(copying);
            if (unavailable != null || !TryReadTransport(out transport))
            {
                transport = null;
                StatusText.Text = unavailable ?? UnsupportedCurlText(CurlUnsupported.ConnectTo);
                return false;
            }

            UpdateDraft();
            draft = _draft.WithUrl(_draft.Url);
            form = _formSeed != null;
            redirects = MaxRedirects;
            return true;
        }

        // Whether the user allows exporting the request for the origin that its URL goes to, when that is another origin
        // than the request was captured or loaded for, naming that origin: asked for every export, No by default, before any
        // question about the content, and never answered by another permission, to send or to save the content. True,
        // without asking, when the origin did not change.
        private bool AllowsCurlOrigin(RequestReplayDraft draft) =>
            !draft.ChangesOrigin ||
            _confirm(Localization.GetLocalizedString("COMPOSER_CURL_ORIGIN", ShownOrigin(draft.Url)));

        // A URL's origin as a question or the status shows it (UntrustedText).
        private static string ShownOrigin(string url) => UntrustedText.Show(Origin(url), MaxShownOriginChars);

        // Why RequestCurlExporter.Prepare, or the check of a form's Content-Type before it (RefuseDroppedFormContentType),
        // refused the request, in the words that the buttons use when they are unavailable for the same reason; null for an
        // exception that is no refusal. pending is what the request still leaves out.
        private static string CurlRefusalText(Exception exception, RequestOmissions pending) => exception switch
        {
            RequestIncompleteException =>
                Localization.GetLocalizedString("COMPOSER_CURL_INCOMPLETE", PendingParts(pending)),
            MultipartFormException { Code: MultipartCodes.ContentTypeDropped } =>
                Localization.GetLocalizedString("COMPOSER_FORM_CONTENT_TYPE_DROPPED"),
            CurlExportException { Code: CurlExportCodes.Unsupported, Reason: CurlUnsupported unsupported } =>
                UnsupportedCurlText(unsupported),
            CurlExportException { Code: CurlExportCodes.HeadBody } =>
                Localization.GetLocalizedString("COMPOSER_CURL_HEAD_BODY"),
            CurlExportException { Code: CurlExportCodes.ArgumentsTooLarge } =>
                Localization.GetLocalizedString("COMPOSER_CURL_TOO_LONG"),
            CurlExportException { Code: ReplayOperation.ControlHeaderCode } =>
                Localization.GetLocalizedString("COMPOSER_CURL_CONTROL_HEADER"),
            CurlExportException or FormatException or ArgumentException =>
                Localization.GetLocalizedString("COMPOSER_CURL_INVALID", exception.Message),
            _ => null,
        };

        // Where to save a cURL script. The dialog never offers to replace a file, and the publisher never does (Exists).
        private string ChooseCurlScriptPathInDialog()
        {
            var dialog = new SaveFileDialog
            {
                Title = Localization.GetLocalizedString("COMPOSER_SAVE_CURL_TITLE"),
                Filter = Localization.GetLocalizedString("COMPOSER_SAVE_CURL_FILTER"),
                FileName = SuggestedCurlScriptName,
                DefaultExt = "ps1",
                AddExtension = true,
                OverwritePrompt = false,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        // What became of a cURL script, by its name only: only Published wrote it, and Exists replaced nothing.
        private static string CurlSaveResultText(PrivateFilePublication published, string name) => published switch
        {
            PrivateFilePublication.Published => Localization.GetLocalizedString("COMPOSER_SAVED_CURL", name),
            PrivateFilePublication.Exists => Localization.GetLocalizedString("COMPOSER_SAVE_EXISTS", name),
            PrivateFilePublication.Unsupported => Localization.GetLocalizedString("COMPOSER_SAVE_UNSUPPORTED"),
            PrivateFilePublication.Cancelled => Localization.GetLocalizedString("COMPOSER_SAVE_CANCELLED"),
            PrivateFilePublication.CleanupFailed => Localization.GetLocalizedString("COMPOSER_SAVE_CLEANUP_FAILED"),
            _ => Localization.GetLocalizedString("COMPOSER_SAVE_FAILED"),
        };

        private async void CopyCurl_Click(object sender, RoutedEventArgs e) => await CopyAsCurlAsync();
        private async void SaveCurl_Click(object sender, RoutedEventArgs e) => await SaveCurlScriptAsync();

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
        private async void UploadFile_Click(object sender, RoutedEventArgs e) => await UploadBodyAsync();
        private void BuildForm_Click(object sender, RoutedEventArgs e) => BuildForm();
        private async void OpenRequest_Click(object sender, RoutedEventArgs e) => await OpenRequestAsync();
        private async void SaveRequest_Click(object sender, RoutedEventArgs e) => await SaveRequestAsync();
        private void ClearBody_Click(object sender, RoutedEventArgs e) => ClearBody();
        private void CancelSend_Click(object sender, RoutedEventArgs e) => CancelSend();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // History opens its menu above the button, listed first so that it opens at its size; a right-click on the button
        // opens it too, and lists it as it opens (HistoryMenu_Opened).
        private void History_Click(object sender, RoutedEventArgs e)
        {
            OpenHistoryView();
            HistoryMenu.PlacementTarget = HistoryButton;
            HistoryMenu.IsOpen = true;
        }

        private void HistoryMenu_Opened(object sender, RoutedEventArgs e) => OpenHistoryView();
        private void HistoryMenu_Closed(object sender, RoutedEventArgs e) => CloseHistoryView();
        private void HistoryClear_Click(object sender, RoutedEventArgs e) => ClearHistory();

        private async void HistoryLoad_Click(object sender, RoutedEventArgs e) =>
            await LoadFromHistoryAsync((ReplayHistoryEntry)((MenuItem)sender).Tag);

        private void HistoryShow_Click(object sender, RoutedEventArgs e) =>
            ShowInReplays((ReplayHistoryEntry)((MenuItem)sender).Tag);

        private void ShowError(Exception exception) => StatusText.Text = ErrorText(exception.Message);

        private static string ErrorText(string message) => Localization.GetLocalizedString("COMPOSER_ERROR", message);

        private static bool IsInputError(Exception exception) =>
            exception is ArgumentException or FormatException or InvalidOperationException or NotSupportedException;

        // A question in a message box that this window owns, titled as the window.
        private MessageBoxResult ShowMessageBox(
            string message, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) =>
            MessageBox.Show(this, message, Title, buttons, image, defaultResult);

        // What Open request and Load would discard, to tell whether the user has edited it: what the editors hold and the
        // parts that a request file left out and that are not supplied yet, edited since the request was loaded; and the
        // transport options, changed from those that the window, the last request file or the last Load set, also before a
        // drop, which keeps them. Supplying a part is an edit, even as the empty part that the editor already showed. A body
        // that the window holds as bytes, an uploaded file's, a form's, or a large one from a request file or the Replays tab,
        // is compared by reference: it is the same bytes until another upload, form or Clear body.
        private sealed record EditorState(
            string Method, string Url, string Headers, string Body, bool Base64, byte[] FileBody,
            RequestOmissions Omissions, TransportState Transport);

        // The transport options as the user left them, which Open request and Load reset (ResetTransport) and a drop keeps:
        // the HTTP version, which a request file keeps too, and Connect to another endpoint and Follow same-origin
        // redirects, each with what it holds also while it is off: the endpoint as typed and the most redirects.
        private sealed record TransportState(
            ReplayHttpVersion HttpVersion, bool ConnectTo, string Endpoint, bool FollowRedirects, int? RedirectLimit);
    }
}
