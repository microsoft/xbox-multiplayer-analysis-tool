// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
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
    // is sent: the selection, the edits, every request's validity and the safety policy, under which a method that can
    // change server state, or captured headers going to another origin, needs the caller's permission. One failed check
    // fails the run with InvalidInput and nothing is sent. Then every selected request is sent once per iteration, in
    // order, or up to Parallelism at a time. Each send is a result whether it got a response or not, so a run that was
    // not cancelled succeeds once every send is done.
    internal sealed class ReplayOperation : IOperation<ReplayRequest, ReplayResult>
    {
        internal const string SkippedCode = "replay.request.skipped";
        internal const string VerifySkippedCode = "replay.verify.skipped";

        internal Func<ReplaySender> SenderFactory { get; set; } = static () => new ReplaySender();

        internal Func<AnalyzeCaptureOperation> CaptureReaderFactory { get; set; } =
            static () => new AnalyzeCaptureOperation();

        // Verify decodes each body up to this size, or MaxResponseBytes if smaller; beyond it, bodies compare as stored.
        internal long MaxVerifyDecodedBytes { get; set; } = ReplayOptions.MaxVerifyDecodedBytes;

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
            ApplyEdits(items, request.Edits ?? ReplayEdits.None, cancellationToken);
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
                try
                {
                    using (item.Draft.CreateRequestToSend())
                    {
                        // Building the request validates the method, URL, headers and body.
                    }
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
                {
                    throw Invalid(ex);
                }
            }

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
            using ReplaySender sender = SenderFactory() ??
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
                        step.Item.SourceIndex);
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
            int? SourceIndex = null)
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
