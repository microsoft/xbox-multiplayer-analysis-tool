// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class WebSocketProxy : IWebSocketProxy
    {
        // TODO: use eventing for driving UI
        public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened { add { } remove { } }
        public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage { add { } remove { } }
        public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed { add { } remove { } }

        private ClientWebSocket _serverWebSocket;
        private WebSocket _clientWebSocket;

        private Thread _clientThread;
        private Thread _serverThread;
        private CancellationTokenSource _tokenSource;
        private WebSocketCloseStatus _closeStatus;
        private string _closeDescription;

        private Logger _logger;



        public async Task StartWebSocketProxy(Uri uri, ClientState clientState, ClientRequest clientRequest, Logger logger)
        {
            _logger = logger;
            await SetupProxy(uri, clientState, clientRequest);

            _tokenSource = new CancellationTokenSource();
            _clientThread = new Thread(ClientThread)
            {
                IsBackground = true,
                Name = $"{uri.Host}-Client"
            };
            _clientThread.Start();

            _serverThread = new Thread(ServerThread)
            {
                IsBackground = true,
                Name = $"{uri.Host}-Server"
            };
            _serverThread.Start();
        }

        private async Task SetupProxy(Uri uri, ClientState clientState, ClientRequest clientRequest)
        {
            _serverWebSocket = new ClientWebSocket();

            // Get all non-Websocket headers to pass on
            HeaderCollection clientHeaders = GetNonWebSocketClientHeaders(clientRequest.Headers);

            // Forward all headers that are not websocket-related
            for (int i = 0; i < clientHeaders.Count(); i++)
            {
                _serverWebSocket.Options.SetRequestHeader(clientHeaders.ElementAt(i).Key, clientHeaders[clientHeaders.ElementAt(i).Key]);
            }

            // accept all connections, don't use the proxy
            _serverWebSocket.Options.RemoteCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });
            _serverWebSocket.Options.Proxy = null;
            _serverWebSocket.Options.CollectHttpResponseDetails = true;

            // WebSocket class requires ws/wss scheme, so try to build a URI replacing just the scheme
            var wssUri = new UriBuilder("wss", uri.Host, -1, uri.AbsolutePath, uri.Query);

            // connect to the real server with our own websocket
            await _serverWebSocket.ConnectAsync(wssUri.Uri, CancellationToken.None);

            // if we succeed, create the proper accept key and pass it back to the client
            string key = clientRequest.Headers["Sec-WebSocket-Key"];
            if(string.IsNullOrEmpty(key))
            {
                return;
            }

            string respKey = CreateSecWebSocketAcceptKey(key);

            // get headers from server to pass on
            HeaderCollection fullServerHeaders = new HeaderCollection();

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

            await clientState.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response.ToString()));

            // now, create a WebSocket around the original incoming HTTP(S) stream and act as the server
            _clientWebSocket = WebSocket.CreateFromStream(clientState.GetStream(), true, null, TimeSpan.FromSeconds(1));
        }

        private string CreateSecWebSocketAcceptKey(string key)
        {
            if(string.IsNullOrEmpty(key))
                return null;

            // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Sec-WebSocket-Accept
            // The server takes the value of the Sec-WebSocket-Key sent in the handshake request,
            // appends 258EAFA5-E914-47DA-95CA-C5AB0DC85B11,
            // takes SHA-1 of the new value,
            // and is then base64 encoded.

            key += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] buffOut = new byte[1024];
            if(SHA1.TryHashData(Encoding.UTF8.GetBytes(key), buffOut, out int written))
            {
                return Convert.ToBase64String(buffOut, 0, written);
            }

            return null;
        }

        public void StopWebSocketProxy()
        {
            if(_tokenSource != null)
            {
                _tokenSource.Cancel();
            }
        }

        private async void ServerThread(object obj)
        {
            byte[] buffer = new byte[8192];
            while(!_tokenSource.IsCancellationRequested)
            {
                try
                {
                    // read messages from the server, if we get a close, tell the client we are done and gracefully dump out of the loop
                    WebSocketReceiveResult result = await _serverWebSocket.ReceiveAsync(buffer, _tokenSource.Token);
                    if(result.MessageType == WebSocketMessageType.Close)
                    {
                        _closeStatus = result.CloseStatus.GetValueOrDefault();
                        _closeDescription = result.CloseStatusDescription;
                        await _clientWebSocket.CloseOutputAsync(_closeStatus, _closeDescription, _tokenSource.Token);
                        return;
                    }
                    else
                    {
                        string mes = new ArraySegment<byte>(buffer, 0, result.Count).ConvertToString();

                        _logger.Log(0, LogLevel.DEBUG, $"Websocket server message: {mes}");

                        await _clientWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, _tokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Log(0, LogLevel.DEBUG, "Server websocket thread exited due to token cancelation.");
                }
                catch (WebSocketException wse)
                {
                    _logger.Log(0, LogLevel.ERROR, $"Unexpected server websocket exception: {wse}");
                    _tokenSource.Cancel();
                }
            }
        }

        private async void ClientThread(object obj)
        {
            byte[] buffer = new byte[8192];
            while(!_tokenSource.IsCancellationRequested)
            {
                try
                {
                    WebSocketReceiveResult result = await _clientWebSocket.ReceiveAsync(buffer, _tokenSource.Token);
                    if(result.MessageType == WebSocketMessageType.Close)
                    {
                        _closeStatus = result.CloseStatus.GetValueOrDefault();
                        _closeDescription = result.CloseStatusDescription;
                        await _serverWebSocket.CloseOutputAsync(_closeStatus, _closeDescription, _tokenSource.Token);
                        return;
                    }
                    else
                    {
                        string mes = new ArraySegment<byte>(buffer, 0, result.Count).ConvertToString();

                        _logger.Log(0, LogLevel.DEBUG, $"Websocket server message: {mes}");

                        await _serverWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, _tokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Log(0, LogLevel.DEBUG, "Client websocket thread exited due to token cancelation.");
                }
                catch (WebSocketException wse)
                {
                    _logger.Log(0, LogLevel.ERROR, $"Unexpected client websocket exception: {wse}");
                    _tokenSource.Cancel();
                }
            }
        }
        private HeaderCollection GetNonWebSocketClientHeaders(HeaderCollection allHeaders)
        {
            HeaderCollection _headers = new HeaderCollection();

            // Get all non-Websocket related headers
            for (int i = 0; i < allHeaders.Count(); i++)
            {
                if (allHeaders.ElementAt(i).Key.ToLower() != "host" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "upgrade" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "connection" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "sec-websocket-key" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "sec-websocket-version" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "origin" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "sec-websocket-protocol" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "sec-websocket-extensions")
                {
                    _headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key];
                }
            }

            _logger.Log(0, LogLevel.DEBUG, $"Client WebSocket Headers to pass on: \n{_headers.ToString()}");

            return _headers;
        }
        private HeaderCollection GetNonWebSocketServerHeaders(IReadOnlyDictionary<String, IEnumerable<String>> allHeaders)
        {
            HeaderCollection _headers = new HeaderCollection();

            // Get all non-Websocket related headers
            for (int i = 0; i < allHeaders.Count(); i++)
            {
                if (allHeaders.ElementAt(i).Key.ToLower() != "upgrade" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "connection" &&
                    allHeaders.ElementAt(i).Key.ToLower() != "sec-websocket-accept")
                {
                    _headers[allHeaders.ElementAt(i).Key] = allHeaders[allHeaders.ElementAt(i).Key].First();
                }
            }

            _logger.Log(0, LogLevel.DEBUG, $"Server WebSocket Headers to pass on: \n{_headers.ToString()}");

            return _headers;
        }
    }
}
