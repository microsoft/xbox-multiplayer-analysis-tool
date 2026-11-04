// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
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
    /// The composer's adapter over the shared ReplayOperation (the command line's is CliReplayCommand): sends one draft
    /// without invoking capture callbacks or changing captured traffic, passes the send to the window's recorder for the
    /// Replays tab, and reports failures as exceptions for the window to show.
    /// </summary>
    internal sealed class RequestReplayClient : IDisposable
    {
        private readonly HttpMessageInvoker _invoker;
        private readonly bool _ownsInvoker;

        internal RequestReplayClient()
        {
            // One transport for the window's lifetime, so repeated sends can reuse connections.
            _invoker = new HttpMessageInvoker(CreateHandler(), disposeHandler: true);
            _ownsInvoker = true;
        }

        // Injected clients belong to their callers.
        internal RequestReplayClient(HttpClient client)
        {
            _invoker = client ?? throw new ArgumentNullException(nameof(client));
        }

        // Avoid looping through Local PC capture, sharing cookies, or forwarding credentials via redirects.
        // Certificate validation remains the platform default; response encodings stay available to inspectors.
        internal static SocketsHttpHandler CreateHandler() => ReplaySender.CreateHandler();

        // Creates the shared operation that every send goes through; tests observe it.
        internal Func<ReplayOperation> OperationFactory { get; set; } = static () => new ReplayOperation();

        // recorder, when given, also receives the send, for the Replays tab, whatever its outcome.
        internal async Task<RequestReplayResult> SendAsync(
            RequestReplayDraft draft,
            bool confirmed,
            CancellationToken cancellationToken,
            IReplayExchangeSink recorder = null)
        {
            ArgumentNullException.ThrowIfNull(draft);
            using (draft.CreateRequestToSend())
            {
                // Invalid input is reported, with what is wrong, before the confirmation check.
            }

            if (draft.RequiresConfirmation && !confirmed)
                throw new InvalidOperationException("Confirm a potentially destructive request before sending it.");

            // The user's confirmation is the permission that xmat.exe takes from --allow-unsafe-methods and
            // --allow-origin-change. Bound both waiting time and buffered response size for interactive sends.
            var sink = new LastExchange(recorder);
            ReplayOperation operation = OperationFactory() ??
                throw new InvalidOperationException("The replay operation factory returned no operation.");
            operation.SenderFactory = () => new ReplaySender(_invoker);
            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(draft),
                    new ReplayOptions(AllowUnsafeMethods: confirmed, AllowOriginChange: confirmed),
                    ReplayEdits.None,
                    sink),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == OperationOutcome.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            if (!result.Succeeded || sink.Exchange == null)
            {
                throw new InvalidOperationException(
                    result.Diagnostics.Count > 0 ? result.Diagnostics[0].Message : "The request could not be sent.");
            }

            ReplayExchange exchange = sink.Exchange;
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
            if (_ownsInvoker)
                _invoker.Dispose();
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

        // Keeps the send for the window and passes it on to the recorder, if any.
        private sealed class LastExchange : IReplayExchangeSink
        {
            private readonly IReplayExchangeSink _recorder;

            internal LastExchange(IReplayExchangeSink recorder)
            {
                _recorder = recorder;
            }

            internal ReplayExchange Exchange { get; private set; }

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                Exchange = exchange;
                _recorder?.Add(result, exchange);
            }
        }
    }
}
