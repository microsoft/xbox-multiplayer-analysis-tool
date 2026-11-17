// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace XMAT.Tests.Replay
{
    // One request as a Windows-authentication test server saw it: on which connection, its request line, and its
    // Authorization scheme and length, never the token.
    internal sealed record LoopbackAuthRequest(int Connection, string Method, string Target, string AuthScheme, int AuthLength);

    // How a Windows-authentication test server answers.
    // - Offered: the WWW-Authenticate values of its 401 (each its own field), such as "Negotiate" or "Basic realm=\"x\"".
    // - Package: the package that its NegotiateAuthentication server runs ("Negotiate" or "NTLM"), or null to authenticate
    //   no one, so every request gets the 401.
    // - FinalTail: a WWW-Authenticate value added to the response that completes authentication, as a server sends the
    //   last Negotiate token for mutual authentication (a canary in tests); null for none.
    // - ProxyChallenge: answer every request with a 407 and these Proxy-Authenticate values instead.
    // - Redirects: target paths that answer 307 to another path once authenticated, as Location values.
    internal sealed record LoopbackAuthOptions(
        IReadOnlyList<string> Offered,
        string Package,
        string FinalTail = null,
        IReadOnlyList<string> ProxyChallenge = null,
        IReadOnlyDictionary<string, string> Redirects = null);

    // A loopback HTTPS origin server that authenticates with Windows (NTLM or Negotiate) as a web server does: it keeps
    // each connection open across requests, answers a request without credentials with 401 and its challenges, runs one
    // NegotiateAuthentication server per connection over the client's tokens, and once the connection is authenticated
    // answers 200 with what authenticated it. It records every request's connection, request line and Authorization
    // scheme and length, and the client certificates offered to it, never a token. It listens on a loopback address only.
    internal sealed class LoopbackAuthServer : IAsyncDisposable
    {
        private const int MaxHeadBytes = 64 * 1024;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly LoopbackTls _tls;
        private readonly LoopbackAuthOptions _options;
        private readonly ConcurrentQueue<LoopbackAuthRequest> _requests = new();
        private readonly ConcurrentQueue<(int Connection, string How)> _authenticated = new();
        private readonly ConcurrentQueue<(int Connection, string Thumbprint)> _clientCertificates = new();
        private readonly ConcurrentBag<Task> _serving = new();
        private readonly Task _accepting;
        private readonly string _host;
        private int _connections;

        internal LoopbackAuthServer(LoopbackTls tls, LoopbackAuthOptions options, IPAddress address = null)
        {
            _tls = tls ?? throw new ArgumentNullException(nameof(tls));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            address ??= IPAddress.Loopback;
            if (!IPAddress.IsLoopback(address))
            {
                throw new ArgumentException("A test server listens on a loopback address only.", nameof(address));
            }

            _host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(AcceptAsync);
        }

        internal int Port { get; }

        // https://ADDRESS:PORT.
        internal string Origin => "https://" + _host + ":" + Port.ToString(CultureInfo.InvariantCulture);

        internal int Connections => Volatile.Read(ref _connections);

        internal IReadOnlyList<LoopbackAuthRequest> Requests => _requests.ToArray();

        // Each connection that authenticated, and how: "package/authentication type".
        internal IReadOnlyList<(int Connection, string How)> Authenticated => _authenticated.ToArray();

        // The thumbprint of each client certificate offered, with the connection that it was offered on.
        internal IReadOnlyList<(int Connection, string Thumbprint)> ClientCertificates => _clientCertificates.ToArray();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Stopped.
            }

            await Task.WhenAll(_serving.ToArray()).ContinueWith(static _ => { }, TaskScheduler.Default);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or
                    InvalidOperationException)
                {
                    return;
                }

                int connection = Interlocked.Increment(ref _connections);
                _serving.Add(ServeAsync(client, connection));
            }
        }

        private async Task ServeAsync(TcpClient client, int connection)
        {
            NegotiateAuthentication server = null;
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                            ServerCertificate = _tls.Certificate,
                            ClientCertificateRequired = _tls.ClientCertificate != LoopbackClientCertificate.NotAsked,
                            EnabledSslProtocols = _tls.SslProtocols,
                            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                            {
                                if (certificate != null)
                                {
                                    _clientCertificates.Enqueue((connection, certificate.GetCertHashString()));
                                }

                                return certificate != null || _tls.ClientCertificate != LoopbackClientCertificate.Required;
                            },
                        },
                        _stop.Token);
                    while (true)
                    {
                        (string method, string target, string authorization)? head = await ReadRequestAsync(ssl, _stop.Token);
                        if (head == null)
                        {
                            return;
                        }

                        (string method, string target, string authorization) = head.Value;
                        int space = authorization?.IndexOf(' ') ?? -1;
                        string scheme = authorization == null ? null : space < 0 ? authorization : authorization[..space];
                        _requests.Enqueue(new LoopbackAuthRequest(connection, method, target, scheme, authorization?.Length ?? 0));
                        if (_options.ProxyChallenge != null)
                        {
                            await WriteAsync(ssl, 407, "Proxy Authentication Required", _options.ProxyChallenge.Select(value => ("Proxy-Authenticate", value)), "");
                            continue;
                        }

                        if (server is { IsAuthenticated: true })
                        {
                            await AnswerAuthenticatedAsync(ssl, server, target, tail: null);
                            continue;
                        }

                        if (authorization == null || _options.Package == null || space < 0)
                        {
                            await WriteAsync(ssl, 401, "Unauthorized", _options.Offered.Select(value => ("WWW-Authenticate", value)), "");
                            continue;
                        }

                        server ??= new NegotiateAuthentication(new NegotiateAuthenticationServerOptions { Package = _options.Package });
                        string blob = server.GetOutgoingBlob(authorization[(space + 1)..], out NegotiateAuthenticationStatusCode status);
                        if (status == NegotiateAuthenticationStatusCode.ContinueNeeded)
                        {
                            await WriteAsync(ssl, 401, "Unauthorized", new[] { ("WWW-Authenticate", scheme + " " + blob) }, "");
                            continue;
                        }

                        if (status != NegotiateAuthenticationStatusCode.Completed)
                        {
                            await WriteAsync(ssl, 401, "Unauthorized", new[] { ("X-Status", status.ToString()) }, "");
                            return;
                        }

                        _authenticated.Enqueue((connection, server.Package + "/" + server.RemoteIdentity.AuthenticationType));
                        string tail = _options.FinalTail ?? (blob == null ? null : scheme + " " + blob);
                        await AnswerAuthenticatedAsync(ssl, server, target, tail);
                    }
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // The client went away or the server is stopping: the connection just closes.
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private async Task AnswerAuthenticatedAsync(Stream stream, NegotiateAuthentication server, string target, string tail)
        {
            var headers = new List<(string, string)>();
            if (tail != null)
            {
                headers.Add(("WWW-Authenticate", tail));
            }

            if (_options.Redirects != null && _options.Redirects.TryGetValue(target, out string location))
            {
                headers.Add(("Location", location));
                await WriteAsync(stream, 307, "Temporary Redirect", headers, "");
                return;
            }

            await WriteAsync(stream, 200, "OK", headers, "authenticated;package=" + server.Package);
        }

        // The request line and its Authorization value; its body, by Content-Length, is read and dropped. Null when the
        // client closed the connection.
        private static async Task<(string Method, string Target, string Authorization)?> ReadRequestAsync(
            Stream stream, CancellationToken cancellationToken)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                head.Add(one[0]);
                int count = head.Count;
                if (count > MaxHeadBytes)
                {
                    return null;
                }

                if (count >= 4 && head[count - 4] == '\r' && head[count - 3] == '\n' && head[count - 2] == '\r' &&
                    head[count - 1] == '\n')
                {
                    break;
                }
            }

            string[] lines = Encoding.Latin1.GetString(head.ToArray()).Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            string authorization = null;
            long length = 0;
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = line[..colon];
                string value = line[(colon + 1)..].Trim();
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = value;
                }
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    length = long.Parse(value, CultureInfo.InvariantCulture);
                }
            }

            var body = new byte[Math.Min(length, 1L << 20)];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "", authorization);
        }

        private static async Task WriteAsync(
            Stream stream, int status, string reason, IEnumerable<(string Name, string Value)> headers, string body)
        {
            byte[] content = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
            foreach ((string name, string value) in headers)
            {
                head.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            head.Append("Content-Length: ").Append(content.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()));
            await stream.WriteAsync(content);
            await stream.FlushAsync();
        }
    }
}
