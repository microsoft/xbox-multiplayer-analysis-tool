// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Replay
{
    // How long a raw send may take, from Begin to the response's end, and how much of the response it keeps; the
    // normalized request to record, made once per run (RawRequestImage.ToMessage), and the origin that the request inherits
    // or the target's when it has none.
    internal sealed record RawSendOptions(
        TimeSpan Timeout,
        long MaxResponseBytes,
        ReplayMessage Request = null,
        Uri ProvenanceOrigin = null);

    // The write state of a raw send, from the history of its write calls to the connection's stream.
    internal static class RawWriteState
    {
        internal const string None = "none";
        internal const string Partial = "partial";
        internal const string Uncertain = "uncertain";
        internal const string Complete = "complete";
    }

    // The points of a raw send that tests can act at, to order events deterministically.
    internal enum RawSendPoint
    {
        BeforeBegin,
        AfterBegin,
        BeforeReserve,
        AfterReserve,
        AfterInvoke,
    }

    // Seams for tests only: an action at each point (with the operation, for a lease's points), the clock of the deadline
    // and the grace, and the protocol that a TLS handshake negotiated (the ALPN postcondition).
    internal sealed record RawSenderHooks(
        Action<RawSendPoint, RawOperation?> At = null,
        IRawSendClock Clock = null,
        Func<SslStream, SslApplicationProtocol> NegotiatedProtocol = null);

    // What a raw sender connects with: one connect-to endpoint, or else the target's address or its name's addresses; the
    // name lookup and the TCP connector (which returns the stream that TLS runs over); the chain policy's trust anchors
    // (tests); and the hooks (tests).
    internal sealed record RawSenderSetup(
        ReplayConnectTo ConnectTo = null,
        Func<X509ChainPolicy> ChainPolicyFactory = null,
        Func<string, CancellationToken, Task<IPAddress[]>> Resolver = null,
        Func<IPEndPoint, CancellationToken, Task<Stream>> Connector = null,
        RawSenderHooks Hooks = null)
    {
        internal static RawSenderSetup Direct { get; } = new();

        internal static Task<IPAddress[]> LookUpAsync(string host, CancellationToken token) =>
            Dns.GetHostAddressesAsync(host, token);

        internal static async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    // Sends one raw request image as its exact bytes over a new connection, and reads one response
    // (RawResponseReader). One deadline covers everything from Begin to the response's end. The send begins at the sender's
    // own invocation of the resolver (a DNS name) or the connector (an IP literal or a connect-to endpoint); a cancellation
    // ordered before then throws with no exchange, and every send that began gives exactly one exchange. Every operation
    // goes through the arbiter's leases, observes only the arbiter's token, and is awaited before the exchange is made.
    // TLS offers only http/1.1, never resumes a session, offers no client certificate, and validates with the platform,
    // without fetching any certificate or revocation data that the server's certificate names.
    internal sealed class RawSender
    {
        private readonly RawSenderSetup _setup;

        internal RawSender(RawSenderSetup setup = null)
        {
            _setup = setup ?? RawSenderSetup.Direct;
        }

        private RawSenderHooks Hooks => _setup.Hooks;

        // A send that began always returns its exchange, the caller's cancellation included (replay.send.cancelled), which
        // the caller records before it lets the cancellation propagate. Only a cancellation before Begin throws.
        internal async Task<ReplayExchange> SendAsync(RawRequestImage image, RawSendOptions options, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(options);
            var arbiter = new RawSendArbiter(Hooks?.Clock);
            var send = new Send(image, options, arbiter);
            using CancellationTokenRegistration registration = token.Register(
                () => arbiter.Deliver(RawSendEvent.Cancel));
            token.ThrowIfCancellationRequested();
            Hooks?.At?.Invoke(RawSendPoint.BeforeBegin, null);

            // Begin's step: Begin and the first operation's Reserve, then its Invoke and Attach (RunAsync runs to its first
            // await synchronously), with no cancellation check between them.
            RawOperation first = _setup.ConnectTo != null || image.Target.IsIPLiteral ? RawOperation.Connect : RawOperation.Lookup;
            RawSendArbiter.Lease lease = arbiter.Begin(options.Timeout, first);
            if (lease == null)
            {
                throw new OperationCanceledException(token);
            }

            Hooks?.At?.Invoke(RawSendPoint.AfterBegin, first);
            send.Started = DateTime.UtcNow;
            send.Stopwatch.Start();
            Task pipeline = RunAsync(send, lease);
            await arbiter.Committed.ConfigureAwait(false);
            await arbiter.SettleAsync().ConfigureAwait(false);
            try
            {
                await pipeline.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A consequence of the closing.
            }

            return send.ToExchange();
        }

        // Setup (lookup, connects, TLS and the ALPN check), then Ready and the writer and reader. Every failure is an event
        // that the arbiter orders; once it has committed, the rest is a consequence, and the pipeline only stops.
        private async Task RunAsync(Send send, RawSendArbiter.Lease firstLease)
        {
            RawSendArbiter arbiter = send.Arbiter;
            try
            {
                List<IPEndPoint> endpoints;
                RawSendArbiter.Lease connectLease = null;
                if (firstLease.Operation == RawOperation.Lookup)
                {
                    Task<IPAddress[]> lookup = Start(
                        arbiter, RawOperation.Lookup, t => Resolve(send.Image.Target.Host, t), firstLease);
                    IPAddress[] addresses;
                    try
                    {
                        addresses = await lookup.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        arbiter.Deliver(RawSendEvent.SetupFailure, ReplaySender.SendFailedCode);
                        return;
                    }

                    endpoints = (addresses ?? Array.Empty<IPAddress>())
                        .Where(address => !ReplayConnectTo.IsDeniedAddress(address))
                        .Select(address => new IPEndPoint(address, send.Image.Target.Port))
                        .ToList();
                    if (endpoints.Count == 0)
                    {
                        arbiter.Deliver(RawSendEvent.SetupFailure, RawCodes.TargetDenied);
                        return;
                    }
                }
                else
                {
                    connectLease = firstLease;
                    endpoints = new List<IPEndPoint>
                    {
                        _setup.ConnectTo?.ToEndPoint() ??
                            new IPEndPoint(send.Image.Target.Address, send.Image.Target.Port),
                    };
                }

                Stream connection = null;
                foreach (IPEndPoint endpoint in endpoints)
                {
                    Task<Stream> connect = Start(arbiter, RawOperation.Connect, t => Connect(endpoint, t), connectLease);
                    connectLease = null;
                    if (connect == null)
                    {
                        return;
                    }

                    try
                    {
                        connection = await connect.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Only a failed TCP connect moves on to the next address.
                        if (arbiter.IsCommitted)
                        {
                            return;
                        }

                        continue;
                    }

                    arbiter.Own(connection);
                    break;
                }

                if (connection == null)
                {
                    arbiter.Deliver(RawSendEvent.SetupFailure, ReplaySender.SendFailedCode);
                    return;
                }

                if (send.Image.Target.IsHttps)
                {
                    var tls = new SslStream(connection, leaveInnerStreamOpen: false);
                    arbiter.Own(tls);
                    SslClientAuthenticationOptions tlsOptions = TlsOptions(send.Image.Target);
                    Task handshake = Start(
                        arbiter,
                        RawOperation.Tls,
                        async t =>
                        {
                            await tls.AuthenticateAsClientAsync(tlsOptions, t).ConfigureAwait(false);
                            return true;
                        });
                    if (handshake == null)
                    {
                        return;
                    }

                    try
                    {
                        await handshake.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        arbiter.Deliver(RawSendEvent.SetupFailure, ReplaySender.SendFailedCode);
                        return;
                    }

                    SslApplicationProtocol protocol = Hooks?.NegotiatedProtocol?.Invoke(tls) ?? tls.NegotiatedApplicationProtocol;
                    if (protocol != default && protocol != SslApplicationProtocol.Http11)
                    {
                        arbiter.Deliver(RawSendEvent.SetupFailure, RawCodes.Alpn);
                        return;
                    }

                    connection = tls;
                }

                arbiter.Deliver(RawSendEvent.Ready);
                send.Writes = new WriteCounter(connection);
                Task writer = Start(
                    arbiter,
                    RawOperation.Writer,
                    async t =>
                    {
                        await send.Image.WriteToAsync(send.Writes, null, t).ConfigureAwait(false);
                        return true;
                    });
                _ = writer?.ContinueWith(
                    completed =>
                    {
                        if (!completed.IsCompletedSuccessfully)
                        {
                            arbiter.Deliver(RawSendEvent.WriterFailure);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                bool head = string.Equals(send.Image.Method, "HEAD", StringComparison.Ordinal);
                Task<RawReadResult> reader = Start(
                    arbiter,
                    RawOperation.Reader,
                    t => new RawResponseReader(connection, send.Options.MaxResponseBytes, head).ReadAsync(t));
                _ = reader?.ContinueWith(
                    completed =>
                    {
                        RawReadResult result = completed.IsCompletedSuccessfully
                            ? completed.Result
                            : new RawReadResult(RawReadKind.ReadFailure, null, ReplaySender.SendFailedCode);
                        arbiter.Deliver(
                            result.Kind switch
                            {
                                RawReadKind.Response => RawSendEvent.Response,
                                RawReadKind.ReaderFailure => RawSendEvent.ReaderFailure,
                                _ => RawSendEvent.ReadFailure,
                            },
                            result.Code,
                            result.Response);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                // Anything else ends the send as a failure in the phase that it is in; after the commit, it is ignored.
                arbiter.Deliver(RawSendEvent.SetupFailure, ReplaySender.SendFailedCode);
                arbiter.Deliver(RawSendEvent.ReaderFailure, ReplaySender.SendFailedCode);
            }
        }

        // An operation's lease: Reserve (unless Begin's step already did), Invoke with the arbiter's token, a synchronous
        // throw made the task's fault, then Attach. Null when the arbiter committed before Reserve: never started.
        private Task<T> Start<T>(
            RawSendArbiter arbiter,
            RawOperation operation,
            Func<CancellationToken, Task<T>> invoke,
            RawSendArbiter.Lease reserved = null)
        {
            if (reserved == null)
            {
                Hooks?.At?.Invoke(RawSendPoint.BeforeReserve, operation);
                reserved = arbiter.Reserve(operation);
                if (reserved == null)
                {
                    return null;
                }
            }

            Hooks?.At?.Invoke(RawSendPoint.AfterReserve, operation);
            Task<T> task;
            try
            {
                task = invoke(arbiter.Token);
            }
            catch (Exception exception)
            {
                task = Task.FromException<T>(exception);
            }

            Hooks?.At?.Invoke(RawSendPoint.AfterInvoke, operation);
            reserved.Attach(task);
            return task;
        }

        private Task<IPAddress[]> Resolve(string host, CancellationToken token) =>
            (_setup.Resolver ?? RawSenderSetup.LookUpAsync)(host, token);

        private Task<Stream> Connect(IPEndPoint endpoint, CancellationToken token) =>
            (_setup.Connector ?? RawSenderSetup.ConnectAsync)(endpoint, token);

        private SslClientAuthenticationOptions TlsOptions(ReplayRawTarget target)
        {
            // The trust anchors may come from a test; downloads and revocation fetching are set after it, so no seam turns
            // them back on.
            X509ChainPolicy policy = _setup.ChainPolicyFactory?.Invoke() ?? new X509ChainPolicy();
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            return new SslClientAuthenticationOptions
            {
                TargetHost = target.TlsName,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                AllowTlsResume = false,
                CertificateChainPolicy = policy,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => null,
            };
        }

        // One send's state, read once the arbiter has committed and every operation has settled.
        private sealed class Send
        {
            internal Send(RawRequestImage image, RawSendOptions options, RawSendArbiter arbiter)
            {
                Image = image;
                Options = options;
                Arbiter = arbiter;
            }

            internal RawRequestImage Image { get; }

            internal RawSendOptions Options { get; }

            internal RawSendArbiter Arbiter { get; }

            internal DateTime Started { get; set; }

            internal Stopwatch Stopwatch { get; } = new();

            internal WriteCounter Writes { get; set; }

            internal ReplayExchange ToExchange()
            {
                RawSendArbiter arbiter = Arbiter;
                RawResponse response = arbiter.Cause == RawSendEvent.Response ? arbiter.Response : null;
                ReplayOutcome outcome = arbiter.Cause switch
                {
                    RawSendEvent.Response => response.TooLarge ? ReplayOutcome.TooLarge : ReplayOutcome.Completed,
                    RawSendEvent.Deadline => ReplayOutcome.TimedOut,
                    _ => ReplayOutcome.Failed,
                };
                return new ReplayExchange
                {
                    Outcome = outcome,
                    Code = arbiter.Code,
                    Request = Options.Request ?? Image.ToMessage(),
                    RequestUri = Image.RequestUri,
                    Started = Started,
                    Response = response?.ToMessage(),
                    StatusCode = response?.StatusCode ?? 0,
                    ReasonPhrase = response?.ReasonPhrase,
                    Version = response?.Version,
                    Duration = Stopwatch.Elapsed,
                    Redirects = 0,
                    Error = null,
                    Raw = true,
                    RequestBytesWritten = Writes?.CompletedBytes ?? 0,
                    RequestWrite = Writes?.State(Image.Length) ?? RawWriteState.None,
                    ResponseFraming = response?.Framing,
                    ResponseTrailerFields = response?.TrailerFields ?? 0,
                    ProvenanceOrigin = Options.ProvenanceOrigin ?? Image.Target.Origin,
                };
            }
        }

        // The connection's stream for the writer only: it counts the write calls that started, the bytes of those that
        // completed (even after the commit), and whether any started call faulted or was interrupted.
        private sealed class WriteCounter : Stream
        {
            private readonly Stream _inner;
            private int _started;
            private long _completed;
            private int _faulted;

            internal WriteCounter(Stream inner)
            {
                _inner = inner;
            }

            internal long CompletedBytes => Interlocked.Read(ref _completed);

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            // none: no call ever started; uncertain: a started call faulted or was interrupted; complete: every byte was
            // handed over in calls that completed; partial: writing stopped between calls.
            internal string State(int length)
            {
                if (Volatile.Read(ref _started) == 0)
                {
                    return RawWriteState.None;
                }

                if (Volatile.Read(ref _faulted) != 0)
                {
                    return RawWriteState.Uncertain;
                }

                return CompletedBytes == length ? RawWriteState.Complete : RawWriteState.Partial;
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _started);
                try
                {
                    await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Exchange(ref _faulted, 1);
                    throw;
                }

                Interlocked.Add(ref _completed, buffer.Length);
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
