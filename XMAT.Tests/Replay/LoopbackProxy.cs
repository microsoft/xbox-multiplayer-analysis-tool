// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace XMAT.Tests.Replay
{
    // What a loopback proxy does with one request that reaches it: answer it (an absolute-form request, or a CONNECT that
    // it refuses), tunnel a CONNECT to a loopback port, intercept a CONNECT with TLS of its own and answer the request
    // inside, or close the connection without an answer.
    internal abstract record ProxyAnswer
    {
        internal sealed record Respond(LoopbackResponse Response) : ProxyAnswer;

        internal sealed record Tunnel(int Port) : ProxyAnswer;

        internal sealed record Intercept(LoopbackTls Tls, LoopbackResponse Response) : ProxyAnswer;

        internal sealed record Close : ProxyAnswer;
    }

    // A minimal HTTP proxy on 127.0.0.1 for replay tests. It records every request that reaches it, absolute-form
    // requests with their bodies and CONNECTs alike, the requests inside the TLS that it intercepts, and every byte that it
    // read from its clients, tunnelled TLS included, then answers as the callback says. It never resolves a name: a tunnel
    // goes to the loopback port that the answer names, whatever the CONNECT asked for.
    internal sealed class LoopbackProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<LoopbackRequest, ProxyAnswer> _answer;
        private readonly ConcurrentQueue<LoopbackRequest> _requests = new();
        private readonly ConcurrentQueue<LoopbackRequest> _intercepted = new();
        private readonly ConcurrentBag<Task> _serving = new();
        private readonly MemoryStream _received = new();
        private readonly Task _accepting;
        private int _connections;
        private int _disposed;

        internal LoopbackProxy(Func<LoopbackRequest, ProxyAnswer> answer)
        {
            _answer = answer;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(AcceptAsync);
        }

        internal int Port { get; }

        internal string Url => $"http://127.0.0.1:{Port}";

        // In arrival order: absolute-form requests and CONNECTs.
        internal IReadOnlyList<LoopbackRequest> Requests => _requests.ToArray();

        // The requests read inside intercepted TLS.
        internal IReadOnlyList<LoopbackRequest> Intercepted => _intercepted.ToArray();

        internal int Connections => Volatile.Read(ref _connections);

        // Every byte read from clients, as Latin-1: heads, bodies, tunnelled and intercepted TLS.
        internal string ReceivedText
        {
            get
            {
                lock (_received)
                {
                    return Encoding.Latin1.GetString(_received.GetBuffer(), 0, (int)_received.Length);
                }
            }
        }

        // Stops listening and waits for every connection to end; a second call does nothing.
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_serving);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                catch (InvalidOperationException) when (_stop.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _serving.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = new RecordingStream(client.GetStream(), _received);
                    LoopbackRequest request = await LoopbackHttpServer.ReadRequestAsync(stream, _stop.Token);
                    if (request == null)
                    {
                        return;
                    }

                    _requests.Enqueue(request);
                    switch (_answer(request))
                    {
                        case ProxyAnswer.Respond respond:
                            await stream.WriteAsync(LoopbackHttpServer.Serialize(respond.Response), _stop.Token);
                            break;
                        case ProxyAnswer.Tunnel tunnel:
                            await EstablishedAsync(stream);
                            await PipeAsync(stream, tunnel.Port);
                            break;
                        case ProxyAnswer.Intercept intercept:
                            await EstablishedAsync(stream);
                            await InterceptAsync(stream, intercept);
                            break;
                    }
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // The client went away or the proxy is stopping: the connection just closes.
                }
            }
        }

        private async Task EstablishedAsync(Stream stream) =>
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _stop.Token);

        private async Task PipeAsync(Stream client, int port)
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, port, _stop.Token);
            NetworkStream server = upstream.GetStream();
            Task toServer = client.CopyToAsync(server, _stop.Token);
            Task toClient = server.CopyToAsync(client, _stop.Token);
            await Task.WhenAny(toServer, toClient);
            upstream.Close();
            client.Close();
            try
            {
                await Task.WhenAll(toServer, toClient);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // One side closing ends the other.
            }
        }

        private async Task InterceptAsync(Stream client, ProxyAnswer.Intercept intercept)
        {
            await using var ssl = new SslStream(client, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = intercept.Tls.Certificate,
                        ApplicationProtocols = (intercept.Tls.Protocols ?? new[] { SslApplicationProtocol.Http11 }).ToList(),
                        ClientCertificateRequired = false,
                    },
                    _stop.Token);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // The client refused the certificate or went away.
                return;
            }

            LoopbackRequest inside = await LoopbackHttpServer.ReadRequestAsync(ssl, _stop.Token);
            if (inside == null)
            {
                return;
            }

            _intercepted.Enqueue(inside);
            await ssl.WriteAsync(LoopbackHttpServer.Serialize(intercept.Response), _stop.Token);
        }

        // Passes everything through and keeps a copy of every byte read.
        private sealed class RecordingStream(Stream inner, MemoryStream received) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                Record(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = await inner.ReadAsync(buffer, cancellationToken);
                return Record(buffer.Span[..read]);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(buffer, cancellationToken);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }

            private int Record(ReadOnlySpan<byte> bytes)
            {
                lock (received)
                {
                    received.Write(bytes);
                }

                return bytes.Length;
            }
        }
    }
}
