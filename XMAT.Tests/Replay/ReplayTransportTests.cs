// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // How replay reaches the server (ReplayTransportOptions): the HTTP version it asks for, a connect-to endpoint, and what
    // it never does: answer an authentication challenge with Windows credentials, accept a certificate that is not valid
    // for the URL's host, or fall back to the URL's host. Loopback servers only; the HTTPS ones use an in-memory
    // certificate authority that only the senders built here trust.
    public sealed class ReplayTransportTests : IDisposable
    {
        private const string Host = "xmat-connect-to.test";
        private readonly TestCertificateAuthority _authority = new();

        public void Dispose() => _authority.Dispose();

        [Theory]
        [InlineData("127.0.0.1:443", "127.0.0.1", 443)]
        [InlineData("10.0.0.1:1", "10.0.0.1", 1)]
        [InlineData("192.168.255.0:65535", "192.168.255.0", 65535)]
        [InlineData("[::1]:8443", "::1", 8443)]
        [InlineData("[2001:DB8::1]:443", "2001:db8::1", 443)]
        public void ConnectTo_AcceptsANumericAddressAndPort(string text, string address, int port)
        {
            Assert.True(ReplayConnectTo.TryParse(text, out ReplayConnectTo target));
            Assert.Equal((address, port), (target.Address, target.Port));
            Assert.Equal(text.StartsWith('[') ? $"[{address}]:{port}" : text, target.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("127.0.0.1")]
        [InlineData("127.0.0.1:")]
        [InlineData("127.0.0.1:0")]
        [InlineData("127.0.0.1:65536")]
        [InlineData("127.0.0.1:0443")]
        [InlineData("127.0.0.1:+443")]
        [InlineData("127.1:443")]
        [InlineData("1.2.3:443")]
        [InlineData("010.0.0.1:443")]
        [InlineData("0x7f.0.0.1:443")]
        [InlineData("256.0.0.1:443")]
        [InlineData(" 127.0.0.1:443")]
        [InlineData("localhost:443")]
        [InlineData("example.test:443")]
        [InlineData("https://127.0.0.1:443")]
        [InlineData("127.0.0.1:443/path")]
        [InlineData("::1:443")]
        [InlineData("[::1]")]
        [InlineData("[::1]:")]
        [InlineData("[fe80::1%3]:443")]
        [InlineData("[127.0.0.1]:443")]
        [InlineData("[#]:443")]
        public void ConnectTo_RefusesEverythingElse(string text) =>
            Assert.False(ReplayConnectTo.TryParse(text, out _));

        [Theory]
        [InlineData("0.0.0.0:443", true)]
        [InlineData("224.0.0.1:443", true)]
        [InlineData("239.255.255.255:443", true)]
        [InlineData("255.255.255.255:443", true)]
        [InlineData("[::]:443", true)]
        [InlineData("[ff02::1]:443", true)]
        [InlineData("[::ffff:224.0.0.1]:443", true)]
        [InlineData("127.0.0.1:443", false)]
        [InlineData("10.1.2.3:443", false)]
        [InlineData("[::1]:443", false)]
        [InlineData("[::ffff:127.0.0.1]:443", false)]
        public void ConnectTo_DeniesUnspecifiedMulticastAndBroadcastAddresses(string text, bool denied)
        {
            Assert.True(ReplayConnectTo.TryParse(text, out ReplayConnectTo target));
            Assert.Equal(denied, target.IsDenied);
        }

        // Over HTTPS, a connect-to send keeps the URL's host as the TLS server name and the Host
        // header, and reaches the endpoint with a certificate that is valid for the URL's host. An IPv4-mapped IPv6
        // endpoint connects over IPv4.
        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("[::ffff:127.0.0.1]")]
        public async Task ConnectTo_KeepsTheUrlsHostForTlsAndHost_AndReachesTheEndpoint(string address)
        {
            using var certificate = _authority.Issue(Host);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate));
            using ReplaySender sender = Trusting(Endpoint(server.Port, address));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = $"https://{Host}:{server.Port}/item" }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
            // The client can retry a connection, so the handshakes are not counted, only their server names checked.
            Assert.NotEmpty(server.ServerNames);
            Assert.All(server.ServerNames, name => Assert.Equal(Host, name));
            LoopbackRequest request = Assert.Single(server.Requests);
            Assert.Equal(($"{Host}:{server.Port}", "/item"), (request.Header("Host"), request.Target));
        }

        // A certificate that is valid only for the endpoint, from a root that the sender trusts, fails on its name; the
        // URL's host certificate from a root that only the platform would have to trust fails as XMAT ships. Either way
        // the TLS server name was the URL's host, and no HTTP header or body reached the endpoint.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectTo_NeverAcceptsACertificateThatIsNotValidForTheUrlsHost(bool trustedRoot)
        {
            using var certificate = trustedRoot ? _authority.Issue(address: IPAddress.Loopback) : _authority.Issue(Host);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate));
            ReplayConnectTo target = Endpoint(server.Port);
            using ReplaySender sender = trustedRoot ? Trusting(target) : new ReplaySender(target);
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = $"https://{Host}:{server.Port}/secret",
                HeadersText = "Authorization: Bearer XMATCANARY",
            };
            draft.SetBodyBytes("XMATCANARY"u8.ToArray());

            ReplayExchange exchange = await sender.SendAsync(draft, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.Empty(server.Requests);
            Assert.All(server.ServerNames, name => Assert.Equal(Host, name));
            Assert.NotEmpty(server.ServerNames);
        }

        // When the endpoint cannot be reached the send fails; the URL's own host, which would have answered, is never
        // tried.
        [Fact]
        public async Task ConnectTo_NeverFallsBackToTheUrlsHost()
        {
            using var certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var urlHost = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate));
            using ReplaySender sender = Trusting(Endpoint(ClosedPort()));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = urlHost.Origin + "/item" }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.Equal(0, urlHost.Connections);
        }

        // A redirect within the origin goes to the same endpoint, again with the URL's host as the TLS server name.
        [Fact]
        public async Task ConnectTo_SameOriginRedirectsGoToTheSameEndpoint()
        {
            using var certificate = _authority.Issue(Host);
            await using var server = new LoopbackHttpServer(
                request => request.Target == "/start" ? LoopbackResponse.Redirect(302, "/next") : LoopbackResponse.Ok(),
                new LoopbackTls(certificate));
            using ReplaySender sender = Trusting(Endpoint(server.Port));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = $"https://{Host}:{server.Port}/start" },
                Options(maxRedirects: 1),
                CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200, 1), (exchange.Outcome, exchange.StatusCode, exchange.Redirects));
            Assert.Equal(new[] { "/start", "/next" }, server.Requests.Select(request => request.Target));
            // The client can retry a connection, so the handshakes are not counted, only their server names checked.
            Assert.NotEmpty(server.ServerNames);
            Assert.All(server.ServerNames, name => Assert.Equal(Host, name));
        }

        // An explicit version is asked for exactly; auto still asks for HTTP/2 and accepts HTTP/1.1, as before. The server
        // speaks HTTP/1.1 only and records which protocol each handshake negotiated: none ("") when the client offered no
        // application protocol, as for HTTP/1.1 only, which therefore never even offers HTTP/2.
        [Theory]
        [InlineData("1.1", true, "", true)]
        [InlineData("1.1", false, "", true)]
        [InlineData("auto", true, "h2", false)]
        [InlineData("auto", false, "http/1.1", true)]
        [InlineData("2", true, "h2", false)]
        [InlineData("2", false, null, false)]
        public async Task HttpVersion_IsAskedForExactly_AndAutoIsUnchanged(
            string versionName, bool serverOffersHttp2, string negotiated, bool completes)
        {
            Assert.True(ReplayTransportOptions.TryParseVersion(versionName, out ReplayHttpVersion version));
            using var certificate = _authority.Issue(address: IPAddress.Loopback);
            SslApplicationProtocol[] protocols = serverOffersHttp2
                ? new[] { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 }
                : new[] { SslApplicationProtocol.Http11 };
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok(), new LoopbackTls(certificate, protocols));
            using ReplaySender sender = Trusting(connectTo: null);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/v" }, Options(version: version), CancellationToken.None);

            Assert.Equal(completes, exchange.Outcome == ReplayOutcome.Completed);
            if (negotiated == null)
            {
                Assert.DoesNotContain("http/1.1", server.Protocols);
            }
            else
            {
                Assert.Equal(negotiated, server.Protocols.First());
            }

            Assert.Equal(completes ? new[] { "HTTP/1.1" } : Array.Empty<string>(), server.Requests.Select(request => request.Version));
            Assert.Equal(completes ? HttpVersion.Version11 : null, exchange.Version);
        }

        // An NTLM or Negotiate challenge is the result: neither the direct nor the connect-to sender answers it with the
        // user's Windows credentials.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnAuthenticationChallenge_IsNeverAnsweredWithWindowsCredentials(bool connectTo)
        {
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
                401, "Unauthorized", "WWW-Authenticate: Negotiate\r\nWWW-Authenticate: NTLM\r\n"));
            using ReplaySender sender = connectTo ? new ReplaySender(Endpoint(server.Port)) : new ReplaySender();
            string url = connectTo ? $"http://{Host}:{server.Port}/" : server.Origin + "/";

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = url }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 401), (exchange.Outcome, exchange.StatusCode));
            LoopbackRequest request = Assert.Single(server.Requests);
            Assert.Null(request.Header("Authorization"));
        }

        // Cancelling a send to an endpoint that never answers closes its connection.
        [Fact]
        public async Task ConnectTo_CancellationClosesTheConnection()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using ReplaySender sender = new(Endpoint(((IPEndPoint)listener.LocalEndpoint).Port));
            using var cancellation = new CancellationTokenSource();
            Task<ReplayExchange> sending = sender.SendAsync(
                new RequestReplayDraft { Url = $"https://{Host}/" }, Options(), cancellation.Token);
            using TcpClient accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            NetworkStream stream = accepted.GetStream();
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // The ClientHello may arrive first; then the connection ends, closed or reset, before the timeout.
                while (await stream.ReadAsync(buffer, timeout.Token) > 0)
                {
                }
            }
            catch (IOException)
            {
                // Reset by the client: closed too.
            }
        }

        // The run's transport decides its sender: a connect-to run gets one of its own for exactly its endpoint, and never
        // the direct sender (which the GUI passes over its pooled connections); a direct run never gets a connect-to one.
        // The run asks for its HTTP version too: HTTP/1.1 only completes with a server that offers HTTP/2, where auto
        // would negotiate HTTP/2, which this server does not speak.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TheRunsTransport_ChoosesItsSender_AndReportsItWithEachSend(bool connectTo)
        {
            using var certificate = _authority.Issue(Host);
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok(),
                new LoopbackTls(certificate, new[] { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 }));
            ReplayConnectTo endpoint = Endpoint(server.Port);
            var connectedTo = new List<ReplayConnectTo>();
            int directSenders = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () =>
                {
                    directSenders++;
                    return Trusting(Endpoint(server.Port));
                },
                ConnectToSenderFactory = target =>
                {
                    connectedTo.Add(target);
                    return Trusting(target);
                },
            };
            var transport = new ReplayTransportOptions(ReplayHttpVersion.Http11, connectTo ? endpoint : null);

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = $"https://{Host}:{server.Port}/" }),
                    new ReplayOptions(AllowConnectTo: true, Transport: transport)),
                OperationContext.None,
                CancellationToken.None);

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(connectTo ? new[] { endpoint } : Array.Empty<ReplayConnectTo>(), connectedTo);
            Assert.Equal(connectTo ? 0 : 1, directSenders);
            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.Equal(
                (ReplayOutcome.Completed, connectTo ? "connect-to" : "direct", "1.1", "1.1"),
                (item.Outcome, item.Transport, item.HttpVersion, item.ResponseVersion));
        }

        // A send without a response reports no response version.
        [Fact]
        public async Task ASendWithoutAResponse_ReportsNoResponseVersion()
        {
            var operation = new ReplayOperation { SenderFactory = () => Trusting(Endpoint(ClosedPort())) };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(new RequestReplayDraft { Url = $"https://{Host}/" })),
                OperationContext.None,
                CancellationToken.None);

            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.Equal((ReplayOutcome.Failed, "direct", "auto", null), (item.Outcome, item.Transport, item.HttpVersion, item.ResponseVersion));
        }

        // Everything about the transport is checked before anything is sent, by the preview too, which needs no consent
        // but names it; the run refuses without it.
        [Theory]
        [InlineData("https://h.test/", "2", null, true, null)]
        [InlineData("http://h.test/", "2", null, true, ReplayOperation.Http2HttpsCode)]
        [InlineData("http://h.test/", "1.1", null, true, null)]
        [InlineData("https://h.test/", "auto", "0.0.0.0:443", true, ReplayOperation.ConnectToDeniedCode)]
        [InlineData("https://h.test/", "auto", "[ff02::1]:443", true, ReplayOperation.ConnectToDeniedCode)]
        [InlineData("http://h.test/", "auto", "127.0.0.1:443", true, ReplayOperation.ConnectToHttpsCode)]
        [InlineData("https://h.test/", "auto", "127.0.0.1:443", false, ReplayOperation.ConnectToPolicyCode)]
        public async Task Transport_IsCheckedBeforeAnythingIsSent(
            string url, string version, string endpoint, bool allowConnectTo, string code)
        {
            Assert.True(ReplayTransportOptions.TryParseVersion(version, out ReplayHttpVersion httpVersion));
            ReplayConnectTo target = null;
            Assert.True(endpoint == null || ReplayConnectTo.TryParse(endpoint, out target));
            var transport = new ReplayTransportOptions(httpVersion, target);
            int senders = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () => Counting(ref senders),
                ConnectToSenderFactory = _ => Counting(ref senders),
            };
            ReplaySource source = ReplaySource.FromDraft(new RequestReplayDraft { Url = url });
            var options = new ReplayOptions(AllowConnectTo: allowConnectTo, Transport: transport);

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source, options);
            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source, options), OperationContext.None, CancellationToken.None);

            Assert.Equal(code == ReplayOperation.ConnectToPolicyCode ? null : code, preview.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(code, run.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(code == null ? 1 : 0, senders);
            if (preview.Succeeded)
            {
                Assert.Equal(transport, preview.Value.Transport);
                Assert.Equal(target != null, preview.Value.UsesConnectTo);
            }
        }

        // A connect-to run serves one origin.
        [Fact]
        public async Task ConnectTo_RefusesARunOfSeveralOrigins()
        {
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:443", out ReplayConnectTo target));
            ProxyConnectionModel[] rows = { Row(1, "https://one.test/a"), Row(2, "https://two.test/b") };

            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromCaptures(rows),
                    new ReplayOptions(AllowConnectTo: true, Transport: new ReplayTransportOptions(ConnectTo: target))),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(ReplayOperation.ConnectToOriginCountCode, Assert.Single(result.Diagnostics).Code);
        }

        // Fiddler's control headers are refused in any case, in a preview too, and also when Connection nominates one, so
        // that it would not be sent: a captured header never chooses where a request goes or which certificate is
        // accepted, and is never dropped silently either. Removing it makes the request replayable.
        [Theory]
        [InlineData("Fiddler-Host: 10.0.0.1", false)]
        [InlineData("x-overridehost: 10.0.0.1", false)]
        [InlineData("X-IgnoreCertCNMismatch: true", false)]
        [InlineData("X-OverrideGateway: 10.0.0.1:8888", false)]
        [InlineData("Fiddler-Host: 10.0.0.1", true)]
        [InlineData("X-OverrideHost: 10.0.0.1", true)]
        public async Task FiddlerControlHeaders_AreRefused_UntilRemoved(string header, bool nominatedByConnection)
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var operation = new ReplayOperation();
            string name = header[..header.IndexOf(':')];
            string connection = nominatedByConnection ? $"Connection: keep-alive, {name}\r\n" : string.Empty;
            RequestReplayDraft Draft() => new() { Url = server.Origin + "/", HeadersText = "Accept: */*\r\n" + connection + header };

            OperationResult<ReplayPlanPreview> preview = operation.Preview(ReplaySource.FromDraft(Draft()));
            OperationResult<ReplayResult> refused = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(Draft())), OperationContext.None, CancellationToken.None);
            int sentWhenRefused = server.Requests.Count;
            OperationResult<ReplayResult> removed = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(Draft()), Edits: new ReplayEdits(RemoveHeaders: new[] { name })),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(ReplayOperation.ControlHeaderCode, Assert.Single(preview.Diagnostics).Code);
            Assert.Equal(ReplayOperation.ControlHeaderCode, Assert.Single(refused.Diagnostics).Code);
            Assert.Equal(0, sentWhenRefused);
            Assert.True(removed.Succeeded, Describe(removed));
            Assert.Null(Assert.Single(server.Requests).Header(name));
        }

        private static ReplaySendOptions Options(int maxRedirects = 0, ReplayHttpVersion version = ReplayHttpVersion.Auto) =>
            new(TimeSpan.FromSeconds(10), ReplaySendOptions.DefaultMaxResponseBytes, maxRedirects, version);

        private static ReplayConnectTo Endpoint(int port, string address = "127.0.0.1")
        {
            Assert.True(ReplayConnectTo.TryParse($"{address}:{port}", out ReplayConnectTo target));
            return target;
        }

        // A sender built as XMAT builds it, which also trusts this test's root.
        private ReplaySender Trusting(ReplayConnectTo connectTo)
        {
            SocketsHttpHandler handler = ReplaySender.CreateHandler(connectTo);
            _authority.Trust(handler);
            return new ReplaySender(new HttpMessageInvoker(handler), ownsInvoker: true);
        }

        private static ReplaySender Counting(ref int senders)
        {
            senders++;
            return new ReplaySender(new HttpMessageInvoker(new OkHandler()), ownsInvoker: true);
        }

        // A port that nothing listens on.
        private static int ClosedPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string Describe<T>(OperationResult<T> result) =>
            $"{result.Outcome}: {string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code))}";

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // Answers 200 without any network.
        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("ok")) });
        }
    }
}
