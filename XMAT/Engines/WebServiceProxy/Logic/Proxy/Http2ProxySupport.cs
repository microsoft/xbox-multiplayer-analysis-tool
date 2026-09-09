// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            var request = context.Request;
            var result = new ClientRequest
            {
                RequestNumber = requestNumber,
                Version = request.Protocol,
                StreamId = context.Features.Get<IHttp2StreamIdFeature>()?.StreamId,
                Scheme = request.Scheme,
                Host = request.Host.Host,
                Port = request.Host.Port ?? GetDefaultPort(request.Scheme),
                Method = request.Method,
                Path = request.PathBase + request.Path + request.QueryString
            };

            foreach (var header in request.Headers)
            {
                foreach (string value in header.Value)
                {
                    if (ProxyHeaderUtilities.IsContentHeader(header.Key))
                        result.ContentHeaders.Add(header.Key, value);
                    else
                        result.Headers.Add(header.Key, value);
                }
            }

            // Scripts operate on complete bodies, so retain the existing
            // buffering behavior for each independent HTTP/2 stream.
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            result.BodyBytes = body.ToArray();

            return result;
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
                Content = new ByteArrayContent(request.BodyBytes ?? Array.Empty<byte>())
            };

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                if (!ProxyHeaderUtilities.IsHopByHopHeader(header.Key))
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.ContentHeaders)
            {
                // ByteArrayContent calculates the length from the potentially
                // script-modified body, so never forward a stale client value.
                if (!string.Equals(
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

    /// <summary>
    /// Associates an intercepted loopback connection with the original client
    /// connection and CONNECT request that created it.
    /// </summary>
    internal sealed class InterceptedTunnelContext
    {
        internal InterceptedTunnelContext(
            int connectionId,
            ClientRequest connectRequest)
        {
            ConnectionId = connectionId;
            ConnectRequest = connectRequest ??
                throw new ArgumentNullException(nameof(connectRequest));
        }

        internal int ConnectionId { get; }
        internal ClientRequest ConnectRequest { get; }
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

        internal InterceptedProxyApplication(
            WebServiceProxy proxy,
            HttpClient httpClient,
            InterceptedTunnelRegistry tunnels,
            Logger logger = null)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tunnels = tunnels ?? throw new ArgumentNullException(nameof(tunnels));
            _logger = logger;
        }

        internal async Task InvokeAsync(HttpContext context)
        {
            // Kestrel sees the loopback relay as its client. Its remote port is
            // the stable key back to XMAT's original device connection.
            if (!_tunnels.TryGet(context.Connection.RemotePort, out var tunnel))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            ClientRequest clientRequest = await AspNetCoreRequestAdapter.CreateAsync(
                context,
                _proxy.GetNextRequestID(),
                context.RequestAborted).ConfigureAwait(false);

            if (!_proxy.RaiseReceivedWebRequest(tunnel.ConnectionId, clientRequest))
            {
                context.Abort();
                return;
            }

            Uri uri = ProxyUriFactory.Create(clientRequest);
            if (context.WebSockets.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(context, uri, clientRequest).ConfigureAwait(false);
                return;
            }

            using HttpRequestMessage upstreamRequest =
                ProxyHttpRequestFactory.Create(clientRequest, uri);
            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await _httpClient.SendAsync(
                    upstreamRequest,
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Log(tunnel.ConnectionId, LogLevel.ERROR, $"Upstream HTTP request failed: {ex.Message}");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
            catch (TaskCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                Log(tunnel.ConnectionId, LogLevel.ERROR, $"Upstream HTTP request timed out: {ex.Message}");
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return;
            }

            using (upstreamResponse)
            {
                // Keep the full response mutable until scripts have run, matching
                // the behavior of the original HTTP/1 proxy implementation.
                ServerResponse serverResponse =
                    await CreateServerResponseAsync(clientRequest, upstreamResponse).ConfigureAwait(false);

                if (!_proxy.RaiseReceivedWebResponse(
                    tunnel.ConnectionId,
                    clientRequest,
                    serverResponse))
                {
                    context.Abort();
                    return;
                }

                await WriteResponseAsync(context, serverResponse).ConfigureAwait(false);
            }
        }

        private async Task ProxyWebSocketAsync(
            HttpContext context,
            Uri uri,
            ClientRequest clientRequest)
        {
            using var upstream = new ClientWebSocket();
            upstream.Options.Proxy = null;
            upstream.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

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

            await upstream.ConnectAsync(upstreamUri, context.RequestAborted).ConfigureAwait(false);
            using WebSocket downstream =
                await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol).ConfigureAwait(false);

            // WebSockets leave HTTP framing after the handshake, so relay messages
            // directly until either peer closes and then stop the opposite pump.
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted);

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
            _logger?.Log(connectionId, level, message);
        }

        private static async Task<ServerResponse> CreateServerResponseAsync(
            ClientRequest request,
            HttpResponseMessage response)
        {
            var result = new ServerResponse
            {
                RequestNumber = request.RequestNumber,
                StreamId = request.StreamId,
                Version = GetProtocol(response.Version),
                Status = ((int)response.StatusCode).ToString(),
                StatusDescription = response.ReasonPhrase,
                BodyBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
            };

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

        private static async Task WriteResponseAsync(
            HttpContext context,
            ServerResponse response)
        {
            context.Response.StatusCode = int.Parse(response.Status);

            CopyHeaders(response.Headers, context.Response);
            CopyHeaders(response.ContentHeaders, context.Response);

            context.Response.ContentLength = response.BodyBytes.Length;
            await context.Response.Body.WriteAsync(
                response.BodyBytes,
                context.RequestAborted).ConfigureAwait(false);
        }

        private static void CopyHeaders(
            HeaderCollection source,
            HttpResponse response)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in source)
            {
                if (ProxyHeaderUtilities.IsHopByHopHeader(header.Key))
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

            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
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

        public async ValueTask DisposeAsync()
        {
            if (_host == null)
                return;

            await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _host.Dispose();
            _host = null;
            Port = 0;
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

        internal static bool IsHopByHopHeader(string headerKey)
        {
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
