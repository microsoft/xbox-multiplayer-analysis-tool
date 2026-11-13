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

    // What one composer run did: the operation's result, with the outcome of every send but none of their messages; how
    // many sends ended, also when the run was cancelled; and the last send with its messages, the only one that the window
    // shows. Every send also went to the recorder, if any, as it ended.
    internal sealed record RequestReplayBatchResult(
        OperationResult<ReplayResult> Result,
        int Sends,
        ReplayItemResult LastItem,
        ReplayExchange LastExchange)
    {
        internal bool WasCancelled => Result.Outcome == OperationOutcome.Cancelled;
    }

    /// <summary>
    /// The composer's adapter over the shared ReplayOperation (the command line's is CliReplayCommand): checks and sends a
    /// draft, or one request per number of a sequence, without invoking capture callbacks or changing captured traffic,
    /// and passes every send to the window's recorder for the Replays tab. SendPlanAsync keeps the outcome of every send
    /// but the messages of the last one only; SendAsync sends one draft and reports failures as exceptions.
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

        // Creates the shared operation that every check and send goes through; tests observe it.
        internal Func<ReplayOperation> OperationFactory { get; set; } = static () => new ReplayOperation();

        // Whether Dispose has run. A composer disposes the client it owns once its run has ended, not while it sends.
        internal bool IsDisposed { get; private set; }

        // The composer's options: one request at a time, each sent once, with the 30-second timeout and the 16 MiB response
        // limit, following at most maxRedirects redirects, which replay follows within the request's origin only, over the
        // transport chosen (null for the default: the HTTP version chosen automatically, directly to the URL's host). A plan
        // that the user confirmed gets the permission that its preview said it needs, and no other.
        internal static ReplayOptions ComposerOptions(
            int maxRedirects, ReplayTransportOptions transport = null, ReplayPlanPreview confirmed = null) =>
            new(
                MaxRedirects: maxRedirects,
                AllowUnsafeMethods: confirmed?.UsesUnsafeMethod == true,
                AllowOriginChange: confirmed?.ChangesOrigin == true,
                AllowConnectTo: confirmed?.UsesConnectTo == true,
                Transport: transport);

        // Checks a composed request, or one per number of a sequence, as sending it would, without sending anything or
        // expanding the sequence: the size of the plan, the request, the transport and the permission they need, with a
        // detached copy of the draft and the transport to send.
        internal OperationResult<ReplayPlanPreview> Preview(
            ReplaySource source, int maxRedirects, ReplayTransportOptions transport = null) =>
            CreateOperation().Preview(source, ComposerOptions(maxRedirects, transport));

        // Sends a previewed plan in one operation, which checks it all again first, over the transport that the preview
        // checked (ReplayPlanPreview.Transport), never the composer's controls as they are now. confirmed says that the user
        // accepted what the preview said the plan needs. recorder, when given, receives every send as it ends, and progress
        // the number of sends that have ended.
        internal async Task<RequestReplayBatchResult> SendPlanAsync(
            ReplayPlanPreview plan,
            int maxRedirects,
            bool confirmed,
            CancellationToken cancellationToken,
            IReplayExchangeSink recorder = null,
            IProgress<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var sink = new LastExchange(recorder, progress);
            OperationResult<ReplayResult> result = await RunAsync(
                plan.ToSource(),
                ComposerOptions(maxRedirects, plan.Transport, confirmed ? plan : null),
                sink,
                cancellationToken).ConfigureAwait(false);
            return new RequestReplayBatchResult(result, sink.Sends, sink.Item, sink.Exchange);
        }

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
            var sink = new LastExchange(recorder, progress: null);
            OperationResult<ReplayResult> result = await RunAsync(
                ReplaySource.FromDraft(draft),
                new ReplayOptions(AllowUnsafeMethods: confirmed, AllowOriginChange: confirmed),
                sink,
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
                    return ToResult(exchange);
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
            IsDisposed = true;
            if (_ownsInvoker)
                _invoker.Dispose();
        }

        // A send that got a response, as the window shows it.
        internal static RequestReplayResult ToResult(ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            return new RequestReplayResult { Response = ToServerResponse(exchange), Duration = exchange.Duration };
        }

        private ReplayOperation CreateOperation() =>
            OperationFactory() ?? throw new InvalidOperationException("The replay operation factory returned no operation.");

        private Task<OperationResult<ReplayResult>> RunAsync(
            ReplaySource source,
            ReplayOptions options,
            LastExchange sink,
            CancellationToken cancellationToken)
        {
            ReplayOperation operation = CreateOperation();
            operation.SenderFactory = () => new ReplaySender(_invoker);
            return operation.ExecuteAsync(
                new ReplayRequest(source, options, ReplayEdits.None, sink), OperationContext.None, cancellationToken);
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

        // Keeps the last send for the window, and no other, counts the sends and passes each on to the recorder, if any,
        // then reports the count. The operation passes the sends one at a time.
        private sealed class LastExchange : IReplayExchangeSink
        {
            private readonly IReplayExchangeSink _recorder;
            private readonly IProgress<int> _progress;

            internal LastExchange(IReplayExchangeSink recorder, IProgress<int> progress)
            {
                _recorder = recorder;
                _progress = progress;
            }

            internal int Sends { get; private set; }

            internal ReplayItemResult Item { get; private set; }

            internal ReplayExchange Exchange { get; private set; }

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                Item = result;
                Exchange = exchange;
                Sends++;
                _recorder?.Add(result, exchange);
                _progress?.Report(Sends);
            }
        }
    }
}
