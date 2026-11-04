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
    // What to send: requests selected from a capture file, captured requests the caller already holds (the GUI), or one
    // composed request.
    internal sealed class ReplaySource
    {
        private ReplaySource(
            string path,
            ReplaySelection selection,
            string deviceId,
            AnalysisLimits limits,
            IReadOnlyList<ProxyConnectionModel> captures,
            RequestReplayDraft draft)
        {
            Path = path;
            Selection = selection;
            DeviceId = deviceId;
            Limits = limits;
            Captures = captures;
            Draft = draft;
        }

        internal string Path { get; }

        internal ReplaySelection Selection { get; }

        // The ordinal device id (d1, d2, ...) that analysis reports for the capture file, or null for every device.
        internal string DeviceId { get; }

        // How the capture file is read; null for the analysis defaults.
        internal AnalysisLimits Limits { get; }

        internal IReadOnlyList<ProxyConnectionModel> Captures { get; }

        internal RequestReplayDraft Draft { get; }

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
    }

    // Changes applied to every selected request before anything is sent. Url replaces the whole URL (one request only);
    // Origin replaces the scheme, host and port and keeps each request's path and query.
    internal sealed record ReplayEdits(
        string Url = null,
        string Origin = null,
        string Method = null,
        IReadOnlyList<KeyValuePair<string, string>> SetHeaders = null,
        IReadOnlyList<string> RemoveHeaders = null,
        byte[] Body = null,
        bool Unconditional = false)
    {
        internal static ReplayEdits None { get; } = new();
    }

    internal sealed record ReplayOptions(
        int Repeat = 1,
        int Parallelism = 1,
        bool Verify = false,
        TimeSpan? Timeout = null,
        long MaxResponseBytes = ReplaySendOptions.DefaultMaxResponseBytes,
        int MaxRedirects = 0,
        bool AllowUnsafeMethods = false,
        bool AllowOriginChange = false)
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

        internal static TimeSpan MaxTimeout { get; } = TimeSpan.FromMinutes(10);

        internal static ReplayOptions Default { get; } = new();

        internal TimeSpan EffectiveTimeout => Timeout ?? ReplaySendOptions.DefaultTimeout;

        // AllowOriginChange applies to the requests the caller edits: replay never follows a redirect to another origin.
        internal ReplaySendOptions ToSendOptions() => new(EffectiveTimeout, MaxResponseBytes, MaxRedirects);

        internal bool IsValid =>
            Repeat is >= 1 and <= MaxRepeat &&
            Parallelism is >= 1 and <= MaxParallelism &&
            EffectiveTimeout > TimeSpan.Zero && EffectiveTimeout <= MaxTimeout &&
            MaxResponseBytes is > 0 and <= MaxResponseLimit &&
            (long)Parallelism * MaxResponseBytes <= MaxInFlightResponseBytes &&
            MaxRedirects is >= 0 and <= MaxRedirectLimit;
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

    // One send, without its messages. Id numbers the sends of a run from 1, in plan order: every selected request once per
    // iteration, iterations in turn. DeviceId and RequestNumber are null for a composed request. SourceIndex is the
    // position of the sent request in ReplaySource.Captures (the GUI's rows), and null for other sources.
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
        int? SourceIndex = null);

    // InputKind and InputSha256 describe a capture file and are null for other sources.
    internal sealed record ReplayResult(string InputKind, string InputSha256, IReadOnlyList<ReplayItemResult> Items)
    {
        internal int Completed => Items.Count(item => item.Outcome == ReplayOutcome.Completed);

        internal int Failed => Items.Count - Completed;

        internal int Matched => Items.Count(item => item.Verification?.Matches == true);

        internal int Mismatched => Items.Count(item => item.Verification is { Matches: false });
    }
}
