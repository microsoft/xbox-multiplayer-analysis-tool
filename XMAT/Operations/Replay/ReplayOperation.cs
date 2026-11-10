// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
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
    // policy, under which a method that can change server state, captured headers going to another origin, a connection
    // to another endpoint than the URL's host, sending through a proxy, or a client certificate, which only the one origin
    // that the caller names may receive, needs the caller's permission. One failed check fails the run with InvalidInput
    // and nothing is sent. Only then is a client certificate looked up and its key checked (ReplayCertificateResolver),
    // which can refuse the run too. Then every selected request is sent once per iteration, in order, or up to
    // Parallelism at a time. Each send is a result whether it got a response or not, so a run that was not cancelled
    // succeeds once every send is done.
    internal sealed class ReplayOperation : IOperation<ReplayRequest, ReplayResult>
    {
        internal const string SkippedCode = "replay.request.skipped";
        internal const string RequestInvalidCode = "replay.request.invalid";
        internal const string VerifySkippedCode = "replay.verify.skipped";
        internal const string SequenceInvalidCode = "replay.sequence.invalid";
        internal const string ControlHeaderCode = ReplayControlHeaders.Code;
        internal const string Http2HttpsCode = "replay.http-version.https-required";
        internal const string ConnectToDeniedCode = "replay.connect-to.target-denied";
        internal const string ConnectToHttpsCode = "replay.connect-to.https-required";
        internal const string ConnectToOriginCountCode = "replay.connect-to.origin-count";
        internal const string ConnectToPolicyCode = "replay.policy.connect-to";
        internal const string ProxyConnectToCode = "replay.proxy.connect-to";
        internal const string ProxyPolicyCode = "replay.policy.proxy";
        internal const string ClientCertificateProxyCode = "replay.client-cert.proxy";
        internal const string ClientCertificateHttpsCode = "replay.client-cert.https-required";
        internal const string ClientCertificateOriginInvalidCode = "replay.client-cert.origin-invalid";
        internal const string ClientCertificateOriginCountCode = "replay.client-cert.origin-count";
        internal const string ClientCertificatePolicyCode = "replay.policy.client-cert";
        internal const string WindowsAuthProxyCode = "replay.windows-auth.proxy";
        internal const string WindowsAuthConnectToCode = "replay.windows-auth.connect-to";
        internal const string WindowsAuthHttpsCode = "replay.windows-auth.https-required";
        internal const string WindowsAuthOriginInvalidCode = "replay.windows-auth.origin-invalid";
        internal const string WindowsAuthOriginCountCode = "replay.windows-auth.origin-count";
        internal const string WindowsAuthHttp2Code = "replay.windows-auth.http2";
        internal const string WindowsAuthAuthorizationCode = "replay.windows-auth.authorization-header";
        internal const string WindowsAuthPolicyCode = "replay.policy.windows-auth";

        // The header that a request of a Windows-authentication run may not carry of its own.
        private static readonly string[] AuthorizationHeader = { "Authorization" };

        // The sender of a run that connects to the URL's host; the GUI's composer passes one over its pooled connections.
        internal Func<ReplaySender> SenderFactory { get; set; } = static () => new ReplaySender();

        // The sender of a run whose transport is a setting of the handler (ReplayTransportOptions.NeedsOwnSender: a
        // connect-to endpoint, a proxy, a client certificate or Windows authentication): always one made for that run and
        // disposed when it ends, so no pooled connection carries its requests, and none of its connections serve another run.
        internal Func<ReplaySenderSetup, ReplaySender> OwnSenderFactory { get; set; } =
            static setup => new ReplaySender(setup);

        // The native calls that resolve a run's client certificate; tests record or fail them.
        internal IReplayCertificateNative CertificateNative { get; set; } = ReplayCertificateNative.Instance;

        // Null except in tests: the trust of a run's own sender and of its client-certificate check, a new policy each time
        // (ReplaySenderSetup.ChainPolicyFactory).
        internal Func<X509ChainPolicy> ServerChainPolicyFactory { get; set; }

        internal Func<AnalyzeCaptureOperation> CaptureReaderFactory { get; set; } =
            static () => new AnalyzeCaptureOperation();

        // Verify decodes each body up to this size, or MaxResponseBytes if smaller; beyond it, bodies compare as stored.
        internal long MaxVerifyDecodedBytes { get; set; } = ReplayOptions.MaxVerifyDecodedBytes;

        // The ceilings of the verification differences (ReplayOptions.VerifyDiff); null for the defaults.
        internal VerifyDiffLimits VerifyDiffLimits { get; set; }

        // The expanded URLs of a sequence are kept until the run ends, so their total length is bounded.
        internal long MaxSequenceUrlCharacters { get; set; } = ReplayOptions.MaxSequenceUrlCharacters;

        // The sender of a raw run's requests: one per run, made from its connect-to endpoint and, in tests, the
        // server trust (ServerChainPolicyFactory). Every raw send has a connection of its own.
        internal Func<RawSenderSetup, RawSender> RawSenderFactory { get; set; } = static setup => new RawSender(setup);

        // Null except in tests: the SHA-256 that a raw run recomputes over its image before any other check
        // (replay.raw.changed), so a test can stand for an image that changed after it was checked.
        internal Func<RawRequestImage, CancellationToken, string> RawDigest { get; set; }

        // Null except in tests: awaited with the send's id after a raw send ends and before its result goes to the sink, so
        // a test can order a cancellation between them.
        internal Func<int, Task> RawSendEnded { get; set; }

        public async Task<OperationResult<ReplayResult>> ExecuteAsync(
            ReplayRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            var diagnostics = new List<OperationDiagnostic>();
            ReplayClientCredential credential = null;
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

                // Only after every check and consent: the client certificate, looked up and its key checked, before any
                // sender exists. The run owns it until it ends, after its sender was disposed.
                ReplayClientCertificate certificate = options.EffectiveTransport.ClientCertificate;
                if (certificate != null)
                {
                    credential = await Task.Run(
                        () => Resolve(certificate, options.AllowClientCertificate, cancellationToken),
                        CancellationToken.None).ConfigureAwait(false);
                }

                IReadOnlyList<ReplayItemResult> items = await RunAsync(
                    prepared.Items, options, credential, request.Sink, context, cancellationToken).ConfigureAwait(false);
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
            finally
            {
                credential?.Dispose();
            }
        }

        // Stage 4 of the checks: the run's client certificate, resolved from the store after every consent was given, or
        // the reason that it cannot be used.
        private ReplayClientCredential Resolve(
            ReplayClientCertificate certificate, ReplayCredentialOrigin origin, CancellationToken cancellationToken)
        {
            IReplayCertificateNative native = CertificateNative ??
                throw new InvalidOperationException("The replay operation has no certificate access.");
            try
            {
                return ReplayCertificateResolver.Resolve(native, certificate, origin, cancellationToken);
            }
            catch (ReplayClientCertificateException ex)
            {
                throw new ReplayInputException(ex.Code, ex.Message);
            }
        }

        // A composed request, or one per number of a sequence, checked as ExecuteAsync checks it before it sends, but
        // without sending anything or expanding the sequence: the options, the plan size, the template and its URL budget,
        // that the request can be built (the widest number's URL for a sequence) and how it may reach the server. The
        // result says how many sends it makes and which consent they need, so the GUI can ask once before it sends, and
        // carries the transport that was checked. An invalid plan fails with the code that ExecuteAsync would report. A raw
        // request is checked through its frozen values, and its plan carries the image that the
        // raw consent must cover (ReplayRawConsent.For); cancellationToken stops recomputing its SHA-256, and throws.
        internal OperationResult<ReplayPlanPreview> Preview(
            ReplaySource source, ReplayOptions options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Draft == null && source.Raw == null)
            {
                throw new ArgumentException(
                    "Only a composed request, a sequence or a raw request can be previewed.", nameof(source));
            }

            options ??= ReplayOptions.Default;
            try
            {
                if (!options.IsValid)
                {
                    throw new ReplayInputException("replay.options.invalid", "The replay options are out of range.");
                }

                if (source.Raw != null)
                {
                    CheckRawValues(source.Raw, null, options, cancellationToken);
                    return OperationResult<ReplayPlanPreview>.Success(new ReplayPlanPreview(
                        null,
                        null,
                        options.Repeat,
                        source.Raw.UsesUnsafeMethod,
                        RawChangesOrigin(source),
                        options.EffectiveTransport,
                        Raw: source.Raw,
                        ProvenanceOrigin: source.ProvenanceOrigin));
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
                    transport,
                    transport.ClientCertificate == null && !transport.WindowsAuth ? null : CredentialOriginOf(widest)));
            }
            catch (ReplayInputException ex)
            {
                return OperationResult<ReplayPlanPreview>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(ex.Code, OperationDiagnosticSeverity.Error, ex.DiagnosticMessage));
            }
        }

        // The requests of a run, prepared to be saved or exported, never sent: loaded, selected, built and edited as
        // ExecuteAsync does, at most MaxSends of them, counted before any draft is built, then checked as data
        // (RequestReplayDraft.ValidateStructure). None of the checks that only sending needs apply, consent included, and
        // nothing on this path makes a sender. A pending omission is kept, for the caller to save or refuse.
        internal OperationResult<ReplayExport> PrepareForExport(ReplayRequest request, CancellationToken cancellationToken)
        {
            if (request?.Source?.Raw != null)
            {
                // A request file or a cURL script holds a parsed request, never a raw request's prepared bytes.
                throw new ArgumentException("A raw request cannot be prepared for export.", nameof(request));
            }

            var diagnostics = new List<OperationDiagnostic>();
            try
            {
                if (request?.Source == null)
                {
                    throw new ReplayInputException("replay.request.missing", "A replay source is required.");
                }

                Prepared gathered = Gather(request, ReplayOptions.Default, diagnostics, cancellationToken);
                if (gathered.Failure != null)
                {
                    return OperationResult<ReplayExport>.Failure(
                        gathered.Failure.Outcome, gathered.Failure.Diagnostics.ToArray());
                }

                var items = new List<ReplayExportItem>(gathered.Items.Count);
                foreach (ReplayItem item in gathered.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        item.Draft.ValidateStructure();
                    }
                    catch (FormatException ex)
                    {
                        throw Invalid(ex);
                    }

                    items.Add(new ReplayExportItem(item.DeviceId, item.RequestNumber, item.Draft));
                }

                return OperationResult<ReplayExport>.Success(
                    new ReplayExport(gathered.InputKind, gathered.InputSha256, items.AsReadOnly()), diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OperationResult<ReplayExport>.Failure(
                    OperationOutcome.Cancelled,
                    new OperationDiagnostic("replay.cancelled", OperationDiagnosticSeverity.Info, "Replay was cancelled."));
            }
            catch (ReplayInputException ex)
            {
                return OperationResult<ReplayExport>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(ex.Code, OperationDiagnosticSeverity.Error, ex.DiagnosticMessage));
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return OperationResult<ReplayExport>.Failure(
                    OperationOutcome.InternalError,
                    new OperationDiagnostic(
                        "replay.internal",
                        OperationDiagnosticSeverity.Error,
                        "The requests could not be prepared because of an internal failure."));
            }
        }

        private Prepared Gather(
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
                items = SelectCaptures(source.Captures, source.CapturedOrigins, diagnostics, cancellationToken);
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

            return new Prepared(items, inputKind, inputSha256, null);
        }

        // The requests of a run, gathered (Gather), then checked as sending needs them checked: valid, complete, allowed
        // and reachable as the options say. A raw request is checked on its own: its frozen values, then
        // each consent, and it is the run's only request.
        private Prepared Prepare(
            ReplayRequest request,
            ReplayOptions options,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            ReplaySource source = request.Source;
            if (source.Raw != null)
            {
                CheckRawValues(source.Raw, request.Edits, options, cancellationToken);
                CheckRawConsents(source, options);
                return new Prepared(
                    new[] { new ReplayItem(null, null, null, null, Raw: source.Raw, Provenance: source.ProvenanceOrigin) },
                    null,
                    null,
                    null);
            }

            Prepared prepared = Gather(request, options, diagnostics, cancellationToken);
            if (prepared.Failure != null)
            {
                return prepared;
            }

            IReadOnlyList<ReplayItem> items = prepared.Items;
            Check(items, options, cancellationToken);
            if (options.Verify)
            {
                // Every captured response's headers are read here, before any parallel send reads them.
                int notVerifiable = 0;
                foreach (ReplayItem item in items)
                {
                    notVerifiable += item.Original != null && !item.Verifiable ? 1 : 0;
                }

                if (notVerifiable > 0)
                {
                    diagnostics.Add(new OperationDiagnostic(
                        VerifySkippedCode,
                        OperationDiagnosticSeverity.Warning,
                        "Some requests are not verified: no response was captured for them, or it was recorded without its body."));
                }
            }

            return prepared;
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

        // The GUI selects rows itself; rows that cannot be replayed are skipped with a warning. A row's captured origin, when
        // the caller gives one (ReplaySource.CapturedOrigins), replaces the capture's own before any check.
        private static List<ReplayItem> SelectCaptures(
            IReadOnlyList<ProxyConnectionModel> captures,
            IReadOnlyList<Uri> capturedOrigins,
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
                    RequestReplayDraft draft = CreateDraft(capture);
                    Uri origin = capturedOrigins?[index];
                    items.Add(new ReplayItem(
                        null, capture.RequestNumber, capture, origin == null ? draft : draft.WithCapturedOrigin(origin), index));
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
            // The CLI refuses these together before anything runs; a form brings its own body and Content-Type.
            if (edits.Form != null &&
                (edits.Body != null ||
                    (edits.SetHeaders ?? Array.Empty<KeyValuePair<string, string>>()).Any(header => IsContentType(header.Key)) ||
                    (edits.RemoveHeaders ?? Array.Empty<string>()).Any(IsContentType)))
            {
                throw new InvalidOperationException("A form cannot be applied with a body or a Content-Type edit.");
            }

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
                    // An edit that supplies a part completes it, as a request file left it out or not; adding or removing a
                    // header never completes a header block that a file left out: only clearing the block does.
                    if (edits.Method != null)
                    {
                        draft.CompleteMethod(edits.Method);
                    }

                    if (edits.Url != null)
                    {
                        draft.CompleteUrl(edits.Url);
                    }

                    if (origin != null)
                    {
                        draft.Url = origin + new Uri(draft.Url, UriKind.Absolute).PathAndQuery;
                    }

                    if (edits.ClearHeaders)
                    {
                        draft.ReplaceHeaders("");
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

                    if (edits.Form != null)
                    {
                        MultipartForm.ApplyTo(draft, edits.Form);
                    }
                    else if (edits.Body != null)
                    {
                        draft.CompleteBody(edits.Body);
                    }
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    throw Invalid(ex);
                }
                catch (MultipartFormException ex)
                {
                    throw new ReplayInputException(ex.Code, ex.Message);
                }
            }
        }

        private static bool IsContentType(string name) => string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

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
                throw OriginChangeRefused();
            }

            if (transport.ConnectTo != null && !options.AllowConnectTo)
            {
                throw ConnectToRefused();
            }

            if (transport.Proxy != null && !options.AllowProxy)
            {
                throw new ReplayInputException(
                    ProxyPolicyCode,
                    "Sending through a proxy gives the proxy the whole of every http:// request, its headers and body " +
                        "included. For HTTPS, the unencrypted CONNECT request exposes the destination host and port and, " +
                        "when supplied, User-Agent information that may identify the application/title, SDK or device. " +
                        "Sending through the proxy was not allowed; nothing was sent.");
            }

            // The consents name the one origin that may receive the certificate or Windows authentication's answers, and it
            // must be this run's: CheckTransport made the run's requests share one, which is never null, and no consent is
            // ever a null origin's.
            ReplayCredentialOrigin runOrigin =
                transport.ClientCertificate == null && !transport.WindowsAuth ? null : CredentialOriginOf(items[0].Draft);
            if (transport.ClientCertificate != null &&
                (options.AllowClientCertificate == null || runOrigin == null ||
                    !options.AllowClientCertificate.Equals(runOrigin)))
            {
                throw new ReplayInputException(
                    ClientCertificatePolicyCode,
                    "A client certificate identifies you to the server that receives it, and XMAT sends it only to a " +
                        "server whose certificate this PC accepts for the origin. Sending it to this run's origin was not " +
                        "allowed; nothing was sent.");
            }

            if (transport.WindowsAuth &&
                (options.AllowWindowsAuth == null || runOrigin == null || !options.AllowWindowsAuth.Equals(runOrigin)))
            {
                throw new ReplayInputException(
                    WindowsAuthPolicyCode,
                    "Windows authentication answers the server's challenge with the Windows credentials that XMAT runs with. " +
                        "Answering this run's origin was not allowed; nothing was sent.");
            }
        }

        // How the requests may reach the server, in this order: a proxy chooses where it connects, so it cannot be combined
        // with a connect-to endpoint, a client certificate or Windows authentication, nor can Windows authentication with a
        // connect-to endpoint; HTTP/2 only and a connect-to endpoint need HTTPS, so the server must prove that it is the
        // URL's host; a connect-to endpoint serves one origin, and never an address that no request may go to; a client
        // certificate and Windows authentication go only over HTTPS, to one supported origin per run, each checked before
        // the other's next step; and Windows authentication needs HTTP/1.1 and requests without an Authorization header of
        // their own, read from their header lines, also one that Connection would drop.
        private static void CheckTransport(IEnumerable<RequestReplayDraft> drafts, ReplayTransportOptions transport)
        {
            bool certificate = transport.ClientCertificate != null;
            bool windows = transport.WindowsAuth;
            if (transport.Proxy != null && transport.ConnectTo != null)
            {
                throw new ReplayInputException(
                    ProxyConnectToCode,
                    "A proxy and a connect-to endpoint cannot be used together: the proxy chooses where it connects; " +
                        "nothing was sent.");
            }

            if (transport.Proxy != null && certificate)
            {
                throw new ReplayInputException(
                    ClientCertificateProxyCode, "A client certificate cannot be sent through a proxy; nothing was sent.");
            }

            if (transport.Proxy != null && windows)
            {
                throw new ReplayInputException(
                    WindowsAuthProxyCode, "Windows authentication cannot be used through a proxy; nothing was sent.");
            }

            if (transport.ConnectTo != null && windows)
            {
                throw new ReplayInputException(
                    WindowsAuthConnectToCode,
                    "Windows authentication cannot be used with another endpoint than the URL's host; nothing was sent.");
            }

            if (transport.HttpVersion == ReplayHttpVersion.Http2 && !drafts.All(IsHttps))
            {
                throw new ReplayInputException(
                    Http2HttpsCode, "HTTP/2 only is supported for HTTPS URLs only; nothing was sent.");
            }

            if (transport.ConnectTo != null)
            {
                CheckConnectTo(drafts, transport.ConnectTo);
            }

            if (!certificate && !windows)
            {
                return;
            }

            bool https = drafts.All(IsHttps);
            if (certificate && !https)
            {
                throw new ReplayInputException(
                    ClientCertificateHttpsCode,
                    "A client certificate is sent only to HTTPS URLs, so the server must prove that it is the URL's host; " +
                        "nothing was sent.");
            }

            if (windows && !https)
            {
                throw new ReplayInputException(
                    WindowsAuthHttpsCode,
                    "Windows authentication answers only HTTPS URLs, so the server must prove that it is the URL's host; " +
                        "nothing was sent.");
            }

            bool invalid = drafts.Any(draft => CredentialOriginOf(draft) == null);
            if (certificate && invalid)
            {
                throw new ReplayInputException(
                    ClientCertificateOriginInvalidCode,
                    "Client-certificate authentication requires a supported HTTPS origin; nothing was sent.");
            }

            if (windows && invalid)
            {
                throw new ReplayInputException(
                    WindowsAuthOriginInvalidCode, "Windows authentication requires a supported HTTPS origin; nothing was sent.");
            }

            bool several = drafts.Select(CredentialOriginOf).Distinct().Skip(1).Any();
            if (certificate && several)
            {
                throw new ReplayInputException(
                    ClientCertificateOriginCountCode,
                    "A client certificate is sent to one origin per run, and the requests of this run do not share one; " +
                        "nothing was sent.");
            }

            if (windows && several)
            {
                throw new ReplayInputException(
                    WindowsAuthOriginCountCode,
                    "Windows authentication answers one origin per run, and the requests of this run do not share one; " +
                        "nothing was sent.");
            }

            if (windows && transport.HttpVersion == ReplayHttpVersion.Http2)
            {
                throw new ReplayInputException(
                    WindowsAuthHttp2Code,
                    "Windows authentication needs HTTP/1.1, so it cannot be used with HTTP/2 only; nothing was sent.");
            }

            if (windows && drafts.Any(draft => draft.HasAnyHeader(AuthorizationHeader)))
            {
                throw new ReplayInputException(
                    WindowsAuthAuthorizationCode,
                    "A request that includes its own Authorization header cannot also use Windows authentication; nothing " +
                        "was sent.");
            }
        }

        private static void CheckConnectTo(IEnumerable<RequestReplayDraft> drafts, ReplayConnectTo connectTo)
        {
            if (connectTo.IsDenied)
            {
                throw ConnectToDenied();
            }

            if (!drafts.All(IsHttps))
            {
                throw ConnectToHttpsRequired();
            }

            // One origin (ReplayAuthority.SameOrigin): every request's is the first's. A request whose host has no canonical
            // form is no origin, not even its own, so a run with one is refused rather than counted with another.
            var first = new Uri(drafts.First().Url);
            if (drafts.Any(draft => !ReplayAuthority.SameOrigin(first, new Uri(draft.Url))))
            {
                throw new ReplayInputException(
                    ConnectToOriginCountCode,
                    "Connecting to another endpoint needs the requests of a run to share one origin; nothing was sent.");
            }
        }

        private static bool IsHttps(RequestReplayDraft draft) =>
            Uri.TryCreate(draft.Url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps;

        // A raw request's frozen-value checks: the image's SHA-256 recomputed (defense in
        // depth: admission is the image's type), the options that Raw mode never takes, connect-to's own rules, and a target
        // that no request may go to. Nothing is resolved, connected or written before every stage has passed.
        private void CheckRawValues(
            RawRequestImage image, ReplayEdits edits, ReplayOptions options, CancellationToken cancellationToken)
        {
            string digest = RawDigest != null ? RawDigest(image, cancellationToken) : image.ComputeSha256(cancellationToken);
            if (!string.Equals(digest, image.Sha256, StringComparison.Ordinal))
            {
                throw RawRefusal(RawCodes.Changed);
            }

            ReplayTransportOptions transport = options.EffectiveTransport;
            string refused =
                transport.Proxy != null ? RawCodes.Proxy :
                transport.ClientCertificate != null ? RawCodes.ClientCert :
                transport.WindowsAuth ? RawCodes.WindowsAuth :
                transport.HttpVersion != ReplayHttpVersion.Http11 ? RawCodes.HttpVersion :
                options.MaxRedirects != 0 ? RawCodes.Redirects :
                options.Verify || options.VerifyDiff || !(edits ?? ReplayEdits.None).Equals(ReplayEdits.None)
                    ? RawCodes.UnsupportedOption
                    : null;
            if (refused != null)
            {
                throw RawRefusal(refused);
            }

            if (transport.ConnectTo != null && transport.ConnectTo.IsDenied)
            {
                throw ConnectToDenied();
            }

            if (transport.ConnectTo != null && !image.Target.IsHttps)
            {
                throw ConnectToHttpsRequired();
            }

            if (image.Target.IsDenied)
            {
                throw RawRefusal(RawCodes.TargetDenied);
            }
        }

        // A raw request's consent checks: each consent on its own, in this order. The raw consent covers
        // this image and target only (ReplayRawConsent.Covers).
        private static void CheckRawConsents(ReplaySource source, ReplayOptions options)
        {
            RawRequestImage image = source.Raw;
            if (options.AllowRaw?.Covers(image) != true)
            {
                throw RawRefusal(RawCodes.PolicyRaw);
            }

            if (image.UsesUnsafeMethod && !options.AllowUnsafeMethods)
            {
                throw RawRefusal(RawCodes.PolicyRawUnsafe);
            }

            if (RawChangesOrigin(source) && !options.AllowOriginChange)
            {
                throw OriginChangeRefused();
            }

            if (options.EffectiveTransport.ConnectTo != null && !options.AllowConnectTo)
            {
                throw ConnectToRefused();
            }
        }

        // A raw request inherits its provenance origin, and a send to a target of another origin needs consent.
        private static bool RawChangesOrigin(ReplaySource source) =>
            source.ProvenanceOrigin != null &&
            !ReplayAuthority.SameOrigin(source.ProvenanceOrigin, source.Raw.Target.Origin);

        private static ReplayInputException RawRefusal(string code) => new(code, RawRequest.Message(code));

        // One wording for every mode: the three baselines that a request inherits its origin from.
        private static ReplayInputException OriginChangeRefused() =>
            new(
                "replay.policy.origin-change",
                "A selected request would go to another origin than the one it was captured or saved for, or first sent " +
                    "to, with its headers, and origin changes are not allowed; nothing was sent.");

        private static ReplayInputException ConnectToRefused() =>
            new(
                ConnectToPolicyCode,
                "Connecting to another endpoint than the URL's host sends the requests, with their headers and body, to " +
                    "that endpoint, and that was not allowed; nothing was sent.");

        private static ReplayInputException ConnectToDenied() =>
            new(
                ConnectToDeniedCode,
                "The connect-to endpoint is an unspecified, multicast or broadcast address; nothing was sent.");

        private static ReplayInputException ConnectToHttpsRequired() =>
            new(
                ConnectToHttpsCode,
                "Connecting to another endpoint needs HTTPS URLs, so the endpoint must prove that it is the URL's host; " +
                    "nothing was sent.");

        // The origin that a request's credentials would go to, compared as the consent is (ReplayCredentialOrigin); null
        // when the URL cannot have one.
        private static ReplayCredentialOrigin CredentialOriginOf(RequestReplayDraft draft) =>
            Uri.TryCreate(draft.Url, UriKind.Absolute, out Uri uri) ? ReplayCredentialOrigin.Of(uri) : null;

        // Building the request validates the method, URL, headers and body. A Fiddler control header is refused wherever it
        // is in the headers, also when Connection nominates it, which building the request drops without sending it.
        private static void Validate(RequestReplayDraft draft)
        {
            if (draft.Omissions.Any)
            {
                throw new ReplayInputException(
                    RequestFileCodes.RequestIncomplete,
                    "A request loaded from a request file still has parts that the file left out (its method, URL, headers " +
                        "or body); supply them before sending. Nothing was sent.");
            }

            try
            {
                using (draft.CreateRequestToSend())
                {
                    // Built only to be validated.
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
            {
                throw Invalid(ex);
            }

            if (ReplayControlHeaders.Has(draft))
            {
                throw new ReplayInputException(
                    ControlHeaderCode,
                    "A selected request has a Fiddler control header (Fiddler-Host, X-OverrideHost, " +
                        "X-IgnoreCertCNMismatch or X-OverrideGateway), which XMAT does not act on; remove it to send " +
                        "the request. Nothing was sent.");
            }
        }

        private static ReplayInputException Invalid(Exception inner) =>
            new(
                RequestInvalidCode,
                "A selected request is not valid with the edits, such as an invalid URL, method, header or body.",
                inner);

        private async Task<IReadOnlyList<ReplayItemResult>> RunAsync(
            IReadOnlyList<ReplayItem> items,
            ReplayOptions options,
            ReplayClientCredential credential,
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

            // The sink's first failure that is not a cancellation, kept apart from the loop's outcome: Parallel.ForEachAsync
            // reports a loop whose token was cancelled as cancelled, whatever its bodies threw (ForEachAsyncState.Complete),
            // so a sink that failed while the run was being cancelled would go unreported, and the caller would take the
            // sends that its sink holds for every send that began. Once the sink threw anything, a cancellation included, it
            // takes no later send.
            Exception sinkFailure = null;
            bool sinkStopped = false;
            ReplaySendOptions sendOptions = options.ToSendOptions();
            ReplayTransportOptions transport = options.EffectiveTransport;
            Func<int, Task> rawSendEnded = RawSendEnded;
            cancellationToken.ThrowIfCancellationRequested();

            // A raw run, whose one request is raw, sends through a raw sender with the normalized request made once for the
            // run; any other run through a ReplaySender.
            RawRequestImage raw = items[0].Raw;
            RawSender rawSender = null;
            RawSendOptions rawOptions = null;
            if (raw != null)
            {
                rawSender = RawSenderFactory(new RawSenderSetup(transport.ConnectTo, ServerChainPolicyFactory)) ??
                    throw new InvalidOperationException("The raw sender factory returned no sender.");
                rawOptions = new RawSendOptions(
                    options.EffectiveTimeout, options.MaxResponseBytes, raw.ToMessage(), items[0].Provenance);
            }

            using ReplaySender sender = raw != null
                ? null
                : (transport.NeedsOwnSender
                    ? OwnSenderFactory(ReplaySenderSetup.For(transport, credential, ServerChainPolicyFactory, options.AllowWindowsAuth))
                    : SenderFactory()) ??
                    throw new InvalidOperationException("The replay sender factory returned no sender.");
            ReportProgress(context, 0, plan.Count);
            Task loop = Parallel.ForEachAsync(
                plan,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (step, token) =>
                {
                    ReplayExchange exchange;
                    if (step.Item.Raw != null)
                    {
                        // Only a cancellation before the send began throws; a send that began returns its exchange.
                        exchange = await rawSender.SendAsync(step.Item.Raw, rawOptions, token).ConfigureAwait(false);
                        if (rawSendEnded != null)
                        {
                            await rawSendEnded(step.Id).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        exchange = await sender.SendAsync(step.Item.Draft, sendOptions, token).ConfigureAwait(false);
                    }

                    ReplayVerification verification = null;
                    if (options.Verify && step.Item.Verifiable && exchange.Outcome == ReplayOutcome.Completed)
                    {
                        long maxDecodedBytes = Math.Min(options.MaxResponseBytes, MaxVerifyDecodedBytes);
                        if (options.VerifyDiff)
                        {
                            // One comparison gives the verdict and its detail, which only the sink receives.
                            VerifyDiff diff = await VerifyDiffOperation.CompareAsync(
                                new VerifyDiffRequest(step.Item.Original, exchange, maxDecodedBytes, VerifyDiffLimits),
                                token).ConfigureAwait(false);
                            exchange.SetVerifyDiff(diff);
                            verification = diff.Verification;
                        }
                        else
                        {
                            verification = ReplayVerifier.Compare(step.Item.Original, exchange, maxDecodedBytes, token);
                        }
                    }

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
                        ReplayTransportOptions.ResponseVersionName(exchange.Version),
                        transport.ClientCertificate != null,
                        transport.WindowsAuth,
                        exchange.AuthHeaderSchemeObserved,
                        exchange.Raw,
                        exchange.RequestBytesWritten,
                        exchange.RequestWrite,
                        exchange.ResponseFraming);
                    lock (gate)
                    {
                        // A raw send that began is always recorded, even when the run is being cancelled: it
                        // goes to the sink without the token check that other sends pass, and the cancellation propagates
                        // when the loop ends. A sink that failed ends the run with its failure, cancelled or not.
                        if (!exchange.Raw)
                        {
                            token.ThrowIfCancellationRequested();
                        }

                        if (sink != null && !sinkStopped)
                        {
                            try
                            {
                                sink.Add(result, exchange);
                            }
                            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                            {
                                sinkStopped = true;
                                if (ex is OperationCanceledException)
                                {
                                    throw;
                                }

                                sinkFailure = ex;
                                throw new ReplaySinkException(ex);
                            }
                        }

                        results[step.Id - 1] = result;
                        done++;
                        ReportProgress(context, done, plan.Count);
                    }
                });
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sinkFailure != null)
            {
                throw new ReplaySinkException(sinkFailure);
            }

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

        // Raw is a raw request's admitted image and Provenance the origin that it inherits; a raw item has no
        // Draft, and is the run's only request.
        private sealed record ReplayItem(
            string DeviceId,
            long? RequestNumber,
            ProxyConnectionModel Original,
            RequestReplayDraft Draft,
            int? SourceIndex = null,
            long? Sequence = null,
            RawRequestImage Raw = null,
            Uri Provenance = null)
        {
            // Whether verify can compare a replay with the captured response: there is one, with a status, and its body was
            // kept. The GUI records a response without its body with a marker (RequestReplayDraft.ResponseBodyNotKeptHeader),
            // which counts only on an empty body: a stored head can come from a peer, and a forged marker never hides a body
            // from verify. A response that was not captured is not verified, never taken for a status that differs.
            internal bool Verifiable =>
                Original != null &&
                int.TryParse(Original.Status, NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                !(Original.ResponseHeaders.ContainsKey(RequestReplayDraft.ResponseBodyNotKeptHeader) &&
                    (Original.ResponseBody?.Length ?? 0) == 0);
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
