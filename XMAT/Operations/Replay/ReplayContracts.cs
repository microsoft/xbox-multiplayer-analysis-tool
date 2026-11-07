// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Replay
{
    // What to send: requests selected from a capture file, captured requests the caller already holds (the GUI), one
    // composed request, or one composed request per number of a sequence.
    internal sealed class ReplaySource
    {
        private ReplaySource(
            string path,
            ReplaySelection selection,
            string deviceId,
            AnalysisLimits limits,
            IReadOnlyList<ProxyConnectionModel> captures,
            RequestReplayDraft draft,
            ReplaySequence sequence = null)
        {
            Path = path;
            Selection = selection;
            DeviceId = deviceId;
            Limits = limits;
            Captures = captures;
            Draft = draft;
            Sequence = sequence;
        }

        internal string Path { get; }

        internal ReplaySelection Selection { get; }

        // The ordinal device id (d1, d2, ...) that analysis reports for the capture file, or null for every device.
        internal string DeviceId { get; }

        // How the capture file is read; null for the analysis defaults.
        internal AnalysisLimits Limits { get; }

        internal IReadOnlyList<ProxyConnectionModel> Captures { get; }

        internal RequestReplayDraft Draft { get; }

        // With Draft as the template whose every '#' each number replaces; null for the other sources.
        internal ReplaySequence Sequence { get; }

        internal static ReplaySource FromFile(
            string path, ReplaySelection selection, string deviceId = null, AnalysisLimits limits = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(selection);
            return new ReplaySource(path, selection, deviceId, limits, null, null);
        }

        internal static ReplaySource FromCaptures(IReadOnlyList<ProxyConnectionModel> captures)
        {
            ArgumentNullException.ThrowIfNull(captures);
            return new ReplaySource(null, null, null, null, captures, null);
        }

        // The draft becomes the only request; it has no captured response to verify against.
        internal static ReplaySource FromDraft(RequestReplayDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);
            return new ReplaySource(null, null, null, null, null, draft);
        }

        // One request per number of the sequence, in ascending order: the template with every '#' in its URL replaced by
        // the number. The operation checks the plan size before it expands the template.
        internal static ReplaySource FromTemplate(RequestReplayDraft template, ReplaySequence sequence)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(sequence);
            return new ReplaySource(null, null, null, null, null, template, sequence);
        }
    }

    // Changes applied to every selected request before anything is sent. Url replaces the whole URL (one request only);
    // Origin replaces the scheme, host and port and keeps each request's path and query. An edit that supplies a part that
    // a request file left out completes it (Method, Url, Body); ClearHeaders replaces every header line with none, which
    // completes a header block that a file left out, before RemoveHeaders and SetHeaders apply. Form is a built multipart
    // form, in place of Body: its Content-Type replaces every Content-Type line, then it completes the body
    // (MultipartForm.ApplyTo), which a request whose header block is left out, or whose Connection nominates Content-Type,
    // refuses.
    internal sealed record ReplayEdits(
        string Url = null,
        string Origin = null,
        string Method = null,
        IReadOnlyList<KeyValuePair<string, string>> SetHeaders = null,
        IReadOnlyList<string> RemoveHeaders = null,
        byte[] Body = null,
        bool Unconditional = false,
        bool ClearHeaders = false,
        MultipartFormBody Form = null)
    {
        internal static ReplayEdits None { get; } = new();
    }

    // A request prepared to be saved or exported, never sent (ReplayOperation.PrepareForExport): its draft, and the device
    // and number of the captured request that it came from, or null for a composed one.
    internal sealed record ReplayExportItem(string DeviceId, long? RequestNumber, RequestReplayDraft Draft);

    internal sealed record ReplayExport(string InputKind, string InputSha256, IReadOnlyList<ReplayExportItem> Items);

    // VerifyDiff also builds the differences of each verified send (VerifyDiffOperation) and hands them to the sink with its
    // exchange (ReplayExchange.VerifyDiff), never in the result; it needs Verify. AllowClientCertificate is the one origin
    // that the transport's client certificate may go to, and AllowWindowsAuth the one origin whose challenges Windows
    // authentication may answer; the run must be to exactly that origin. An allowance that the transport does not use
    // grants nothing.
    internal sealed record ReplayOptions(
        int Repeat = 1,
        int Parallelism = 1,
        bool Verify = false,
        TimeSpan? Timeout = null,
        long MaxResponseBytes = ReplaySendOptions.DefaultMaxResponseBytes,
        int MaxRedirects = 0,
        bool AllowUnsafeMethods = false,
        bool AllowOriginChange = false,
        bool AllowConnectTo = false,
        ReplayTransportOptions Transport = null,
        bool VerifyDiff = false,
        bool AllowProxy = false,
        ReplayCredentialOrigin AllowClientCertificate = null,
        ReplayCredentialOrigin AllowWindowsAuth = null)
    {
        internal const int MaxRepeat = 1000;
        internal const int MaxParallelism = 16;
        internal const int MaxSends = 10_000;
        internal const int MaxRedirectLimit = 10;
        internal const long MaxResponseLimit = 512L * 1024 * 1024;

        // The responses that sends in flight may hold together: Parallelism x MaxResponseBytes. A payload budget, not a
        // bound on the process's memory.
        internal const long MaxInFlightResponseBytes = 1024L * 1024 * 1024;

        // Reissue and verify decodes each body to compare it up to this size, or MaxResponseBytes if smaller.
        internal const long MaxVerifyDecodedBytes = 64L * 1024 * 1024;

        // A sequence's expanded URLs together, in characters (32 MiB as UTF-16).
        internal const long MaxSequenceUrlCharacters = 16L * 1024 * 1024;

        internal static TimeSpan MaxTimeout { get; } = TimeSpan.FromMinutes(10);

        internal static ReplayOptions Default { get; } = new();

        internal TimeSpan EffectiveTimeout => Timeout ?? ReplaySendOptions.DefaultTimeout;

        // How the requests reach the server; never null. Windows authentication needs HTTP/1.1, so a run with it whose
        // version is chosen automatically asks for exactly HTTP/1.1; one that asked for HTTP/2 only keeps it, and the
        // operation refuses it (ReplayOperation.WindowsAuthHttp2Code).
        internal ReplayTransportOptions EffectiveTransport =>
            Transport is { WindowsAuth: true, HttpVersion: ReplayHttpVersion.Auto }
                ? Transport with { HttpVersion = ReplayHttpVersion.Http11 }
                : Transport ?? ReplayTransportOptions.Default;

        // AllowOriginChange applies to the requests the caller edits: replay never follows a redirect to another origin.
        internal ReplaySendOptions ToSendOptions() =>
            new(EffectiveTimeout, MaxResponseBytes, MaxRedirects, EffectiveTransport.HttpVersion);

        internal bool IsValid =>
            Repeat is >= 1 and <= MaxRepeat &&
            Parallelism is >= 1 and <= MaxParallelism &&
            EffectiveTimeout > TimeSpan.Zero && EffectiveTimeout <= MaxTimeout &&
            MaxResponseBytes is > 0 and <= MaxResponseLimit &&
            (long)Parallelism * MaxResponseBytes <= MaxInFlightResponseBytes &&
            MaxRedirects is >= 0 and <= MaxRedirectLimit &&
            (Verify || !VerifyDiff);
    }

    // Receives each send as it completes, one call at a time, so it can write or show the messages without keeping them
    // all in memory. An exception from the sink ends the run and propagates to the caller of ExecuteAsync.
    internal interface IReplayExchangeSink
    {
        void Add(ReplayItemResult result, ReplayExchange exchange);
    }

    internal sealed record ReplayRequest(
        ReplaySource Source,
        ReplayOptions Options = null,
        ReplayEdits Edits = null,
        IReplayExchangeSink Sink = null);

    // What a composed request, or one per number of a sequence, would send (ReplayOperation.Preview). Template is a copy
    // of the draft that was checked, so later edits to that draft change nothing here; send it with ToSource and
    // Transport, which is how the plan was checked to reach the server. Sends counts every send, repeats included.
    // UsesUnsafeMethod, ChangesOrigin, UsesConnectTo, UsesProxy, UsesClientCertificate and UsesWindowsAuth say which consent
    // the options must carry (AllowUnsafeMethods, AllowOriginChange, AllowConnectTo, AllowProxy, and AllowClientCertificate
    // and AllowWindowsAuth naming CredentialOrigin, the one origin of the plan) for the operation to send them; it checks
    // them again when it runs.
    internal sealed record ReplayPlanPreview(
        RequestReplayDraft Template,
        ReplaySequence Sequence,
        int Sends,
        bool UsesUnsafeMethod,
        bool ChangesOrigin,
        ReplayTransportOptions Transport = null,
        ReplayCredentialOrigin CredentialOrigin = null)
    {
        internal bool UsesConnectTo => Transport?.ConnectTo != null;

        internal bool UsesProxy => Transport?.Proxy != null;

        internal bool UsesClientCertificate => Transport?.ClientCertificate != null;

        internal bool UsesWindowsAuth => Transport?.WindowsAuth == true;

        internal ReplaySource ToSource() =>
            Sequence == null ? ReplaySource.FromDraft(Template) : ReplaySource.FromTemplate(Template, Sequence);
    }

    // One send, without its messages. Id numbers the sends of a run from 1, in plan order: every selected request once per
    // iteration, iterations in turn. DeviceId and RequestNumber are null for a composed request. SourceIndex is the
    // position of the sent request in ReplaySource.Captures (the GUI's rows), and null for other sources. Sequence is the
    // number that a sequence put in the URL, and null for other sources. Transport is how the send was configured to reach
    // the server ("direct", "connect-to" or "proxy"), HttpVersion the version it asked for ("auto", "1.1" or "2") and
    // ResponseVersion the version of the response that it got ("1.1" or "2"), null without a response.
    // ClientCertificateConfigured says that the send could offer a client certificate, never that one was sent: a server
    // that does not ask for one, or that fails the check, receives none. WindowsAuthConfigured says that the send could
    // answer its origin's Negotiate and NTLM challenges, never that it did; AuthHeaderSchemeObserved is the Authorization
    // label ("Negotiate" or "NTLM") that its final request carried, or null when it carried neither, which proves neither
    // that authentication succeeded nor which package was negotiated, and null is no proof that none was used.
    internal sealed record ReplayItemResult(
        int Id,
        string DeviceId,
        long? RequestNumber,
        int Iteration,
        ReplayOutcome Outcome,
        int StatusCode,
        TimeSpan Duration,
        long ResponseBytes,
        int Redirects,
        string Code,
        ReplayVerification Verification,
        int? SourceIndex = null,
        long? Sequence = null,
        string Transport = ReplayTransportOptions.DirectName,
        string HttpVersion = ReplayTransportOptions.AutoVersionName,
        string ResponseVersion = null,
        bool ClientCertificateConfigured = false,
        bool WindowsAuthConfigured = false,
        string AuthHeaderSchemeObserved = null);

    // InputKind and InputSha256 describe a capture file and are null for other sources.
    internal sealed record ReplayResult(string InputKind, string InputSha256, IReadOnlyList<ReplayItemResult> Items)
    {
        internal int Completed => Items.Count(item => item.Outcome == ReplayOutcome.Completed);

        internal int Failed => Items.Count - Completed;

        internal int Matched => Items.Count(item => item.Verification?.Matches == true);

        internal int Mismatched => Items.Count(item => item.Verification is { Matches: false });
    }
}
