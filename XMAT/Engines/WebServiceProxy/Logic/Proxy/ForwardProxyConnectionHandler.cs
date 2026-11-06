// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class ForwardProxyConnectionHandler : ConnectionHandler
    {
        private readonly HttpClient _httpClient;
        private readonly InterceptedHttpHost _interceptedHost;
        private readonly InterceptedTunnelRegistry _interceptedTunnels;
        private readonly Logger _logger;
        private readonly WebServiceProxy _proxy;
        private readonly InitialProxyRequestReader _requestReader;
        private readonly CancellationToken _applicationStopping;

        internal Action<LogLevel, string> LogObserver { get; set; }

        internal Func<
            Stream,
            ProxyRequestReadFailure,
            CancellationToken,
            Task> FailureResponseWriterOverride { get; set; }

        internal ProxyRequestBufferBudget RequestBufferBudget =>
            _requestReader.BufferBudget;

        private const int RejectedInputDrainBytes =
            64 * 1024;
        private static readonly TimeSpan
            RejectedInputDrainTime =
                TimeSpan.FromMilliseconds(250);

        private static readonly byte[] HeaderTooLargeResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 431 Request Header Fields Too Large\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] BodyTooLargeResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 413 Content Too Large\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] TimeoutResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 408 Request Timeout\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] BufferExhaustedResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");

        public ForwardProxyConnectionHandler(
            HttpClient httpClient,
            InterceptedHttpHost interceptedHost,
            InterceptedTunnelRegistry interceptedTunnels,
            Logger logger,
            WebServiceProxy proxy,
            IHostApplicationLifetime applicationLifetime,
            InitialProxyRequestReader requestReader = null)
        {
            _httpClient = httpClient;
            _interceptedHost = interceptedHost;
            _interceptedTunnels = interceptedTunnels;
            _logger = logger;
            _proxy = proxy;
            _requestReader =
                requestReader ??
                new InitialProxyRequestReader(
                    ProxyRequestReadPolicy.Default,
                    ProxyRequestBufferBudget.Shared);
            _applicationStopping = applicationLifetime.ApplicationStopping;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                connection.ConnectionClosed,
                _applicationStopping);
            var cancellationToken = connectionCancellation.Token;
            var connectionID = _proxy.GetNextConnectionID();
            var remoteEndPoint = connection.RemoteEndPoint?.ToString() ?? "unknown";

            if (cancellationToken.IsCancellationRequested ||
                !_proxy.RaiseReceivedInitialConnection(connectionID, remoteEndPoint))
            {
                _logger.Log(connectionID, LogLevel.WARN, $"REJECTING connection from {remoteEndPoint}");
                return;
            }

            _logger.Log(connectionID, LogLevel.INFO, $"Accepting connection from {remoteEndPoint}");

            try
            {
                ProxyRequestReadResult readResult =
                    await _requestReader.ReadAsync(
                            connection.Transport.Input,
                            cancellationToken)
                        .ConfigureAwait(false);
                var stream =
                    new DuplexPipeStream(
                        connection.Transport);
                if (readResult.Failure !=
                    ProxyRequestReadFailure.None)
                {
                    _logger.Log(
                        connectionID,
                        LogLevel.WARN,
                        $"Initial request rejected: {readResult.Failure}");
                    if (!cancellationToken
                        .IsCancellationRequested)
                    {
                        Func<
                            Stream,
                            ProxyRequestReadFailure,
                            CancellationToken,
                            Task> writer =
                                FailureResponseWriterOverride;
                        if (writer == null)
                        {
                            await WriteReadFailureAsync(
                                    stream,
                                    readResult.Failure,
                                    _applicationStopping)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await writer(
                                    stream,
                                    readResult.Failure,
                                    _applicationStopping)
                                .ConfigureAwait(false);
                        }
                    }
                    RejectedInputDrainResult drain =
                        await DrainRejectedInputAsync(
                            connection.Transport.Input,
                            _applicationStopping)
                            .ConfigureAwait(false);
                    _logger.Log(
                        connectionID,
                        LogLevel.DEBUG,
                        $"Rejected request close path: {drain.Outcome}; " +
                        $"drained {drain.Bytes} bytes.");
                    await connection.Transport.Output
                        .CompleteAsync()
                        .ConfigureAwait(false);
                    await connection.Transport.Input
                        .CompleteAsync()
                        .ConfigureAwait(false);
                    return;
                }

                ClientRequest request =
                    readResult.Request;
                request.RequestNumber =
                    _proxy.GetNextRequestID();
                if (request.Method == "CONNECT")
                {
                    await HandleConnectAsync(connectionID, stream, request, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    request.Scheme = "http";
                    await HandleHttpRequestAsync(connectionID, stream, request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                LogConnection(
                    connectionID,
                    LogLevel.DEBUG,
                    "Connection cancelled");
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                ObjectDisposedException)
            {
                LogConnection(
                    connectionID,
                    LogLevel.WARN,
                    $"Connection ended while reading or writing: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogConnection(
                    connectionID,
                    LogLevel.ERROR,
                    $"Exception in connection handler: {ex}");
            }
            finally
            {
                _proxy.RaiseConnectionClosed(connectionID);
            }
        }

        private async Task HandleConnectAsync(int connectionID, Stream clientStream, ClientRequest connectRequest, string remoteEndPoint, CancellationToken ct)
        {
            connectRequest.Scheme = "https";
            var clientIP = remoteEndPoint.Contains(':') ? remoteEndPoint.Split(':')[0] : remoteEndPoint;

            if (!await _proxy.RaiseReceivedSslConnectionRequestAsync(
                connectionID,
                clientIP,
                connectRequest,
                ct).ConfigureAwait(false))
            {
                _logger.Log(connectionID, LogLevel.ERROR, "REJECTING SSL connection request");
                return;
            }

            // Send 200 Connection Established
            var response = new ServerResponse
            {
                RequestNumber = connectRequest.RequestNumber,
                Version = connectRequest.Version,
                Status = "200",
                StatusDescription = "Connection Established"
            };
            response.Headers["FiddlerGateway"] = "Direct";
            response.Headers["StartTime"] = DateTime.Now.ToString("HH:mm:ss.fff");

            if (!await _proxy.RaiseReceivedWebResponseAsync(
                connectionID,
                connectRequest,
                response,
                ct).ConfigureAwait(false))
            {
                _logger.Log(connectionID, LogLevel.WARN, "CONNECT response was aborted.");
                return;
            }

            byte[] responseBytes = response.ToByteArray();
            await clientStream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);

            string hostname = connectRequest.Host ?? GetHostFromPath(connectRequest.Path);
            int port = connectRequest.Port > 0 ? connectRequest.Port : GetPortFromPath(connectRequest.Path);

            // If the host is on the bypass list, tunnel raw bytes without TLS interception
            if (_proxy.BypassList != null && _proxy.BypassList.IsBypassed(hostname))
            {
                _logger.Log(connectionID, LogLevel.INFO, $"Bypassing TLS interception for {hostname}:{port}");
                _proxy.RaiseCompletedSslConnectionRequest(connectionID, connectRequest);
                await TunnelConnectionAsync(connectionID, clientStream, hostname, port, ct).ConfigureAwait(false);
                return;
            }

            // Keep this listener protocol-agnostic after CONNECT. Raw TLS bytes
            // are handed to the private Kestrel host, which negotiates HTTP/1.1
            // or HTTP/2 and invokes the shared capture application per request.
            await TunnelInterceptedConnectionAsync(
                connectionID,
                clientStream,
                connectRequest,
                ct).ConfigureAwait(false);
        }

        private async Task HandleHttpRequestAsync(int connectionID, Stream clientStream, ClientRequest request, CancellationToken ct)
        {
            if (!await _proxy.RaiseReceivedWebRequestAsync(
                connectionID,
                request,
                ct).ConfigureAwait(false))
            {
                _logger.Log(connectionID, LogLevel.WARN, "Client request was aborted.");
                return;
            }

            if (!Uri.TryCreate(request.Path, UriKind.Absolute, out Uri uri))
            {
                string path = request.Path;
                string query = string.Empty;

                if (path.Contains('?'))
                {
                    var parts = path.Split('?', 2);
                    path = parts[0];
                    query = "?" + parts[1];
                }
                else if (path.Contains('#'))
                {
                    var parts = path.Split('#', 2);
                    path = parts[0];
                    query = "#" + parts[1];
                }

                var ub = new UriBuilder(request.Scheme, request.Host, request.Port, path, query);
                uri = ub.Uri;
            }

            if (WebSocketProxy.IsWebSocketUpgrade(request))
            {
                await HandleWebSocketAsync(connectionID, uri, clientStream, request, ct).ConfigureAwait(false);
            }
            else
            {
                await ForwardHttpAsync(connectionID, uri, clientStream, request, ct).ConfigureAwait(false);
            }
        }

        private async Task ForwardHttpAsync(int connectionID, Uri uri, Stream clientStream, ClientRequest clientRequest, CancellationToken ct)
        {
            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(clientRequest.Method),
                RequestUri = uri
            };

            requestMessage.Content = new ByteArrayContent(clientRequest.BodyBytes ?? Array.Empty<byte>());
            requestMessage.Headers.Clear();
            requestMessage.Content.Headers.Clear();

            clientRequest.Headers.CopyTo(requestMessage.Headers);
            clientRequest.ContentHeaders.CopyTo(requestMessage.Content.Headers);

            HttpResponseMessage result;
            try
            {
                result = await _httpClient.SendAsync(requestMessage, ct).ConfigureAwait(false);
                _logger.Log(connectionID, LogLevel.DEBUG, $"Request forwarded to {uri}");
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed sending proxied request: {ex}");
                return;
            }

            var serverResponse = await ParseServerResponseAsync(connectionID, result).ConfigureAwait(false);
            if (serverResponse == null)
            {
                _logger.Log(connectionID, LogLevel.ERROR, "Could not parse server response");
                return;
            }

            serverResponse.RequestNumber = clientRequest.RequestNumber;

            if (!await _proxy.RaiseReceivedWebResponseAsync(
                connectionID,
                clientRequest,
                serverResponse,
                ct).ConfigureAwait(false))
            {
                _logger.Log(connectionID, LogLevel.WARN, "Server response was aborted.");
                return;
            }

            try
            {
                await clientStream.WriteAsync(serverResponse.ToByteArray(), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _logger.Log(connectionID, LogLevel.DEBUG, "Response sent to client");
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                ObjectDisposedException)
            {
                LogConnection(
                    connectionID,
                    LogLevel.WARN,
                    $"Client connection ended while writing the response: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogConnection(
                    connectionID,
                    LogLevel.ERROR,
                    $"Failed writing response to client: {ex}");
            }
        }

        private async Task HandleWebSocketAsync(int connectionID, Uri uri, Stream clientStream, ClientRequest clientRequest, CancellationToken ct)
        {
            var wsProxy = new WebSocketProxy();
            wsProxy.WebSocketOpened += (_, args) => _proxy.RaiseWebSocketOpened(args);
            wsProxy.WebSocketMessage += (_, args) => _proxy.RaiseWebSocketMessage(args);
            wsProxy.WebSocketClosed += (_, args) => _proxy.RaiseWebSocketClosed(args);

            await wsProxy.StartWebSocketProxy(
                connectionID,
                uri,
                clientStream,
                clientRequest,
                _logger,
                ct).ConfigureAwait(false);
        }

        private async Task<ServerResponse> ParseServerResponseAsync(int connectionID, HttpResponseMessage response)
        {
            if (response == null)
            {
                _logger.Log(connectionID, LogLevel.ERROR, "Server response is null");
                return null;
            }

            var serverResponse = new ServerResponse
            {
                Status = ((int)response.StatusCode).ToString(),
                StatusDescription = response.ReasonPhrase,
                Version = "HTTP/" + response.Version.ToString(),
            };

            response.Headers.TransferEncodingChunked = false;
            serverResponse.Headers.CopyFrom(response.Headers);
            serverResponse.ContentHeaders.CopyFrom(response.Content.Headers);
            serverResponse.Headers["Connection"] = "close";
            serverResponse.BodyBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return serverResponse;
        }

        private static async Task WriteReadFailureAsync(
            Stream stream,
            ProxyRequestReadFailure failure,
            CancellationToken cancellationToken)
        {
            byte[] response =
                failure switch
                {
                    ProxyRequestReadFailure
                        .HeaderTooLarge =>
                        HeaderTooLargeResponse,
                    ProxyRequestReadFailure
                        .BodyTooLarge =>
                        BodyTooLargeResponse,
                    ProxyRequestReadFailure
                        .Timeout =>
                        TimeoutResponse,
                    ProxyRequestReadFailure
                        .BufferExhausted =>
                        BufferExhaustedResponse,
                    _ => null,
                };
            if (response == null ||
                cancellationToken
                    .IsCancellationRequested)
            {
                return;
            }
            await stream.WriteAsync(
                    response,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<RejectedInputDrainResult>
            DrainRejectedInputAsync(
            PipeReader reader,
            CancellationToken cancellationToken)
        {
            using var drainCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);
            drainCancellation.CancelAfter(
                RejectedInputDrainTime);
            var drained = 0;
            try
            {
                while (true)
                {
                    ReadResult result =
                        await reader.ReadAsync(
                                drainCancellation.Token)
                            .ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer =
                        result.Buffer;
                    if (drained >=
                        RejectedInputDrainBytes)
                    {
                        if (buffer.IsEmpty)
                        {
                            reader.AdvanceTo(
                                buffer.Start,
                                buffer.End);
                            if (result.IsCompleted)
                            {
                                return new RejectedInputDrainResult(
                                    drained,
                                    "peer FIN");
                            }
                            continue;
                        }
                        reader.AdvanceTo(
                            buffer.Start,
                            buffer.Start);
                        return new RejectedInputDrainResult(
                            drained,
                            "drain cap");
                    }
                    int count =
                        checked(
                            (int)Math.Min(
                                RejectedInputDrainBytes -
                                drained,
                                buffer.Length));
                    SequencePosition consumed =
                        buffer.GetPosition(count);
                    reader.AdvanceTo(
                        consumed,
                        consumed);
                    drained += count;
                    if (result.IsCompleted ||
                        count == 0)
                    {
                        return new RejectedInputDrainResult(
                            drained,
                            result.IsCompleted
                                ? "peer FIN"
                                : "empty read");
                    }
                }
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or
                IOException or
                ObjectDisposedException)
            {
                // A bounded best-effort drain must not replace the policy response.
                return new RejectedInputDrainResult(
                    drained,
                    ex is OperationCanceledException
                        ? "drain timeout or cancellation"
                        : "drain I/O ended");
            }
        }

        private readonly record struct RejectedInputDrainResult(
            int Bytes,
            string Outcome);

        private void LogConnection(
            int connectionID,
            LogLevel level,
            string message)
        {
            _logger.Log(
                connectionID,
                level,
                message);
            LogObserver?.Invoke(
                level,
                message);
        }

        private static string GetHostFromPath(string path) =>
            HostAuthority.TryParse(path, out string host, out _) ? host : path;

        private static int GetPortFromPath(string path) =>
            HostAuthority.TryParse(path, out _, out int? port) && port.HasValue ? port.Value : 443;

        private async Task TunnelConnectionAsync(int connectionID, Stream clientStream, string hostname, int port, CancellationToken ct)
        {
            using var tcpClient = new TcpClient();
            try
            {
                await tcpClient.ConnectAsync(hostname, port, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed to connect to {hostname}:{port} for bypass tunnel: {ex.Message}");
                return;
            }
            catch (IOException ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed to connect to {hostname}:{port} for bypass tunnel: {ex.Message}");
                return;
            }
            catch (AuthenticationException ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed to connect to {hostname}:{port} for bypass tunnel: {ex.Message}");
                return;
            }

            using var serverStream = tcpClient.GetStream();

            var clientToServer = RelayAsync(clientStream, serverStream, ct);
            var serverToClient = RelayAsync(serverStream, clientStream, ct);

            await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
        }

        private async Task TunnelInterceptedConnectionAsync(
            int connectionID,
            Stream clientStream,
            ClientRequest connectRequest,
            CancellationToken ct)
        {
            using var tcpClient = new TcpClient();
            int localPort = 0;
            bool registered = false;

            try
            {
                await tcpClient.ConnectAsync(
                    IPAddress.Loopback,
                    _interceptedHost.Port,
                    ct).ConfigureAwait(false);

                // Kestrel exposes this ephemeral port as RemotePort, allowing its
                // request delegate and certificate selector to recover the
                // original XMAT connection without modifying encrypted bytes.
                localPort = ((IPEndPoint)tcpClient.Client.LocalEndPoint).Port;
                registered = _interceptedTunnels.TryRegister(
                    localPort,
                    new InterceptedTunnelContext(connectionID, connectRequest));
                if (!registered)
                    throw new InvalidOperationException(
                        $"An intercepted tunnel is already registered for port {localPort}.");

                using NetworkStream serverStream = tcpClient.GetStream();
                using var relayCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                var clientToServer = RelayAsync(
                    clientStream,
                    serverStream,
                    relayCancellation.Token);
                var serverToClient = RelayAsync(
                    serverStream,
                    clientStream,
                    relayCancellation.Token);

                // Once either direction closes, cancel and drain both pumps
                // before removing the correlation entry or disposing the socket.
                await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
                relayCancellation.Cancel();
                await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is SocketException or IOException or AuthenticationException or
                InvalidOperationException)
            {
                _logger.Log(
                    connectionID,
                    LogLevel.ERROR,
                    $"Failed opening intercepted TLS tunnel: {ex.Message}");
                _proxy.RaiseFailedSslConnectionRequest(connectionID, ex);
            }
            finally
            {
                if (registered)
                    _interceptedTunnels.Remove(localPort);
            }
        }

        private static async Task RelayAsync(Stream from, Stream to, CancellationToken ct)
        {
            byte[] buffer = new byte[8192];
            try
            {
                int bytesRead;
                while ((bytesRead = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    await to.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Connection closed or cancelled — expected during tunnel teardown
            }
            catch (IOException)
            {
                // Connection closed or cancelled — expected during tunnel teardown
            }
            catch (ObjectDisposedException)
            {
                // Connection closed or cancelled — expected during tunnel teardown
            }
        }

    }

    /// <summary>
    /// Wraps an IDuplexPipe as a Stream for use with SslStream and other stream-based APIs.
    /// </summary>
    internal class DuplexPipeStream : Stream
    {
        private readonly IDuplexPipe _pipe;
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        public DuplexPipeStream(IDuplexPipe pipe)
        {
            _pipe = pipe;
            _reader = pipe.Input;
            _writer = pipe.Output;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var readableBuffer = result.Buffer;

            if (readableBuffer.IsEmpty && result.IsCompleted)
                return 0;

            int bytesToCopy = (int)Math.Min(count, readableBuffer.Length);
            readableBuffer.Slice(0, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));
            _reader.AdvanceTo(readableBuffer.GetPosition(bytesToCopy));

            return bytesToCopy;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var readableBuffer = result.Buffer;

            if (readableBuffer.IsEmpty && result.IsCompleted)
                return 0;

            int bytesToCopy = (int)Math.Min(buffer.Length, readableBuffer.Length);
            readableBuffer.Slice(0, bytesToCopy).CopyTo(buffer.Span);
            _reader.AdvanceTo(readableBuffer.GetPosition(bytesToCopy));

            return bytesToCopy;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _writer.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Flush()
        {
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
