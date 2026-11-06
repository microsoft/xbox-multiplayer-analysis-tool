// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // What a Replay menu item asks for: the captured rows to reissue, in list order, and how.
    internal sealed record ReissueRequest(
        IReadOnlyList<ProxyConnectionModel> Captures,
        int Repeat = 1,
        bool Unconditional = false,
        bool Verify = false);

    internal enum ReplaySourceKind
    {
        // Sent again as captured, from the Replay menu.
        Reissued,

        // Sent from the composer that Edit and resend opened on a captured request.
        Edited,

        // Composed from nothing, with Actions > Compose a New Request.
        Composed,

        // Opened in the composer from a request file, with Open request; SourceName is the file's name, never its path.
        RequestFile,
    }

    // Where a row of the Replays tab comes from: the tab, request number and table row of the request that it sends again,
    // as they were when the send started; the name of the request file that the request was opened from; or nothing for a
    // composed request. It is kept in memory only.
    internal sealed record ReplayProvenance(
        ReplaySourceKind Kind,
        string SourceName = null,
        long? RequestNumber = null,
        long? RecordRowId = null)
    {
        internal static ReplayProvenance Composed { get; } = new(ReplaySourceKind.Composed);

        internal static ReplayProvenance Of(ReplaySourceKind kind, string sourceName, ProxyConnectionModel row)
        {
            ArgumentNullException.ThrowIfNull(row);
            return new ReplayProvenance(kind, sourceName ?? string.Empty, row.RequestNumber, row.RecordRowId);
        }

        // A request opened from a request file, by the file's name alone.
        internal static ReplayProvenance FromRequestFile(string fileName) =>
            new(ReplaySourceKind.RequestFile, fileName ?? string.Empty);

        // What the request details show, such as "Local PC #12".
        internal string Describe() => Kind switch
        {
            ReplaySourceKind.Reissued =>
                Localization.GetLocalizedString("REPLAY_SOURCE_REISSUED", SourceName, RequestNumber).Trim(),
            ReplaySourceKind.Edited =>
                Localization.GetLocalizedString("REPLAY_SOURCE_EDITED", SourceName, RequestNumber).Trim(),
            ReplaySourceKind.RequestFile => Localization.GetLocalizedString("REPLAY_SOURCE_REQUEST_FILE", SourceName),
            _ => Localization.GetLocalizedString("REPLAY_SOURCE_COMPOSED"),
        };
    }

    // What Reissue and verify found for one send that it verified, as the Replays tab keeps it with the send's row
    // (ReplayRecorder): Verdict, the verdict that ReplayOperation made, which the row keeps whatever else happens; and Diff,
    // the verification differences that came with that verdict (ReplayExchange.VerifyDiff), when the tab kept them. They
    // hold captured content (lines, JSON values, byte excerpts), so they are kept in memory only and never logged, saved
    // or exported. When the tab did not keep them, NotKeptBytes is their size (VerifyDiff.SerializedBytes); BudgetBytes is
    // how much of them the tab keeps. Diff and NotKeptBytes are both null for a send that came without differences.
    internal sealed record RecordedVerification(
        ReplayVerification Verdict,
        VerifyDiff Diff = null,
        int? NotKeptBytes = null,
        long BudgetBytes = VerifyDiffLimits.MaxRetainedBytes);

    // The requests of a Replay menu run, fixed on the UI thread before the user is asked about them: copies of the rows
    // that can be replayed, as they are then, and where each comes from. A live capture that updates a row afterwards
    // changes nothing: what the user confirms is what is sent. The copies share the rows' bodies and header dictionaries,
    // which an update replaces and never changes in place (ProxyConnectionModel.RequestBody).
    internal sealed class ReplaySnapshot
    {
        private ReplaySnapshot(
            ReissueRequest request,
            IReadOnlyList<ProxyConnectionModel> captures,
            IReadOnlyList<ReplayProvenance> provenance)
        {
            Request = request;
            Captures = captures;
            Provenance = provenance;
        }

        internal ReissueRequest Request { get; }

        internal IReadOnlyList<ProxyConnectionModel> Captures { get; }

        // Where each of Captures comes from, by index (ReplayItemResult.SourceIndex).
        internal IReadOnlyList<ReplayProvenance> Provenance { get; }

        // The requests whose method can change server state, as ReplayOperation's policy counts them: any method but exactly
        // GET, HEAD and OPTIONS (RequestReplayDraft.IsUnsafeMethod).
        internal int UnsafeRequests => Captures.Count(capture => RequestReplayDraft.IsUnsafeMethod(capture.Method));

        internal long Sends => (long)Captures.Count * Request.Repeat;

        internal long UnsafeSends => (long)UnsafeRequests * Request.Repeat;

        internal static ReplaySnapshot Of(ReissueRequest request, string sourceName)
        {
            ArgumentNullException.ThrowIfNull(request);
            var captures = new List<ProxyConnectionModel>();
            var provenance = new List<ReplayProvenance>();
            foreach (ProxyConnectionModel row in request.Captures ?? Array.Empty<ProxyConnectionModel>())
            {
                if (RequestReplayDraft.CanReplay(row))
                {
                    captures.Add(new ProxyConnectionModel(row));
                    provenance.Add(ReplayProvenance.Of(ReplaySourceKind.Reissued, sourceName, row));
                }
            }

            return new ReplaySnapshot(request, captures, provenance);
        }

        internal ReplayProvenance ProvenanceOf(ReplayItemResult result) =>
            result?.SourceIndex is int index && index >= 0 && index < Provenance.Count ? Provenance[index] : null;
    }

    // What a GUI replay run did: the operation's result, or none when XMAT was busy and nothing was sent; the tab it was
    // recorded in and the most bodies that tab keeps; the sends that the tab recorded, the bodies it did not keep and the
    // sends it could not record; whether the user cancelled the run; and the verification differences that the tab did
    // not keep, and how much of them it keeps.
    internal sealed record ReplayRunResult(
        OperationResult<ReplayResult> Result,
        string Destination = null,
        long BodyBudgetBytes = 0,
        int Recorded = 0,
        int OmittedBodies = 0,
        int NotRecorded = 0,
        bool CancelledByUser = false,
        int OmittedVerifyDiffs = 0,
        long VerifyDiffBudgetBytes = 0)
    {
        internal static ReplayRunResult Busy { get; } = new(Result: null);

        internal bool WasBusy => Result == null;
    }

    // The GUI's sink for ReplayOperation: each send becomes a row of the read-only Replays tab, with the request XMAT sent
    // and the response it got, so replays can be inspected and analyzed like captured traffic. ReplayOperation calls Add
    // off the UI thread, one send at a time; the row is added on the UI thread, once to the tab's rows and once to the table
    // that the tab's analysis reads (GetCaptureRecords), and only while the tab is open: a send that finds the tab closed,
    // or that the UI thread does not take, as once XMAT is shutting down, is counted and stops the run. The tab keeps at
    // most BodyBudgetBytes of request and response bodies, its earlier rows included; a send whose bodies do not fit is
    // still recorded, with its status, headers and timing, without those bodies, with a note that says so, and with a
    // header in its stored request or response head that says how many bytes were not kept
    // (RequestReplayDraft.RequestBodyNotKeptHeader, ResponseBodyNotKeptHeader), which saved captures and exports keep.
    // A verified send's row keeps its verdict (RecordedVerification), and its verification differences while they fit what
    // is left of VerifyDiffBudgetBytes (counted as VerifyDiff.SerializedBytes, a payload budget, not a measure of memory),
    // its earlier rows included; differences that do not fit are not kept, and the row's note says so, but the send is
    // recorded and its verdict is the same either way.
    internal sealed class ReplayRecorder : IReplayExchangeSink
    {
        internal const long DefaultBodyBudgetBytes = 256L * 1024 * 1024;

        private readonly WebServiceDeviceCaptureController _controller;
        private readonly IDataTable _table;
        private readonly Action<Action> _onUiThread;
        private readonly Func<ReplayItemResult, ReplayProvenance> _provenanceOf;
        private readonly Func<bool> _isOpen;
        private readonly Action _stop;
        private readonly Action<ProxyConnectionModel> _recorded;
        private long _retainedBodyBytes;
        private long _retainedVerifyDiffBytes;
        private long _lastNumber;
        private int _notRecorded;

        // Created on the UI thread, which onUiThread runs an action on synchronously, or not at all once it no longer takes
        // rows (UI types pass their dispatcher). isOpen says, on the UI thread, whether the tab still takes rows, and stop
        // ends the run once it does not. Replay rows are numbered after the rows the tab already has, whose bodies and
        // verification differences count against the budgets. recorded is told, on the UI thread, of each row once it is in
        // the tab and counted, and never of a send that was not recorded (the composer's history, ReplayHistory).
        internal ReplayRecorder(
            WebServiceDeviceCaptureController controller,
            IDataTable table,
            Action<Action> onUiThread,
            Func<ReplayItemResult, ReplayProvenance> provenanceOf,
            Func<bool> isOpen = null,
            Action stop = null,
            long bodyBudgetBytes = DefaultBodyBudgetBytes,
            long verifyDiffBudgetBytes = VerifyDiffLimits.MaxRetainedBytes,
            Action<ProxyConnectionModel> recorded = null)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _table = table;
            _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
            _provenanceOf = provenanceOf ?? throw new ArgumentNullException(nameof(provenanceOf));
            _isOpen = isOpen ?? (() => true);
            _stop = stop;
            _recorded = recorded;
            ArgumentOutOfRangeException.ThrowIfNegative(bodyBudgetBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(verifyDiffBudgetBytes);
            BodyBudgetBytes = bodyBudgetBytes;
            VerifyDiffBudgetBytes = verifyDiffBudgetBytes;
            foreach (ProxyConnectionModel row in controller.ProxyConnections)
            {
                _lastNumber = Math.Max(_lastNumber, row.RequestNumber);
                _retainedBodyBytes += BodyBytes(row);
                _retainedVerifyDiffBytes += row.Verification?.Diff?.SerializedBytes ?? 0;
            }
        }

        // The name of the tab that the rows go to.
        internal string Destination => _controller.DeviceName;

        internal long BodyBudgetBytes { get; }

        internal long VerifyDiffBudgetBytes { get; }

        // The counts below change on the UI thread, but for NotRecorded; read them there once the run has ended.
        internal long RetainedBodyBytes => _retainedBodyBytes;

        internal long RetainedVerifyDiffBytes => _retainedVerifyDiffBytes;

        internal int Recorded { get; private set; }

        // Request and response bodies that sends had but their rows do not keep.
        internal int OmittedBodies { get; private set; }

        // Verification differences that verified sends came with but their rows do not keep.
        internal int OmittedVerifyDiffs { get; private set; }

        // Sends that found the tab closed, or that the UI thread did not take.
        internal int NotRecorded => Volatile.Read(ref _notRecorded);

        // The row that this recorder added last, the exact object in the tab's rows; null until it adds one. A recorder
        // records one run, so this is never a row of an earlier run (ReplayRecordingTarget.TrySelectLastRecorded).
        internal ProxyConnectionModel LastRecorded { get; private set; }

        public void Add(ReplayItemResult result, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exchange);
            ReplayProvenance provenance = _provenanceOf(result) ??
                new ReplayProvenance(ReplaySourceKind.Reissued, string.Empty, result.RequestNumber);
            bool taken = false;
            _onUiThread(() =>
            {
                taken = true;
                Record(provenance, exchange, result);
            });
            if (!taken)
            {
                NotRecordedStop();
            }
        }

        // A capture row for one send: the request as XMAT built it and the response, with no status when none came, and
        // without the bodies that the tab does not keep, which a header in the head and the row's note account for: a body
        // over what is left of the tab's budget, and a response body over the send's response size limit, which never came
        // (TooLarge). The note also says how a send with the composer's transport options went (TransportNote), from what
        // the operation recorded for it (sent), never from the composer's current controls, and, for a verified send, its
        // verdict and whether the tab kept its verification differences (VerifyNote), which the row keeps with it
        // (verification). The row's host and port are the URL's authority as it was sent (ReplayAuthority).
        internal static ProxyConnectionModel ToRow(
            long number,
            ReplayProvenance provenance,
            ReplayExchange exchange,
            bool keepRequestBody = true,
            bool keepResponseBody = true,
            long bodyBudgetBytes = DefaultBodyBudgetBytes,
            ReplayItemResult sent = null,
            RecordedVerification verification = null)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            Uri uri = exchange.RequestUri;
            ReplayMessage request = exchange.Request;
            ReplayMessage response = exchange.Response;
            byte[] requestBody = request?.Body ?? Array.Empty<byte>();
            byte[] responseBody = response?.Body ?? Array.Empty<byte>();
            bool requestOmitted = !keepRequestBody && requestBody.Length != 0;
            bool responseOmitted = !keepResponseBody && responseBody.Length != 0;
            bool responseTooLarge = exchange.Outcome == ReplayOutcome.TooLarge && response != null;
            string requestHead = Head(
                request, requestOmitted ? RequestReplayDraft.RequestBodyNotKeptHeader : null, Bytes(requestBody.LongLength));
            string responseHead = Head(
                response,
                responseOmitted || responseTooLarge ? RequestReplayDraft.ResponseBodyNotKeptHeader : null,
                responseTooLarge ? response.DeclaredLength() : Bytes(responseBody.LongLength));
            long budgetMiB = bodyBudgetBytes / (1024 * 1024);
            string bodiesNote = (requestOmitted, responseOmitted) switch
            {
                (true, true) => Localization.GetLocalizedString(
                    "REPLAY_NOTE_BODIES", requestBody.LongLength, responseBody.LongLength, budgetMiB),
                (true, false) => Localization.GetLocalizedString(
                    "REPLAY_NOTE_REQUEST_BODY", requestBody.LongLength, budgetMiB),
                (false, true) => Localization.GetLocalizedString(
                    "REPLAY_NOTE_RESPONSE_BODY", responseBody.LongLength, budgetMiB),
                _ => null,
            };
            string tooLargeNote = responseTooLarge
                ? Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_TOO_LARGE")
                : null;
            string note = string.Join(
                "\n",
                new[] { bodiesNote, tooLargeNote, TransportNote(sent), VerifyNote(verification) }
                    .Where(line => line != null));
            return new ProxyConnectionModel(
                number,
                exchange.StatusCode == 0 ? string.Empty : exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                exchange.Started,
                exchange.Duration,
                uri?.Scheme ?? string.Empty,
                uri == null ? string.Empty : ReplayAuthority.Ascii(uri),
                request?.StartLine.Split(' ')[0] ?? string.Empty,
                uri?.PathAndQuery ?? string.Empty,
                requestHead,
                requestOmitted ? Array.Empty<byte>() : requestBody,
                responseHead,
                responseOmitted ? Array.Empty<byte>() : responseBody,
                "0.0.0.0")
            {
                Replay = provenance,
                ReplayNote = note.Length == 0 ? null : note,
                Verification = verification,
            };
        }

        // How a send went with the composer's transport options, as the operation recorded it (ReplayItemResult, in the
        // names of ReplayTransportOptions): through another endpoint than the URL's host, and the HTTP version requested
        // and the one that the response came with ("1.1" or "2", shown as HTTP/1.1 or HTTP/2). Null for a send with the
        // default transport, directly to the URL's host with the version chosen automatically; a requested version that
        // replay does not name is shown as Automatic.
        internal static string TransportNote(ReplayItemResult sent)
        {
            bool connectTo = sent?.Transport == ReplayTransportOptions.ConnectToName;
            string requestedName = sent?.HttpVersion ?? ReplayTransportOptions.AutoVersionName;
            if (sent == null || (!connectTo && requestedName == ReplayTransportOptions.AutoVersionName))
            {
                return null;
            }

            ReplayHttpVersion requestedVersion =
                ReplayTransportOptions.TryParseVersion(requestedName, out ReplayHttpVersion parsed)
                    ? parsed
                    : ReplayHttpVersion.Auto;
            string requested = Localization.GetLocalizedString(requestedVersion switch
            {
                ReplayHttpVersion.Http11 => "COMPOSER_HTTP_VERSION_11",
                ReplayHttpVersion.Http2 => "COMPOSER_HTTP_VERSION_2",
                _ => "COMPOSER_HTTP_VERSION_AUTO",
            });
            string version = sent.ResponseVersion == null
                ? Localization.GetLocalizedString("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", requested)
                : Localization.GetLocalizedString("REPLAY_NOTE_HTTP_VERSION", requested, "HTTP/" + sent.ResponseVersion);
            return connectTo ? Localization.GetLocalizedString("REPLAY_NOTE_CONNECT_TO") + " " + version : version;
        }

        // A verified send's verdict, and, when the tab did not keep its verification differences, that it did not; null for
        // a send that was not verified.
        internal static string VerifyNote(RecordedVerification verification)
        {
            if (verification == null)
            {
                return null;
            }

            string verdict = verification.Verdict.Matches
                ? Localization.GetLocalizedString("REPLAY_NOTE_VERIFY_MATCHED")
                : Localization.GetLocalizedString(
                    "REPLAY_NOTE_VERIFY_DIFFERED", VerifyDiffWording.Differences(verification.Verdict));
            return verification.NotKeptBytes is int notKept
                ? verdict + " " + Localization.GetLocalizedString(
                    "REPLAY_NOTE_VERIFY_DIFF_NOT_KEPT", notKept, verification.BudgetBytes / (1024 * 1024))
                : verdict;
        }

        // On the UI thread, which also closes tabs: a row goes into the open tab, or nowhere.
        private void Record(ReplayProvenance provenance, ReplayExchange exchange, ReplayItemResult result)
        {
            if (!_isOpen())
            {
                NotRecordedStop();
                return;
            }

            bool keepRequest = TryRetain(exchange.Request?.Body.LongLength ?? 0);
            bool keepResponse = TryRetain(exchange.Response?.Body.LongLength ?? 0);
            OmittedBodies += (keepRequest ? 0 : 1) + (keepResponse ? 0 : 1);
            ProxyConnectionModel row = ToRow(
                _lastNumber + 1,
                provenance,
                exchange,
                keepRequest,
                keepResponse,
                BodyBudgetBytes,
                result,
                RetainVerification(result, exchange));
            if (_table != null)
            {
                row.AddToDataTable(_controller.DeviceName, _table);
            }

            _controller.ProxyConnections.Add(row);
            _lastNumber++;
            Recorded++;
            LastRecorded = row;
            _recorded?.Invoke(row);
        }

        // A send's verdict, with the verification differences that came with that very verdict while they fit what is left
        // of the budget; null for a send that was not verified. The verdict is the operation's, kept or not.
        private RecordedVerification RetainVerification(ReplayItemResult result, ReplayExchange exchange)
        {
            if (result.Verification == null)
            {
                return null;
            }

            VerifyDiff diff = exchange.VerifyDiff;
            if (diff == null || !ReferenceEquals(diff.Verification, result.Verification))
            {
                return new RecordedVerification(result.Verification, BudgetBytes: VerifyDiffBudgetBytes);
            }

            if (diff.SerializedBytes > VerifyDiffBudgetBytes - _retainedVerifyDiffBytes)
            {
                OmittedVerifyDiffs++;
                return new RecordedVerification(
                    result.Verification, NotKeptBytes: diff.SerializedBytes, BudgetBytes: VerifyDiffBudgetBytes);
            }

            _retainedVerifyDiffBytes += diff.SerializedBytes;
            return new RecordedVerification(result.Verification, diff, BudgetBytes: VerifyDiffBudgetBytes);
        }

        // A send that the tab cannot take is counted, and the run stops: nothing after it would be recorded either.
        private void NotRecordedStop()
        {
            Interlocked.Increment(ref _notRecorded);
            _stop?.Invoke();
        }

        // Keeps a body when it fits what is left of the budget; an empty body always fits.
        private bool TryRetain(long bytes)
        {
            if (bytes == 0)
            {
                return true;
            }

            if (bytes > BodyBudgetBytes - _retainedBodyBytes)
            {
                return false;
            }

            _retainedBodyBytes += bytes;
            return true;
        }

        private static long BodyBytes(ProxyConnectionModel row) =>
            (row.RequestBody?.LongLength ?? 0) + (row.ResponseBody?.LongLength ?? 0);

        // The start line and header lines, as captures store them, then the empty line that ends the head; notKeptHeader,
        // when a body was not kept, adds that header with notKeptValue: the body's size in bytes, or what the response
        // declared it to be.
        private static string Head(ReplayMessage message, string notKeptHeader, string notKeptValue)
        {
            if (message == null)
            {
                return string.Empty;
            }

            var head = new StringBuilder(message.StartLine).Append("\r\n");
            foreach (KeyValuePair<string, string> header in message.Headers.Concat(message.ContentHeaders))
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (notKeptHeader != null)
            {
                head.Append(notKeptHeader).Append(": ").Append(notKeptValue).Append("\r\n");
            }

            return head.Append("\r\n").ToString();
        }

        private static string Bytes(long count) => count.ToString(CultureInfo.InvariantCulture);
    }

    // One run's hold on the Replays tab: the recorder that adds the run's rows, and the capture-data operation that keeps
    // the tab from being closed, replaced or analyzed until the run ends. Dispose it once the run has ended.
    internal sealed class ReplayRecording : IDisposable
    {
        private readonly Func<bool> _isRevoked;
        private IDisposable _operation;

        // isRevoked says whether XMAT revoked the operation (CaptureAppModel.IsCaptureDataOperationRevoked).
        internal ReplayRecording(
            ICaptureDeviceContext destination,
            ReplayRecorder recorder,
            IDisposable operation,
            Func<bool> isRevoked = null)
        {
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
            Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _isRevoked = isRevoked ?? (() => false);
        }

        internal ICaptureDeviceContext Destination { get; }

        internal ReplayRecorder Recorder { get; }

        // XMAT took the run's operation back to close (CaptureAppModel.CancelCaptureDataOperationAsync), also when the user
        // had cancelled the run first: the rows that the run recorded stay, but what the run does once it ends must not
        // change what XMAT shows.
        internal bool IsRevoked => _isRevoked();

        public void Dispose() => Interlocked.Exchange(ref _operation, null)?.Dispose();
    }

    // Where the GUI records replays and composer sends: the Replays tab of an application model, the tab that the replay
    // feature created and keeps by reference (CaptureAppModel.GetOrAddReplayContext), never a tab found by its name. A
    // recording holds the model's capture-data operation, so no load, import, analysis or tab close runs while it adds
    // rows; closing XMAT cancels its run and waits for it to end (CaptureAppModel.CanCloseAsync), and once XMAT exits
    // without that (CaptureAppModel.BeginShutdown), no recording starts and none adds rows.
    internal sealed class ReplayRecordingTarget
    {
        private readonly CaptureAppModel _model;
        private readonly Action<Action> _onUiThread;
        private readonly Func<WebServiceDeviceCaptureController, IDataTable> _tableOf;
        private readonly long _bodyBudgetBytes;
        private readonly long _verifyDiffBudgetBytes;

        // onUiThread runs an action on the UI thread synchronously, or not at all once that thread no longer takes work:
        // UI types pass their dispatcher. tableOf gives the table that the tab's rows go to; by default the capture
        // method's, which the tab reads.
        internal ReplayRecordingTarget(
            CaptureAppModel model,
            Action<Action> onUiThread,
            Func<WebServiceDeviceCaptureController, IDataTable> tableOf = null,
            long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes,
            long verifyDiffBudgetBytes = VerifyDiffLimits.MaxRetainedBytes)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
            _tableOf = tableOf ??
                (controller => (controller.CaptureMethod as WebServiceCaptureMethod)?.WebProxyConnectionsTable);
            ArgumentOutOfRangeException.ThrowIfNegative(bodyBudgetBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(verifyDiffBudgetBytes);
            _bodyBudgetBytes = bodyBudgetBytes;
            _verifyDiffBudgetBytes = verifyDiffBudgetBytes;
        }

        // False while an analysis or another capture-data operation, such as another replay, is running, and once XMAT is
        // shutting down.
        internal bool CanBegin => _model.CanModifyCaptureData && !_model.IsShuttingDown;

        // XMAT is exiting without waiting for replays (CaptureAppModel.BeginShutdown).
        internal bool IsShuttingDown => _model.IsShuttingDown;

        // The rows that this session's Replays tab recorded, which every composer's history shows.
        internal ReplayHistory History => _model.ReplayHistory;

        // Starts recording one run, on the UI thread; null when XMAT is busy or shutting down (CanBegin). Until the
        // recording is disposed, closing XMAT cancels the run through cancellation and waits for the recording to end.
        // show selects the tab.
        internal ReplayRecording TryBegin(
            CancellationTokenSource cancellation,
            Func<ReplayItemResult, ReplayProvenance> provenanceOf,
            bool show = false)
        {
            ArgumentNullException.ThrowIfNull(cancellation);
            ArgumentNullException.ThrowIfNull(provenanceOf);
            if (!_model.TryBeginCancellableCaptureDataOperation(cancellation, out IDisposable operation))
            {
                return null;
            }

            try
            {
                ICaptureDeviceContext destination = _model.GetOrAddReplayContext();
                var controller = (WebServiceDeviceCaptureController)destination.CaptureController;
                var recorder = new ReplayRecorder(
                    controller,
                    _tableOf(controller),
                    OnUiThreadUntilShutdown,
                    provenanceOf,
                    () => !_model.IsShuttingDown && _model.CaptureDeviceContexts.Contains(destination),
                    cancellation.Cancel,
                    _bodyBudgetBytes,
                    _verifyDiffBudgetBytes,
                    row => _model.ReplayHistory.Add(destination, row));
                if (show)
                {
                    _model.SelectedDeviceContext = destination;
                }

                return new ReplayRecording(
                    destination, recorder, operation, () => _model.IsCaptureDataOperationRevoked(operation));
            }
            catch
            {
                operation.Dispose();
                throw;
            }
        }

        // Inspect session: selects, on the UI thread once a run has ended, the row that its recording added last, in the tab
        // that it added it to, by reference; never a row found by its tab's name or by its number, which other tabs can
        // share. Nothing is selected, and false returned, when the run added no row, once XMAT has taken the run back to
        // close (ReplayRecording.IsRevoked) or is shutting down, or once that tab or row is gone. The tab's selection is
        // cleared first, so a view that does not show the row, such as one that filters it out, selects nothing rather than
        // keeping another row.
        internal bool TrySelectLastRecorded(ReplayRecording recording)
        {
            ArgumentNullException.ThrowIfNull(recording);
            ProxyConnectionModel row = recording.Recorder.LastRecorded;
            return row != null && !recording.IsRevoked && TrySelect(recording.Destination, row);
        }

        // Selects row in tab, by reference, as Inspect session does: nothing is selected, and false returned, once XMAT is
        // shutting down, or when tab is no longer one of the application's tabs or row no longer one of its rows. The tab's
        // selection is cleared first, so a view that does not show the row selects nothing rather than keeping another row.
        internal bool TrySelect(ICaptureDeviceContext tab, ProxyConnectionModel row)
        {
            ArgumentNullException.ThrowIfNull(tab);
            ArgumentNullException.ThrowIfNull(row);
            if (_model.IsShuttingDown ||
                !_model.CaptureDeviceContexts.Any(context => ReferenceEquals(context, tab)) ||
                tab.CaptureController is not WebServiceDeviceCaptureController controller ||
                !controller.ProxyConnections.Any(connection => ReferenceEquals(connection, row)))
            {
                return false;
            }

            _model.SelectedDeviceContext = tab;
            controller.SelectedConnectionModel = null;
            controller.SelectedConnectionModel = row;
            return true;
        }

        // Once XMAT is shutting down a send no longer waits for the UI thread, which may be tearing down the tabs and the
        // database: its row is not added.
        private void OnUiThreadUntilShutdown(Action action)
        {
            if (!_model.IsShuttingDown)
            {
                _onUiThread(action);
            }
        }
    }
}