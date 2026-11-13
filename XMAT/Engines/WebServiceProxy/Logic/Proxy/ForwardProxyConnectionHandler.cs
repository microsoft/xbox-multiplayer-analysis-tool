// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

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
        private static readonly byte[] ContinueResponse =
            "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
        private static readonly byte[] UnsupportedTransferCodingResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 501 Not Implemented\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] MalformedTransferCodingResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 400 Bad Request\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] UnsupportedExpectationResponse =
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 417 Expectation Failed\r\n" +
                "Connection: close\r\n" +
                "Content-Length: 0\r\n\r\n");
        private static readonly byte[] ChunkTerminator = "\r\n"u8.ToArray();

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
            ClientProcessResolver processResolver = _proxy.ProcessResolver;
            IClientProcessResolverClock processClock = processResolver.Clock;
            var processContext = new ClientProcessContext(
                _proxy.RunId,
                connectionID,
                connection.RemoteEndPoint as IPEndPoint,
                connection.LocalEndPoint as IPEndPoint,
                processClock.UtcNow,
                processClock.Timestamp);
            try
            {
                _ = processResolver.TryEnqueue(processContext);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                processContext.Publish(new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Unresolved));
            }

            if (cancellationToken.IsCancellationRequested ||
                !_proxy.RaiseReceivedInitialConnection(connectionID, remoteEndPoint, processContext))
            {
                _logger.Log(connectionID, LogLevel.WARN, $"REJECTING connection from {remoteEndPoint}");
                return;
            }

            _logger.Log(connectionID, LogLevel.INFO, $"Accepting connection from {remoteEndPoint}");

            Guid? sslRegistration = null;
            try
            {
                var stream =
                    new DuplexPipeStream(
                        connection.Transport);
                ProxyRequestReadResult readResult =
                    await _requestReader.ReadAsync(
                            connection.Transport.Input,
                            async token =>
                            {
                                await stream.WriteAsync(ContinueResponse, token).ConfigureAwait(false);
                                await stream.FlushAsync(token).ConfigureAwait(false);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
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
                    request.Scheme = "https";
                    sslRegistration = _proxy.TrackSslConnection(
                        connectionID,
                        (connection.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? remoteEndPoint,
                        request,
                        () => connection.Abort(new ConnectionAbortedException(
                            "The enabled SSL script rejected this active connection.")),
                        cancellationToken);
                    await HandleConnectAsync(connectionID, stream, request, remoteEndPoint, processContext,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    request.Scheme = "http";
                    await HandleHttpRequestAsync(connectionID, stream, request, processContext,
                        cancellationToken).ConfigureAwait(false);
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
                if (sslRegistration.HasValue)
                    _proxy.UntrackSslConnection(sslRegistration.Value);
                _proxy.RaiseConnectionClosed(connectionID, processContext);
            }
        }

        private async Task HandleConnectAsync(
            int connectionID,
            Stream clientStream,
            ClientRequest connectRequest,
            string remoteEndPoint,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            connectRequest.Scheme = "https";
            var clientIP = remoteEndPoint.Contains(':') ? remoteEndPoint.Split(':')[0] : remoteEndPoint;

            if (!await _proxy.RaiseReceivedSslConnectionRequestAsync(
                connectionID,
                clientIP,
                connectRequest,
                ct,
                processContext).ConfigureAwait(false))
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
                ct,
                processContext).ConfigureAwait(false))
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
                _proxy.RaiseCompletedSslConnectionRequest(connectionID, connectRequest, processContext);
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
                processContext,
                ct).ConfigureAwait(false);
        }

        private async Task HandleHttpRequestAsync(
            int connectionID,
            Stream clientStream,
            ClientRequest request,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            if (!await _proxy.RaiseReceivedWebRequestAsync(
                connectionID,
                request,
                ct,
                processContext).ConfigureAwait(false))
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
            if (!HostListEntry.IsSupportedTargetScheme(uri.Scheme))
            {
                _logger.Log(connectionID, LogLevel.WARN, "Client request used an unsupported target scheme.");
                return;
            }

            if (WebSocketProxy.IsWebSocketUpgrade(request))
            {
                await HandleWebSocketAsync(connectionID, uri, clientStream, request, processContext, ct).ConfigureAwait(
                    false);
            }
            else
            {
                await ForwardHttpAsync(connectionID, uri, clientStream, request, processContext, ct).ConfigureAwait(
                    false);
            }
        }

        private async Task ForwardHttpAsync(
            int connectionID,
            Uri uri,
            Stream clientStream,
            ClientRequest clientRequest,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            if (ProxyHeaderUtilities.HasUnsupportedTransferCoding(clientRequest))
            {
                await WriteProxyErrorAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    HttpStatusCode.NotImplemented,
                    "Not Implemented",
                    processContext,
                    captureCompletion: false,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(clientRequest.Method),
                RequestUri = uri
            };

            requestMessage.Content = clientRequest.UnknownLengthBody
                ? new BufferedRequestContent(clientRequest.BodyBytes ?? Array.Empty<byte>(), knownLength: false)
                : new ByteArrayContent(clientRequest.BodyBytes ?? Array.Empty<byte>());
            requestMessage.Headers.Clear();
            requestMessage.Content.Headers.Clear();

            clientRequest.Headers.CopyTo(requestMessage.Headers);
            clientRequest.ContentHeaders.CopyTo(requestMessage.Content.Headers);
            // Scripts may replace the body; let the buffered content calculate its length.
            requestMessage.Content.Headers.Remove("Content-Length");

            HttpResponseMessage result;
            try
            {
                result = await _httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                _logger.Log(connectionID, LogLevel.DEBUG, $"Request forwarded to {uri}");
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Upstream HTTP request to {uri} timed out: {ex.Message}");
                await WriteProxyErrorAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    HttpStatusCode.GatewayTimeout,
                    "Gateway Timeout",
                    processContext,
                    captureCompletion: true,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Upstream HTTP request to {uri} timed out: {ex.Message}");
                await WriteProxyErrorAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    HttpStatusCode.GatewayTimeout,
                    "Gateway Timeout",
                    processContext,
                    captureCompletion: true,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Upstream HTTP request to {uri} failed: {ex}");
                await WriteProxyErrorAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    HttpStatusCode.BadGateway,
                    "Bad Gateway",
                    processContext,
                    captureCompletion: true,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            using (result)
            {
                await RelayResponseAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    result,
                    processContext,
                    ct).ConfigureAwait(false);
            }
        }

        // Buffers the response so scripts can change it, unless it streams (event streams, audio,
        // video, Stream all, or a body past the capture limit). The client gets the origin's framing.
        private async Task RelayResponseAsync(
            int connectionID,
            Stream clientStream,
            ClientRequest clientRequest,
            HttpResponseMessage result,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            if (ProxyResponseStreaming.HasMessageBody(clientRequest.Method, result.StatusCode) &&
                ProxyHeaderUtilities.HasUnsupportedTransferCoding(result.Headers))
            {
                _logger.Log(
                    connectionID,
                    LogLevel.ERROR,
                    $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} used " +
                    $"unsupported transfer coding: {result.Headers.TransferEncoding}.");
                await WriteProxyErrorAsync(
                    connectionID,
                    clientStream,
                    clientRequest,
                    HttpStatusCode.BadGateway,
                    "Bad Gateway",
                    processContext,
                    captureCompletion: true,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            bool originChunked = result.Headers.TransferEncodingChunked == true;
            long? declaredLength = result.Content.Headers.ContentLength;
            bool clientHttp10 =
                string.Equals(clientRequest.Version, "HTTP/1.0", StringComparison.OrdinalIgnoreCase);
            // Chunked framing needs HTTP/1.1 on both sides: the status line carries the origin's version.
            bool chunkedAllowed =
                !clientHttp10 &&
                result.Version >= HttpVersion.Version11;
            ServerResponse serverResponse = CreateServerResponse(result);
            serverResponse.RequestNumber = clientRequest.RequestNumber;

            if (!ProxyResponseStreaming.HasMessageBody(clientRequest.Method, result.StatusCode))
            {
                if (await AcceptResponseAsync(connectionID, clientRequest, serverResponse, processContext, ct)
                        .ConfigureAwait(false))
                {
                    await WriteResponseAsync(connectionID, clientStream, serverResponse.ToByteArray(), ct)
                        .ConfigureAwait(false);
                }
                return;
            }

            ResponseStreamingOptions options = _proxy.EffectiveResponseStreaming;
            Stream body = await result.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var reader = new IdleTimeoutReader(body, _httpClient.Timeout, ct);
            var capture = new CapturedResponseBody(options.CaptureLimitBytes);
            ReadOnlyMemory<byte> overflow = ReadOnlyMemory<byte>.Empty;
            if (!ProxyResponseStreaming.StreamsImmediately(result, options))
            {
                BufferedResponseBody buffered;
                try
                {
                    buffered = await ProxyResponseStreaming.ReadUpToLimitAsync(
                        reader,
                        options.CaptureLimitBytes,
                        declaredLength).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    _logger.Log(
                        connectionID,
                        LogLevel.ERROR,
                        $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} failed: " +
                        ex.Message);
                    await WriteProxyErrorAsync(
                        connectionID,
                        clientStream,
                        clientRequest,
                        HttpStatusCode.BadGateway,
                        "Bad Gateway",
                        processContext,
                        captureCompletion: true,
                        cancellationToken: ct).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Log(
                        connectionID,
                        LogLevel.ERROR,
                        $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} timed out: " +
                        ex.Message);
                    await WriteProxyErrorAsync(
                        connectionID,
                        clientStream,
                        clientRequest,
                        HttpStatusCode.GatewayTimeout,
                        "Gateway Timeout",
                        processContext,
                        captureCompletion: true,
                        cancellationToken: ct).ConfigureAwait(false);
                    return;
                }

                capture = buffered.Capture;
                overflow = buffered.Overflow;
                if (buffered.Complete)
                {
                    serverResponse.BodyBytes = capture.Freeze();
                    serverResponse.Trailers.CopyFrom(result.TrailingHeaders);
                    if (!await AcceptResponseAsync(connectionID, clientRequest, serverResponse, processContext, ct)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    string[] trailerNames = AllowedTrailerNames(result, serverResponse.Trailers);
                    ResponseFraming bufferedFraming = declaredLength.HasValue
                        ? ResponseFraming.ContentLength
                        : clientHttp10
                            ? ResponseFraming.CloseDelimited
                            : chunkedAllowed && (originChunked || trailerNames.Length != 0)
                                ? ResponseFraming.Chunked
                                : ResponseFraming.ContentLength;
                    await WriteBufferedResponseAsync(
                        connectionID,
                        clientStream,
                        serverResponse,
                        bufferedFraming,
                        trailerNames,
                        ct).ConfigureAwait(false);
                    return;
                }
            }

            ResponseFraming framing = declaredLength.HasValue
                ? ResponseFraming.ContentLength
                : chunkedAllowed ? ResponseFraming.Chunked : ResponseFraming.CloseDelimited;
            await StreamResponseAsync(
                connectionID,
                clientStream,
                clientRequest,
                serverResponse,
                result,
                reader,
                capture,
                overflow,
                framing,
                declaredLength ?? 0,
                AllowedTrailerNames(result, trailers: null),
                processContext,
                ct).ConfigureAwait(false);
        }

        private async Task<bool> AcceptResponseAsync(
            int connectionID,
            ClientRequest clientRequest,
            ServerResponse serverResponse,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            if (await _proxy.RaiseReceivedWebResponseAsync(
                    connectionID,
                    clientRequest,
                    serverResponse,
                    ct,
                    processContext).ConfigureAwait(false))
            {
                return true;
            }

            _logger.Log(connectionID, LogLevel.WARN, "Server response was aborted.");
            return false;
        }

        private async Task WriteResponseAsync(
            int connectionID,
            Stream clientStream,
            byte[] response,
            CancellationToken ct)
        {
            try
            {
                await clientStream.WriteAsync(response, ct).ConfigureAwait(false);
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

        // The headers go out first; the body follows as it arrives, teed into a capture copy that
        // keeps up to the capture limit. Scripts see that copy only after the body has ended.
        private async Task StreamResponseAsync(
            int connectionID,
            Stream clientStream,
            ClientRequest clientRequest,
            ServerResponse serverResponse,
            HttpResponseMessage result,
            IdleTimeoutReader reader,
            CapturedResponseBody capture,
            ReadOnlyMemory<byte> overflow,
            ResponseFraming framing,
            long contentLength,
            IReadOnlyList<string> trailerNames,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            WebServiceProxy.StreamedResponseFinalizer finalizer =
                _proxy.RegisterStreamedResponseFinalizer(
                    connectionID,
                    clientRequest,
                    serverResponse,
                    processContext);
            try
            {
                await clientStream.WriteAsync(
                    BuildHead(serverResponse, framing, contentLength, trailerNames),
                    ct)
                    .ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                serverResponse.BodyBytes = Array.Empty<byte>();
                serverResponse.CaptureTruncatedAt = 0;
                await _proxy.RaiseWebResponseProgressAsync(
                    connectionID,
                    clientRequest,
                    serverResponse,
                    _applicationStopping,
                    processContext).ConfigureAwait(false);

                await ProxyResponseStreaming.RelayBodyAsync(
                    reader,
                    capture,
                    overflow,
                    async data =>
                    {
                        if (framing == ResponseFraming.Chunked)
                        {
                            await WriteChunkAsync(clientStream, data, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await clientStream.WriteAsync(data, ct).ConfigureAwait(false);
                            await clientStream.FlushAsync(ct).ConfigureAwait(false);
                        }
                    },
                    NotifyCaptureAsync).ConfigureAwait(false);

                serverResponse.Trailers.CopyFrom(result.TrailingHeaders);
                if (framing == ResponseFraming.Chunked)
                {
                    await WriteLastChunkAsync(clientStream, serverResponse.Trailers, ct).ConfigureAwait(false);
                }
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _logger.Log(connectionID, LogLevel.DEBUG, "Streamed response sent to client");
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                ObjectDisposedException or
                HttpRequestException or
                TimeoutException or
                OperationCanceledException)
            {
                if (!_applicationStopping.IsCancellationRequested)
                {
                    LogConnection(
                        connectionID,
                        LogLevel.WARN,
                        $"Streamed response ended early: {ex.Message}");
                }
            }
            finally
            {
                await CompleteClientOutputAsync(clientStream, connectionID).ConfigureAwait(false);
                DateTime transportEndedAt = DateTime.Now;
                try
                {
                    result.Dispose();
                }
                finally
                {
                    finalizer.Complete(
                        transportEndedAt,
                        () => capture.CopyFinalTo(serverResponse));
                }
            }

            // Capture updates use the proxy's stop token, not the connection's: after
            // "Connection: close" the client may hang up as soon as it has the response.
            Task NotifyCaptureAsync()
            {
                capture.CopyProgressTo(serverResponse);
                return _proxy.RaiseWebResponseProgressAsync(
                    connectionID,
                    clientRequest,
                    serverResponse,
                    _applicationStopping,
                    processContext);
            }
        }

        private enum ResponseFraming
        {
            ContentLength,
            Chunked,
            CloseDelimited,
        }

        private async Task WriteBufferedResponseAsync(
            int connectionId,
            Stream clientStream,
            ServerResponse response,
            ResponseFraming framing,
            IReadOnlyList<string> trailerNames,
            CancellationToken cancellationToken)
        {
            byte[] body = response.BodyBytes ?? Array.Empty<byte>();
            try
            {
                await clientStream.WriteAsync(
                    BuildHead(response, framing, body.Length, trailerNames),
                    cancellationToken).ConfigureAwait(false);
                if (framing == ResponseFraming.Chunked)
                {
                    for (var offset = 0; offset < body.Length; offset += ProxyResponseStreaming.ReadBufferSize)
                    {
                        await WriteChunkAsync(
                            clientStream,
                            body.AsMemory(
                                offset,
                                Math.Min(ProxyResponseStreaming.ReadBufferSize, body.Length - offset)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    await WriteLastChunkAsync(
                        clientStream,
                        response.Trailers,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    for (var offset = 0; offset < body.Length; offset += ProxyResponseStreaming.ReadBufferSize)
                    {
                        await clientStream.WriteAsync(
                            body.AsMemory(
                                offset,
                                Math.Min(ProxyResponseStreaming.ReadBufferSize, body.Length - offset)),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger.Log(connectionId, LogLevel.DEBUG, "Response sent to client");
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                ObjectDisposedException)
            {
                LogConnection(
                    connectionId,
                    LogLevel.WARN,
                    $"Client connection ended while writing the response: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogConnection(
                    connectionId,
                    LogLevel.ERROR,
                    $"Failed writing response to client: {ex}");
            }
        }

        // The capture keeps the origin's headers; the client gets framing for the decoded body.
        private static byte[] BuildHead(
            ServerResponse response,
            ResponseFraming framing,
            long contentLength,
            IReadOnlyList<string> trailerNames)
        {
            var head = new StringBuilder();
            head.Append(response.Version).Append(' ').Append(response.Status).Append(' ')
                .Append(response.StatusDescription).Append("\r\n");
            AppendHeaders(head, response.Headers);
            AppendHeaders(head, response.ContentHeaders);
            if (framing == ResponseFraming.ContentLength)
            {
                head.Append("Content-Length: ")
                    .Append(contentLength.ToString(CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }
            else if (framing == ResponseFraming.Chunked)
            {
                head.Append("Transfer-Encoding: chunked\r\n");
                if (trailerNames.Count != 0)
                {
                    head.Append("Trailer: ")
                        .AppendJoin(", ", trailerNames)
                        .Append("\r\n");
                }
            }
            head.Append("\r\n");
            return Encoding.ASCII.GetBytes(head.ToString());
        }

        private static void AppendHeaders(StringBuilder head, HeaderCollection headers)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Trailer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (string value in header.Value)
                {
                    head.Append(header.Key).Append(": ").Append(value).Append("\r\n");
                }
            }
        }

        private static async Task WriteChunkAsync(
            Stream stream,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            byte[] size = Encoding.ASCII.GetBytes(data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            int length = size.Length + data.Length + ChunkTerminator.Length;
            byte[] chunk = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                size.CopyTo(chunk, 0);
                data.Span.CopyTo(chunk.AsSpan(size.Length));
                ChunkTerminator.CopyTo(chunk, size.Length + data.Length);
                await stream.WriteAsync(
                    chunk.AsMemory(0, length),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        private static async Task WriteLastChunkAsync(
            Stream stream,
            HeaderCollection trailers,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync("0\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (KeyValuePair<string, IEnumerable<string>> trailer in trailers)
            {
                if (!ProxyHeaderUtilities.IsAllowedTrailerField(trailer.Key))
                    continue;
                foreach (string value in trailer.Value)
                {
                    byte[] line = Encoding.ASCII.GetBytes($"{trailer.Key}: {value}\r\n");
                    await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                }
            }
            await stream.WriteAsync(ChunkTerminator, cancellationToken).ConfigureAwait(false);
        }

        private static string[] AllowedTrailerNames(
            HttpResponseMessage response,
            HeaderCollection trailers)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string declaration in response.Headers.Trailer)
            {
                foreach (string name in declaration.Split(',', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries))
                {
                    if (ProxyHeaderUtilities.IsAllowedTrailerField(name) && seen.Add(name))
                        names.Add(name);
                }
            }
            if (trailers != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> trailer in trailers)
                {
                    if (ProxyHeaderUtilities.IsAllowedTrailerField(trailer.Key) && seen.Add(trailer.Key))
                        names.Add(trailer.Key);
                }
            }
            return names.ToArray();
        }

        private async Task WriteProxyErrorAsync(
            int connectionId,
            Stream clientStream,
            ClientRequest request,
            HttpStatusCode status,
            string reason,
            ClientProcessContext processContext,
            bool captureCompletion,
            CancellationToken cancellationToken)
        {
            var response = new ServerResponse
            {
                RequestNumber = request.RequestNumber,
                StreamId = request.StreamId,
                Version = request.Version,
                Status = ((int)status).ToString(CultureInfo.InvariantCulture),
                StatusDescription = reason,
                BodyBytes = Array.Empty<byte>(),
            };
            response.Headers["Connection"] = "close";
            response.ContentHeaders["Content-Length"] = "0";
            try
            {
                await clientStream.WriteAsync(response.ToByteArray(), cancellationToken).ConfigureAwait(false);
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger.Log(connectionId, LogLevel.DEBUG, "Proxy-generated response sent to client");
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                ObjectDisposedException or
                OperationCanceledException)
            {
                LogConnection(
                    connectionId,
                    LogLevel.WARN,
                    $"Client connection ended while writing a proxy-generated response: {ex.Message}");
                return;
            }

            if (!captureCompletion)
                return;

            if (_proxy.TryRegisterStreamedResponseFinalizer(
                    connectionId,
                    request,
                    response,
                    processContext,
                    out WebServiceProxy.StreamedResponseFinalizer finalizer,
                    proxyGenerated: true))
            {
                finalizer.Complete(DateTime.Now, prepareResponse: null);
            }
        }

        private async Task CompleteClientOutputAsync(Stream clientStream, int connectionId)
        {
            if (clientStream is not DuplexPipeStream duplex)
                return;
            try
            {
                await duplex.CompleteOutputAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                LogConnection(
                    connectionId,
                    LogLevel.DEBUG,
                    $"Client output was already closed: {ex.Message}");
            }
        }

        private async Task HandleWebSocketAsync(
            int connectionID,
            Uri uri,
            Stream clientStream,
            ClientRequest clientRequest,
            ClientProcessContext processContext,
            CancellationToken ct)
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
                processContext,
                ct).ConfigureAwait(false);
        }

        // Headers only: the body is read by RelayResponseAsync. The capture stores the decoded
        // body, so it never claims chunked framing (#148); this connection closes after one response.
        private static ServerResponse CreateServerResponse(HttpResponseMessage response)
        {
            var serverResponse = new ServerResponse
            {
                Status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                StatusDescription = response.ReasonPhrase,
                Version = "HTTP/" + response.Version.ToString(),
            };

            response.Headers.TransferEncodingChunked = false;
            serverResponse.Headers.CopyFrom(response.Headers);
            serverResponse.ContentHeaders.CopyFrom(response.Content.Headers);
            serverResponse.Headers["Connection"] = "close";
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
                    ProxyRequestReadFailure
                        .UnsupportedTransferCoding =>
                        UnsupportedTransferCodingResponse,
                    ProxyRequestReadFailure
                        .MalformedTransferCoding =>
                        MalformedTransferCodingResponse,
                    ProxyRequestReadFailure
                        .UnsupportedExpectation =>
                        UnsupportedExpectationResponse,
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
            ClientProcessContext processContext,
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
                    new InterceptedTunnelContext(connectionID, connectRequest, processContext));
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
                _proxy.RaiseFailedSslConnectionRequest(connectionID, ex, processContext);
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

        internal ValueTask CompleteOutputAsync() => _writer.CompleteAsync();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
