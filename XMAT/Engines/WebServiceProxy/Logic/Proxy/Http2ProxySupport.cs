// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture.Proxy
{
    /// <summary>
    /// Converts Kestrel's protocol-neutral request representation into the
    /// request model used by capture events, scripts, and persisted sessions.
    /// </summary>
    internal static class AspNetCoreRequestAdapter
    {
        internal static async Task<ClientRequest> CreateAsync(
            HttpContext context,
            int requestNumber,
            CancellationToken cancellationToken,
            ProxyRequestReadPolicy policy = null,
            ProxyRequestBufferBudget bufferBudget = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            policy ??= ProxyRequestReadPolicy.Default;
            bufferBudget ??= ProxyRequestBufferBudget.Shared;
            policy.Validate();

            var request = context.Request;
            if (request.ContentLength > policy.MaxBodyBytes)
            {
                throw new ProxyRequestBodyReadException(
                    ProxyRequestBodyReadFailure.BodyTooLarge);
            }

            var result = new ClientRequest
            {
                RequestNumber = requestNumber,
                Version = request.Protocol,
                StreamId = context.Features.Get<IHttp2StreamIdFeature>()?.StreamId,
                Scheme = request.Scheme,
                // Kestrel keeps IPv6 brackets; capture and policy use the bracket-free IP literal
                // and the DNS spelling the client sent.
                Host = HostAuthority.TryNormalizeHost(request.Host.Host, out string host) ? host : request.Host.Host,
                Port = request.Host.Port ?? GetDefaultPort(request.Scheme),
                Method = request.Method,
                Path = request.PathBase + request.Path + request.QueryString
            };

            foreach (var header in request.Headers)
            {
                if (header.Key.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (string value in header.Value)
                {
                    string storedValue = StoredHeaderValue(header.Key, value);
                    if (storedValue == null)
                        continue;

                    if (ProxyHeaderUtilities.IsContentHeader(header.Key))
                        result.ContentHeaders.Add(header.Key, storedValue);
                    else
                        result.Headers.Add(header.Key, storedValue);
                }
            }

            result.BodyBytes = await ReadBodyAsync(
                request.Body,
                result,
                policy,
                bufferBudget,
                cancellationToken).ConfigureAwait(false);

            // Chunked HTTP/1.1 or HTTP/2 without content-length: forward it without a length too.
            result.UnknownLengthBody = request.ContentLength == null &&
                (result.BodyBytes.Length != 0 ||
                 request.Headers.TransferEncoding.ToString().Contains("chunked", StringComparison.OrdinalIgnoreCase));

            return result;
        }

        private static async Task<byte[]> ReadBodyAsync(
            Stream source,
            ClientRequest request,
            ProxyRequestReadPolicy policy,
            ProxyRequestBufferBudget bufferBudget,
            CancellationToken cancellationToken)
        {
            const int BufferSize = 16 * 1024;
            using ProxyRequestBufferLease lease = bufferBudget.CreateLease();
            using var totalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalDeadline.CancelAfter(policy.BodyTotalTimeout);
            using var body = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long retainedHeaders = InitialProxyRequestReader.EstimateRetainedRequestBytes(request);
                if (!lease.TryEnsure(checked(retainedHeaders + buffer.Length)))
                {
                    throw new ProxyRequestBodyReadException(
                        ProxyRequestBodyReadFailure.BufferExhausted);
                }

                while (true)
                {
                    using var inactivityDeadline =
                        CancellationTokenSource.CreateLinkedTokenSource(totalDeadline.Token);
                    inactivityDeadline.CancelAfter(policy.BodyInactivityTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(
                            buffer.AsMemory(0, BufferSize),
                            inactivityDeadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ProxyRequestBodyReadException(
                            totalDeadline.IsCancellationRequested
                                ? ProxyRequestBodyReadFailure.TotalTimeout
                                : ProxyRequestBodyReadFailure.InactivityTimeout);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalDeadline.IsCancellationRequested)
                    {
                        throw new ProxyRequestBodyReadException(
                            ProxyRequestBodyReadFailure.TotalTimeout);
                    }
                    if (read == 0)
                    {
                        break;
                    }

                    long nextLength = checked(body.Length + read);
                    if (nextLength > policy.MaxBodyBytes)
                    {
                        throw new ProxyRequestBodyReadException(
                            ProxyRequestBodyReadFailure.BodyTooLarge);
                    }

                    long reservation = checked(retainedHeaders + buffer.Length + (nextLength * 3));
                    if (!lease.TryEnsure(reservation))
                    {
                        throw new ProxyRequestBodyReadException(
                            ProxyRequestBodyReadFailure.BufferExhausted);
                    }

                    body.Write(buffer, 0, read);
                }

                return body.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Kestrel already removed the final chunked coding from request.Body,
        // so drop it from the stored header too; other codings stay as sent.
        private static string StoredHeaderValue(string key, string value)
        {
            if (!string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                return value;

            int separator = value.LastIndexOf(',');
            if (!string.Equals(value[(separator + 1)..].Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                return value;

            string remaining = separator < 0
                ? string.Empty
                : value[..separator].TrimEnd(' ', '\t', ',');
            return remaining.Length == 0 ? null : remaining;
        }

        private static int GetDefaultPort(string scheme)
        {
            return string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? 443
                : 80;
        }
    }

    /// <summary>
    /// Builds the origin request independently of the protocol used by the client.
    /// </summary>
    internal static class ProxyHttpRequestFactory
    {
        internal static HttpRequestMessage Create(ClientRequest request, Uri uri)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(uri);

            var message = new HttpRequestMessage
            {
                Method = new HttpMethod(request.Method),
                RequestUri = uri,
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = new BufferedRequestContent(
                    request.BodyBytes ?? Array.Empty<byte>(),
                    knownLength: !request.UnknownLengthBody)
            };

            var connectionHeaderNames = ProxyHeaderUtilities.GetConnectionHeaderNames(request.Headers);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                if (!ProxyHeaderUtilities.IsHopByHopHeader(header.Key, connectionHeaderNames))
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.ContentHeaders)
            {
                // Content calculates the length from the potentially
                // script-modified body, so never forward a stale client value.
                if (!ProxyHeaderUtilities.IsHopByHopHeader(header.Key, connectionHeaderNames) &&
                    !string.Equals(
                        header.Key,
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    message.Content.Headers.TryAddWithoutValidation(
                        header.Key,
                        header.Value);
                }
            }
            return message;
        }
    }

    // ByteArrayContent disables duplex HTTP/2 sending. An origin that
    // responds while reading can then exhaust its response window before the
    // upload finishes. Custom content allows HttpClient to drain both directions,
    // while XMAT still captures and edits complete buffered bodies. A body the
    // client sent without a length is forwarded the same way (chunked on HTTP/1.1).
    internal sealed class BufferedRequestContent(byte[] body, bool knownLength = true) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? body.Length : 0;
            return knownLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext context, CancellationToken cancellationToken) =>
            stream.WriteAsync(body, cancellationToken).AsTask();
    }

    /// <summary>
    /// Associates an intercepted loopback connection with the original client
    /// connection and CONNECT request that created it.
    /// </summary>
    internal sealed class InterceptedTunnelContext
    {
        internal InterceptedTunnelContext(
            int connectionId,
            ClientRequest connectRequest,
            ClientProcessContext processContext = null)
        {
            ConnectionId = connectionId;
            ConnectRequest = connectRequest ??
                throw new ArgumentNullException(nameof(connectRequest));
            ProcessContext = processContext;
        }

        internal int ConnectionId { get; }
        internal ClientRequest ConnectRequest { get; }
        internal ClientProcessContext ProcessContext { get; }
        private int _tlsCompleted;

        internal bool TryMarkTlsCompleted()
        {
            // Certificate selection can be entered concurrently; publish the
            // completion event exactly once for the original CONNECT request.
            return Interlocked.Exchange(ref _tlsCompleted, 1) == 0;
        }
    }

    /// <summary>
    /// Correlates Kestrel's loopback remote port with the original proxy tunnel.
    /// The entry lives only as long as the bidirectional byte relay.
    /// </summary>
    internal sealed class InterceptedTunnelRegistry
    {
        private readonly ConcurrentDictionary<int, InterceptedTunnelContext> _tunnels = new();

        internal void Register(int remotePort, InterceptedTunnelContext context)
        {
            if (!TryRegister(remotePort, context))
                throw new InvalidOperationException($"A tunnel is already registered for port {remotePort}.");
        }

        internal bool TryRegister(int remotePort, InterceptedTunnelContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return _tunnels.TryAdd(remotePort, context);
        }

        internal bool TryGet(int remotePort, out InterceptedTunnelContext context)
        {
            return _tunnels.TryGetValue(remotePort, out context);
        }

        internal void Remove(int remotePort)
        {
            _tunnels.TryRemove(remotePort, out _);
        }
    }

    /// <summary>
    /// Handles HTTP/1.1 and HTTP/2 requests after Kestrel has terminated TLS
    /// and decoded the wire protocol.
    /// </summary>
    internal sealed class InterceptedProxyApplication
    {
        private readonly WebServiceProxy _proxy;
        private readonly HttpClient _httpClient;
        private readonly InterceptedTunnelRegistry _tunnels;
        private readonly Logger _logger;
        private readonly CancellationToken _stoppingToken;
        private readonly ProxyRequestReadPolicy _requestReadPolicy;
        private readonly ProxyRequestBufferBudget _requestBufferBudget;
        private readonly Action<int, LogLevel, string> _logObserver;

        internal InterceptedProxyApplication(
            WebServiceProxy proxy,
            HttpClient httpClient,
            InterceptedTunnelRegistry tunnels,
            Logger logger = null,
            ProxyRequestReadPolicy requestReadPolicy = null,
            ProxyRequestBufferBudget requestBufferBudget = null,
            Action<int, LogLevel, string> logObserver = null,
            CancellationToken stoppingToken = default)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tunnels = tunnels ?? throw new ArgumentNullException(nameof(tunnels));
            _logger = logger;
            _stoppingToken = stoppingToken;
            _requestReadPolicy = requestReadPolicy ?? ProxyRequestReadPolicy.Default;
            _requestReadPolicy.Validate();
            _requestBufferBudget = requestBufferBudget ?? ProxyRequestBufferBudget.Shared;
            _logObserver = logObserver;
        }

        internal async Task InvokeAsync(HttpContext context)
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                _stoppingToken);
            try
            {
                await ProcessRequestAsync(
                    context,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                Log(0, LogLevel.DEBUG, "Intercepted request canceled during shutdown or client disconnect.");
                context.Abort();
            }
        }

        private async Task ProcessRequestAsync(
            HttpContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ProxyHeaderUtilities.HasUnsupportedExpectation(
                    context.Request.Headers.Expect.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status417ExpectationFailed;
                context.Response.ContentLength = 0;
                return;
            }
            string transferEncoding = context.Request.Headers.TransferEncoding.ToString();
            if (ProxyHeaderUtilities.HasMalformedTransferCoding(transferEncoding))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentLength = 0;
                return;
            }
            if (ProxyHeaderUtilities.HasUnsupportedTransferCoding(transferEncoding))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.ContentLength = 0;
                return;
            }

            // Kestrel sees the loopback relay as its client. Its remote port is
            // the stable key back to XMAT's original device connection.
            if (!_tunnels.TryGet(context.Connection.RemotePort, out var tunnel))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            ClientRequest clientRequest;
            try
            {
                clientRequest = await AspNetCoreRequestAdapter.CreateAsync(
                    context,
                    _proxy.GetNextRequestID(),
                    cancellationToken,
                    _requestReadPolicy,
                    _requestBufferBudget).ConfigureAwait(false);
            }
            catch (ProxyRequestBodyReadException ex)
            {
                int statusCode = ex.Failure switch
                {
                    ProxyRequestBodyReadFailure.InactivityTimeout or
                    ProxyRequestBodyReadFailure.TotalTimeout => StatusCodes.Status408RequestTimeout,
                    ProxyRequestBodyReadFailure.BodyTooLarge => StatusCodes.Status413PayloadTooLarge,
                    _ => StatusCodes.Status503ServiceUnavailable,
                };
                string limit = ex.Failure switch
                {
                    ProxyRequestBodyReadFailure.InactivityTimeout =>
                        $"the {_requestReadPolicy.BodyInactivityTimeout:g} request-body inactivity limit",
                    ProxyRequestBodyReadFailure.TotalTimeout =>
                        $"the {_requestReadPolicy.BodyTotalTimeout:g} total request-body time limit",
                    ProxyRequestBodyReadFailure.BodyTooLarge =>
                        $"the {_requestReadPolicy.MaxBodyBytes} byte request-body size limit",
                    _ =>
                        $"the {_requestBufferBudget.MaximumBytes} byte aggregate request-buffer budget",
                };
                Log(tunnel.ConnectionId, LogLevel.WARN, $"Intercepted request body rejected at {limit}.");
                context.Response.StatusCode = statusCode;
                context.Response.ContentLength = 0;
                if (context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
                    context.Response.Headers.Connection = "close";
                return;
            }

            if (ProxyHeaderUtilities.HasUnsupportedTransferCoding(clientRequest))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.ContentLength = 0;
                return;
            }

            if (!await _proxy.RaiseReceivedWebRequestAsync(
                tunnel.ConnectionId,
                clientRequest,
                cancellationToken,
                tunnel.ProcessContext).ConfigureAwait(false))
            {
                context.Abort();
                return;
            }

            if (ProxyHeaderUtilities.HasUnsupportedTransferCoding(clientRequest))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.ContentLength = 0;
                return;
            }

            Uri uri = ProxyUriFactory.Create(clientRequest);
            if (!HostListEntry.IsSupportedTargetScheme(uri.Scheme))
            {
                Log(tunnel.ConnectionId, LogLevel.WARN, "Client request used an unsupported target scheme.");
                context.Abort();
                return;
            }
            if (context.WebSockets.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(
                    context,
                    tunnel.ConnectionId,
                    uri,
                    clientRequest,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            using HttpRequestMessage upstreamRequest =
                ProxyHttpRequestFactory.Create(clientRequest, uri);
            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await _httpClient.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Log(tunnel.ConnectionId, LogLevel.ERROR, $"Upstream HTTP request to {uri} failed: {ex.Message}");
                await CommitProxyResponseAsync(
                    context,
                    tunnel,
                    clientRequest,
                    HttpStatusCode.BadGateway,
                    "Bad Gateway").ConfigureAwait(false);
                return;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log(tunnel.ConnectionId, LogLevel.ERROR, $"Upstream HTTP request to {uri} timed out: {ex.Message}");
                await CommitProxyResponseAsync(
                    context,
                    tunnel,
                    clientRequest,
                    HttpStatusCode.GatewayTimeout,
                    "Gateway Timeout").ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log(tunnel.ConnectionId, LogLevel.ERROR, $"Upstream HTTP request to {uri} timed out: {ex.Message}");
                await CommitProxyResponseAsync(
                    context,
                    tunnel,
                    clientRequest,
                    HttpStatusCode.GatewayTimeout,
                    "Gateway Timeout").ConfigureAwait(false);
                return;
            }

            using (upstreamResponse)
            {
                await RelayResponseAsync(
                    context,
                    tunnel,
                    clientRequest,
                    upstreamResponse,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Buffers the response so scripts can change it, unless it streams (event streams, audio,
        // video, Stream all, or a body past the capture limit). Kestrel frames it for the client:
        // without a Content-Length it is chunked on HTTP/1.1 and plain DATA frames on HTTP/2.
        private async Task RelayResponseAsync(
            HttpContext context,
            InterceptedTunnelContext tunnel,
            ClientRequest clientRequest,
            HttpResponseMessage upstreamResponse,
            CancellationToken cancellationToken)
        {
            if (ProxyResponseStreaming.HasMessageBody(clientRequest.Method, upstreamResponse.StatusCode) &&
                ProxyHeaderUtilities.HasUnsupportedTransferCoding(upstreamResponse.Headers))
            {
                Log(
                    tunnel.ConnectionId,
                    LogLevel.ERROR,
                    $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} used " +
                    $"unsupported transfer coding: {upstreamResponse.Headers.TransferEncoding}.");
                await CommitProxyResponseAsync(
                    context,
                    tunnel,
                    clientRequest,
                    HttpStatusCode.BadGateway,
                    "Bad Gateway").ConfigureAwait(false);
                return;
            }

            long? declaredLength = upstreamResponse.Content.Headers.ContentLength;
            ServerResponse serverResponse = CreateServerResponse(clientRequest, upstreamResponse);
            if (!ProxyResponseStreaming.HasMessageBody(clientRequest.Method, upstreamResponse.StatusCode))
            {
                if (!await AcceptResponseAsync(tunnel, clientRequest, serverResponse, cancellationToken)
                        .ConfigureAwait(false))
                {
                    context.Abort();
                    return;
                }
                await WriteResponseAsync(context, tunnel.ConnectionId, serverResponse, declaredLength.HasValue, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            ResponseStreamingOptions options = _proxy.EffectiveResponseStreaming;
            Stream body = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var reader = new IdleTimeoutReader(body, _httpClient.Timeout, cancellationToken);
            var capture = new CapturedResponseBody(options.CaptureLimitBytes);
            ReadOnlyMemory<byte> overflow = ReadOnlyMemory<byte>.Empty;
            if (!ProxyResponseStreaming.StreamsImmediately(upstreamResponse, options))
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
                    Log(
                        tunnel.ConnectionId,
                        LogLevel.ERROR,
                        $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} failed: " +
                        ex.Message);
                    await CommitProxyResponseAsync(
                        context,
                        tunnel,
                        clientRequest,
                        HttpStatusCode.BadGateway,
                        "Bad Gateway").ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException ex)
                {
                    Log(
                        tunnel.ConnectionId,
                        LogLevel.ERROR,
                        $"Upstream response for {clientRequest.Method} {clientRequest.Host}{clientRequest.Path} timed out: " +
                        ex.Message);
                    await CommitProxyResponseAsync(
                        context,
                        tunnel,
                        clientRequest,
                        HttpStatusCode.GatewayTimeout,
                        "Gateway Timeout").ConfigureAwait(false);
                    return;
                }

                capture = buffered.Capture;
                overflow = buffered.Overflow;
                if (buffered.Complete)
                {
                    // Keep the full response mutable until scripts have run, matching
                    // the behavior of the original HTTP/1 proxy implementation.
                    serverResponse.BodyBytes = capture.Freeze();
                    serverResponse.Trailers.CopyFrom(upstreamResponse.TrailingHeaders);
                    if (!await AcceptResponseAsync(tunnel, clientRequest, serverResponse, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        context.Abort();
                        return;
                    }
                    await WriteResponseAsync(context, tunnel.ConnectionId, serverResponse, declaredLength.HasValue, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }

            await StreamResponseAsync(
                context,
                tunnel,
                clientRequest,
                serverResponse,
                upstreamResponse,
                reader,
                capture,
                overflow,
                declaredLength,
                cancellationToken).ConfigureAwait(false);
        }

        private Task<bool> AcceptResponseAsync(
            InterceptedTunnelContext tunnel,
            ClientRequest clientRequest,
            ServerResponse serverResponse,
            CancellationToken cancellationToken) =>
            _proxy.RaiseReceivedWebResponseAsync(
                tunnel.ConnectionId,
                clientRequest,
                serverResponse,
                cancellationToken,
                tunnel.ProcessContext);

        private async Task CommitProxyResponseAsync(
            HttpContext context,
            InterceptedTunnelContext tunnel,
            ClientRequest request,
            HttpStatusCode status,
            string reason)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
                return;

            context.Response.StatusCode = (int)status;
            context.Response.ContentLength = 0;
            try
            {
                await context.Response.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or
                OperationCanceledException or
                ObjectDisposedException)
            {
                Log(
                    tunnel.ConnectionId,
                    LogLevel.WARN,
                    $"Client connection ended while writing a proxy-generated response: {ex.Message}");
                return;
            }
            DateTime committedAt = DateTime.Now;
            var response = new ServerResponse
            {
                RequestNumber = request.RequestNumber,
                StreamId = request.StreamId,
                Version = context.Request.Protocol,
                Status = ((int)status).ToString(CultureInfo.InvariantCulture),
                StatusDescription = reason,
                BodyBytes = Array.Empty<byte>(),
            };
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in
                context.Response.Headers)
            {
                HeaderCollection destination = ProxyHeaderUtilities.IsContentHeader(header.Key)
                    ? response.ContentHeaders
                    : response.Headers;
                foreach (string value in header.Value)
                    destination.Add(header.Key, value);
            }
            if (!response.ContentHeaders.Contains("Content-Length"))
                response.ContentHeaders["Content-Length"] = "0";

            if (_proxy.TryRegisterStreamedResponseFinalizer(
                    tunnel.ConnectionId,
                    request,
                    response,
                    tunnel.ProcessContext,
                    out WebServiceProxy.StreamedResponseFinalizer finalizer,
                    proxyGenerated: true))
            {
                finalizer.Complete(committedAt, prepareResponse: null);
            }
        }

        // The headers go out first; the body follows as it arrives, teed into a capture copy that
        // keeps up to the capture limit. Scripts see that copy only after the body has ended.
        private async Task StreamResponseAsync(
            HttpContext context,
            InterceptedTunnelContext tunnel,
            ClientRequest clientRequest,
            ServerResponse serverResponse,
            HttpResponseMessage upstreamResponse,
            IdleTimeoutReader reader,
            CapturedResponseBody capture,
            ReadOnlyMemory<byte> overflow,
            long? declaredLength,
            CancellationToken cancellationToken)
        {
            WebServiceProxy.StreamedResponseFinalizer finalizer =
                _proxy.RegisterStreamedResponseFinalizer(
                    tunnel.ConnectionId,
                    clientRequest,
                    serverResponse,
                    tunnel.ProcessContext);
            try
            {
                HttpResponse response = context.Response;
                response.StatusCode = int.Parse(serverResponse.Status, CultureInfo.InvariantCulture);
                CopyResponseHeaders(serverResponse, response);
                response.ContentLength = declaredLength;
                if (response.SupportsTrailers())
                {
                    var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string declaration in upstreamResponse.Headers.Trailer)
                    {
                        foreach (string name in declaration.Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries))
                        {
                            if (ProxyHeaderUtilities.IsAllowedTrailerField(name) && declared.Add(name))
                                response.DeclareTrailer(name);
                        }
                    }
                }

                await response.StartAsync(cancellationToken).ConfigureAwait(false);
                serverResponse.BodyBytes = Array.Empty<byte>();
                serverResponse.CaptureTruncatedAt = 0;
                await _proxy.RaiseWebResponseProgressAsync(
                    tunnel.ConnectionId,
                    clientRequest,
                    serverResponse,
                    _stoppingToken,
                    tunnel.ProcessContext).ConfigureAwait(false);

                await ProxyResponseStreaming.RelayBodyAsync(
                    reader,
                    capture,
                    overflow,
                    async data =>
                    {
                        await response.Body.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    },
                    NotifyCaptureAsync).ConfigureAwait(false);

                serverResponse.Trailers.CopyFrom(upstreamResponse.TrailingHeaders);
                AppendTrailers(response, serverResponse.Trailers, tunnel.ConnectionId);
                await response.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                if (!_stoppingToken.IsCancellationRequested)
                {
                    Log(tunnel.ConnectionId, LogLevel.WARN, $"Streamed response ended early: {ex.Message}");
                }
                context.Abort();
            }
            finally
            {
                DateTime transportEndedAt = DateTime.Now;
                try
                {
                    upstreamResponse.Dispose();
                }
                finally
                {
                    finalizer.Complete(
                        transportEndedAt,
                        () => capture.CopyFinalTo(serverResponse));
                }
            }

            // Capture updates use the proxy's stop token: the client may already have hung up.
            Task NotifyCaptureAsync()
            {
                capture.CopyProgressTo(serverResponse);
                return _proxy.RaiseWebResponseProgressAsync(
                    tunnel.ConnectionId,
                    clientRequest,
                    serverResponse,
                    _stoppingToken,
                    tunnel.ProcessContext);
            }
        }

        // Kestrel sends trailers only over HTTP/2; HTTP/1.1 clients get the body without them.
        private void AppendTrailers(HttpResponse response, HeaderCollection trailers, int connectionId)
        {
            if (trailers.Count == 0 || !response.SupportsTrailers())
                return;

            foreach (KeyValuePair<string, IEnumerable<string>> trailer in trailers)
            {
                if (!ProxyHeaderUtilities.IsAllowedTrailerField(trailer.Key))
                    continue;
                try
                {
                    response.AppendTrailer(
                        trailer.Key,
                        new Microsoft.Extensions.Primitives.StringValues(new List<string>(trailer.Value).ToArray()));
                }
                catch (InvalidOperationException ex)
                {
                    Log(connectionId, LogLevel.WARN, $"Could not forward trailer {trailer.Key}: {ex.Message}");
                }
            }
        }

        private async Task ProxyWebSocketAsync(
            HttpContext context,
            int connectionId,
            Uri uri,
            ClientRequest clientRequest,
            CancellationToken cancellationToken)
        {
            using var upstream = new ClientWebSocket();
            upstream.Options.Proxy = null;
            upstream.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            upstream.Options.CollectHttpResponseDetails = true;

            foreach (KeyValuePair<string, IEnumerable<string>> header in clientRequest.Headers)
            {
                if (ProxyHeaderUtilities.IsWebSocketHandshakeHeader(header.Key) ||
                    ProxyHeaderUtilities.IsHopByHopHeader(header.Key))
                {
                    continue;
                }

                foreach (string value in header.Value)
                    upstream.Options.SetRequestHeader(header.Key, value);
            }

            foreach (string protocol in context.WebSockets.WebSocketRequestedProtocols)
                upstream.Options.AddSubProtocol(protocol);

            var upstreamUri = new UriBuilder(uri)
            {
                Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Port = uri.IsDefaultPort ? -1 : uri.Port
            }.Uri;

            try
            {
                await upstream.ConnectAsync(upstreamUri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                // A failure racing with client abort or shutdown is a cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                RespondToFailedUpgrade(context, connectionId, upstream, ex);
                return;
            }

            using WebSocket downstream =
                await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol).ConfigureAwait(false);

            // WebSockets leave HTTP framing after the handshake, so relay messages
            // directly until either peer closes and then stop the opposite pump.
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

            Task downstreamToUpstream = RelayWebSocketAsync(
                downstream,
                upstream,
                relayCancellation.Token);
            Task upstreamToDownstream = RelayWebSocketAsync(
                upstream,
                downstream,
                relayCancellation.Token);

            await Task.WhenAny(downstreamToUpstream, upstreamToDownstream).ConfigureAwait(false);
            relayCancellation.Cancel();
            await Task.WhenAll(downstreamToUpstream, upstreamToDownstream).ConfigureAwait(false);
        }

        // The server either answered the upgrade without switching protocols (for example 401
        // for an expired token) or could not be reached. Give the client that answer, or 502,
        // as the HTTP/1 WebSocket path does, instead of failing the whole request.
        private void RespondToFailedUpgrade(
            HttpContext context,
            int connectionId,
            ClientWebSocket upstream,
            Exception exception)
        {
            int status = (int)upstream.HttpStatusCode;
            if (status < StatusCodes.Status200OK)
            {
                Log(connectionId, LogLevel.ERROR,
                    $"Upstream WebSocket handshake failed: {exception.GetBaseException().Message}");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            Log(connectionId, LogLevel.INFO, $"Upstream refused the WebSocket upgrade with status {status}.");
            context.Response.StatusCode = status;
            if (upstream.HttpResponseHeaders == null)
                return;

            foreach (KeyValuePair<string, IEnumerable<string>> header in upstream.HttpResponseHeaders)
            {
                // The upstream body is not available, so its length must not be forwarded.
                if (ProxyHeaderUtilities.IsHopByHopHeader(header.Key) ||
                    ProxyHeaderUtilities.IsWebSocketHandshakeHeader(header.Key) ||
                    string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                context.Response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(
                    new List<string>(header.Value).ToArray());
            }
        }

        private static async Task RelayWebSocketAsync(
            WebSocket source,
            WebSocket destination,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8192];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await source.ReceiveAsync(
                        buffer,
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await destination.CloseOutputAsync(
                            result.CloseStatus.GetValueOrDefault(WebSocketCloseStatus.NormalClosure),
                            result.CloseStatusDescription,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await destination.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        private void Log(int connectionId, LogLevel level, string message)
        {
            _logObserver?.Invoke(connectionId, level, message);
            _logger?.Log(connectionId, level, message);
        }

        // Headers only: RelayResponseAsync reads the body.
        private static ServerResponse CreateServerResponse(
            ClientRequest request,
            HttpResponseMessage response)
        {
            var result = new ServerResponse
            {
                RequestNumber = request.RequestNumber,
                StreamId = request.StreamId,
                Version = GetProtocol(response.Version),
                Status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                StatusDescription = response.ReasonPhrase,
            };

            // HttpClient de-chunks any body it reads but keeps the header, so drop
            // that coding; bodyless responses keep their headers (nothing decoded).
            if (ProxyResponseStreaming.HasMessageBody(request.Method, response.StatusCode))
                response.Headers.TransferEncodingChunked = false;
            result.Headers.CopyFrom(response.Headers);
            result.ContentHeaders.CopyFrom(response.Content.Headers);
            return result;
        }

        private static string GetProtocol(Version version)
        {
            return version.Minor == 0
                ? $"HTTP/{version.Major}"
                : $"HTTP/{version.Major}.{version.Minor}";
        }

        private async Task WriteResponseAsync(
            HttpContext context,
            int connectionId,
            ServerResponse response,
            bool originDeclaredLength,
            CancellationToken cancellationToken)
        {
            var statusCode = int.Parse(response.Status, CultureInfo.InvariantCulture);
            context.Response.StatusCode = statusCode;
            CopyResponseHeaders(response, context.Response);

            if (statusCode < 200 || statusCode == StatusCodes.Status204NoContent)
            {
                context.Response.ContentLength = null;
                return;
            }

            if (statusCode == StatusCodes.Status205ResetContent)
            {
                context.Response.ContentLength = 0;
                return;
            }

            // HEAD and 304 may describe the representation's length, not the
            // empty response body. Preserve that metadata without writing.
            if (HttpMethods.IsHead(context.Request.Method) ||
                statusCode == StatusCodes.Status304NotModified)
            {
                return;
            }

            // Content-Length stays Content-Length, for the possibly script-edited body. A body
            // the origin sent without one stays that way, so trailers can follow it.
            context.Response.ContentLength = originDeclaredLength ? response.BodyBytes.Length : null;
            if (response.Trailers.Count != 0 && context.Response.SupportsTrailers())
            {
                foreach (KeyValuePair<string, IEnumerable<string>> trailer in response.Trailers)
                {
                    if (ProxyHeaderUtilities.IsAllowedTrailerField(trailer.Key))
                        context.Response.DeclareTrailer(trailer.Key);
                }
            }
            for (var offset = 0;
                offset < response.BodyBytes.Length;
                offset += ProxyResponseStreaming.ReadBufferSize)
            {
                await context.Response.Body.WriteAsync(
                    response.BodyBytes.AsMemory(
                        offset,
                        Math.Min(
                            ProxyResponseStreaming.ReadBufferSize,
                            response.BodyBytes.Length - offset)),
                    cancellationToken).ConfigureAwait(false);
            }
            AppendTrailers(context.Response, response.Trailers, connectionId);
        }

        private static void CopyResponseHeaders(ServerResponse source, HttpResponse response)
        {
            var connectionHeaderNames = ProxyHeaderUtilities.GetConnectionHeaderNames(source.Headers);
            CopyHeaders(source.Headers, response, connectionHeaderNames);
            CopyHeaders(source.ContentHeaders, response, connectionHeaderNames);
        }

        private static void CopyHeaders(
            HeaderCollection source,
            HttpResponse response,
            ISet<string> connectionHeaderNames)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in source)
            {
                if (ProxyHeaderUtilities.IsHopByHopHeader(header.Key, connectionHeaderNames))
                    continue;

                response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(
                    new System.Collections.Generic.List<string>(header.Value).ToArray());
            }
        }
    }

    /// <summary>
    /// Private loopback Kestrel host that performs dynamic TLS termination,
    /// ALPN negotiation, HTTP/2 framing, HPACK, and stream multiplexing.
    /// </summary>
    internal sealed class InterceptedHttpHost : IAsyncDisposable
    {
        private readonly Func<int, string, X509Certificate2> _certificateSelector;
        private readonly RequestDelegate _application;
        private IHost _host;

        internal InterceptedHttpHost(
            Func<int, string, X509Certificate2> certificateSelector,
            RequestDelegate application)
        {
            _certificateSelector = certificateSelector ??
                throw new ArgumentNullException(nameof(certificateSelector));
            _application = application ??
                throw new ArgumentNullException(nameof(application));
        }

        internal int Port { get; private set; }

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_host != null)
                throw new InvalidOperationException("The intercepted HTTP host is already running.");

            var builder = new HostBuilder();
            builder.ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    // The request adapter applies the shared bounded read policy and
                    // reports 408/413 consistently for HTTP/1.1 and HTTP/2.
                    options.Limits.MinRequestBodyDataRate = null;
                    options.Limits.MaxRequestBodySize = null;
                    // Port zero lets the OS choose a collision-free private port.
                    // Only the outer forward proxy connects to this listener.
                    options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        listenOptions.UseHttps(httpsOptions =>
                        {
                            httpsOptions.ServerCertificateSelector =
                                (connection, hostName) =>
                                    _certificateSelector(
                                        (connection.RemoteEndPoint as IPEndPoint)?.Port ?? 0,
                                        hostName);
                        });
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(_application);
                });
            });

            _host = builder.Build();
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);

            var server = _host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            string address = System.Linq.Enumerable.First(addresses.Addresses);
            Port = new Uri(address).Port;
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(StopAndDisposeAsync(CancellationToken.None, ProxyShutdownPolicy.Deadline));
        }

        // Graceful until the deadline token (or the default budget) fires, then connections are aborted.
        internal async Task StopAndDisposeAsync(
            CancellationToken deadline,
            TimeSpan? maxDuration = null)
        {
            if (_host == null)
                return;

            var host = _host;
            var hostDisposed = false;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            if (maxDuration.HasValue)
                budget.CancelAfter(maxDuration.Value);
            try
            {
                await ProxyHostShutdown.StopAndDisposeAsync(
                    host,
                    budget.Token).ConfigureAwait(false);
                hostDisposed = true;
            }
            catch (ProxyHostDisposalException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                hostDisposed = true;
                throw;
            }
            finally
            {
                if (hostDisposed && ReferenceEquals(_host, host))
                {
                    _host = null;
                    Port = 0;
                }
            }
        }
    }

    internal static class ProxyUriFactory
    {
        internal static Uri Create(ClientRequest request)
        {
            if (Uri.TryCreate(request.Path, UriKind.Absolute, out Uri uri))
                return uri;

            string path = request.Path;
            string query = string.Empty;
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = path[queryIndex..];
                path = path[..queryIndex];
            }

            return new UriBuilder(
                request.Scheme,
                request.Host,
                request.Port,
                path,
                query).Uri;
        }
    }

    internal static class ProxyHeaderUtilities
    {
        private static readonly HashSet<string> ForbiddenTrailerFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Age",
                "Authentication-Info",
                "Authorization",
                "Cache-Control",
                "Cookie",
                "Content-Encoding",
                "Content-Length",
                "Content-Range",
                "Content-Type",
                "Date",
                "Expect",
                "Expires",
                "Host",
                "If-Match",
                "If-Modified-Since",
                "If-None-Match",
                "If-Range",
                "If-Unmodified-Since",
                "Location",
                "Max-Forwards",
                "Pragma",
                "Proxy-Authenticate",
                "Proxy-Authentication-Info",
                "Proxy-Authorization",
                "Range",
                "Retry-After",
                "Set-Cookie",
                "Vary",
                "Warning",
                "WWW-Authenticate",
            };

        internal static bool HasUnsupportedTransferCoding(HttpResponseHeaders headers)
        {
            var count = 0;
            foreach (TransferCodingHeaderValue coding in headers.TransferEncoding)
            {
                count++;
                if (!coding.Value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return count > 1;
        }

        internal static bool HasUnsupportedTransferCoding(ClientRequest request) =>
            request.Headers.Contains("Transfer-Encoding");

        internal static bool HasUnsupportedTransferCoding(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string[] codings = value.Split(',', StringSplitOptions.TrimEntries);
            return codings.Length > 1 &&
                codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasMalformedTransferCoding(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string[] codings = value.Split(',', StringSplitOptions.TrimEntries);
            if (codings.Any(string.IsNullOrEmpty) ||
                !codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return codings.Count(
                coding => coding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) != 1;
        }

        internal static bool HasUnsupportedExpectation(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            foreach (string part in value.Split(','))
            {
                string expectation = part.Trim();
                if (expectation.Length == 0)
                    continue;
                if (!expectation.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool IsAllowedTrailerField(string name)
        {
            if (string.IsNullOrEmpty(name) ||
                ForbiddenTrailerFields.Contains(name) ||
                IsHopByHopHeader(name))
            {
                return false;
            }

            foreach (char character in name)
            {
                if (!char.IsAsciiLetterOrDigit(character) &&
                    !"`!#$%&'*+-.^_|~".Contains(character))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsContentHeader(string headerKey)
        {
            return headerKey.ToLowerInvariant() switch
            {
                "allow" or "content-disposition" or "content-encoding" or
                "content-language" or "content-length" or "content-location" or
                "content-md5" or "content-range" or "content-type" or
                "expires" or "last-modified" => true,
                _ => false
            };
        }

        internal static ISet<string> GetConnectionHeaderNames(HeaderCollection headers)
        {
            return (headers.GetHeaderValuesAsList("Connection") ?? Array.Empty<string>())
                .Where(value => value != null)
                .SelectMany(value => value.Split(','))
                .Select(name => name.Trim())
                .Where(name => name.Length != 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal static bool IsHopByHopHeader(
            string headerKey,
            ISet<string> connectionHeaderNames = null)
        {
            if (connectionHeaderNames?.Contains(headerKey) == true)
                return true;

            return headerKey.ToLowerInvariant() switch
            {
                "connection" or "keep-alive" or "proxy-authenticate" or
                "proxy-authorization" or "proxy-connection" or "te" or
                "trailer" or "transfer-encoding" or "upgrade" => true,
                _ => false
            };
        }

        internal static bool IsWebSocketHandshakeHeader(string headerKey)
        {
            return headerKey.ToLowerInvariant() switch
            {
                "host" or "origin" or "sec-websocket-accept" or
                "sec-websocket-extensions" or "sec-websocket-key" or
                "sec-websocket-protocol" or "sec-websocket-version" => true,
                _ => false
            };
        }
    }
}
