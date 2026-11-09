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
    // composed request. Origin, which the recorder sets for every row from its send (ReplayExchange.ProvenanceOrigin), is
    // the origin that the request was captured or saved for, or first sent to: a request made from the row again, by Load,
    // a drop, Edit and resend or Reissue, keeps it, so that sending it to another origin asks first. It is kept in memory
    // only.
    internal sealed record ReplayProvenance(
        ReplaySourceKind Kind,
        string SourceName = null,
        long? RequestNumber = null,
        long? RecordRowId = null,
        Uri Origin = null)
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
    // that can be replayed, as they are then, where each comes from, and the origin that each row of the Replays tab keeps
    // (ReplayProvenance.Origin). A live capture that updates a row afterwards changes nothing: what the user confirms is
    // what is sent. The copies share the rows' bodies and header dictionaries, which an update replaces and never changes in
    // place (ProxyConnectionModel.RequestBody).
    internal sealed class ReplaySnapshot
    {
        private ReplaySnapshot(
            ReissueRequest request,
            IReadOnlyList<ProxyConnectionModel> captures,
            IReadOnlyList<ReplayProvenance> provenance,
            IReadOnlyList<Uri> capturedOrigins,
            int originChangeRequests)
        {
            Request = request;
            Captures = captures;
            Provenance = provenance;
            CapturedOrigins = capturedOrigins;
            OriginChangeRequests = originChangeRequests;
        }

        internal ReissueRequest Request { get; }

        internal IReadOnlyList<ProxyConnectionModel> Captures { get; }

        // Where each of Captures comes from, by index (ReplayItemResult.SourceIndex).
        internal IReadOnlyList<ReplayProvenance> Provenance { get; }

        // The origin that each of Captures was captured or saved for, or first sent to, by index, as its row of the Replays
        // tab keeps it, which the operation restores before its checks (ReplaySource.FromCaptures): null for a row that keeps
        // none, such as a row of another tab, and the whole list null when no row keeps one.
        internal IReadOnlyList<Uri> CapturedOrigins { get; }

        // The requests whose URL goes to another origin than the one restored for them, with their headers, which the
        // operation sends only with AllowOriginChange (RequestReplayDraft.ChangesOrigin).
        internal int OriginChangeRequests { get; }

        // The requests whose method can change server state, as ReplayOperation's policy counts them: any method but exactly
        // GET, HEAD and OPTIONS (RequestReplayDraft.IsUnsafeMethod).
        internal int UnsafeRequests => Captures.Count(capture => RequestReplayDraft.IsUnsafeMethod(capture.Method));

        internal long Sends => (long)Captures.Count * Request.Repeat;

        internal long UnsafeSends => (long)UnsafeRequests * Request.Repeat;

        internal long OriginChangeSends => (long)OriginChangeRequests * Request.Repeat;

        internal static ReplaySnapshot Of(ReissueRequest request, string sourceName)
        {
            ArgumentNullException.ThrowIfNull(request);
            var captures = new List<ProxyConnectionModel>();
            var provenance = new List<ReplayProvenance>();
            var origins = new List<Uri>();
            int originChanges = 0;
            foreach (ProxyConnectionModel row in request.Captures ?? Array.Empty<ProxyConnectionModel>())
            {
                if (RequestReplayDraft.CanReplay(row))
                {
                    var copy = new ProxyConnectionModel(row);
                    Uri origin = copy.Replay?.Origin;
                    captures.Add(copy);
                    provenance.Add(ReplayProvenance.Of(ReplaySourceKind.Reissued, sourceName, row));
                    origins.Add(origin);
                    if (origin != null && GoesElsewhere(copy, origin))
                    {
                        originChanges++;
                    }
                }
            }

            return new ReplaySnapshot(
                request, captures, provenance, origins.Any(origin => origin != null) ? origins : null, originChanges);
        }

        internal ReplayProvenance ProvenanceOf(ReplayItemResult result) =>
            result?.SourceIndex is int index && index >= 0 && index < Provenance.Count ? Provenance[index] : null;

        // Whether a row goes to another origin than origin, as the operation decides it once it has restored origin
        // (RequestReplayDraft.ChangesOrigin): the destination that the operation makes the row's URL from, an absolute
        // target's or else the row's own authority, zone and all (ReplayDestination.ReadCaptureDestination), so that the
        // count reads no more of a row than that decision does, never its whole target. A row whose destination is malformed
        // or over its size limit counts as none, since Reissue refuses it before its question (CheckCaptureDestinations).
        // The count only decides whether to ask: the operation's own check is the guard.
        private static bool GoesElsewhere(ProxyConnectionModel row, Uri origin)
        {
            CaptureDestination destination = ReplayDestination.ReadCaptureDestination(row);
            return !destination.IsMalformed && !ReplayAuthority.SameOrigin(origin, destination.Authority);
        }
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
    // recorded and its verdict is the same either way. LastItem is the last send that the recorder was given, set before its
    // row is added and whether or not the tab takes it, and LastItemRecorded says whether that send's own row is in the tab.
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
        private ReplayItemResult _lastItem;
        private ProxyConnectionModel _lastItemRow;

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

        // The last send that this recorder was given, with its outcome, code and write state, whether or not the tab took
        // its row; null until a send is given. A run's status is worded from it when the run's last send has no exchange to
        // show, as when the tab could not take that send's row.
        internal ReplayItemResult LastItem => Volatile.Read(ref _lastItem);

        // On the UI thread once the run has ended: whether the row of LastItem, that very send and never an earlier one, is
        // in the tab's rows. A failure before the table took the row leaves it false, and one after the row went into the
        // tab's rows, such as in a handler of those rows, leaves it true.
        internal bool LastItemRecorded =>
            Volatile.Read(ref _lastItemRow) is ProxyConnectionModel row &&
            _controller.ProxyConnections.Any(shown => ReferenceEquals(shown, row));

        public void Add(ReplayItemResult result, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exchange);
            // Before the UI thread adds the row, and whatever then becomes of it: from now on LastItem is this send, and the
            // row of an earlier send is not its row.
            Volatile.Write(ref _lastItemRow, null);
            Volatile.Write(ref _lastItem, result);
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
        // (verification). A raw send's row is the normalized view of its request (RawRequestImage.ToMessage), with the
        // response as read: its note starts with what XMAT wrote of the request (RawWriteNote), then why it got no complete
        // response, if it did not, a response that ended with the connection and response trailers that the row does not
        // keep (RawResponseNotes). The row's host and port are the URL's authority as it was sent (ReplayAuthority), and its
        // request time is when the send began, as this PC's local time like every capture's, though the exchange keeps it in
        // UTC.
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
            var lines = new List<string> { RawWriteNote(sent) };
            lines.AddRange(RawResponseNotes(sent, exchange));
            lines.AddRange(new[] { bodiesNote, tooLargeNote, TransportNote(sent), VerifyNote(verification) });
            string note = string.Join("\n", lines.Where(line => line != null));
            return new ProxyConnectionModel(
                number,
                exchange.StatusCode == 0 ? string.Empty : exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                exchange.Started.ToLocalTime(),
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
                SentRaw = sent?.Raw == true,
                RawRequestWrite = sent?.RequestWrite,
            };
        }

        // What XMAT wrote of a raw send's request, by its write state and never by its outcome: a raw request when XMAT
        // handed every byte to the connection, otherwise an attempted request, whatever the response, with the state's
        // sentence (RawSendWording.WriteSentence), which says nothing of what the server received. Null for other sends.
        private static string RawWriteNote(ReplayItemResult sent)
        {
            if (sent?.Raw != true)
            {
                return null;
            }

            string written = RawSendWording.WriteSentence(sent.RequestWrite, sent.RequestBytesWritten);
            return sent.RequestWrite == RawWriteState.Complete
                ? Localization.GetLocalizedString("REPLAY_NOTE_RAW", written)
                : Localization.GetLocalizedString("REPLAY_NOTE_RAW_ATTEMPTED", written);
        }

        // What a raw send's row cannot show of its response, in order: why it got no complete response, in its code's words
        // and never an exception's (RawSendWording.CodeText), for an outcome other than a response, kept whole or too large;
        // that the response ended when the server closed the connection, so it may be incomplete; and that the response's
        // trailer fields were read and not kept. Nothing for other sends.
        private static IEnumerable<string> RawResponseNotes(ReplayItemResult sent, ReplayExchange exchange)
        {
            if (sent?.Raw != true)
            {
                yield break;
            }

            if (sent.Outcome is not (ReplayOutcome.Completed or ReplayOutcome.TooLarge))
            {
                yield return RawSendWording.CodeText(sent.Code);
            }

            if (sent.ResponseFraming == RawResponseReader.CloseFraming)
            {
                yield return Localization.GetLocalizedString("REPLAY_NOTE_RAW_CLOSE_DELIMITED");
            }

            if (exchange.ResponseTrailerFields > 0)
            {
                yield return Localization.GetLocalizedString("REPLAY_NOTE_RAW_TRAILERS", exchange.ResponseTrailerFields);
            }
        }

        // How a send went with the composer's transport options, as the operation recorded it (ReplayItemResult, in the
        // names of ReplayTransportOptions): through another endpoint than the URL's host, or than the target origin's host
        // for a raw send, or through an upstream proxy, never named; whether a client certificate was configured, never which
        // one and never whether it was sent, which is not recorded; whether Windows authentication was configured, never
        // whether a challenge was answered or with which package, which the label that the request carried does not prove;
        // and the HTTP version requested and the one that the response came with ("1.1" or "2", shown as HTTP/1.1 or
        // HTTP/2), which with Windows authentication is HTTP/1.1 only, as the run recorded it. Null for a send with the
        // default transport, directly to the URL's host with the version chosen automatically and neither credential; a
        // requested version that replay does not name is shown as Automatic.
        internal static string TransportNote(ReplayItemResult sent)
        {
            string transportKey = sent?.Transport switch
            {
                ReplayTransportOptions.ConnectToName => sent.Raw ? "REPLAY_NOTE_RAW_CONNECT_TO" : "REPLAY_NOTE_CONNECT_TO",
                ReplayTransportOptions.ProxyName => "REPLAY_NOTE_PROXY",
                _ => null,
            };
            string requestedName = sent?.HttpVersion ?? ReplayTransportOptions.AutoVersionName;
            bool certificate = sent?.ClientCertificateConfigured == true;
            bool windowsAuth = sent?.WindowsAuthConfigured == true;
            if (sent == null || (!certificate && !windowsAuth &&
                transportKey == null && requestedName == ReplayTransportOptions.AutoVersionName))
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
            if (windowsAuth)
            {
                version = Localization.GetLocalizedString("REPLAY_NOTE_WINDOWS_AUTH") + " " + version;
            }

            if (certificate)
            {
                version = Localization.GetLocalizedString("REPLAY_NOTE_CLIENT_CERT") + " " + version;
            }

            return transportKey == null ? version : Localization.GetLocalizedString(transportKey) + " " + version;
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

        // On the UI thread, which also closes tabs: a row goes into the open tab, or nowhere. Its provenance takes the origin
        // that the send gives (ReplayExchange.ProvenanceOrigin): the request's captured, saved or first origin, which a
        // request made from the row again keeps.
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
                provenance with { Origin = exchange.ProvenanceOrigin },
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

            // The table took the row, which goes into the tab's rows now (LastItemRecorded).
            Volatile.Write(ref _lastItemRow, row);
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

    // The GUI's words for a raw send (slice L), which the composer's status and the Replays tab's rows share: the sentence of
    // each write state (RawWriteState), which every text that names a state uses word for word and none of which says that
    // the server received anything, and the fixed text of each code, which is the core's own message (RawRequest.Message)
    // where it has one. Never an exception's message.
    internal static class RawSendWording
    {
        // The state's own sentence; null for a send without a write state.
        internal static string StateSentence(string requestWrite) => requestWrite switch
        {
            RawWriteState.None => Localization.GetLocalizedString("COMPOSER_RAW_STATE_NONE"),
            RawWriteState.Partial => Localization.GetLocalizedString("COMPOSER_RAW_STATE_PARTIAL"),
            RawWriteState.Uncertain => Localization.GetLocalizedString("COMPOSER_RAW_STATE_UNCERTAIN"),
            RawWriteState.Complete => Localization.GetLocalizedString("COMPOSER_RAW_STATE_COMPLETE"),
            _ => null,
        };

        // The state's sentence and what it allows to say: for partial and uncertain, the bytes that the write calls which
        // completed handed to the connection's stream, never what reached the network or the server; for complete, that this
        // does not prove that the server received them.
        internal static string WriteSentence(string requestWrite, long? bytesWritten) => requestWrite switch
        {
            RawWriteState.Partial or RawWriteState.Uncertain => StateSentence(requestWrite) + " " +
                Localization.GetLocalizedString("COMPOSER_RAW_HANDED", bytesWritten ?? 0),
            RawWriteState.Complete => StateSentence(requestWrite) + " " +
                Localization.GetLocalizedString("COMPOSER_RAW_NO_RECEIPT"),
            _ => StateSentence(requestWrite),
        };

        // What the composer's history shows of a raw send's row after its request (design 5.2, X2): Raw mode with the
        // complete state's sentence when XMAT handed every byte to the connection, and otherwise an attempted request with
        // its state's sentence, which the row keeps without the count of bytes. Null for a row that no raw send recorded,
        // and for one without a write state, which a raw send's row always has.
        internal static string HistoryMarker(bool sentRaw, string requestWrite)
        {
            string sentence = sentRaw ? StateSentence(requestWrite) : null;
            return sentence == null
                ? null
                : Localization.GetLocalizedString(
                    requestWrite == RawWriteState.Complete ? "COMPOSER_HISTORY_RAW" : "COMPOSER_HISTORY_RAW_ATTEMPTED",
                    sentence);
        }

        // Load's status for a raw send's row of the tab named source (design 5.2): that it loaded the row's normalized view
        // in Parsed mode, with the state's sentence, an attempted request's own words when the state is not complete, and
        // that the view can differ from the bytes that XMAT prepared. Null for any other row, as for HistoryMarker.
        internal static string LoadedStatus(bool sentRaw, string requestWrite, string source, long number)
        {
            string sentence = sentRaw ? StateSentence(requestWrite) : null;
            return sentence == null
                ? null
                : Localization.GetLocalizedString(
                    requestWrite == RawWriteState.Complete
                        ? "COMPOSER_HISTORY_LOADED_RAW"
                        : "COMPOSER_HISTORY_LOADED_RAW_ATTEMPTED",
                    source,
                    number,
                    sentence);
        }

        // The en-us key of a code's fixed text in Raw mode: each Raw mode code's, which mirrors the core's message
        // (RawRequest.Message), and a timeout's; null for any other code, such as the command line's own.
        internal static string KeyOf(string code) => code switch
        {
            RawCodes.InputTooLarge => "COMPOSER_RAW_INPUT_TOO_LARGE",
            RawCodes.Invalid => "COMPOSER_RAW_INVALID",
            RawCodes.Method => "COMPOSER_RAW_METHOD",
            RawCodes.Target => "COMPOSER_RAW_REQUEST_TARGET",
            RawCodes.Version => "COMPOSER_RAW_VERSION",
            RawCodes.DuplicateField => "COMPOSER_RAW_DUPLICATE_FIELD",
            RawCodes.Host => "COMPOSER_RAW_HOST",
            RawCodes.Connection => "COMPOSER_RAW_CONNECTION",
            RawCodes.Expect => "COMPOSER_RAW_EXPECT",
            RawCodes.Upgrade => "COMPOSER_RAW_UPGRADE",
            RawCodes.Trailer => "COMPOSER_RAW_TRAILER",
            RawCodes.Framing => "COMPOSER_RAW_FRAMING",
            RawCodes.BodyNotAllowed => "COMPOSER_RAW_BODY_NOT_ALLOWED",
            RawCodes.FixContentLength => "COMPOSER_RAW_FIX_CONTENT_LENGTH",
            RawCodes.Changed => "COMPOSER_RAW_CHANGED",
            RawCodes.TargetDenied => "COMPOSER_RAW_TARGET_DENIED",
            RawCodes.Proxy => "COMPOSER_RAW_PROXY",
            RawCodes.ClientCert => "COMPOSER_RAW_CLIENT_CERT",
            RawCodes.WindowsAuth => "COMPOSER_RAW_WINDOWS_AUTH",
            RawCodes.HttpVersion => "COMPOSER_RAW_HTTP_VERSION",
            RawCodes.Redirects => "COMPOSER_RAW_REDIRECTS",
            RawCodes.UnsupportedOption => "COMPOSER_RAW_UNSUPPORTED_OPTION",
            RawCodes.PolicyRaw => "COMPOSER_RAW_POLICY",
            RawCodes.PolicyRawUnsafe => "COMPOSER_RAW_POLICY_UNSAFE",
            RawCodes.Alpn => "COMPOSER_RAW_ALPN",
            RawCodes.ResponseInvalid => "COMPOSER_RAW_RESPONSE_INVALID",
            RawCodes.ResponseCoding => "COMPOSER_RAW_RESPONSE_CODING",
            RawCodes.ResponseIncomplete => "COMPOSER_RAW_RESPONSE_INCOMPLETE",
            RawCodes.SendCancelled => "COMPOSER_RAW_SEND_CANCELLED",
            ReplaySender.TimeoutCode => "COMPOSER_TIMEOUT",
            _ => null,
        };

        // Why a raw send got no complete response, in its code's words: replay.send.failed, which has no core message, and
        // any failure without a text of its own, as one that could not be completed (COMPOSER_RAW_SEND_FAILED).
        internal static string CodeText(string code) =>
            Localization.GetLocalizedString(KeyOf(code) ?? "COMPOSER_RAW_SEND_FAILED");
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