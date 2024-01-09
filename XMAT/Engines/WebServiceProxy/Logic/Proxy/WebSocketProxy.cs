// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class WebSocketProxy

    {
        private ClientWebSocket _serverWebSocket;
        private WebSocket _clientWebSocket;

        private Thread _clientThread;
        private Thread _serverThread;
        private CancellationTokenSource _tokenSource;
        private WebSocketCloseStatus _closeStatus;
        private string _closeDescription;

        public async Task StartWebSocketProxy(Uri uri, ClientState clientState, ClientRequest clientRequest)
        {
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

            // TODO: we need to add all headers that aren't websocket-related
            var auth = clientRequest.Headers.GetHeaderValuesAsString("Authorization");
            if (!string.IsNullOrEmpty(auth))
            {
                _serverWebSocket.Options.SetRequestHeader("Authorization", auth);
            }

            // accept all connections, don't use the proxy
            _serverWebSocket.Options.RemoteCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });
            _serverWebSocket.Options.Proxy = null;

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

            // TODO: add headers from the real server connection to the headers here??
            string response =
$@"HTTP/1.1 101 Switching Protocols
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Accept: {respKey}
Date: {DateTime.Now:R}

";
            await clientState.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response));

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
                        await _clientWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, _tokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    PublicUtilities.AppLog(LogLevel.DEBUG, "Server websocket thread exited due to token cancelation.");
                }
                catch (WebSocketException wse)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Unexpected server websocket exception: {wse}");
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
                        await _serverWebSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, _tokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    PublicUtilities.AppLog(LogLevel.DEBUG, "Client websocket thread exited due to token cancelation.");
                }
                catch (WebSocketException wse)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Unexpected client websocket exception: {wse}");
                    _tokenSource.Cancel();
                }
            }
        }
    }
}
