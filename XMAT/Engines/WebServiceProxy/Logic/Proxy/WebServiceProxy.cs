// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    public struct WebServiceProxyOptions
    {
        public int Port { get; set; }
        public string LogFilePath { get; set; }
        public LogLevel CurrentLogLevel { get; set; }
    }

    internal class WebServiceProxy : IWebServiceProxy
    {
        public event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
        public event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
        public event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
        public event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
        public event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
        public event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
        public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
        public event EventHandler ProxyStopped;

        public bool IsProxyEnabled { get { return _listenThread != null; } }

        private const int ServerLoggingId = 0;

        private readonly Logger _logger = new();
        private static bool _isInitialized = false;
        private static readonly CertificateManager _certManager = new(false);
        private CancellationTokenSource _cancellationToken = null;
        private int _port = -1;
        private readonly AutoResetEvent _exitEvent = new(false);
        private readonly HttpClient _httpClient = new(new HttpClientHandler() { UseProxy = false, Proxy = null, AllowAutoRedirect = false });
        private Thread _listenThread;

        // these need to be thread-safe
        private int _availableConnectionId;
        private int _availableRequestId;

        internal static bool Initialize(string certPath)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException("Only call Initialize once.");
            }

            if (_certManager.Initialize())
            {
                _certManager.ExportRootCertificate(certPath);
                _isInitialized = true;
            }

            return _isInitialized;
        }

        public void Reset()
        {
            _availableConnectionId = 999;
            _availableRequestId = 0;
        }

        internal static WebServiceProxy CreateProxy()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Please call Initialize first.");
            }

            // create a new instance and return it
            return new WebServiceProxy();
        }

        public void StartProxy(WebServiceProxyOptions options)
        {
            Reset();

            _logger.InitLog(options.LogFilePath, options.CurrentLogLevel);
            _port = options.Port;
            _cancellationToken = new CancellationTokenSource();

            _logger.Log(ServerLoggingId, LogLevel.INFO, $"Proxy listening on port {_port}");
            _listenThread = new Thread(ListenThread);
            _listenThread.Name = $"ListenerThread:{_port}";
            _listenThread.Start();
        }

        public void StopProxy()
        {
            if(_listenThread == null)
                return;

            _cancellationToken?.Cancel();
            if(!_exitEvent.WaitOne(5000))
            {
                _logger.Log(ServerLoggingId, LogLevel.ERROR, "Exit event didn't arrive, exiting anyway...");
            }
            ProxyStopped?.Invoke(this, EventArgs.Empty);
            _logger.CloseLog();
            _listenThread = null;
        }

        private async void ListenThread(object obj)
        {
            // Create a TCP/IP (IPv4) socket and listen for incoming connections.
            TcpListener listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();

            // if token cancels, call Stop on our listener
            _cancellationToken.Token.Register(() => listener.Stop());

            while(!_cancellationToken.IsCancellationRequested)
            {
                _logger.Log(ServerLoggingId, LogLevel.INFO, "Waiting for a client to connect...");
                TcpClient client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (SocketException sockEx)
                {
                    _logger.Log(ServerLoggingId, LogLevel.WARN, $"Listener exception, probably cancellation token: {sockEx}");
                }

                if(client != null)
                {
                    var _ = Task.Run(async () => 
                    {
                        try
                        {
                            await ProcessClient(client).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log(ServerLoggingId, LogLevel.ERROR, $"Exception in ProcessClient: {ex}");
                        }
                    }, _cancellationToken.Token);
                }
            }

            _logger.Log(ServerLoggingId, LogLevel.INFO, "Stopping listener...");
            listener.Stop();
            _exitEvent.Set();
        }

        private async Task ProcessClient(TcpClient tcpClient)
        {
            Socket clientSocket = tcpClient.Client;
            IPEndPoint client_ep = (IPEndPoint)clientSocket.RemoteEndPoint;
            string remoteAddress = client_ep.Address.ToString();
            string remotePort = client_ep.Port.ToString();

            var connectionID = GetNextConnectionID();
            if (!RaiseReceivedInitialConnection(connectionID, tcpClient))
            {
                _logger.Log(ServerLoggingId, LogLevel.WARN, $"REJECTING connection from {remoteAddress}:{remotePort}");
                return;
            }

            var clientState = new ClientState(connectionID, tcpClient);
            _logger.Log(clientState.ID, LogLevel.INFO, $"Accepting connection from {remoteAddress}:{remotePort}");

            ClientRequest request = await ReadRequestAsync(clientState).ConfigureAwait(false);
            _logger.Log(clientState.ID, LogLevel.DEBUG, $"Received request from client:\n{request}");

            if(request == null)
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, "Failed reading initial request");
                CloseClientState(clientState);
                return;
            }

            if (request.Method == "CONNECT")
            {
                request.Scheme = "http";

                // we are an HTTPs request, process the CONNECT method
                bool success = await ProcessConnectRequest(tcpClient, clientState, request).ConfigureAwait(false);
                if(success)
                {
                    //...and read the actual request that comes next
                    request = await ReadRequestAsync(clientState).ConfigureAwait(false);
                    if(request == null)
                    {
                        _logger.Log(clientState.ID, LogLevel.ERROR, "Failed reading request");
                        CloseClientState(clientState);
                        return;
                    }
                    request.Scheme = "https";
                }
                else
                {
                    _logger.Log(clientState.ID, LogLevel.ERROR, "Failed reading CONNECT request");
                    CloseClientState(clientState);
                    return;
                }
            }
            else
            {
                request.Scheme = "http";
            }

            // in either case, we now have a standard HTTP request, forward it appropriately
            if (!await ForwardRequestAsync(clientState, request).ConfigureAwait(false))
            {
                _logger.Log(clientState.ID, LogLevel.FATAL, "Forwarding the request failed");
                CloseClientState(clientState);
                return;
            }
        }

        private async Task<bool> ProcessConnectRequest(TcpClient tcpClient, ClientState clientState, ClientRequest connectRequest)
        {
            _logger.Log(clientState.ID, LogLevel.DEBUG, "Processing CONNECT request");

            if (!RaiseReceivedSslConnectionRequest(clientState, connectRequest))
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, "REJECTING SSL connection request");
                tcpClient.Close();
                return false;
            }

            clientState.RequestHistory.Add(connectRequest);

            var serverResponse = new ServerResponse
            {
                RequestNumber = connectRequest.RequestNumber,
                Version = connectRequest.Version,
                Status = "200",
                StatusDescription = "Connection Established"
            };

            serverResponse.Headers["FiddlerGateway"] = "Direct";
            serverResponse.Headers["StartTime"] = DateTime.Now.ToString("HH:mm:ss.fff");
            serverResponse.Headers["Connection"] = "close";

            if (!RaiseReceivedWebResponse(clientState.ID, connectRequest, serverResponse))
            {
                _logger.Log(clientState.ID, LogLevel.WARN, "Server response was aborted.");
                return false;
            }
            clientState.ResponseHistory.Add(serverResponse);

            _logger.Log(clientState.ID, LogLevel.DEBUG, $"Sending verify to the client:\n{serverResponse}");

            byte[] resp = serverResponse.ToByteArray();
            tcpClient.Client.Send(resp);

            // A client has connected. Create the
            // SslStream using the client's network stream.
            clientState.SslStream = new SslStream(tcpClient.GetStream(), false);

            // Authenticate the server but don't require the client to authenticate.
            _logger.Log(clientState.ID, LogLevel.DEBUG, "Authenticating as server...");

            var certificate = _certManager.GetCertificateForHost(connectRequest.Path);
            try
            {
                await clientState.SslStream.AuthenticateAsServerAsync(
                    certificate,
                    false,
                    SslProtocols.None,
                    true).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.Log(clientState.ID, LogLevel.FATAL, $"Authentication failed - closing the connection:\n{e}");
                RaiseFailedSslConnectionRequest(clientState, e);
                CloseClientState(clientState);
                return false;
            }

            // Log the properties and settings for the authenticated stream.
            LogSecurityLevel(clientState);
            LogSecurityServices(clientState);
            LogCertificateInformation(clientState);
            LogStreamProperties(clientState);

            RaiseCompletedSslConnectionRequest(clientState, connectRequest);

            // Set timeouts for the read and write to 1 second.
            clientState.TcpClient.ReceiveTimeout = 1000;
            clientState.TcpClient.SendTimeout = 1000;
            clientState.SslStream.ReadTimeout = 1000;
            clientState.SslStream.WriteTimeout = 1000;

            return true;
        }

        private async Task<ClientRequest> ReadRequestAsync(ClientState clientState)
        {
            _logger.Log(clientState.ID, LogLevel.DEBUG, "Reading client request...");

            var lines = await ReadRequestAndHeadersAsync(clientState, clientState.GetStream()).ConfigureAwait(false);
            if(lines == null)
                return null;

            var clientRequest = ParseRequestAndHeaders(clientState.ID, lines);

            // if we're a CONNECT, bail out, there's nothing more to read...
            if(clientRequest.Method == "CONNECT")
                return clientRequest;

            int contentLength = -1;

            // use our "content-length" header, if we have one, to determine exactly how much to read
            string length = clientRequest.ContentHeaders["content-length"];
            if(int.TryParse(length, out int convertedLength))
            {
                contentLength = convertedLength;
                _logger.Log(clientState.ID, LogLevel.DEBUG, $"Content-Length: {contentLength}");
            }

            // TODO: Is it valid to just ignore the body on a GET request that's a websocket upgrade request?

            using(var ms = new MemoryStream())
            {
                // Read the message sent by the client until we can read no more
                // bytes within the timeout.
                var buffer = new byte[8192];
                int bytes = 0;
                int totalBytes = 0;

                do
                {
                    try
                    {
                        using var timeoutToken = new CancellationTokenSource();
                        using var readToken = new CancellationTokenSource();

                        var readTask = clientState.GetStream().ReadAsync(buffer, 0, buffer.Length, readToken.Token);
                        var timeoutTask = Task.Delay(1000, timeoutToken.Token);
                        var completedTask = await Task.WhenAny(readTask, timeoutTask );
                        if (completedTask == timeoutTask)
                        {
                            // cancel the pending read and bail out
                            readToken.Cancel();
                            return clientRequest;
                        }

                        // cancel the timeout task and continue
                        timeoutToken.Cancel();

                        bytes = await readTask;
                        if (bytes > 0)
                        {
                            await ms.WriteAsync(buffer, 0, bytes).ConfigureAwait(false);
                            totalBytes += bytes;

                            if (totalBytes == contentLength)
                            {
                                _logger.Log(clientState.ID, LogLevel.DEBUG, $"Read total Content-Length: {contentLength}/{totalBytes}");
                                break;
                            }
                        }
                        else
                        {
                            _logger.Log(clientState.ID, LogLevel.DEBUG, $"Read timed out");
                        }
                    }
                    catch (Exception ex)
                    {
                        bytes = 0;
                        _logger.Log(clientState.ID, LogLevel.ERROR, $"EXCEPTION from stream... [{ex}]");
                    }

                    _logger.Log(clientState.ID, LogLevel.DEBUG, $"READ {bytes}/{totalBytes}/{contentLength} bytes from stream");
                }
                while (bytes > 0);

                clientRequest.BodyBytes = ms.ToArray();
                return clientRequest;
            }
        }

        private async Task<bool> ForwardRequestAsync(ClientState clientState, ClientRequest clientRequest)
        {
            _logger.Log(clientState.ID, LogLevel.DEBUG, $"Forwarding client request:\n{clientRequest}");

            // if for whatever reason we should not pipe the request through
            // the proxy, then we will just swallow it
            if (!RaiseReceivedWebRequest(clientState.ID, clientRequest))
            {
                _logger.Log(clientState.ID, LogLevel.WARN, "Client request was aborted.");
                return false;
            }

            // if the client specified an absolute URI in the request line, use it directly
            // otherwise we assume it's relative and try to create a proper Uri out of it
            if(!Uri.TryCreate(clientRequest.Path, UriKind.Absolute, out Uri uri))
            {
                string path = clientRequest.Path;
                string query = string.Empty;

                // split out the query string or the fragment, if it exists
                if(clientRequest.Path.Contains('?'))
                {
                    var pathSplit = path.Split('?');
                    path = pathSplit[0];
                    if(pathSplit.Length > 1)
                        query = "?" + pathSplit[1];
                }
                else if(clientRequest.Path.Contains('#'))
                {
                    var pathSplit = path.Split('#');
                    path = pathSplit[0];
                    if(pathSplit.Length > 1)
                        query = "#" + pathSplit[1];
                }

                // if the port is -1, the default port is used by UriBuilder
                var ub = new UriBuilder(clientRequest.Scheme, clientRequest.Host, clientRequest.Port, path, query);
                uri = ub.Uri;
            }

            string websocketUpgrade = clientRequest.Headers["Upgrade"];
            if(!string.IsNullOrEmpty(websocketUpgrade))
            {
                await HandleWebSocketRequest(uri, clientState, clientRequest);
                return true;
            }
            else
            {
                await HandleWebRequest(uri, clientState, clientRequest);
                // TODO: we ignore keep-alive and just terminate the connection
                CloseClientState(clientState);
                return true;
            }
        }

        private async Task HandleWebSocketRequest(Uri uri, ClientState clientState, ClientRequest clientRequest)
        {
            // TODO: this needs to be fleshed out into something bigger,
            // but since it only partially works and needs a native implementation anyway,
            // I'm leaving it as-is for now.

            // this will spawn read/write threads that will run until the websocket disconnects,
            // after which the threads will terminate and all will be cleaned up
            var wspc = new WebSocketProxy();
            await wspc.StartWebSocketProxy(uri, clientState, clientRequest);
        }

        private async Task<bool> HandleWebRequest(Uri uri, ClientState clientState, ClientRequest clientRequest)
        {
            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(clientRequest.Method),
                RequestUri = uri
            };

            // build up the body
            requestMessage.Content = new ByteArrayContent(clientRequest.BodyBytes ?? Array.Empty<byte>());

            // build up the headers
            requestMessage.Headers.Clear();
            requestMessage.Content.Headers.Clear();

            clientRequest.Headers.CopyTo(requestMessage.Headers);
            clientRequest.ContentHeaders.CopyTo(requestMessage.Content.Headers);

            clientState.RequestHistory.Add(clientRequest);

            HttpResponseMessage result;
            try
            {
                result = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);
                _logger.Log(clientState.ID, LogLevel.DEBUG, $"REQUEST SENT:\n{requestMessage}");
            }
            catch (Exception ex)
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, $"Failed sending proxied request: {ex}");
                return false;
            }

            if (!await ReturnResponseAsync(clientRequest, clientState, result).ConfigureAwait(false))
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, "Failed sending response to client.");
                return false;
            }

            return true;
        }

        private ClientRequest ParseRequestAndHeaders(int clientId, List<string> lines)
        {
            if(lines == null || lines.Count == 0)
            {
                _logger.Log(clientId, LogLevel.ERROR, "Connect request has no data.");
                return null;
            }

            string[] firstLine = lines[0].Split(' ');
            if(firstLine.Length < 3)
            {
                _logger.Log(clientId, LogLevel.ERROR, $"Invalid method: {lines[0]}");
                return null;
            }

            var clientRequest = new ClientRequest
            {
                Method  = firstLine[0],
                Path    = firstLine[1],
                Version = firstLine[2],
                RequestNumber = GetNextRequestID()
            };

            for(int i = 1; i < lines.Count; i++)
            {
                string line = lines[i];
                string[] header = line.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if(header.Length < 2)
                {
                    _logger.Log(clientId, LogLevel.ERROR, $"Malformed header: {line}");
                }
                else
                {
                    string name = header[0].Trim();
                    string value = header[1].Trim();

                    if(IsContentHeader(name))
                        clientRequest.ContentHeaders[name] = value;
                    else
                        clientRequest.Headers[name] = value;
                }
            }

            string hostFull = clientRequest.Headers["Host"];
            if(!string.IsNullOrEmpty(hostFull))
            {
                string[] hostSplit = hostFull.Split(':');
                clientRequest.Host = hostSplit[0].Trim();
                if(hostSplit.Length > 1)
                {
                    clientRequest.Port = int.Parse(hostSplit[1]);
                }
                else
                {
                    clientRequest.Port = -1;
                }
            }
            return clientRequest;
        }

        private async Task<bool> ReturnResponseAsync(ClientRequest clientRequest, ClientState clientState, HttpResponseMessage responseMessage)
        {
            ServerResponse serverResponse = await ParseServerResponseAsync(clientState.ID, responseMessage).ConfigureAwait(false);
            if (serverResponse == null)
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, "Could not parse server response");
                return false;
            }

            serverResponse.RequestNumber = clientRequest.RequestNumber;

            // if for whatever reason we should not pipe the response through
            // the proxy, then we will just swallow it
            if (!RaiseReceivedWebResponse(clientState.ID, clientRequest, serverResponse))
            {
                _logger.Log(clientState.ID, LogLevel.WARN, "Server response was aborted.");
                return false;
            }
            clientState.ResponseHistory.Add(serverResponse);

            if (!await WriteResponseAsync(clientState, serverResponse).ConfigureAwait(false))
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, "Unable to write message to client stream, closing.");
                return false;
            }

            _logger.Log(clientState.ID, LogLevel.DEBUG, $"Response sent to client:\n{serverResponse}");
            return true;
        }

        private async Task<ServerResponse> ParseServerResponseAsync(int clientId, HttpResponseMessage response)
        {
            if (response == null)
            {
                _logger.Log(clientId, LogLevel.ERROR, "Server Response is a null object in ParseServerResponseAsync");
                return null;
            }

            var serverResponse = new ServerResponse
            {
                Status = ((int)response.StatusCode).ToString(),
                StatusDescription = response.ReasonPhrase,
                Version = "HTTP/" + response.Version.ToString(),
            };

            // because we currently use HttpClient to get the real data, we will never have a chunked response
            // even if the real response from the server was.  So, disable that header.
            response.Headers.TransferEncodingChunked = false;

            serverResponse.Headers.CopyFrom(response.Headers);
            serverResponse.ContentHeaders.CopyFrom(response.Content.Headers);

            // TODO: enforce the fact that we don't handle keep-alives
            serverResponse.Headers["Connection"] = "close";
            serverResponse.BodyBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return serverResponse;
        }

        private async Task<bool> WriteResponseAsync(ClientState clientState, ServerResponse response)
        {
            try
            {
                await clientState.GetStream().WriteAsync(response.ToByteArray()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log(clientState.ID, LogLevel.ERROR, $"EXCEPTION writing bytes to stream... [{ex}]");
                return false;
            }

            _logger.Log(clientState.ID, LogLevel.DEBUG, $"WROTE response to stream");
            return true;
        }

        private async Task<List<string>> ReadRequestAndHeadersAsync(ClientState clientState, Stream stream)
        {
            StreamReader sr = new StreamReader(stream);
            List<string> lines = new List<string>();
            string line;

            do
            {
                line = await sr.ReadLineAsync();

                if(line != null)
                {
                    if (line != string.Empty)
                        lines.Add(line);
                }
                else
                {
                    _logger.Log(clientState.ID, LogLevel.DEBUG, "Read headers timed out");
                    return null;
                }
            }
            while (line != string.Empty); // read until \r\n\r\n

            return lines;
        }

        private void CloseClientState(ClientState clientState)
        {
            if (clientState.SslStream != null)
            {
                clientState.SslStream.Close();
                clientState.SslStream = null;
            }

            if (clientState.TcpClient != null)
            {
                clientState.TcpClient.Close();
                clientState.TcpClient = null;
            }

            ConnectionClosed?.Invoke(this,
                new ConnectionClosedEventArgs
                {
                    ConnectionID =  clientState.ID,
                    Timestamp = DateTime.Now
                }
            );
        }

        private bool RaiseReceivedInitialConnection(int connectionID, TcpClient client)
        {
            var requestEvent = new InitialConnectionEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                TcpClient = client,
                AcceptConnection = true
            };

            ReceivedInitialConnection?.Invoke(this, requestEvent);
            return requestEvent.AcceptConnection;
        }

        private bool RaiseReceivedSslConnectionRequest(ClientState clientState, ClientRequest clientRequest)
        {
            string clientIP = (clientState.TcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

            var requestEvent = new SslConnectionRequestEventArgs
            {
                ConnectionID = clientState.ID,
                Timestamp = DateTime.Now,
                ClientIP = clientIP,
                Request = clientRequest,
                AcceptConnection = true
            };

            ReceivedSslConnectionRequest?.Invoke(this, requestEvent);
            return requestEvent.AcceptConnection;
        }

        private void RaiseCompletedSslConnectionRequest(ClientState clientState, ClientRequest clientRequest)
        {
            CompletedSslConnectionRequest?.Invoke(this,
                new SslConnectionCompletionEventArgs
                {
                    ConnectionID = clientState.ID,
                    Timestamp = DateTime.Now,
                    Request = clientRequest
                }
            );
        }

        private bool RaiseReceivedWebRequest(int connectionID, ClientRequest clientRequest)
        {
            var requestEvent = new HttpRequestEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                Request = clientRequest,
                AcceptRequest = true
            };

            ReceivedWebRequest?.Invoke(this, requestEvent);
            return requestEvent.AcceptRequest;
        }

        private bool RaiseReceivedWebResponse(int connectionID, ClientRequest clientRequest, ServerResponse serverResponse)
        {
            var responseEvent = new HttpResponseEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                Request = clientRequest,
                Response = serverResponse,
                SendResponse = true
            };

            ReceivedWebResponse?.Invoke(this, responseEvent);
            return responseEvent.SendResponse;
        }

        private void RaiseFailedSslConnectionRequest(ClientState clientState, Exception ex)
        {
            FailedSslConnectionRequest?.Invoke(this,
                new ConnectionFailureEventArgs
                {
                    ConnectionID = clientState.ID,
                    Timestamp = DateTime.Now,
                    Exception = ex
                }
            );
        }

        private static bool IsRequestHeader(string headerKey)
        {
            var lowercase = headerKey.ToLower();
            var isRequestHeader = false;
            isRequestHeader |= lowercase.Equals("accept");
            isRequestHeader |= lowercase.Equals("accept-charset");
            isRequestHeader |= lowercase.Equals("accept-encoding");
            isRequestHeader |= lowercase.Equals("accept-language");
            isRequestHeader |= lowercase.Equals("authorization");
            isRequestHeader |= lowercase.Equals("cache-control");
            isRequestHeader |= lowercase.Equals("connection");
            isRequestHeader |= lowercase.Equals("date");
            isRequestHeader |= lowercase.Equals("expect");
            isRequestHeader |= lowercase.Equals("from");
            isRequestHeader |= lowercase.Equals("host");
            isRequestHeader |= lowercase.Equals("if-match");
            isRequestHeader |= lowercase.Equals("if-modified-since");
            isRequestHeader |= lowercase.Equals("if-none-match");
            isRequestHeader |= lowercase.Equals("if-range");
            isRequestHeader |= lowercase.Equals("if-unmodified-since");
            isRequestHeader |= lowercase.Equals("max-forwards");
            isRequestHeader |= lowercase.Equals("proxy-authorization");
            isRequestHeader |= lowercase.Equals("range");
            isRequestHeader |= lowercase.Equals("referrer");
            isRequestHeader |= lowercase.Equals("te");
            isRequestHeader |= lowercase.Equals("trailer");
            isRequestHeader |= lowercase.Equals("transfer-encoding");
            isRequestHeader |= lowercase.Equals("upgrade");
            isRequestHeader |= lowercase.Equals("user-agent");
            isRequestHeader |= lowercase.Equals("via");
            isRequestHeader |= lowercase.Equals("warning");
            return isRequestHeader;
        }

        private static bool IsContentHeader(string headerKey)
        {
            var lowercase = headerKey.ToLower();
            var isContentHeader = false;
            isContentHeader |= lowercase.Equals("allow");
            isContentHeader |= lowercase.Equals("content-disposition");
            isContentHeader |= lowercase.Equals("content-encoding");
            isContentHeader |= lowercase.Equals("content-language");
            isContentHeader |= lowercase.Equals("content-length");
            isContentHeader |= lowercase.Equals("content-location");
            isContentHeader |= lowercase.Equals("content-md5");
            isContentHeader |= lowercase.Equals("content-range");
            isContentHeader |= lowercase.Equals("content-type");
            isContentHeader |= lowercase.Equals("expires");
            isContentHeader |= lowercase.Equals("last-modified");
            return isContentHeader;
        }

        private static bool IsResponseHeader(string headerKey)
        {
            var lowercase = headerKey.ToLower();
            var isResponseHeader = false;
            isResponseHeader |= lowercase.Equals("accept-ranges");
            isResponseHeader |= lowercase.Equals("age");
            isResponseHeader |= lowercase.Equals("cache-control");
            isResponseHeader |= lowercase.Equals("connection");
            isResponseHeader |= lowercase.Equals("date");
            isResponseHeader |= lowercase.Equals("etag");
            isResponseHeader |= lowercase.Equals("location");
            isResponseHeader |= lowercase.Equals("pragma");
            isResponseHeader |= lowercase.Equals("proxy-authenticate");
            isResponseHeader |= lowercase.Equals("retry-after");
            isResponseHeader |= lowercase.Equals("server");
            isResponseHeader |= lowercase.Equals("trailer");
            isResponseHeader |= lowercase.Equals("transfer-encoding");
            isResponseHeader |= lowercase.Equals("upgrade");
            isResponseHeader |= lowercase.Equals("vary");
            isResponseHeader |= lowercase.Equals("via");
            isResponseHeader |= lowercase.Equals("warning");
            isResponseHeader |= lowercase.Equals("www-authenticate");
            return isResponseHeader;
        }

        private int GetNextConnectionID()
        {
            return Interlocked.Increment(ref _availableConnectionId);
        }

        private int GetNextRequestID()
        {
            return Interlocked.Increment(ref _availableRequestId);
        }


        private void LogSecurityLevel(ClientState clientState)
        {
            _logger.Log(clientState.ID, LogLevel.INFO, $"Cipher: {clientState.SslStream.CipherAlgorithm} strength {clientState.SslStream.CipherStrength}");
            _logger.Log(clientState.ID, LogLevel.INFO, $"Hash: {clientState.SslStream.HashAlgorithm} strength {clientState.SslStream.HashStrength}");
            _logger.Log(clientState.ID, LogLevel.INFO, $"Key exchange: {clientState.SslStream.KeyExchangeAlgorithm} strength {clientState.SslStream.KeyExchangeStrength}");
            _logger.Log(clientState.ID, LogLevel.INFO, $"Protocol: {clientState.SslStream.SslProtocol}");
        }

        private void LogSecurityServices(ClientState clientState)
        {
            _logger.Log(clientState.ID, LogLevel.INFO, $"Authenticated: {clientState.SslStream.IsAuthenticated}, Server: {clientState.SslStream.IsServer}");
            _logger.Log(clientState.ID, LogLevel.INFO, $"Signed: {clientState.SslStream.IsSigned}, Encrypted: {clientState.SslStream.IsEncrypted}");
        }

        private void LogStreamProperties(ClientState clientState)
        {
            _logger.Log(clientState.ID, LogLevel.INFO, $"Can read: {clientState.SslStream.CanRead}, Write {clientState.SslStream.CanWrite}, Timeout: {clientState.SslStream.CanTimeout}");
        }

        private void LogCertificateInformation(ClientState clientState)
        {
            _logger.Log(clientState.ID,  LogLevel.INFO, $"Certificate revocation list checked: {clientState.SslStream.CheckCertRevocationStatus}");

            X509Certificate localCertificate = clientState.SslStream.LocalCertificate;
            if (clientState.SslStream.LocalCertificate != null)
            {
                _logger.Log(clientState.ID,  LogLevel.INFO, $"Local cert issued to {localCertificate.Subject}, valid {localCertificate.GetEffectiveDateString()} to {localCertificate.GetExpirationDateString()}.");
            }
            else
            {
                _logger.Log(clientState.ID,  LogLevel.INFO, "Local certificate is null.");
            }

            // Display the properties of the client's certificate.
            X509Certificate remoteCertificate = clientState.SslStream.RemoteCertificate;
            if (clientState.SslStream.RemoteCertificate != null)
            {
                _logger.Log(clientState.ID,  LogLevel.INFO, $"Remote cert issued to {remoteCertificate.Subject}, valid {remoteCertificate.GetEffectiveDateString()} to {remoteCertificate.GetExpirationDateString()}.");
            }
            else
            {
                _logger.Log(clientState.ID,  LogLevel.INFO, "Remote certificate is null.");
            }
        }
    }
}
