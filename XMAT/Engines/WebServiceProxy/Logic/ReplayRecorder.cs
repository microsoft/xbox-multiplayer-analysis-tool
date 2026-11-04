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
    // changes nothing: what the user confirms is what is sent.
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

        // The requests whose method can change server state, as ReplayOperation's policy counts them
        // (RequestReplayDraft.UsesUnsafeMethod).
        internal int UnsafeRequests => Captures.Count(capture => capture.Method is not ("GET" or "HEAD" or "OPTIONS"));

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
    // recorded in and the most bodies that tab keeps; the bodies the tab did not keep and the sends it could not record;
    // and whether the user cancelled the run.
    internal sealed record ReplayRunResult(
        OperationResult<ReplayResult> Result,
        string Destination = null,
        long BodyBudgetBytes = 0,
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
    // that the tab's analysis reads (GetCaptureRecords), and only while the tab is open: a send that finds the tab closed
    // is counted and stops the run. The tab keeps at most BodyBudgetBytes of request and response bodies, its earlier rows
    // included; a send whose bodies do not fit is still recorded, with its status, headers and timing, without those
    // bodies and with a note that says so.
    internal sealed class ReplayRecorder : IReplayExchangeSink
    {
        internal const string DeviceName = "Replays";
        internal const long DefaultBodyBudgetBytes = 256L * 1024 * 1024;

        private readonly WebServiceDeviceCaptureController _controller;
        private readonly IDataTable _table;
        private readonly Action<Action> _onUiThread;
        private readonly Func<ReplayItemResult, ReplayProvenance> _provenanceOf;
        private readonly Func<bool> _isOpen;
        private readonly Action _stop;
        private long _retainedBodyBytes;
        private long _lastNumber;

        // Created on the UI thread, which onUiThread runs an action on synchronously (UI types pass their dispatcher).
        // isOpen says whether the tab is still open, and stop ends the run once it is not. Replay rows are numbered after
        // the rows the tab already has, whose bodies count against the budget.
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

        // The counts below change on the UI thread; read them there.
        internal long RetainedBodyBytes => _retainedBodyBytes;

        internal int Recorded { get; private set; }

        // Request and response bodies that sends had but their rows do not keep.
        internal int OmittedBodies { get; private set; }

        // Sends that found the tab closed.
        internal int NotRecorded { get; private set; }

        public void Add(ReplayItemResult result, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exchange);
            ReplayProvenance provenance = _provenanceOf(result) ??
                new ReplayProvenance(ReplaySourceKind.Reissued, string.Empty, result.RequestNumber);
            _onUiThread(() => Record(provenance, exchange));
        }

        // A capture row for one send: the request as XMAT built it and the response, with no status when none came, and
        // without the bodies that the tab does not keep.
        internal static ProxyConnectionModel ToRow(
            long number,
            ReplayProvenance provenance,
            ReplayExchange exchange,
            bool keepRequestBody = true,
            bool keepResponseBody = true)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            Uri uri = exchange.RequestUri;
            ReplayMessage request = exchange.Request;
            ReplayMessage response = exchange.Response;
            byte[] requestBody = request?.Body ?? Array.Empty<byte>();
            byte[] responseBody = response?.Body ?? Array.Empty<byte>();
            bool requestOmitted = !keepRequestBody && requestBody.Length != 0;
            bool responseOmitted = !keepResponseBody && responseBody.Length != 0;
            return new ProxyConnectionModel(
                number,
                exchange.StatusCode == 0 ? string.Empty : exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                exchange.Started,
                exchange.Duration,
                uri?.Scheme ?? string.Empty,
                uri?.Authority ?? string.Empty,
                request?.StartLine.Split(' ')[0] ?? string.Empty,
                uri?.PathAndQuery ?? string.Empty,
                Head(request),
                requestOmitted ? Array.Empty<byte>() : requestBody,
                Head(response),
                responseOmitted ? Array.Empty<byte>() : responseBody,
                "0.0.0.0")
            {
                Replay = provenance,
                ReplayNote = (requestOmitted, responseOmitted) switch
                {
                    (true, true) => Localization.GetLocalizedString("REPLAY_NOTE_BODIES"),
                    (true, false) => Localization.GetLocalizedString("REPLAY_NOTE_REQUEST_BODY"),
                    (false, true) => Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_BODY"),
                    _ => null,
                },
            };
        }

        // On the UI thread, which also closes tabs: a row goes into the open tab, or nowhere.
        private void Record(ReplayProvenance provenance, ReplayExchange exchange)
        {
            if (!_isOpen())
            {
                NotRecorded++;
                _stop?.Invoke();
                return;
            }

            bool keepRequest = TryRetain(exchange.Request?.Body.LongLength ?? 0);
            bool keepResponse = TryRetain(exchange.Response?.Body.LongLength ?? 0);
            OmittedBodies += (keepRequest ? 0 : 1) + (keepResponse ? 0 : 1);
            ProxyConnectionModel row = ToRow(_lastNumber + 1, provenance, exchange, keepRequest, keepResponse);
            if (_table != null)
            {
                row.AddToDataTable(_controller.DeviceName, _table);
            }

            _controller.ProxyConnections.Add(row);
            _lastNumber++;
            Recorded++;
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

        // The start line and header lines, as captures store them.
        private static string Head(ReplayMessage message)
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

            return head.ToString();
        }
    }

    // One run's hold on the Replays tab: the recorder that adds the run's rows, and the capture-data operation that keeps
    // the tab from being closed, replaced or analyzed until the run ends. Dispose it once the run has ended.
    internal sealed class ReplayRecording : IDisposable
    {
        private IDisposable _operation;

        internal ReplayRecording(ICaptureDeviceContext destination, ReplayRecorder recorder, IDisposable operation)
        {
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
            Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        internal ICaptureDeviceContext Destination { get; }

        internal ReplayRecorder Recorder { get; }

        public void Dispose() => Interlocked.Exchange(ref _operation, null)?.Dispose();
    }

    // Where the GUI records replays and composer sends: the Replays tab of an application model, the tab that the replay
    // feature created and keeps by reference (CaptureAppModel.GetOrAddReplayContext), never a tab found by its name. A
    // recording holds the model's capture-data operation, so no load, import, analysis or tab close runs while it adds
    // rows, and closing XMAT cancels its run and waits for it to end (CaptureAppModel.CanCloseAsync).
    internal sealed class ReplayRecordingTarget
    {
        private readonly CaptureAppModel _model;
        private readonly Action<Action> _onUiThread;
        private readonly Func<WebServiceDeviceCaptureController, IDataTable> _tableOf;
        private readonly long _bodyBudgetBytes;

        // onUiThread runs an action on the UI thread synchronously: UI types pass their dispatcher. tableOf gives the table
        // that the tab's rows go to; by default the capture method's, which the tab reads.
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

        // False while an analysis or another capture-data operation, such as another replay, is running.
        internal bool CanBegin => _model.CanModifyCaptureData;

        // Starts recording one run, on the UI thread; null when XMAT is busy (CanBegin). Until the recording is disposed,
        // closing XMAT cancels the run through cancellation and waits for the recording to end. show selects the tab.
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
                ICaptureDeviceContext destination = _model.GetOrAddReplayContext(ReplayRecorder.DeviceName);
                var controller = (WebServiceDeviceCaptureController)destination.CaptureController;
                var recorder = new ReplayRecorder(
                    controller,
                    _tableOf(controller),
                    _onUiThread,
                    provenanceOf,
                    () => _model.CaptureDeviceContexts.Contains(destination),
                    cancellation.Cancel,
                    _bodyBudgetBytes);
                if (show)
                {
                    _model.SelectedDeviceContext = destination;
                }

                return new ReplayRecording(destination, recorder, operation);
            }
            catch
            {
                operation.Dispose();
                throw;
            }
        }
    }
}