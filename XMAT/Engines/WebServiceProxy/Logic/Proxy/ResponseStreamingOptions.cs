// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Net.Http.Headers;
using System.Threading;

namespace XMAT.WebServiceCapture.Proxy
{
    /// <summary>
    /// Fiddler-style response streaming choices from Preferences > Web Proxy. Responses are
    /// buffered so scripts can change them. Event streams, audio and video (when enabled), every
    /// response (when streaming all) and bodies that pass the capture limit stream as they arrive.
    /// </summary>
    internal sealed record ResponseStreamingOptions
    {
        internal const int DefaultCaptureLimitMegabytes = 32;
        internal const int MinimumCaptureLimitMegabytes = 1;

        // A 40 MiB response peaked at about 1.5 GB through the full capture path; 48 MiB exceeded 2 GB. As base64, a
        // 32 MiB request plus a 40 MiB response also stays below xmat's 128 MiB row limit.
        internal const int MaximumCaptureLimitMegabytes = 40;

        private const int BytesPerMegabyte = 1024 * 1024;

        private static ResponseStreamingOptions s_current = new();

        internal bool AutoStreamMediaAndEvents { get; init; } = true;

        internal bool StreamAllResponses { get; init; }

        // The most body bytes kept for the capture; longer bodies are forwarded in full but stored truncated.
        internal int CaptureLimitBytes { get; init; } = DefaultCaptureLimitMegabytes * BytesPerMegabyte;

        // Used by proxies without their own options; the preferences replace it on load and save.
        internal static ResponseStreamingOptions Current
        {
            get => Volatile.Read(ref s_current);
            set => Volatile.Write(ref s_current, value ?? new ResponseStreamingOptions());
        }

        internal static ResponseStreamingOptions FromPreferences(
            bool autoStreamMediaAndEvents,
            bool streamAllResponses,
            int captureLimitMegabytes) =>
            new()
            {
                AutoStreamMediaAndEvents = autoStreamMediaAndEvents,
                StreamAllResponses = streamAllResponses,
                CaptureLimitBytes = ClampMegabytes(captureLimitMegabytes) * BytesPerMegabyte,
            };

        internal static int ClampMegabytes(int megabytes) =>
            Math.Clamp(megabytes, MinimumCaptureLimitMegabytes, MaximumCaptureLimitMegabytes);

        // Fiddler streams audio and video automatically; server-sent events never end, so they stream too.
        internal static bool IsAutoStreamedType(MediaTypeHeaderValue contentType)
        {
            string mediaType = contentType?.MediaType;
            return mediaType != null &&
                (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        }
    }
}
