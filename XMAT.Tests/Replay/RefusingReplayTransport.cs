// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // The transport of the composer's Raw fixtures: every route that a run of theirs can take, other than the
    // one that a fixture scripts, ends here at once, in memory, with no socket, name lookup or timeout. A Parsed send over
    // the client's own invoker (RequestReplayClient.RunAsync's SenderFactory) or over a run's own sender, for Connect to
    // another endpoint (ReplayOperation.OwnSenderFactory), reaches a handler that notes it and refuses it with an
    // HttpRequestException. A raw send's name lookup and TCP connect (RawSenderSetup.Resolver and Connector) are noted and
    // refused with an IOException, unless the fixture scripts them over these. A test disposes the clients that it makes
    // from the transport, then the transport, which disposes its handler.
    internal sealed class RefusingReplayTransport : IDisposable
    {
        // The message of every refusal.
        internal const string Refusal = "Refused by the test's transport.";

        private readonly List<string> _attempts = new();
        private readonly HttpClient _http;

        internal RefusingReplayTransport()
        {
            _http = new HttpClient(new Handler(Note), disposeHandler: true);
        }

        // What reached the transport, in order: "own" and the endpoint when a run made its own sender, then each Parsed
        // request's method and URL; or "lookup" and the name, or "connect" and the endpoint, of a raw send.
        internal IReadOnlyList<string> Attempts
        {
            get
            {
                lock (_attempts)
                {
                    return _attempts.ToArray();
                }
            }
        }

        // A composer client over the transport, whose runs use Operation(raw, configure). The test disposes it.
        internal RequestReplayClient Client(
            Func<RawSenderSetup, RawSenderSetup> raw = null, Action<ReplayOperation> configure = null)
        {
            var client = new RequestReplayClient(_http);
            client.OperationFactory = () => Operation(raw, configure);
            return client;
        }

        // An operation whose own senders send through the transport, and whose raw senders look up and connect through raw,
        // applied over the transport's refusals, so that whatever raw does not script is refused. configure sets the rest.
        internal ReplayOperation Operation(
            Func<RawSenderSetup, RawSenderSetup> raw = null, Action<ReplayOperation> configure = null)
        {
            var operation = new ReplayOperation
            {
                OwnSenderFactory = setup =>
                {
                    Note("own " + (setup.ConnectTo?.ToString() ?? setup.Proxy?.ToString() ?? "sender"));
                    return new ReplaySender(_http, ownsInvoker: false, setup);
                },
                RawSenderFactory = setup =>
                {
                    RawSenderSetup refused = setup with { Resolver = RefuseLookupAsync, Connector = RefuseConnectAsync };
                    return new RawSender(raw == null ? refused : raw(refused));
                },
            };
            configure?.Invoke(operation);
            return operation;
        }

        public void Dispose() => _http.Dispose();

        private Task<IPAddress[]> RefuseLookupAsync(string host, CancellationToken token)
        {
            Note("lookup " + host);
            return Task.FromException<IPAddress[]>(new IOException(Refusal));
        }

        private Task<Stream> RefuseConnectAsync(IPEndPoint endpoint, CancellationToken token)
        {
            Note("connect " + endpoint);
            return Task.FromException<Stream>(new IOException(Refusal));
        }

        private void Note(string attempt)
        {
            lock (_attempts)
            {
                _attempts.Add(attempt);
            }
        }

        // Notes each request that reaches it and refuses it at once.
        private sealed class Handler(Action<string> note) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                note(request.Method.Method + " " + request.RequestUri);
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(Refusal));
            }
        }
    }
}
