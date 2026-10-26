// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    internal sealed class RequestReplayResult
    {
        internal ServerResponse Response { get; init; }
        internal TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Sends standalone replays without invoking capture callbacks or modifying stored traffic.
    /// </summary>
    internal sealed class RequestReplayClient : IDisposable
    {
        private readonly HttpClient _client;
        private readonly bool _ownsClient;

        internal RequestReplayClient()
        {
            // Bound both waiting time and buffered response size for interactive sends.
            _client = new HttpClient(CreateHandler())
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 16 * 1024 * 1024
            };
            _ownsClient = true;
        }

        internal RequestReplayClient(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        // Avoid looping through Local PC capture, sharing cookies, or forwarding credentials via redirects.
        // Certificate validation remains the platform default; response encodings stay available to inspectors.
        internal static SocketsHttpHandler CreateHandler() => new()
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };

        internal async Task<RequestReplayResult> SendAsync(
            RequestReplayDraft draft, bool confirmed, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            using var request = draft.CreateRequest();
            if (draft.RequiresConfirmation && !confirmed)
                throw new InvalidOperationException("Confirm a potentially destructive request before sending it.");

            var stopwatch = Stopwatch.StartNew();
            // Buffer within SendAsync so its timeout and cancellation cover the response body as well as headers.
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            // HTTP error responses are useful debugging results, not transport failures.
            var result = new ServerResponse
            {
                Version = response.Version.Minor == 0
                    ? $"HTTP/{response.Version.Major}"
                    : $"HTTP/{response.Version.Major}.{response.Version.Minor}",
                Status = ((int)response.StatusCode).ToString(),
                StatusDescription = response.ReasonPhrase,
                BodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            };
            result.Headers.CopyFrom(response.Headers);
            result.ContentHeaders.CopyFrom(response.Content.Headers);
            return new RequestReplayResult { Response = result, Duration = stopwatch.Elapsed };
        }

        public void Dispose()
        {
            // Injected clients belong to their callers; only the default transport is owned here.
            if (_ownsClient)
                _client.Dispose();
        }
    }
}
