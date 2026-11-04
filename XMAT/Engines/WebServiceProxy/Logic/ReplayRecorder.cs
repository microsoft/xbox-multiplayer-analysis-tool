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
    }

    // Where a row of the Replays tab comes from: the tab, request number and table row of the request that it sends again,
    // as they were when the send started, or nothing for a composed request. It is kept in memory only.
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

        // What the request details show, such as "Local PC #12".
        internal string Describe() => Kind switch
        {
            ReplaySourceKind.Reissued =>
                Localization.GetLocalizedString("REPLAY_SOURCE_REISSUED", SourceName, RequestNumber).Trim(),
            ReplaySourceKind.Edited =>
                Localization.GetLocalizedString("REPLAY_SOURCE_EDITED", SourceName, RequestNumber).Trim(),
            _ => Localization.GetLocalizedString("REPLAY_SOURCE_COMPOSED"),
        };
    }

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
    // sends it could not record; and whether the user cancelled the run.
    internal sealed record ReplayRunResult(
        OperationResult<ReplayResult> Result,
        string Destination = null,
        long BodyBudgetBytes = 0,
        int Recorded = 0,
        int OmittedBodies = 0,
        int NotRecorded = 0,
        bool CancelledByUser = false)
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
    internal sealed class ReplayRecorder : IReplayExchangeSink
    {
        internal const long DefaultBodyBudgetBytes = 256L * 1024 * 1024;

        private readonly WebServiceDeviceCaptureController _controller;
        private readonly IDataTable _table;
        private readonly Action<Action> _onUiThread;
        private readonly Func<ReplayItemResult, ReplayProvenance> _provenanceOf;
        private readonly Func<bool> _isOpen;
        private readonly Action _stop;
        private long _retainedBodyBytes;
        private long _lastNumber;
        private int _notRecorded;

        // Created on the UI thread, which onUiThread runs an action on synchronously, or not at all once it no longer takes
        // rows (UI types pass their dispatcher). isOpen says, on the UI thread, whether the tab still takes rows, and stop
        // ends the run once it does not. Replay rows are numbered after the rows the tab already has, whose bodies count
        // against the budget.
        internal ReplayRecorder(
            WebServiceDeviceCaptureController controller,
            IDataTable table,
            Action<Action> onUiThread,
            Func<ReplayItemResult, ReplayProvenance> provenanceOf,
            Func<bool> isOpen = null,
            Action stop = null,
            long bodyBudgetBytes = DefaultBodyBudgetBytes)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _table = table;
            _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
            _provenanceOf = provenanceOf ?? throw new ArgumentNullException(nameof(provenanceOf));
            _isOpen = isOpen ?? (() => true);
            _stop = stop;
            ArgumentOutOfRangeException.ThrowIfNegative(bodyBudgetBytes);
            BodyBudgetBytes = bodyBudgetBytes;
            foreach (ProxyConnectionModel row in controller.ProxyConnections)
            {
                _lastNumber = Math.Max(_lastNumber, row.RequestNumber);
                _retainedBodyBytes += BodyBytes(row);
            }
        }

        // The name of the tab that the rows go to.
        internal string Destination => _controller.DeviceName;

        internal long BodyBudgetBytes { get; }

        // The counts below change on the UI thread, but for NotRecorded; read them there once the run has ended.
        internal long RetainedBodyBytes => _retainedBodyBytes;

        internal int Recorded { get; private set; }

        // Request and response bodies that sends had but their rows do not keep.
        internal int OmittedBodies { get; private set; }

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
                Record(provenance, exchange);
            });
            if (!taken)
            {
                NotRecordedStop();
            }
        }

        // A capture row for one send: the request as XMAT built it and the response, with no status when none came, and
        // without the bodies that the tab does not keep, which a header in the head and the row's note account for.
        internal static ProxyConnectionModel ToRow(
            long number,
            ReplayProvenance provenance,
            ReplayExchange exchange,
            bool keepRequestBody = true,
            bool keepResponseBody = true,
            long bodyBudgetBytes = DefaultBodyBudgetBytes)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            Uri uri = exchange.RequestUri;
            ReplayMessage request = exchange.Request;
            ReplayMessage response = exchange.Response;
            byte[] requestBody = request?.Body ?? Array.Empty<byte>();
            byte[] responseBody = response?.Body ?? Array.Empty<byte>();
            bool requestOmitted = !keepRequestBody && requestBody.Length != 0;
            bool responseOmitted = !keepResponseBody && responseBody.Length != 0;
            string requestHead = Head(
                request, requestOmitted ? RequestReplayDraft.RequestBodyNotKeptHeader : null, requestBody.LongLength);
            string responseHead = Head(
                response, responseOmitted ? RequestReplayDraft.ResponseBodyNotKeptHeader : null, responseBody.LongLength);
            long budgetMiB = bodyBudgetBytes / (1024 * 1024);
            return new ProxyConnectionModel(
                number,
                exchange.StatusCode == 0 ? string.Empty : exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                exchange.Started,
                exchange.Duration,
                uri?.Scheme ?? string.Empty,
                uri?.Authority ?? string.Empty,
                request?.StartLine.Split(' ')[0] ?? string.Empty,
                uri?.PathAndQuery ?? string.Empty,
                requestHead,
                requestOmitted ? Array.Empty<byte>() : requestBody,
                responseHead,
                responseOmitted ? Array.Empty<byte>() : responseBody,
                "0.0.0.0")
            {
                Replay = provenance,
                ReplayNote = (requestOmitted, responseOmitted) switch
                {
                    (true, true) => Localization.GetLocalizedString(
                        "REPLAY_NOTE_BODIES", requestBody.LongLength, responseBody.LongLength, budgetMiB),
                    (true, false) => Localization.GetLocalizedString(
                        "REPLAY_NOTE_REQUEST_BODY", requestBody.LongLength, budgetMiB),
                    (false, true) => Localization.GetLocalizedString(
                        "REPLAY_NOTE_RESPONSE_BODY", responseBody.LongLength, budgetMiB),
                    _ => null,
                },
            };
        }

        // On the UI thread, which also closes tabs: a row goes into the open tab, or nowhere.
        private void Record(ReplayProvenance provenance, ReplayExchange exchange)
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
                _lastNumber + 1, provenance, exchange, keepRequest, keepResponse, BodyBudgetBytes);
            if (_table != null)
            {
                row.AddToDataTable(_controller.DeviceName, _table);
            }

            _controller.ProxyConnections.Add(row);
            _lastNumber++;
            Recorded++;
            LastRecorded = row;
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
        // when a body was not kept, adds that header with the body's size in bytes.
        private static string Head(ReplayMessage message, string notKeptHeader, long notKeptBytes)
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
                head.Append(notKeptHeader).Append(": ")
                    .Append(notKeptBytes.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            return head.Append("\r\n").ToString();
        }
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

        // onUiThread runs an action on the UI thread synchronously, or not at all once that thread no longer takes work:
        // UI types pass their dispatcher. tableOf gives the table that the tab's rows go to; by default the capture
        // method's, which the tab reads.
        internal ReplayRecordingTarget(
            CaptureAppModel model,
            Action<Action> onUiThread,
            Func<WebServiceDeviceCaptureController, IDataTable> tableOf = null,
            long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
            _tableOf = tableOf ??
                (controller => (controller.CaptureMethod as WebServiceCaptureMethod)?.WebProxyConnectionsTable);
            ArgumentOutOfRangeException.ThrowIfNegative(bodyBudgetBytes);
            _bodyBudgetBytes = bodyBudgetBytes;
        }

        // False while an analysis or another capture-data operation, such as another replay, is running, and once XMAT is
        // shutting down.
        internal bool CanBegin => _model.CanModifyCaptureData && !_model.IsShuttingDown;

        // XMAT is exiting without waiting for replays (CaptureAppModel.BeginShutdown).
        internal bool IsShuttingDown => _model.IsShuttingDown;

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
                    _bodyBudgetBytes);
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
            if (row == null ||
                recording.IsRevoked ||
                _model.IsShuttingDown ||
                !_model.CaptureDeviceContexts.Any(context => ReferenceEquals(context, recording.Destination)) ||
                recording.Destination.CaptureController is not WebServiceDeviceCaptureController controller ||
                !controller.ProxyConnections.Any(connection => ReferenceEquals(connection, row)))
            {
                return false;
            }

            _model.SelectedDeviceContext = recording.Destination;
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