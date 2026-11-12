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
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed sending proxied request: {ex}");
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
            bool originChunked = result.Headers.TransferEncodingChunked == true;
            long? declaredLength = result.Content.Headers.ContentLength;
            // Chunked framing needs HTTP/1.1 on both sides: the status line carries the origin's version.
            bool chunkedAllowed =
                !string.Equals(clientRequest.Version, "HTTP/1.0", StringComparison.OrdinalIgnoreCase) &&
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
            byte[] received = Array.Empty<byte>();
            if (!ProxyResponseStreaming.StreamsImmediately(result, options))
            {
                bool complete;
                try
                {
                    (received, complete) = await ProxyResponseStreaming.ReadUpToLimitAsync(
                        reader,
                        options.CaptureLimitBytes,
                        declaredLength).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException)
                {
                    _logger.Log(connectionID, LogLevel.ERROR, $"Failed reading the server response: {ex.Message}");
                    return;
                }

                if (complete)
                {
                    serverResponse.BodyBytes = received;
                    serverResponse.Trailers.CopyFrom(result.TrailingHeaders);
                    if (!await AcceptResponseAsync(connectionID, clientRequest, serverResponse, processContext, ct)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    // Chunked stays chunked (and carries trailers) for HTTP/1.1 clients, like Fiddler;
                    // otherwise the possibly script-edited body gets its own Content-Length.
                    bool chunked = chunkedAllowed && (originChunked || serverResponse.Trailers.Count > 0);
                    await WriteResponseAsync(
                        connectionID,
                        clientStream,
                        FrameBufferedResponse(serverResponse, chunked),
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
                received,
                framing,
                declaredLength ?? 0,
                options.CaptureLimitBytes,
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
            byte[] received,
            ResponseFraming framing,
            long contentLength,
            int captureLimit,
            ClientProcessContext processContext,
            CancellationToken ct)
        {
            var capture = new CapturedResponseBody(captureLimit);
            try
            {
                await clientStream.WriteAsync(BuildHead(serverResponse, framing, contentLength), ct)
                    .ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                await NotifyCaptureAsync().ConfigureAwait(false);

                await ProxyResponseStreaming.RelayBodyAsync(
                    reader,
                    received,
                    capture,
                    async data =>
                    {
                        await clientStream.WriteAsync(
                            framing == ResponseFraming.Chunked ? Chunk(data.Span) : data,
                            ct).ConfigureAwait(false);
                        await clientStream.FlushAsync(ct).ConfigureAwait(false);
                    },
                    NotifyCaptureAsync).ConfigureAwait(false);

                serverResponse.Trailers.CopyFrom(result.TrailingHeaders);
                if (framing == ResponseFraming.Chunked)
                {
                    await clientStream.WriteAsync(LastChunk(serverResponse.Trailers), ct).ConfigureAwait(false);
                }
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _logger.Log(connectionID, LogLevel.DEBUG, "Streamed response sent to client");
            }
            catch (Exception ex) when (
                !_applicationStopping.IsCancellationRequested &&
                ex is IOException or
                SocketException or
                ObjectDisposedException or
                HttpRequestException or
                TimeoutException or
                OperationCanceledException)
            {
                // The response ended early, but it ended: its capture completes with what arrived. Release
                // the origin first, because the capture event may wait for a script.
                LogConnection(
                    connectionID,
                    LogLevel.WARN,
                    $"Streamed response ended early: {ex.Message}");
                result.Dispose();
            }

            capture.CopyTo(serverResponse);
            await _proxy.RaiseReceivedWebResponseAsync(
                connectionID,
                clientRequest,
                serverResponse,
                _applicationStopping,
                processContext,
                responseForwarded: true).ConfigureAwait(false);

            // Capture updates use the proxy's stop token, not the connection's: after
            // "Connection: close" the client may hang up as soon as it has the response.
            Task NotifyCaptureAsync()
            {
                capture.CopyTo(serverResponse);
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

        private static byte[] FrameBufferedResponse(ServerResponse response, bool chunked)
        {
            byte[] body = response.BodyBytes ?? Array.Empty<byte>();
            using var framed = new MemoryStream();
            framed.Write(BuildHead(
                response,
                chunked ? ResponseFraming.Chunked : ResponseFraming.ContentLength,
                body.Length));
            if (!chunked)
            {
                framed.Write(body);
            }
            else
            {
                if (body.Length != 0)
                    framed.Write(Chunk(body));
                framed.Write(LastChunk(response.Trailers));
            }
            return framed.ToArray();
        }

        // The capture keeps the origin's headers; the client gets framing for the decoded body.
        private static byte[] BuildHead(ServerResponse response, ResponseFraming framing, long contentLength)
        {
            var head = new StringBuilder();
            head.Append(response.Version).Append(' ').Append(response.Status).Append(' ')
                .Append(response.StatusDescription).Append("\r\n");
            AppendHeaders(head, response.Headers, framing);
            AppendHeaders(head, response.ContentHeaders, framing);
            if (framing == ResponseFraming.ContentLength)
            {
                head.Append("Content-Length: ")
                    .Append(contentLength.ToString(CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }
            else if (framing == ResponseFraming.Chunked)
            {
                head.Append("Transfer-Encoding: chunked\r\n");
            }
            head.Append("\r\n");
            return Encoding.ASCII.GetBytes(head.ToString());
        }

        private static void AppendHeaders(StringBuilder head, HeaderCollection headers, ResponseFraming framing)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
            {
                // Only a chunked body can carry the trailers that a Trailer header announces.
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    (framing != ResponseFraming.Chunked &&
                        header.Key.Equals("Trailer", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                foreach (string value in header.Value)
                {
                    head.Append(header.Key).Append(": ").Append(value).Append("\r\n");
                }
            }
        }

        private static byte[] Chunk(ReadOnlySpan<byte> data)
        {
            byte[] size = Encoding.ASCII.GetBytes(data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            byte[] chunk = new byte[size.Length + data.Length + 2];
            size.CopyTo(chunk, 0);
            data.CopyTo(chunk.AsSpan(size.Length));
            chunk[^2] = (byte)'\r';
            chunk[^1] = (byte)'\n';
            return chunk;
        }

        private static byte[] LastChunk(HeaderCollection trailers)
        {
            var last = new StringBuilder("0\r\n");
            foreach (KeyValuePair<string, IEnumerable<string>> trailer in trailers)
            {
                foreach (string value in trailer.Value)
                {
                    last.Append(trailer.Key).Append(": ").Append(value).Append("\r\n");
                }
            }
            last.Append("\r\n");
            return Encoding.ASCII.GetBytes(last.ToString());
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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
