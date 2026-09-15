// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class WebSocketProxy : IWebSocketProxy
    {
        // Limits only the payload copy published to the viewer; relayed messages remain unchanged.
        internal const int MaximumCapturedMessageBytes = 1024 * 1024;

        public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
        public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
        public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;

        private ClientWebSocket _serverWebSocket;
        private WebSocket _clientWebSocket;
        private Logger _logger;
        private int _connectionID;
        private int _requestNumber;

        public async Task StartWebSocketProxy(
            int connectionID,
            Uri uri,
            Stream clientStream,
            ClientRequest clientRequest,
            Logger logger,
            CancellationToken ct)
        {
            _connectionID = connectionID;
            _requestNumber = clientRequest.RequestNumber;
            _logger = logger;
            bool opened = false;

            try
            {
                if (!await SetupProxy(uri, clientStream, clientRequest, ct).ConfigureAwait(false))
                {
                    return;
                }

                opened = true;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var clientTask = RelayClientToServerAsync(cts);
                var serverTask = RelayServerToClientAsync(cts);
                Task<bool> firstCompleted = await Task.WhenAny(clientTask, serverTask).ConfigureAwait(false);
                bool gracefulClose = await firstCompleted.ConfigureAwait(false);
                Task<bool[]> bothRelays = Task.WhenAll(clientTask, serverTask);

                // Keep the opposite relay alive briefly so both peers can complete the close handshake.
                if (gracefulClose)
                {
                    Task closeTimeout = Task.Delay(TimeSpan.FromSeconds(5), ct);
                    if (await Task.WhenAny(bothRelays, closeTimeout).ConfigureAwait(false) != bothRelays)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }

                try
                {
                    await bothRelays.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                if (opened)
                {
                    WebSocketClosed?.Invoke(this, new WebSocketClosedEventArgs
                    {
                        Timestamp = DateTime.Now,
                        ConnectionID = _connectionID,
                        RequestNumber = _requestNumber
                    });
                }

                _clientWebSocket?.Dispose();
                _serverWebSocket?.Dispose();
            }
        }

        private async Task<bool> SetupProxy(Uri uri, Stream clientStream, ClientRequest clientRequest, CancellationToken ct)
        {
            if (!IsValidHandshake(clientRequest))
            {
                await WriteErrorResponseAsync(clientStream, "400 Bad Request", ct).ConfigureAwait(false);
                return false;
            }

            _serverWebSocket = new ClientWebSocket();

            HeaderCollection clientHeaders = GetNonWebSocketClientHeaders(clientRequest.Headers);

            for (int i = 0; i < clientHeaders.Count(); i++)
            {
                _serverWebSocket.Options.SetRequestHeader(
                    clientHeaders.ElementAt(i).Key,
                    clientHeaders[clientHeaders.ElementAt(i).Key]);
            }

            foreach (string subProtocol in GetRequestedSubprotocols(clientRequest))
            {
                _serverWebSocket.Options.AddSubProtocol(subProtocol);
            }

            _serverWebSocket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            _serverWebSocket.Options.Proxy = null;
            _serverWebSocket.Options.CollectHttpResponseDetails = true;

            try
            {
                await _serverWebSocket.ConnectAsync(CreateUpstreamUri(uri), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                _logger.Log(_connectionID, LogLevel.ERROR, $"Upstream WebSocket handshake failed: {ex.Message}");
                await WriteErrorResponseAsync(clientStream, "502 Bad Gateway", ct).ConfigureAwait(false);
                return false;
            }

            if (!PublishOpened(
                _connectionID,
                _requestNumber,
                CreateUpstreamUri(uri).ToString(),
                _serverWebSocket.SubProtocol))
            {
                await WriteErrorResponseAsync(clientStream, "403 Forbidden", ct).ConfigureAwait(false);
                return false;
            }

            string key = clientRequest.Headers["Sec-WebSocket-Key"];
            string respKey = CreateSecWebSocketAcceptKey(key);

            HeaderCollection serverHeaders = GetNonWebSocketServerHeaders(_serverWebSocket.HttpResponseHeaders);

            StringBuilder response = new StringBuilder();
            response.AppendLine("HTTP/1.1 101 Switching Protocols");
            response.AppendLine("Upgrade: websocket");
            response.AppendLine("Connection: Upgrade");
            response.AppendLine($"Sec-WebSocket-Accept: {respKey}");

            if (!string.IsNullOrEmpty(_serverWebSocket.SubProtocol))
            {
                response.AppendLine($"Sec-WebSocket-Protocol: {_serverWebSocket.SubProtocol}");
            }

            for (int i = 0; i < serverHeaders.Count(); i++)
            {
                response.AppendLine(serverHeaders.ElementAt(i).Key + ": " + serverHeaders[serverHeaders.ElementAt(i).Key]);
            }

            response.AppendLine($"Date: {DateTime.Now:R}");
            response.AppendLine("");

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(response.ToString()), ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);

            _clientWebSocket = WebSocket.CreateFromStream(
                clientStream,
                isServer: true,
                subProtocol: _serverWebSocket.SubProtocol,
                keepAliveInterval: TimeSpan.FromSeconds(30));

            return true;
        }

        internal static Uri CreateUpstreamUri(Uri uri)
        {
            string scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";

            return new UriBuilder(uri)
            {
                Scheme = scheme,
                Port = uri.IsDefaultPort ? -1 : uri.Port
            }.Uri;
        }

        internal static bool IsWebSocketUpgrade(ClientRequest request)
        {
            if (request == null)
            {
                return false;
            }

            bool hasUpgrade = HeaderContainsToken(request.Headers["Upgrade"], "websocket");
            bool hasConnectionUpgrade = HeaderContainsToken(request.Headers["Connection"], "upgrade");
            return hasUpgrade && hasConnectionUpgrade;
        }

        internal static bool IsValidHandshake(ClientRequest request)
        {
            if (!IsWebSocketUpgrade(request) ||
                !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.Version, "HTTP/1.1", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.Headers["Sec-WebSocket-Version"], "13", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                return Convert.FromBase64String(request.Headers["Sec-WebSocket-Key"]).Length == 16;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool HeaderContainsToken(string value, string expected)
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(token => token.Equals(expected, StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerable<string> GetRequestedSubprotocols(ClientRequest request)
        {
            return request.Headers["Sec-WebSocket-Protocol"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        internal static string CreateSecWebSocketAcceptKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            key += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] buffOut = new byte[1024];
            if (SHA1.TryHashData(Encoding.UTF8.GetBytes(key), buffOut, out int written))
            {
                return Convert.ToBase64String(buffOut, 0, written);
            }

            return null;
        }

        private async Task<bool> RelayServerToClientAsync(CancellationTokenSource cts)
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();
            bool messageTruncated = false;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _serverWebSocket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _clientWebSocket.CloseOutputAsync(
                            result.CloseStatus.GetValueOrDefault(),
                            result.CloseStatusDescription,
                            cts.Token).ConfigureAwait(false);
                        return true;
                    }

                    _logger.Log(_connectionID, LogLevel.DEBUG, $"WebSocket server→client: {result.Count} bytes");
                    await _clientWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, cts.Token).ConfigureAwait(false);

                    messageTruncated |= AppendCapturedPayload(
                        message,
                        buffer,
                        result.Count,
                        MaximumCapturedMessageBytes);
                    if (result.EndOfMessage)
                    {
                        PublishMessage(
                            _connectionID,
                            _requestNumber,
                            true,
                            result.MessageType,
                            message.ToArray(),
                            messageTruncated);
                        message.SetLength(0);
                        messageTruncated = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (WebSocketException wse)
            {
                _logger.Log(_connectionID, LogLevel.ERROR, $"Server WebSocket exception: {wse}");
                return false;
            }

            return false;
        }

        private async Task<bool> RelayClientToServerAsync(CancellationTokenSource cts)
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();
            bool messageTruncated = false;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _clientWebSocket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _serverWebSocket.CloseOutputAsync(
                            result.CloseStatus.GetValueOrDefault(),
                            result.CloseStatusDescription,
                            cts.Token).ConfigureAwait(false);
                        return true;
                    }

                    _logger.Log(_connectionID, LogLevel.DEBUG, $"WebSocket client→server: {result.Count} bytes");
                    await _serverWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, cts.Token).ConfigureAwait(false);

                    messageTruncated |= AppendCapturedPayload(
                        message,
                        buffer,
                        result.Count,
                        MaximumCapturedMessageBytes);
                    if (result.EndOfMessage)
                    {
                        PublishMessage(
                            _connectionID,
                            _requestNumber,
                            false,
                            result.MessageType,
                            message.ToArray(),
                            messageTruncated);
                        message.SetLength(0);
                        messageTruncated = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (WebSocketException wse)
            {
                _logger.Log(_connectionID, LogLevel.ERROR, $"Client WebSocket exception: {wse}");
                return false;
            }

            return false;
        }

        internal bool PublishOpened(
            int connectionID,
            int requestNumber,
            string remoteEndPoint,
            string subProtocol)
        {
            var args = new WebSocketOpenedEventArgs
            {
                Timestamp = DateTime.Now,
                ConnectionID = connectionID,
                RequestNumber = requestNumber,
                RemoteEndPoint = remoteEndPoint,
                SubProtocol = subProtocol,
                AcceptConnection = true
            };

            WebSocketOpened?.Invoke(this, args);
            return args.AcceptConnection;
        }

        internal void PublishMessage(
            int connectionID,
            int requestNumber,
            bool fromHost,
            WebSocketMessageType messageType,
            byte[] message,
            bool payloadTruncated = false)
        {
            WebSocketMessage?.Invoke(this, new WebSocketMessageEventArgs
            {
                Timestamp = DateTime.Now,
                ConnectionID = connectionID,
                RequestNumber = requestNumber,
                FromHost = fromHost,
                MessageType = messageType,
                Message = message,
                PayloadTruncated = payloadTruncated
            });
        }

        internal static bool AppendCapturedPayload(
            MemoryStream destination,
            byte[] source,
            int count,
            int maximumBytes)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(source);

            // Frames are already forwarded; retain only a bounded copy for the complete-message event.
            int remaining = Math.Max(0, maximumBytes - checked((int)destination.Length));
            int bytesToCapture = Math.Min(count, remaining);
            if (bytesToCapture > 0)
            {
                destination.Write(source, 0, bytesToCapture);
            }

            return bytesToCapture < count;
        }

        private static async Task WriteErrorResponseAsync(Stream clientStream, string status, CancellationToken ct)
        {
            byte[] response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            await clientStream.WriteAsync(response, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }

        private HeaderCollection GetNonWebSocketClientHeaders(HeaderCollection allHeaders)
        {
            HeaderCollection headers = new HeaderCollection();

            for (int i = 0; i < allHeaders.Count(); i++)
            {
                var key = allHeaders.ElementAt(i).Key.ToLower();
                if (key != "host" && key != "upgrade" && key != "connection" &&
                    key != "sec-websocket-key" && key != "sec-websocket-version" &&
                    key != "sec-websocket-protocol" &&
                    key != "sec-websocket-extensions")
                {
                    headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key];
                }
            }

            return headers;
        }

        internal static HeaderCollection GetNonWebSocketServerHeaders(IReadOnlyDictionary<string, IEnumerable<string>> allHeaders)
        {
            HeaderCollection headers = new HeaderCollection();

            for (int i = 0; i < allHeaders.Count(); i++)
            {
                var key = allHeaders.ElementAt(i).Key.ToLower();
                // The downstream socket does not enable extensions, so upstream negotiation cannot be forwarded.
                if (key != "upgrade" && key != "connection" &&
                    key != "sec-websocket-accept" &&
                    key != "sec-websocket-protocol" &&
                    key != "sec-websocket-extensions")
                {
                    headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key].First();
                }
            }

            return headers;
        }
    }
}
