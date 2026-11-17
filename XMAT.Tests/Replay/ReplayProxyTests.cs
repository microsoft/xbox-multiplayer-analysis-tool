// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // Replay through an upstream HTTP proxy: the proxy URL that is accepted, the proxy that the handler uses,
    // the checks and the permission that come before anything is sent, what the proxy receives, and how its failures are
    // reported. Loopback proxies and servers only; the HTTPS ones use in-memory certificate authorities, trusted only by
    // the senders that a test builds to trust them. The canaries stand for a request's credentials and data.
    public sealed class ReplayProxyTests : IDisposable
    {
        private const string Host = "xmat-proxy.test";
        private const string Canary = "XMATPROXYCANARY";
        private readonly TestCertificateAuthority _authority = new();

        public void Dispose() => _authority.Dispose();

        [Theory]
        [InlineData("http://proxy.test", "proxy.test", 80, "http://proxy.test:80")]
        [InlineData("http://proxy.test:8080/", "proxy.test", 8080, "http://proxy.test:8080")]
        [InlineData("HTTP://Proxy.Example-1.test:3128", "proxy.example-1.test", 3128, "http://proxy.example-1.test:3128")]
        [InlineData("http://localhost:1", "localhost", 1, "http://localhost:1")]
        [InlineData("http://127.0.0.1:65535", "127.0.0.1", 65535, "http://127.0.0.1:65535")]
        [InlineData("http://[::1]:8888", "::1", 8888, "http://[::1]:8888")]
        [InlineData("http://[2001:DB8::1]", "2001:db8::1", 80, "http://[2001:db8::1]:80")]
        [InlineData("http://xn--bcher-kva.example:8080", "xn--bcher-kva.example", 8080, "http://xn--bcher-kva.example:8080")]
        public void Proxy_AcceptsAnHttpHostAndPort_InCanonicalForm(string text, string host, int port, string canonical)
        {
            Assert.True(ReplayProxy.TryParse(text, out ReplayProxy proxy));

            Assert.Equal((host, port, canonical), (proxy.Host, proxy.Port, proxy.ToString()));
            Assert.Equal((host, port), (proxy.ToUri().IdnHost, proxy.ToUri().Port));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("proxy.test:8080")]
        [InlineData("https://proxy.test:8080")]
        [InlineData("socks5://127.0.0.1:1080")]
        [InlineData("http://")]
        [InlineData("http:///")]
        [InlineData("http://:8080")]
        [InlineData("http://user:pass@proxy.test:8080")]
        [InlineData("http://user@proxy.test")]
        [InlineData("http://proxy.test:8080/path")]
        [InlineData("http://proxy.test/?q=1")]
        [InlineData("http://proxy.test#f")]
        [InlineData("http://proxy.test//")]
        [InlineData("http://proxy.test:")]
        [InlineData("http://proxy.test:0")]
        [InlineData("http://proxy.test:65536")]
        [InlineData("http://proxy.test:080")]
        [InlineData("http://proxy.test:+80")]
        [InlineData("http://proxy.test:80:80")]
        [InlineData("http://proxy.test.")]
        [InlineData("http://proxy..test")]
        [InlineData("http://-proxy.test")]
        [InlineData("http://proxy-.test")]
        [InlineData("http://pro_xy.test")]
        [InlineData("http://proxy.123")]
        [InlineData("http://proxy.0x10")]
        [InlineData("http://b\u00fccher.example")]
        [InlineData("http://127.1")]
        [InlineData("http://127.0.0.1.5")]
        [InlineData("http://010.0.0.1")]
        [InlineData("http://256.0.0.1")]
        [InlineData("http://2130706433")]
        [InlineData("http://0x7f.0.0.1")]
        [InlineData("http://0x7F000001")]
        [InlineData(" http://proxy.test")]
        [InlineData("http://proxy.test ")]
        [InlineData("http://proxy.test\\")]
        [InlineData("http:\\\\proxy.test")]
        [InlineData("http://proxy%2Etest")]
        [InlineData("http://[fe80::1%253]:80")]
        [InlineData("http://[::1]8080")]
        [InlineData("http://[::1]:")]
        [InlineData("http://[127.0.0.1]:80")]
        [InlineData("http://[]:80")]
        [InlineData("http://::1:80")]
        public void Proxy_RefusesEverythingElse(string text) => Assert.False(ReplayProxy.TryParse(text, out _));

        [Fact]
        public void Proxy_HostNamesKeepToDnsLengths()
        {
            string label = new('a', 63);
            string longest = string.Join('.', label, label, label, new string('b', 61));

            Assert.Equal(253, longest.Length);
            Assert.True(ReplayProxy.TryParse("http://" + longest, out _));
            Assert.False(ReplayProxy.TryParse("http://" + longest + "c", out _));
            Assert.False(ReplayProxy.TryParse("http://" + new string('a', 64) + ".test", out _));
        }

        // The HTTP stack sends a request directly when its proxy throws or names none (HttpConnectionPoolManager), so every
        // member of FixedProxy answers for any destination, and no credentials can be given to it.
        [Fact]
        public void FixedProxy_SendsEveryDestinationThroughTheProxy_AndNeverThrows()
        {
            var proxy = new FixedProxy(Proxy("http://127.0.0.1:3128"));
            Uri[] destinations =
            {
                null, new("http://127.0.0.1/"), new("http://localhost/"), new("https://[::1]:443/"), new("https://e.test/x"),
                new("file:///c:/x"),
            };

            foreach (Uri destination in destinations)
            {
                Assert.Equal(new Uri("http://127.0.0.1:3128/"), proxy.GetProxy(destination));
                Assert.False(proxy.IsBypassed(destination));
            }

            proxy.Credentials = CredentialCache.DefaultCredentials;
            Assert.Null(proxy.Credentials);
        }

        // Only a sender made for a proxy uses one, and never the system's proxy or any proxy credentials; a handler is made
        // for an endpoint or a proxy, never both.
        [Fact]
        public void OnlyAProxySender_UsesAProxy_WithoutCredentials()
        {
            ReplayProxy proxy = Proxy("http://127.0.0.1:3128");
            using SocketsHttpHandler through = ReplaySender.CreateHandler(new ReplaySenderSetup(Proxy: proxy));
            using SocketsHttpHandler direct = ReplaySender.CreateHandler();

            Assert.True(through.UseProxy);
            Assert.Equal(new Uri("http://127.0.0.1:3128/"), Assert.IsType<FixedProxy>(through.Proxy).GetProxy(null));
            Assert.Null(through.DefaultProxyCredentials);
            Assert.Null(through.Credentials);
            Assert.Null(through.ConnectCallback);
            Assert.False(direct.UseProxy);
            Assert.Throws<ArgumentException>(
                () => ReplaySender.CreateHandler(new ReplaySenderSetup(ConnectTo: Endpoint("127.0.0.1:443"), Proxy: proxy)));
        }

        // Everything is checked before anything is sent: a proxy is never combined with a connect-to endpoint, in a preview
        // too, and a run sends nothing through a proxy without its permission. The preview names the permission instead.
        // A run that may send gets a sender of its own, made for the proxy; the direct sender is never used.
        [Theory]
        [InlineData(true, true, ReplayOperation.ProxyConnectToCode, ReplayOperation.ProxyConnectToCode)]
        [InlineData(false, false, null, ReplayOperation.ProxyPolicyCode)]
        [InlineData(false, true, null, null)]
        public async Task TheProxy_IsCheckedAndAllowed_BeforeAnythingIsSent(
            bool connectTo, bool allowProxy, string previewCode, string runCode)
        {
            ReplayProxy proxy = Proxy("http://127.0.0.1:3128");
            var transport = new ReplayTransportOptions(ConnectTo: connectTo ? Endpoint("127.0.0.1:443") : null, Proxy: proxy);
            var setups = new List<ReplaySenderSetup>();
            int directSenders = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () =>
                {
                    directSenders++;
                    return Answering();
                },
                OwnSenderFactory = setup =>
                {
                    setups.Add(setup);
                    return Answering();
                },
            };
            ReplaySource source = ReplaySource.FromDraft(CanaryDraft($"https://{Host}/item"));
            var options = new ReplayOptions(AllowUnsafeMethods: true, AllowConnectTo: true, AllowProxy: allowProxy, Transport: transport);

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source, options);
            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source, options), OperationContext.None, CancellationToken.None);

            Assert.Equal(previewCode, preview.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(runCode, run.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(0, directSenders);
            Assert.Equal(
                runCode == null ? new[] { new ReplaySenderSetup(Proxy: proxy) } : Array.Empty<ReplaySenderSetup>(), setups);
            if (preview.Succeeded)
            {
                Assert.True(preview.Value.UsesProxy);
                Assert.Same(transport, preview.Value.Transport);
            }

            if (run.Succeeded)
            {
                Assert.Equal(ReplayTransportOptions.ProxyName, Assert.Single(run.Value.Items).Transport);
            }
        }

        // The run's own sender is disposed when the run ends, whether it completed or was cancelled.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TheRunsOwnSender_IsDisposedWhenTheRunEnds(bool cancel)
        {
            var handler = new TrackingHandler(hang: cancel);
            var operation = new ReplayOperation
            {
                OwnSenderFactory = setup => new ReplaySender(new HttpMessageInvoker(handler), ownsInvoker: true, setup),
            };
            using var cancellation = new CancellationTokenSource();

            Task<OperationResult<ReplayResult>> running = operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = $"http://{Host}/" }),
                    new ReplayOptions(AllowProxy: true, Transport: new ReplayTransportOptions(Proxy: Proxy("http://127.0.0.1:9")))),
                OperationContext.None,
                cancellation.Token);
            if (cancel)
            {
                await handler.Sending.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
            }

            OperationResult<ReplayResult> result = await running;

            Assert.Equal(cancel ? OperationOutcome.Cancelled : OperationOutcome.Succeeded, result.Outcome);
            Assert.True(handler.Disposed);
        }

        // With permission, an http request goes to the proxy whole, in absolute form, its credentials and body included,
        // and a loopback URL too: never to the server directly. The record is the request as replay built it.
        [Fact]
        public async Task AnHttpRequest_GoesToTheProxyWhole_EvenForALoopbackUrl()
        {
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok("direct"));
            await using var proxy = new LoopbackProxy(_ => new ProxyAnswer.Respond(LoopbackResponse.Ok("via proxy")));
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy(proxy.Url)));

            ReplayExchange exchange = await sender.SendAsync(CanaryDraft(origin.Origin + "/item?q=1"), Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200, null), (exchange.Outcome, exchange.StatusCode, exchange.Code));
            Assert.Equal("via proxy"u8.ToArray(), exchange.Response.Body);
            Assert.Equal("POST /item?q=1 HTTP/1.1", exchange.Request.StartLine);
            Assert.Equal(0, origin.Connections);
            LoopbackRequest received = Assert.Single(proxy.Requests);
            Assert.Equal(("POST", origin.Origin + "/item?q=1"), (received.Method, received.Target));
            Assert.Equal(
                ($"Bearer {Canary}", $"session={Canary}", Canary, $"{Canary}-agent"),
                (received.Header("Authorization"), received.Header("Cookie"), received.Header("X-Secret"), received.Header("User-Agent")));
            Assert.Equal(Encoding.ASCII.GetBytes(Canary + "-body"), received.Body);
            Assert.Null(received.Header("Proxy-Authorization"));
        }

        // An https request goes through a CONNECT tunnel, with TLS to the server inside it. The proxy reads the
        // destination's host and port and the request's User-Agent, and no other header or byte of the request: the
        // canary appears once in everything that it read, in that User-Agent.
        [Fact]
        public async Task AnHttpsRequest_ShowsTheProxyOnlyTheHostPortAndUserAgent()
        {
            using X509Certificate2 certificate = _authority.Issue(Host);
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok("secure"), new LoopbackTls(certificate));
            await using var proxy = new LoopbackProxy(_ => new ProxyAnswer.Tunnel(origin.Port));
            using ReplaySender sender = Trusting(Proxy(proxy.Url));

            ReplayExchange exchange = await sender.SendAsync(CanaryDraft($"https://{Host}:{origin.Port}/item"), Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
            await proxy.DisposeAsync();
            LoopbackRequest connect = Assert.Single(proxy.Requests);
            Assert.Equal(("CONNECT", $"{Host}:{origin.Port}"), (connect.Method, connect.Target));
            Assert.Equal(
                new[] { $"Host: {Host}:{origin.Port}", $"User-Agent: {Canary}-agent" },
                connect.Headers.Select(header => header.Key + ": " + header.Value));
            Assert.Equal(1, Occurrences(proxy.ReceivedText, Canary));
            Assert.Equal(new[] { Host }, origin.ServerNames);
            LoopbackRequest arrived = Assert.Single(origin.Requests);
            Assert.Equal(($"Bearer {Canary}", "/item"), (arrived.Header("Authorization"), arrived.Target));
        }

        // A proxy that intercepts HTTPS with a certificate that this PC does not trust fails the send, as a server would:
        // the server's certificate is checked as without a proxy (on Windows, once the handshake ends), so the request
        // never leaves this PC: the proxy reads no request inside the TLS, and no canary but the CONNECT's User-Agent.
        [Fact]
        public async Task AnInterceptingProxy_ThatThisPcDoesNotTrust_FailsTheSend()
        {
            using var interceptor = new TestCertificateAuthority();
            using X509Certificate2 forged = interceptor.Issue(Host);
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var proxy = new LoopbackProxy(
                _ => new ProxyAnswer.Intercept(new LoopbackTls(forged), LoopbackResponse.Ok("forged")));
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy(proxy.Url)));

            ReplayExchange exchange = await sender.SendAsync(CanaryDraft($"https://{Host}:{origin.Port}/item"), Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.Null(exchange.Response);
            await proxy.DisposeAsync();
            Assert.Empty(proxy.Intercepted);
            Assert.Equal(1, Occurrences(proxy.ReceivedText, Canary));
            Assert.Equal(0, origin.Connections);
        }

        // A proxy that intercepts HTTPS with a certificate that this PC trusts receives the whole request, and it,
        // not XMAT, decides whether the server is genuine. Here it answers in the server's place.
        [Fact]
        public async Task AnInterceptingProxy_ThatThisPcTrusts_ReceivesTheWholeRequest()
        {
            using X509Certificate2 forged = _authority.Issue(Host);
            await using var proxy = new LoopbackProxy(
                _ => new ProxyAnswer.Intercept(new LoopbackTls(forged), LoopbackResponse.Ok("from the proxy")));
            using ReplaySender sender = Trusting(Proxy(proxy.Url));

            ReplayExchange exchange = await sender.SendAsync(CanaryDraft($"https://{Host}/item"), Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
            Assert.Equal("from the proxy"u8.ToArray(), exchange.Response.Body);
            await proxy.DisposeAsync();
            LoopbackRequest inside = Assert.Single(proxy.Intercepted);
            Assert.Equal(($"Bearer {Canary}", $"session={Canary}"), (inside.Header("Authorization"), inside.Header("Cookie")));
            Assert.Equal(Encoding.ASCII.GetBytes(Canary + "-body"), inside.Body);
        }

        // XMAT never authenticates to a proxy. A 407, as the answer to an http request, to the CONNECT of an
        // https one, or inside its tunnel, is replay.proxy.auth-required: no response or status is kept for it, nothing is
        // retried, and no proxy credentials are ever sent, whichever schemes the proxy offers.
        [Theory]
        [InlineData("http")]
        [InlineData("connect")]
        [InlineData("tunnel")]
        public async Task A407_IsAuthRequired_NeverAnsweredOrRetried(string where)
        {
            var challenge = new LoopbackResponse(
                407,
                "Proxy Authentication Required",
                "Proxy-Authenticate: Negotiate\r\nProxy-Authenticate: NTLM\r\nProxy-Authenticate: Basic realm=\"proxy\"\r\n",
                Encoding.ASCII.GetBytes(Canary + "-page"));
            using X509Certificate2 forged = _authority.Issue(Host);
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var proxy = new LoopbackProxy(_ => where == "tunnel"
                ? new ProxyAnswer.Intercept(new LoopbackTls(forged), challenge)
                : new ProxyAnswer.Respond(challenge));
            using ReplaySender sender = Trusting(Proxy(proxy.Url));
            string url = where == "http" ? origin.Origin + "/item" : $"https://{Host}:{origin.Port}/item";

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = url }, Options(maxRedirects: 3), CancellationToken.None);

            Assert.Equal(
                (ReplayOutcome.Failed, ReplaySender.ProxyAuthRequiredCode, 0, (ReplayMessage)null, (Exception)null),
                (exchange.Outcome, exchange.Code, exchange.StatusCode, exchange.Response, exchange.Error));
            await proxy.DisposeAsync();
            Assert.Single(proxy.Requests);
            Assert.Equal(where == "tunnel" ? 1 : 0, proxy.Intercepted.Count);
            Assert.All(proxy.Requests.Concat(proxy.Intercepted), request => Assert.Null(request.Header("Proxy-Authorization")));
            Assert.Equal(0, origin.Connections);
        }

        // Without a proxy, a 407 is the server's response, like any other status.
        [Fact]
        public async Task A407WithoutAProxy_IsTheServersResponse()
        {
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
                407, "Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"server\"\r\n"));
            using var sender = new ReplaySender();

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = server.Origin + "/" }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 407, null), (exchange.Outcome, exchange.StatusCode, exchange.Code));
            Assert.NotNull(exchange.Response);
        }

        // A server's own authentication challenge through the proxy is the result, as without one: never answered with the
        // user's Windows credentials.
        [Fact]
        public async Task AServersChallenge_ThroughTheProxy_IsTheResult()
        {
            await using var proxy = new LoopbackProxy(_ => new ProxyAnswer.Respond(new LoopbackResponse(
                401, "Unauthorized", "WWW-Authenticate: Negotiate\r\nWWW-Authenticate: NTLM\r\n")));
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy(proxy.Url)));

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = $"http://{Host}/" }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 401, null), (exchange.Outcome, exchange.StatusCode, exchange.Code));
            await proxy.DisposeAsync();
            Assert.Null(Assert.Single(proxy.Requests).Header("Authorization"));
        }

        // A proxy that answers the CONNECT with another status than 200 refused the tunnel; one that closes the
        // connection without an answer is reported as the runtime reports it, a failed send.
        [Theory]
        [InlineData(true, ReplaySender.ProxyTunnelFailedCode)]
        [InlineData(false, ReplaySender.SendFailedCode)]
        public async Task ATunnelThatTheProxyRefuses_Fails(bool answered, string code)
        {
            await using var proxy = new LoopbackProxy(_ => answered
                ? new ProxyAnswer.Respond(new LoopbackResponse(502, "Bad Gateway"))
                : new ProxyAnswer.Close());
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy(proxy.Url)));

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = $"https://{Host}/" }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, code, 0), (exchange.Outcome, exchange.Code, exchange.StatusCode));
            Assert.Null(exchange.Response);
        }

        // A proxy that cannot be reached fails the send; nothing falls back to sending directly. The proxy's port is held,
        // bound and never listening, for the whole test, so no other program can take it in the meantime.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AProxyThatCannotBeReached_IsUnreachable_AndNothingGoesDirect(bool https)
        {
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using Socket refusing = RefusingPort(out int port);
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy($"http://127.0.0.1:{port}")));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = https ? $"https://{Host}:{origin.Port}/" : origin.Origin + "/" },
                Options(),
                CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.ProxyUnreachableCode), (exchange.Outcome, exchange.Code));
            Assert.Equal(0, origin.Connections);
        }

        // A redirect that is followed goes through the proxy too, never directly.
        [Fact]
        public async Task ARedirectThatIsFollowed_GoesThroughTheProxyToo()
        {
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Ok("direct"));
            await using var proxy = new LoopbackProxy(request => new ProxyAnswer.Respond(
                request.Target.EndsWith("/a", StringComparison.Ordinal) ? LoopbackResponse.Redirect(302, "/b") : LoopbackResponse.Ok("b")));
            using var sender = new ReplaySender(new ReplaySenderSetup(Proxy: Proxy(proxy.Url)));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = origin.Origin + "/a" }, Options(maxRedirects: 1), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200, 1), (exchange.Outcome, exchange.StatusCode, exchange.Redirects));
            Assert.Equal(new[] { origin.Origin + "/a", origin.Origin + "/b" }, proxy.Requests.Select(request => request.Target));
            Assert.Equal(0, origin.Connections);
        }

        private static ReplaySendOptions Options(int maxRedirects = 0) =>
            new(TimeSpan.FromSeconds(10), ReplaySendOptions.DefaultMaxResponseBytes, maxRedirects);

        private static ReplayProxy Proxy(string text)
        {
            Assert.True(ReplayProxy.TryParse(text, out ReplayProxy proxy));
            return proxy;
        }

        private static ReplayConnectTo Endpoint(string text)
        {
            Assert.True(ReplayConnectTo.TryParse(text, out ReplayConnectTo target));
            return target;
        }

        // A POST whose credentials, cookie, secret header, User-Agent and body each carry the canary.
        private static RequestReplayDraft CanaryDraft(string url)
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = url,
                HeadersText = $"Authorization: Bearer {Canary}\r\nCookie: session={Canary}\r\nX-Secret: {Canary}\r\n" +
                    $"User-Agent: {Canary}-agent\r\nContent-Type: text/plain",
            };
            draft.SetBodyBytes(Encoding.ASCII.GetBytes(Canary + "-body"));
            return draft;
        }

        // A proxy sender built as XMAT builds it, which also trusts this test's root, as a PC trusts a root it installed.
        private ReplaySender Trusting(ReplayProxy proxy)
        {
            var setup = new ReplaySenderSetup(Proxy: proxy);
            SocketsHttpHandler handler = ReplaySender.CreateHandler(setup);
            _authority.Trust(handler);
            return new ReplaySender(new HttpMessageInvoker(handler), ownsInvoker: true, setup);
        }

        private static ReplaySender Answering() =>
            new(new HttpMessageInvoker(new TrackingHandler(hang: false)), ownsInvoker: true);

        private static int Occurrences(string text, string value)
        {
            int count = 0;
            for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        // A loopback port that refuses every connection for as long as the caller holds the socket: bound, exclusively, so
        // that no other program can take the port, and never listening.
        private static Socket RefusingPort(out int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)socket.LocalEndPoint).Port;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Answers 200 without any network, or waits until the send is cancelled; records its disposal.
        private sealed class TrackingHandler(bool hang) : HttpMessageHandler
        {
            private readonly TaskCompletionSource _sending = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Sending => _sending.Task;

            internal bool Disposed { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _sending.TrySetResult();
                if (hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ok"u8.ToArray()) };
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
