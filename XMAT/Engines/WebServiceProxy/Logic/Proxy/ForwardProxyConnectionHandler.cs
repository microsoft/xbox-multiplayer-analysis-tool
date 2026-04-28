// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Connections;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class ForwardProxyConnectionHandler : ConnectionHandler
    {
        private readonly CertificateManager _certManager;
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly WebServiceProxy _proxy;

        public ForwardProxyConnectionHandler(
            CertificateManager certManager,
            HttpClient httpClient,
            Logger logger,
            WebServiceProxy proxy)
        {
            _certManager = certManager;
            _httpClient = httpClient;
            _logger = logger;
            _proxy = proxy;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            var connectionID = _proxy.GetNextConnectionID();
            var remoteEndPoint = connection.RemoteEndPoint?.ToString() ?? "unknown";

            if (!_proxy.RaiseReceivedInitialConnection(connectionID, remoteEndPoint))
            {
                _logger.Log(connectionID, LogLevel.WARN, $"REJECTING connection from {remoteEndPoint}");
                return;
            }

            _logger.Log(connectionID, LogLevel.INFO, $"Accepting connection from {remoteEndPoint}");

            try
            {
                var stream = new DuplexPipeStream(connection.Transport);
                var request = await ReadHttpRequestAsync(connectionID, stream, connection.ConnectionClosed).ConfigureAwait(false);

                if (request == null)
                {
                    _logger.Log(connectionID, LogLevel.ERROR, "Failed reading initial request");
                    return;
                }

                if (request.Method == "CONNECT")
                {
                    await HandleConnectAsync(connectionID, stream, request, remoteEndPoint, connection.ConnectionClosed).ConfigureAwait(false);
                }
                else
                {
                    request.Scheme = "http";
                    await HandleHttpRequestAsync(connectionID, stream, request, connection.ConnectionClosed).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Log(connectionID, LogLevel.DEBUG, "Connection cancelled");
            }
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Exception in connection handler: {ex}");
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

            if (!_proxy.RaiseReceivedSslConnectionRequest(connectionID, clientIP, connectRequest))
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
            response.Headers["Connection"] = "close";

            if (!_proxy.RaiseReceivedWebResponse(connectionID, connectRequest, response))
            {
                _logger.Log(connectionID, LogLevel.WARN, "CONNECT response was aborted.");
                return;
            }

            byte[] responseBytes = response.ToByteArray();
            await clientStream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);

            // Perform TLS handshake with dynamic certificate
            var sslStream = new SslStream(clientStream, leaveInnerStreamOpen: true);
            var hostname = GetHostFromPath(connectRequest.Path);
            var certificate = _certManager.GetCertificateForHost(hostname);

            try
            {
                await sslStream.AuthenticateAsServerAsync(
                    certificate,
                    clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.None,
                    checkCertificateRevocation: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.FATAL, $"TLS authentication failed: {ex}");
                _proxy.RaiseFailedSslConnectionRequest(connectionID, ex);
                return;
            }

            _proxy.RaiseCompletedSslConnectionRequest(connectionID, connectRequest);
            _logger.Log(connectionID, LogLevel.INFO, $"TLS established: {sslStream.SslProtocol}, cipher suite: {sslStream.NegotiatedCipherSuite}");

            // Read the actual HTTP request from the TLS stream
            var innerRequest = await ReadHttpRequestAsync(connectionID, sslStream, ct).ConfigureAwait(false);
            if (innerRequest == null)
            {
                _logger.Log(connectionID, LogLevel.ERROR, "Failed reading request from TLS stream");
                return;
            }

            innerRequest.Scheme = "https";
            innerRequest.Host = hostname;
            if (connectRequest.Port > 0)
                innerRequest.Port = connectRequest.Port;

            await HandleHttpRequestAsync(connectionID, sslStream, innerRequest, ct).ConfigureAwait(false);
        }

        private async Task HandleHttpRequestAsync(int connectionID, Stream clientStream, ClientRequest request, CancellationToken ct)
        {
            if (!_proxy.RaiseReceivedWebRequest(connectionID, request))
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

            string websocketUpgrade = request.Headers["Upgrade"];
            if (!string.IsNullOrEmpty(websocketUpgrade))
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

            if (!_proxy.RaiseReceivedWebResponse(connectionID, clientRequest, serverResponse))
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
            catch (Exception ex)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Failed writing response to client: {ex}");
            }
        }

        private async Task HandleWebSocketAsync(int connectionID, Uri uri, Stream clientStream, ClientRequest clientRequest, CancellationToken ct)
        {
            var wsProxy = new WebSocketProxy();
            await wsProxy.StartWebSocketProxy(uri, clientStream, clientRequest, _logger, ct);
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

        private async Task<ClientRequest> ReadHttpRequestAsync(int connectionID, Stream stream, CancellationToken ct)
        {
            _logger.Log(connectionID, LogLevel.DEBUG, "Reading client request...");

            byte[] buffer = new byte[8192];
            int totalRead = 0;

            try
            {
                do
                {
                    int bytesRead = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        if (totalRead == 0) return null;
                        break;
                    }
                    totalRead += bytesRead;

                    if (totalRead >= buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    // Check if we have a complete header section (ends with \r\n\r\n)
                    if (ContainsHeaderEnd(buffer, totalRead))
                        break;
                }
                while (!ct.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Log(connectionID, LogLevel.WARN, $"Exception reading request: {ex.Message}");
                if (totalRead == 0) return null;
            }

            if (totalRead == 0) return null;

            // Parse headers and body
            int headerEnd = FindHeaderEnd(buffer, totalRead);
            if (headerEnd < 0)
            {
                _logger.Log(connectionID, LogLevel.ERROR, "No header terminator found");
                return null;
            }

            string headerSection = Encoding.UTF8.GetString(buffer, 0, headerEnd);
            string[] lines = headerSection.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            var request = ParseRequestLines(connectionID, lines);
            if (request == null) return null;

            // Extract body if present
            int bodyStart = headerEnd + 4; // skip \r\n\r\n
            if (bodyStart < totalRead)
            {
                int bodyLength = totalRead - bodyStart;

                // Read remaining body if Content-Length indicates more
                string lengthStr = request.ContentHeaders["content-length"];
                if (int.TryParse(lengthStr, out int contentLength) && contentLength > bodyLength)
                {
                    if (buffer.Length < bodyStart + contentLength)
                        Array.Resize(ref buffer, bodyStart + contentLength);

                    while (bodyLength < contentLength && !ct.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, bodyStart + bodyLength, contentLength - bodyLength, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        bodyLength += read;
                    }
                }

                request.BodyBytes = new byte[bodyLength];
                Array.Copy(buffer, bodyStart, request.BodyBytes, 0, bodyLength);
            }

            return request;
        }

        private ClientRequest ParseRequestLines(int connectionID, string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                _logger.Log(connectionID, LogLevel.ERROR, "Request has no data");
                return null;
            }

            string[] firstLine = lines[0].Split(' ');
            if (firstLine.Length < 3)
            {
                _logger.Log(connectionID, LogLevel.ERROR, $"Invalid request line: {lines[0]}");
                return null;
            }

            var request = new ClientRequest
            {
                Method = firstLine[0],
                Path = firstLine[1],
                Version = firstLine[2],
                RequestNumber = _proxy.GetNextRequestID()
            };

            for (int i = 1; i < lines.Length; i++)
            {
                string[] header = lines[i].Split(':', 2, StringSplitOptions.TrimEntries);
                if (header.Length < 2) continue;

                string name = header[0];
                string value = header[1];

                if (IsContentHeader(name))
                    request.ContentHeaders[name] = value;
                else
                    request.Headers[name] = value;
            }

            string hostFull = request.Headers["Host"];
            if (!string.IsNullOrEmpty(hostFull))
            {
                string[] hostSplit = hostFull.Split(':');
                request.Host = hostSplit[0].Trim();
                request.Port = hostSplit.Length > 1 ? int.Parse(hostSplit[1]) : -1;
            }
            else if (request.Method == "CONNECT")
            {
                // For CONNECT, the path is host:port
                string[] pathSplit = request.Path.Split(':');
                request.Host = pathSplit[0];
                request.Port = pathSplit.Length > 1 ? int.Parse(pathSplit[1]) : 443;
            }

            return request;
        }

        private static bool ContainsHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return true;
            }
            return false;
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static string GetHostFromPath(string path)
        {
            if (path.Contains(':'))
                return path.Split(':')[0];
            return path;
        }

        private static bool IsContentHeader(string headerKey)
        {
            var lowercase = headerKey.ToLower();
            return lowercase switch
            {
                "allow" or "content-disposition" or "content-encoding" or
                "content-language" or "content-length" or "content-location" or
                "content-md5" or "content-range" or "content-type" or
                "expires" or "last-modified" => true,
                _ => false
            };
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
