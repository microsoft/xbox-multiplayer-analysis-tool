// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class WebSocketProxy : IWebSocketProxy
    {
        public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened { add { } remove { } }
        public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage { add { } remove { } }
        public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed { add { } remove { } }

        private ClientWebSocket _serverWebSocket;
        private WebSocket _clientWebSocket;
        private Logger _logger;

        public async Task StartWebSocketProxy(Uri uri, Stream clientStream, ClientRequest clientRequest, Logger logger, CancellationToken ct)
        {
            _logger = logger;
            await SetupProxy(uri, clientStream, clientRequest, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Run bidirectional relay as parallel tasks instead of threads
            var clientTask = RelayClientToServerAsync(cts);
            var serverTask = RelayServerToClientAsync(cts);

            await Task.WhenAny(clientTask, serverTask).ConfigureAwait(false);
            cts.Cancel();
            await Task.WhenAll(clientTask, serverTask).ConfigureAwait(false);
        }

        private async Task SetupProxy(Uri uri, Stream clientStream, ClientRequest clientRequest, CancellationToken ct)
        {
            _serverWebSocket = new ClientWebSocket();

            HeaderCollection clientHeaders = GetNonWebSocketClientHeaders(clientRequest.Headers);

            for (int i = 0; i < clientHeaders.Count(); i++)
            {
                _serverWebSocket.Options.SetRequestHeader(
                    clientHeaders.ElementAt(i).Key,
                    clientHeaders[clientHeaders.ElementAt(i).Key]);
            }

            _serverWebSocket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            _serverWebSocket.Options.Proxy = null;
            _serverWebSocket.Options.CollectHttpResponseDetails = true;

            var wssUri = new UriBuilder("wss", uri.Host, -1, uri.AbsolutePath, uri.Query);
            await _serverWebSocket.ConnectAsync(wssUri.Uri, ct).ConfigureAwait(false);

            string key = clientRequest.Headers["Sec-WebSocket-Key"];
            if (string.IsNullOrEmpty(key))
                return;

            string respKey = CreateSecWebSocketAcceptKey(key);

            HeaderCollection serverHeaders = GetNonWebSocketServerHeaders(_serverWebSocket.HttpResponseHeaders);

            StringBuilder response = new StringBuilder();
            response.AppendLine("HTTP/1.1 101 Switching Protocols");
            response.AppendLine("Upgrade: websocket");
            response.AppendLine("Connection: Upgrade");
            response.AppendLine($"Sec-WebSocket-Accept: {respKey}");

            for (int i = 0; i < serverHeaders.Count(); i++)
            {
                response.AppendLine(serverHeaders.ElementAt(i).Key + ": " + serverHeaders[serverHeaders.ElementAt(i).Key]);
            }

            response.AppendLine($"Date: {DateTime.Now:R}");
            response.AppendLine("");

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(response.ToString()), ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);

            _clientWebSocket = WebSocket.CreateFromStream(clientStream, true, null, TimeSpan.FromSeconds(30));
        }

        private static string CreateSecWebSocketAcceptKey(string key)
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

        private async Task RelayServerToClientAsync(CancellationTokenSource cts)
        {
            byte[] buffer = new byte[8192];
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
                        return;
                    }

                    _logger.Log(0, LogLevel.DEBUG, $"WebSocket server→client: {result.Count} bytes");
                    await _clientWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException wse)
            {
                _logger.Log(0, LogLevel.ERROR, $"Server WebSocket exception: {wse}");
                cts.Cancel();
            }
        }

        private async Task RelayClientToServerAsync(CancellationTokenSource cts)
        {
            byte[] buffer = new byte[8192];
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
                        return;
                    }

                    _logger.Log(0, LogLevel.DEBUG, $"WebSocket client→server: {result.Count} bytes");
                    await _serverWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException wse)
            {
                _logger.Log(0, LogLevel.ERROR, $"Client WebSocket exception: {wse}");
                cts.Cancel();
            }
        }

        private HeaderCollection GetNonWebSocketClientHeaders(HeaderCollection allHeaders)
        {
            HeaderCollection headers = new HeaderCollection();

            for (int i = 0; i < allHeaders.Count(); i++)
            {
                var key = allHeaders.ElementAt(i).Key.ToLower();
                if (key != "host" && key != "upgrade" && key != "connection" &&
                    key != "sec-websocket-key" && key != "sec-websocket-version" &&
                    key != "origin" && key != "sec-websocket-protocol" &&
                    key != "sec-websocket-extensions")
                {
                    headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key];
                }
            }

            return headers;
        }

        private HeaderCollection GetNonWebSocketServerHeaders(IReadOnlyDictionary<string, IEnumerable<string>> allHeaders)
        {
            HeaderCollection headers = new HeaderCollection();

            for (int i = 0; i < allHeaders.Count(); i++)
            {
                var key = allHeaders.ElementAt(i).Key.ToLower();
                if (key != "upgrade" && key != "connection" && key != "sec-websocket-accept")
                {
                    headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key].First();
                }
            }

            return headers;
        }
    }
}
