// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    internal sealed class RequestReplayResult
    {
        internal ServerResponse Response { get; init; }
        internal TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// The composer's adapter over the shared ReplaySender: sends standalone replays without invoking capture callbacks
    /// or modifying stored traffic, and reports failures as exceptions for the window to show.
    /// </summary>
    internal sealed class RequestReplayClient : IDisposable
    {
        private readonly ReplaySender _sender;

        // Bound both waiting time and buffered response size for interactive sends.
        private readonly ReplaySendOptions _options = ReplaySendOptions.Default;

        internal RequestReplayClient()
        {
            _sender = new ReplaySender();
        }

        // Injected clients belong to their callers.
        internal RequestReplayClient(HttpClient client)
        {
            _sender = new ReplaySender(client ?? throw new ArgumentNullException(nameof(client)));
        }

        // Avoid looping through Local PC capture, sharing cookies, or forwarding credentials via redirects.
        // Certificate validation remains the platform default; response encodings stay available to inspectors.
        internal static SocketsHttpHandler CreateHandler() => ReplaySender.CreateHandler();

        internal async Task<RequestReplayResult> SendAsync(
            RequestReplayDraft draft, bool confirmed, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            using (draft.CreateRequest())
            {
                // Invalid input is reported before the confirmation check.
            }

            if (draft.RequiresConfirmation && !confirmed)
                throw new InvalidOperationException("Confirm a potentially destructive request before sending it.");

            ReplayExchange exchange = await _sender.SendAsync(draft, _options, cancellationToken).ConfigureAwait(false);
            switch (exchange.Outcome)
            {
                case ReplayOutcome.Completed:
                    // HTTP error responses are useful debugging results, not transport failures.
                    return new RequestReplayResult { Response = ToServerResponse(exchange), Duration = exchange.Duration };
                case ReplayOutcome.TimedOut:
                    // A cancellation that the caller did not request is a timeout, as with HttpClient's own timeout.
                    throw new TaskCanceledException("The request timed out.", new TimeoutException());
                case ReplayOutcome.TooLarge:
                    throw new HttpRequestException("The response is larger than the 16 MiB that the composer keeps.");
                default:
                    if (exchange.Error != null)
                        ExceptionDispatchInfo.Capture(exchange.Error).Throw();
                    throw new HttpRequestException("The request failed.");
            }
        }

        public void Dispose()
        {
            _sender.Dispose();
        }

        private static ServerResponse ToServerResponse(ReplayExchange exchange)
        {
            var response = new ServerResponse
            {
                Version = ReplayMessage.FormatVersion(exchange.Version),
                Status = exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                StatusDescription = exchange.ReasonPhrase,
                BodyBytes = exchange.Response.Body
            };
            foreach (var header in exchange.Response.Headers)
                response.Headers.Add(header.Key, header.Value);
            foreach (var header in exchange.Response.ContentHeaders)
                response.ContentHeaders.Add(header.Key, header.Value);
            return response;
        }
    }
}
