// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Replay
{
    // Reissues captured requests, or sends a composed one, for the GUI and xmat.exe. Everything is checked before anything
    // is sent: the selection, the edits, every request's validity, how the requests may reach the server and the safety
    // policy, under which a method that can change server state, captured headers going to another origin, or a
    // connection to another endpoint than the URL's host, needs the caller's permission. One failed check fails the run
    // with InvalidInput and nothing is sent. Then every selected request is sent once per iteration, in order, or up to
    // Parallelism at a time. Each send is a result whether it got a response or not, so a run that was not cancelled
    // succeeds once every send is done.
    internal sealed class ReplayOperation : IOperation<ReplayRequest, ReplayResult>
    {
        internal const string SkippedCode = "replay.request.skipped";
        internal const string VerifySkippedCode = "replay.verify.skipped";
        internal const string SequenceInvalidCode = "replay.sequence.invalid";
        internal const string ControlHeaderCode = "replay.request.control-header";
        internal const string Http2HttpsCode = "replay.http-version.https-required";
        internal const string ConnectToDeniedCode = "replay.connect-to.target-denied";
        internal const string ConnectToHttpsCode = "replay.connect-to.https-required";
        internal const string ConnectToOriginCountCode = "replay.connect-to.origin-count";
        internal const string ConnectToPolicyCode = "replay.policy.connect-to";

        // Headers that tell Fiddler how to send a request. XMAT never acts on them, and refuses a request that carries one
        // rather than send it as if it did: a captured header is never a permission. Removing it makes the request
        // replayable.
        private static readonly string[] ControlHeaders =
        {
            "Fiddler-Host",
            "X-OverrideHost",
            "X-IgnoreCertCNMismatch",
            "X-OverrideGateway",
        };

        // The sender of a run that connects to the URL's host; the GUI's composer passes one over its pooled connections.
        internal Func<ReplaySender> SenderFactory { get; set; } = static () => new ReplaySender();

        // The sender of a run that connects to another endpoint (ReplayTransportOptions.ConnectTo): always one of its own,
        // so no pooled connection to the URL's host carries its requests, and none of its connections serve another run.
        internal Func<ReplayConnectTo, ReplaySender> ConnectToSenderFactory { get; set; } =
            static target => new ReplaySender(target);

        internal Func<AnalyzeCaptureOperation> CaptureReaderFactory { get; set; } =
            static () => new AnalyzeCaptureOperation();

        // Verify decodes each body up to this size, or MaxResponseBytes if smaller; beyond it, bodies compare as stored.
        internal long MaxVerifyDecodedBytes { get; set; } = ReplayOptions.MaxVerifyDecodedBytes;

        // The expanded URLs of a sequence are kept until the run ends, so their total length is bounded.
        internal long MaxSequenceUrlCharacters { get; set; } = ReplayOptions.MaxSequenceUrlCharacters;

        public async Task<OperationResult<ReplayResult>> ExecuteAsync(
            ReplayRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            var diagnostics = new List<OperationDiagnostic>();
            try
            {
                if (request?.Source == null)
                {
                    throw new ReplayInputException("replay.request.missing", "A replay source is required.");
                }

                ReplayOptions options = request.Options ?? ReplayOptions.Default;
                if (!options.IsValid)
                {
                    throw new ReplayInputException("replay.options.invalid", "The replay options are out of range.");
                }

                Prepared prepared = await Task.Run(
                    () => Prepare(request, options, diagnostics, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                if (prepared.Failure != null)
                {
                    return prepared.Failure;
                }

                IReadOnlyList<ReplayItemResult> items = await RunAsync(
                    prepared.Items, options, request.Sink, context, cancellationToken).ConfigureAwait(false);
                return OperationResult<ReplayResult>.Success(
                    new ReplayResult(prepared.InputKind, prepared.InputSha256, items),
                    diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    OperationOutcome.Cancelled, "replay.cancelled", OperationDiagnosticSeverity.Info, "Replay was cancelled.");
            }
            catch (ReplayInputException ex)
            {
                return Failure(OperationOutcome.InvalidInput, ex.Code, OperationDiagnosticSeverity.Error, ex.DiagnosticMessage);
            }
            catch (ReplaySinkException ex)
            {
                // The sink belongs to the caller, which decides what its failure means.
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return Failure(
                    OperationOutcome.InternalError,
                    "replay.internal",
                    OperationDiagnosticSeverity.Error,
                    "Replay could not be completed because of an internal failure.");
            }
        }

        // A composed request, or one per number of a sequence, checked as ExecuteAsync checks it before it sends, but
        // without sending anything or expanding the sequence: the options, the plan size, the template and its URL budget,
        // that the request can be built (the widest number's URL for a sequence) and how it may reach the server. The
        // result says how many sends it makes and which consent they need, so the GUI can ask once before it sends, and
        // carries the transport that was checked. An invalid plan fails with the code that ExecuteAsync would report.
        internal OperationResult<ReplayPlanPreview> Preview(ReplaySource source, ReplayOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Draft == null)
            {
                throw new ArgumentException("Only a composed request or sequence can be previewed.", nameof(source));
            }

            options ??= ReplayOptions.Default;
            try
            {
                if (!options.IsValid)
                {
                    throw new ReplayInputException("replay.options.invalid", "The replay options are out of range.");
                }

                RequestReplayDraft template = source.Draft.WithUrl(source.Draft.Url);
                RequestReplayDraft widest = template;
                int requests = 1;
                if (source.Sequence != null)
                {
                    requests = source.Sequence.Count;
                    CheckPlanSize(requests, options);
                    CheckSequence(template.Url, source.Sequence, MaxSequenceUrlCharacters);
                    widest = template.WithUrl(source.Sequence.Expand(template.Url, source.Sequence.End));
                }

                Validate(widest);
                ReplayTransportOptions transport = options.EffectiveTransport;
                CheckTransport(new[] { widest }, transport);
                return OperationResult<ReplayPlanPreview>.Success(new ReplayPlanPreview(
                    template,
                    source.Sequence,
                    requests * options.Repeat,
                    widest.UsesUnsafeMethod,
                    widest.ChangesOrigin,
                    transport));
            }
            catch (ReplayInputException ex)
            {
                return OperationResult<ReplayPlanPreview>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(ex.Code, OperationDiagnosticSeverity.Error, ex.DiagnosticMessage));
            }
        }

        private Prepared Prepare(
            ReplayRequest request,
            ReplayOptions options,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            ReplaySource source = request.Source;
            string inputKind = null;
            string inputSha256 = null;
            List<ReplayItem> items;
            bool edited = false;
            if (source.Path != null)
            {
                AnalyzeCaptureOperation reader = CaptureReaderFactory() ??
                    throw new InvalidOperationException("The capture reader factory returned no reader.");
                OperationResult<CaptureRecordSet> read = reader.ReadCapture(source.Path, source.Limits, cancellationToken);
                if (!read.Succeeded)
                {
                    return new Prepared(
                        null, null, null, OperationResult<ReplayResult>.Failure(read.Outcome, read.Diagnostics.ToArray()));
                }

                diagnostics.AddRange(read.Diagnostics);
                inputKind = read.Value.InputKind;
                inputSha256 = read.Value.InputSha256;
                List<(string DeviceId, long Number, IDataRecord Record)> selected =
                    SelectRecords(read.Value, source, cancellationToken);

                // Checked before any draft exists, so a selection that is too large costs nothing to refuse.
                CheckPlanSize(selected.Count, options);
                items = CreateItems(selected, source.Selection, diagnostics, cancellationToken);
            }
            else if (source.Captures != null)
            {
                CheckPlanSize(source.Captures.Count, options);
                items = SelectCaptures(source.Captures, diagnostics, cancellationToken);
            }
            else if (source.Sequence != null)
            {
                // Checked before the template is expanded, so a sequence that is too large costs nothing to refuse. The
                // edits apply once, to a copy of the template, so every expanded request shares its header text and body.
                CheckPlanSize(source.Sequence.Count, options);
                RequestReplayDraft template = source.Draft.WithUrl(source.Draft.Url);
                ApplyEdits(
                    new[] { new ReplayItem(null, null, null, template) },
                    request.Edits ?? ReplayEdits.None,
                    cancellationToken);
                items = ExpandSequence(template, source.Sequence, MaxSequenceUrlCharacters, cancellationToken);
                edited = true;
            }
            else
            {
                items = new List<ReplayItem> { new(null, null, null, source.Draft) };
            }

            if (items.Count == 0)
            {
                throw new ReplayInputException(
                    "replay.request.none", "The selection contains no request that can be replayed.");
            }

            CheckPlanSize(items.Count, options);
            if (!edited)
            {
                ApplyEdits(items, request.Edits ?? ReplayEdits.None, cancellationToken);
            }

            Check(items, options, cancellationToken);
            if (options.Verify)
            {
                // Every captured response's headers are read here, before any parallel send reads them.
                int notKept = 0;
                foreach (ReplayItem item in items)
                {
                    notKept += item.ResponseBodyNotKept ? 1 : 0;
                }

                if (notKept > 0)
                {
                    diagnostics.Add(new OperationDiagnostic(
                        VerifySkippedCode,
                        OperationDiagnosticSeverity.Warning,
                        "Some requests are not verified: their captured response was recorded without its body."));
                }
            }

            return new Prepared(items, inputKind, inputSha256, null);
        }

        private static void CheckPlanSize(int requests, ReplayOptions options)
        {
            if ((long)requests * options.Repeat > ReplayOptions.MaxSends)
            {
                throw new ReplayInputException(
                    "replay.plan.too-large", "The selected requests, repeated, would exceed 10,000 sends.");
            }
        }

        // The records that the selection names, checked against the capture: every number named on its own exists, and
        // unless everything is replayed, no selected number belongs to two records, whether on one device or on several.
        private static List<(string DeviceId, long Number, IDataRecord Record)> SelectRecords(
            CaptureRecordSet capture,
            ReplaySource source,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<CaptureDeviceRecords> devices = capture.Devices;
            if (source.DeviceId != null)
            {
                CaptureDeviceRecords device = capture.Devices.FirstOrDefault(
                    item => string.Equals(item.Id, source.DeviceId, StringComparison.Ordinal)) ??
                    throw new ReplayInputException("replay.device.not-found", "The requested device was not found.");
                devices = new[] { device };
            }

            ReplaySelection selection = source.Selection;
            var selected = new List<(string DeviceId, long Number, IDataRecord Record)>();
            foreach (CaptureDeviceRecords device in devices)
            {
                foreach (IDataRecord record in device.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long number = record.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
                    if (selection.Contains(number))
                    {
                        selected.Add((device.Id, number, record));
                    }
                }
            }

            var selectedNumbers = new HashSet<long>(selected.Select(item => item.Number));
            if (selected.Count == 0 || selection.Numbers.Any(number => !selectedNumbers.Contains(number)))
            {
                throw new ReplayInputException("replay.request.not-found", "A selected request is not in the capture.");
            }

            // On different devices or on one, two requests with a selected number leave unclear which one to send.
            if (!selection.IsAll && selectedNumbers.Count != selected.Count)
            {
                throw new ReplayInputException(
                    "replay.request.ambiguous", "A selected request number belongs to more than one request in the capture.");
            }

            return selected;
        }

        private static List<ReplayItem> CreateItems(
            List<(string DeviceId, long Number, IDataRecord Record)> selected,
            ReplaySelection selection,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            var items = new List<ReplayItem>();
            bool skipped = false;
            foreach ((string deviceId, long number, IDataRecord record) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProxyConnectionModel model = ToModel(record);
                if (!RequestReplayDraft.CanReplay(model))
                {
                    if (selection.Names(number))
                    {
                        throw new ReplayInputException(
                            "replay.request.unsupported",
                            "A selected request cannot be replayed: only HTTP and HTTPS requests without a protocol " +
                                "upgrade, recorded with their request body, can.");
                    }

                    skipped = true;
                    continue;
                }

                items.Add(new ReplayItem(deviceId, number, model, CreateDraft(model)));
            }

            if (skipped)
            {
                diagnostics.Add(Skipped());
            }

            return items;
        }

        // One request per number of the sequence, in ascending order: the template with every '#' in its URL replaced. The
        // numbers may change only the path and query, so the template has its '#' there, and every expanded URL must keep
        // the scheme, host and port of the first. The header text and body are shared, not copied; the expanded URLs,
        // which are kept until the run ends, are checked to fit maxUrlCharacters together before any is made.
        private static List<ReplayItem> ExpandSequence(
            RequestReplayDraft template,
            ReplaySequence sequence,
            long maxUrlCharacters,
            CancellationToken cancellationToken)
        {
            CheckSequence(template.Url, sequence, maxUrlCharacters);
            var items = new List<ReplayItem>(sequence.Count);
            string origin = null;
            foreach (long value in sequence.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = sequence.Expand(template.Url, value);
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    throw Invalid(null);
                }

                string current = uri.Scheme + "://" + uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
                origin ??= current;
                if (!string.Equals(current, origin, StringComparison.Ordinal))
                {
                    throw new ReplayInputException(
                        SequenceInvalidCode, "Every number of a sequence must keep the scheme, host and port of the URL.");
                }

                items.Add(new ReplayItem(null, null, null, template.WithUrl(url), Sequence: value));
            }

            return items;
        }

        // A template has its every '#' in the path or query, and its expanded URLs fit maxUrlCharacters together.
        private static void CheckSequence(string template, ReplaySequence sequence, long maxUrlCharacters)
        {
            if (!ReplaySequence.IsPathAndQueryTemplate(template))
            {
                throw new ReplayInputException(
                    SequenceInvalidCode,
                    "A sequence needs a '#' in the path or query of the URL, and none in its scheme, host or port.");
            }

            if ((long)sequence.Count * sequence.MaxExpandedLength(template) > maxUrlCharacters)
            {
                throw new ReplayInputException(
                    "replay.plan.too-large",
                    "The URLs of the sequence would be too long together; use fewer numbers or a shorter URL.");
            }
        }

        // The GUI selects rows itself; rows that cannot be replayed are skipped with a warning.
        private static List<ReplayItem> SelectCaptures(
            IReadOnlyList<ProxyConnectionModel> captures,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            var items = new List<ReplayItem>();
            for (int index = 0; index < captures.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProxyConnectionModel capture = captures[index];
                if (RequestReplayDraft.CanReplay(capture))
                {
                    items.Add(new ReplayItem(null, capture.RequestNumber, capture, CreateDraft(capture), index));
                }
            }

            if (items.Count < captures.Count)
            {
                diagnostics.Add(Skipped());
            }

            return items;
        }

        private static OperationDiagnostic Skipped() =>
            new(
                SkippedCode,
                OperationDiagnosticSeverity.Warning,
                "Selected requests that cannot be replayed (CONNECT tunnels, protocol upgrades and traffic other than " +
                    "HTTP and HTTPS) were skipped.");

        private static ProxyConnectionModel ToModel(IDataRecord record)
        {
            try
            {
                return new ProxyConnectionModel(record);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ReplayInputException(
                    "replay.request.malformed", "A selected request in the capture is malformed.", ex);
            }
        }

        private static RequestReplayDraft CreateDraft(ProxyConnectionModel capture)
        {
            try
            {
                return RequestReplayDraft.FromCapture(capture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ReplayInputException(
                    "replay.request.malformed", "A selected request in the capture is malformed.", ex);
            }
        }

        private static void ApplyEdits(IReadOnlyList<ReplayItem> items, ReplayEdits edits, CancellationToken cancellationToken)
        {
            if (edits.Url != null && edits.Origin != null)
            {
                throw new ReplayInputException(
                    "replay.edit.conflict", "A new URL and a new origin cannot be set together.");
            }

            if (edits.Url != null && items.Count != 1)
            {
                throw new ReplayInputException(
                    "replay.edit.url-needs-one-request", "A new URL can be set only when one request is selected.");
            }

            string origin = null;
            if (edits.Origin != null)
            {
                if (!TryParseOrigin(edits.Origin, out origin))
                {
                    throw new ReplayInputException(
                        "replay.edit.invalid-origin",
                        "The new origin must be an absolute HTTP or HTTPS URL without a path, query, fragment or credentials.");
                }
            }

            foreach (ReplayItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestReplayDraft draft = item.Draft;
                try
                {
                    if (edits.Method != null)
                    {
                        draft.Method = edits.Method;
                    }

                    if (edits.Url != null)
                    {
                        draft.Url = edits.Url;
                    }

                    if (origin != null)
                    {
                        draft.Url = origin + new Uri(draft.Url, UriKind.Absolute).PathAndQuery;
                    }

                    foreach (string name in edits.RemoveHeaders ?? Array.Empty<string>())
                    {
                        draft.RemoveHeader(name);
                    }

                    foreach (KeyValuePair<string, string> header in
                        edits.SetHeaders ?? Array.Empty<KeyValuePair<string, string>>())
                    {
                        draft.SetHeader(header.Key, header.Value);
                    }

                    if (edits.Unconditional)
                    {
                        draft.RemoveConditionalHeaders();
                    }

                    if (edits.Body != null)
                    {
                        draft.SetBodyBytes(edits.Body);
                    }
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    throw Invalid(ex);
                }
            }
        }

        // An origin is an absolute HTTP or HTTPS URL with nothing after its authority but an optional "/".
        internal static bool TryParseOrigin(string text, out string origin)
        {
            origin = null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            origin = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        private static void Check(IReadOnlyList<ReplayItem> items, ReplayOptions options, CancellationToken cancellationToken)
        {
            foreach (ReplayItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Validate(item.Draft);
            }

            ReplayTransportOptions transport = options.EffectiveTransport;
            CheckTransport(items.Select(item => item.Draft), transport);
            if (!options.AllowUnsafeMethods && items.Any(item => item.Draft.UsesUnsafeMethod))
            {
                throw new ReplayInputException(
                    "replay.policy.unsafe-method",
                    "A selected request uses a method that can change server state (anything but GET, HEAD and " +
                        "OPTIONS), and unsafe methods are not allowed; nothing was sent.");
            }

            if (!options.AllowOriginChange && items.Any(item => item.Draft.ChangesOrigin))
            {
                throw new ReplayInputException(
                    "replay.policy.origin-change",
                    "A selected request would go to another origin than the one it was captured for, with its captured " +
                        "headers, and origin changes are not allowed; nothing was sent.");
            }

            if (transport.ConnectTo != null && !options.AllowConnectTo)
            {
                throw new ReplayInputException(
                    ConnectToPolicyCode,
                    "Connecting to another endpoint than the URL's host sends the requests, with their headers and body, " +
                        "to that endpoint, and that was not allowed; nothing was sent.");
            }
        }

        // How the requests may reach the server: HTTP/2 only and a connect-to endpoint need HTTPS, so the server must
        // prove that it is the URL's host; a connect-to endpoint serves one origin, and never an address that no request
        // may go to.
        private static void CheckTransport(IEnumerable<RequestReplayDraft> drafts, ReplayTransportOptions transport)
        {
            if (transport.HttpVersion == ReplayHttpVersion.Http2 && !drafts.All(IsHttps))
            {
                throw new ReplayInputException(
                    Http2HttpsCode, "HTTP/2 only is supported for HTTPS URLs only; nothing was sent.");
            }

            if (transport.ConnectTo == null)
            {
                return;
            }

            if (transport.ConnectTo.IsDenied)
            {
                throw new ReplayInputException(
                    ConnectToDeniedCode,
                    "The connect-to endpoint is an unspecified, multicast or broadcast address; nothing was sent.");
            }

            if (!drafts.All(IsHttps))
            {
                throw new ReplayInputException(
                    ConnectToHttpsCode,
                    "Connecting to another endpoint needs HTTPS URLs, so the endpoint must prove that it is the URL's " +
                        "host; nothing was sent.");
            }

            if (drafts.Select(draft => new Uri(draft.Url).GetLeftPart(UriPartial.Authority))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            {
                throw new ReplayInputException(
                    ConnectToOriginCountCode,
                    "Connecting to another endpoint needs the requests of a run to share one origin; nothing was sent.");
            }
        }

        private static bool IsHttps(RequestReplayDraft draft) =>
            Uri.TryCreate(draft.Url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps;

        // Building the request validates the method, URL, headers and body; a Fiddler control header is refused.
        private static void Validate(RequestReplayDraft draft)
        {
            HttpRequestMessage message;
            try
            {
                message = draft.CreateRequestToSend();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
            {
                throw Invalid(ex);
            }

            using (message)
            {
                if (message.Headers.NonValidated.Any(header =>
                    ControlHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new ReplayInputException(
                        ControlHeaderCode,
                        "A selected request has a Fiddler control header (Fiddler-Host, X-OverrideHost, " +
                            "X-IgnoreCertCNMismatch or X-OverrideGateway), which XMAT does not act on; remove it to send " +
                            "the request. Nothing was sent.");
                }
            }
        }

        private static ReplayInputException Invalid(Exception inner) =>
            new(
                "replay.request.invalid",
                "A selected request is not valid with the edits, such as an invalid URL, method, header or body.",
                inner);

        private async Task<IReadOnlyList<ReplayItemResult>> RunAsync(
            IReadOnlyList<ReplayItem> items,
            ReplayOptions options,
            IReplayExchangeSink sink,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var plan = new List<(int Id, ReplayItem Item, int Iteration)>(items.Count * options.Repeat);
            for (int iteration = 1; iteration <= options.Repeat; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ReplayItem item in items)
                {
                    plan.Add((plan.Count + 1, item, iteration));
                }
            }

            var results = new ReplayItemResult[plan.Count];
            var gate = new object();
            int done = 0;
            ReplaySendOptions sendOptions = options.ToSendOptions();
            ReplayTransportOptions transport = options.EffectiveTransport;
            using ReplaySender sender =
                (transport.ConnectTo == null ? SenderFactory() : ConnectToSenderFactory(transport.ConnectTo)) ??
                throw new InvalidOperationException("The replay sender factory returned no sender.");
            ReportProgress(context, 0, plan.Count);
            await Parallel.ForEachAsync(
                plan,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (step, token) =>
                {
                    ReplayExchange exchange =
                        await sender.SendAsync(step.Item.Draft, sendOptions, token).ConfigureAwait(false);
                    ReplayVerification verification =
                        options.Verify && step.Item.Original != null && !step.Item.ResponseBodyNotKept &&
                            exchange.Outcome == ReplayOutcome.Completed
                            ? ReplayVerifier.Compare(
                                step.Item.Original,
                                exchange,
                                Math.Min(options.MaxResponseBytes, MaxVerifyDecodedBytes),
                                token)
                            : null;
                    var result = new ReplayItemResult(
                        step.Id,
                        step.Item.DeviceId,
                        step.Item.RequestNumber,
                        step.Iteration,
                        exchange.Outcome,
                        exchange.StatusCode,
                        exchange.Duration,
                        exchange.Response?.Body.LongLength ?? 0,
                        exchange.Redirects,
                        exchange.Code,
                        verification,
                        step.Item.SourceIndex,
                        step.Item.Sequence,
                        transport.TransportName,
                        transport.HttpVersionName,
                        ReplayTransportOptions.ResponseVersionName(exchange.Version));
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (sink != null)
                        {
                            try
                            {
                                sink.Add(result, exchange);
                            }
                            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex) && ex is not OperationCanceledException)
                            {
                                throw new ReplaySinkException(ex);
                            }
                        }

                        results[step.Id - 1] = result;
                        done++;
                        ReportProgress(context, done, plan.Count);
                    }
                }).ConfigureAwait(false);
            return results;
        }

        private static void ReportProgress(OperationContext context, int done, int total)
        {
            try
            {
                context.Progress.Report(new OperationProgress("replay.send", total == 0 ? null : (double)done / total));
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Progress is best effort and cannot fail the operation.
            }
        }

        private static OperationResult<ReplayResult> Failure(
            OperationOutcome outcome, string code, OperationDiagnosticSeverity severity, string message) =>
            OperationResult<ReplayResult>.Failure(outcome, new OperationDiagnostic(code, severity, message));

        private sealed record ReplayItem(
            string DeviceId,
            long? RequestNumber,
            ProxyConnectionModel Original,
            RequestReplayDraft Draft,
            int? SourceIndex = null,
            long? Sequence = null)
        {
            // The captured response was recorded by the GUI without its body, so verify has nothing to compare it with.
            internal bool ResponseBodyNotKept =>
                Original?.ResponseHeaders.ContainsKey(RequestReplayDraft.ResponseBodyNotKeptHeader) == true;
        }

        private sealed record Prepared(
            IReadOnlyList<ReplayItem> Items,
            string InputKind,
            string InputSha256,
            OperationResult<ReplayResult> Failure);

        private sealed class ReplayInputException : Exception
        {
            internal ReplayInputException(string code, string diagnosticMessage, Exception innerException = null)
                : base("The replay input is invalid.", innerException)
            {
                Code = code;
                DiagnosticMessage = diagnosticMessage;
            }

            internal string Code { get; }

            internal string DiagnosticMessage { get; }
        }

        private sealed class ReplaySinkException : Exception
        {
            internal ReplaySinkException(Exception innerException)
                : base("The replay sink failed.", innerException)
            {
            }
        }
    }
}
