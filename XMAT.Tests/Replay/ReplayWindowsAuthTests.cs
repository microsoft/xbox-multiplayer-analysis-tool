// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Windows authentication: checked before anything is sent, in a fixed order and with a client
    // certificate's checks; answered with the Windows credentials that XMAT runs with only for the consented origin's
    // Negotiate and NTLM challenges, over exact HTTP/1.1, by a sender of the run's own; the token that the HTTP stack
    // adds is removed from the live message after each hop, only its scheme label kept; and the response's Windows
    // challenges are recorded without their tokens. The live servers authenticate this PC's user over HTTPS on loopback,
    // where Negotiate negotiates NTLM (Kerberos is not exercised); their certificates come from an in-memory authority that
    // only the run's trust includes, and they never log a token.
    public sealed class ReplayWindowsAuthTests : IDisposable
    {
        private const string Url = "https://a.test:8443/item";
        private const string Bucher = "https://xn--bcher-kva.example:8443";
        private const string Canary = "WEFNQVRDQU5BUlk=";
        private static readonly string[] Targets = { "same-origin", "other-port", "other-host" };
        private readonly TestCertificateAuthority _authority = new();

        // Each TLS version, alone or with a client certificate, and where a redirect points.
        public static TheoryData<SslProtocols, bool, string> Containment
        {
            get
            {
                var cases = new TheoryData<SslProtocols, bool, string>();
                foreach (SslProtocols protocol in new[] { SslProtocols.Tls12, SslProtocols.Tls13 })
                {
                    foreach (bool withCertificate in new[] { false, true })
                    {
                        foreach (string target in Targets)
                        {
                            cases.Add(protocol, withCertificate, target);
                        }
                    }
                }

                return cases;
            }
        }

        public void Dispose() => _authority.Dispose();

        // What the requests may not be with Windows authentication is refused first, in this order, together with a
        // client certificate's and a proxy's own checks: nothing touches a certificate, and no sender exists. An
        // Authorization header of the request's own is read from its header lines, in any case and also when it is empty,
        // repeated or nominated by Connection; Connection alone, nominating Authorization, is no such header, so that run
        // goes on to need its consent.
        [Theory]
        [InlineData("proxy", ReplayOperation.WindowsAuthProxyCode)]
        [InlineData("certificate-proxy", ReplayOperation.ClientCertificateProxyCode)]
        [InlineData("proxy-connect-to", ReplayOperation.ProxyConnectToCode)]
        [InlineData("connect-to", ReplayOperation.WindowsAuthConnectToCode)]
        [InlineData("http2-http", ReplayOperation.Http2HttpsCode)]
        [InlineData("http", ReplayOperation.WindowsAuthHttpsCode)]
        [InlineData("certificate-http", ReplayOperation.ClientCertificateHttpsCode)]
        [InlineData("zone", ReplayDestination.ScopedIpv6Code)]
        [InlineData("numeric", ReplayOperation.WindowsAuthOriginInvalidCode)]
        [InlineData("certificate-zone", ReplayDestination.ScopedIpv6Code)]
        [InlineData("origins", ReplayOperation.WindowsAuthOriginCountCode)]
        [InlineData("certificate-origins", ReplayOperation.ClientCertificateOriginCountCode)]
        [InlineData("http2", ReplayOperation.WindowsAuthHttp2Code)]
        [InlineData("authorization", ReplayOperation.WindowsAuthAuthorizationCode)]
        [InlineData("authorization-empty", ReplayOperation.WindowsAuthAuthorizationCode)]
        [InlineData("authorization-repeated", ReplayOperation.WindowsAuthAuthorizationCode)]
        [InlineData("authorization-nominated", ReplayOperation.WindowsAuthAuthorizationCode)]
        [InlineData("connection-only", ReplayOperation.WindowsAuthPolicyCode)]
        public async Task HowTheRequestsMayReachTheServer_IsCheckedFirst_InTheContractsOrder(string shape, string code)
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client);
            var setups = new List<ReplaySenderSetup>();
            Assert.True(ReplayClientCertificate.TryParse(client.Thumbprint, out ReplayClientCertificate certificate));
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:443", out ReplayConnectTo endpoint));
            Assert.True(ReplayProxy.TryParse("http://127.0.0.1:3128", out ReplayProxy proxy));
            bool withCertificate = shape.StartsWith("certificate-", StringComparison.Ordinal);
            var transport = new ReplayTransportOptions(
                shape is "http2" or "http2-http" ? ReplayHttpVersion.Http2 : ReplayHttpVersion.Auto,
                shape is "connect-to" or "proxy-connect-to" ? endpoint : null,
                shape.EndsWith("proxy", StringComparison.Ordinal) || shape == "proxy-connect-to" ? proxy : null,
                withCertificate ? certificate : null,
                WindowsAuth: true);
            ReplaySource source = shape switch
            {
                "origins" or "certificate-origins" => ReplaySource.FromCaptures(new[] { Row(1, Url), Row(2, "https://b.test:8443/item") }),
                _ => ReplaySource.FromDraft(new RequestReplayDraft { Url = UrlOf(shape), HeadersText = HeadersOf(shape) }),
            };

            OperationResult<ReplayResult> result = await Operation(native, setups).ExecuteAsync(
                new ReplayRequest(source, new ReplayOptions(AllowConnectTo: true, AllowProxy: true, Transport: transport)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Empty(native.Calls);
            Assert.Empty(setups);
        }

        // The consent names the one origin that Windows authentication may answer: without it, or for another origin, the
        // run is refused; with a client certificate as well, each needs its own consent for the run's origin, the
        // certificate's checked first, and neither refusal opens the store.
        [Theory]
        [InlineData(false, null, null, ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData(false, "https://a.test:9443", null, ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData(false, "https://b.test:8443", null, ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData(false, "https://a.test.:8443", null, ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData(true, "https://a.test:8443", null, ReplayOperation.ClientCertificatePolicyCode)]
        [InlineData(true, null, null, ReplayOperation.ClientCertificatePolicyCode)]
        [InlineData(true, null, "https://a.test:8443", ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData(true, "https://b.test:8443", "https://a.test:8443", ReplayOperation.WindowsAuthPolicyCode)]
        public async Task WithoutConsentForTheRunsOrigin_NothingIsResolvedOrSent(
            bool withCertificate, string windowsConsent, string certificateConsent, string code)
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client);
            var setups = new List<ReplaySenderSetup>();
            Assert.True(ReplayClientCertificate.TryParse(client.Thumbprint, out ReplayClientCertificate certificate));

            OperationResult<ReplayResult> result = await Operation(native, setups).ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }),
                    new ReplayOptions(
                        Transport: new ReplayTransportOptions(ClientCertificate: withCertificate ? certificate : null, WindowsAuth: true),
                        AllowClientCertificate: certificateConsent == null ? null : Origin(certificateConsent),
                        AllowWindowsAuth: windowsConsent == null ? null : Origin(windowsConsent))),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Empty(native.Calls);
            Assert.Empty(setups);
        }

        // An allowed run is previewed and sent at exactly HTTP/1.1, whether its version was chosen automatically or asked
        // for, through a sender of its own made for Windows authentication and the consented origin; the version of the
        // response is reported and recorded as it came, here HTTP/1.0, and never changes the request's, which is recorded as
        // the HTTP/1.1 that it was sent with.
        [Theory]
        [InlineData("Auto")]
        [InlineData("Http11")]
        public async Task AnAllowedRun_GetsASenderOfItsOwn_ForTheOrigin_AtExactlyHttp11(string version)
        {
            var setups = new List<ReplaySenderSetup>();
            var seen = new List<(Version, HttpVersionPolicy)>();
            var sink = new Sink();
            var operation = new ReplayOperation
            {
                SenderFactory = () => throw new InvalidOperationException("A Windows-authentication run has a sender of its own."),
                OwnSenderFactory = setup =>
                {
                    lock (setups)
                    {
                        setups.Add(setup);
                    }

                    return new ReplaySender(
                        new HttpMessageInvoker(new Answering(request =>
                        {
                            lock (seen)
                            {
                                seen.Add((request.Version, request.VersionPolicy));
                            }

                            return Ok(HttpVersion.Version10);
                        })),
                        ownsInvoker: true,
                        setup);
                },
            };
            ReplayCredentialOrigin origin = Origin("https://a.test:8443");
            var options = new ReplayOptions(
                Transport: new ReplayTransportOptions(Enum.Parse<ReplayHttpVersion>(version), WindowsAuth: true), AllowWindowsAuth: origin);
            ReplaySource source = ReplaySource.FromDraft(new RequestReplayDraft { Url = Url });

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source, options);
            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(source, options, Sink: sink), OperationContext.None, CancellationToken.None);

            Assert.True(preview.Succeeded);
            Assert.Equal(
                (ReplayHttpVersion.Http11, true, origin),
                (preview.Value.Transport.HttpVersion, preview.Value.UsesWindowsAuth, preview.Value.CredentialOrigin));
            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.Equal(
                (ReplayOutcome.Completed, "1.1", "1.0", true, false),
                (item.Outcome, item.HttpVersion, item.ResponseVersion, item.WindowsAuthConfigured, item.ClientCertificateConfigured));
            ReplayExchange exchange = Assert.Single(sink.Sent).Exchange;
            Assert.Equal(("GET /item HTTP/1.1", "HTTP/1.0 200 OK"), (exchange.Request.StartLine, exchange.Response.StartLine));
            ReplaySenderSetup used = Assert.Single(setups);
            Assert.Equal((true, origin, null, null), (used.WindowsAuth, used.CredentialOrigin, used.Proxy, used.ConnectTo));
            Assert.Equal(new[] { (HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact) }, seen);
        }

        // The credentials answer only the consented origin, in any spelling of it, and only its Negotiate and NTLM
        // challenges, with the default network credentials; anything else gets none, including a URL that has no origin,
        // such as one whose host IDNA refuses (a capital palochka) although Uri writes its host as the consented one's.
        [Theory]
        [InlineData(Bucher, "https://bücher.example:8443/a", "Negotiate", true)]
        [InlineData(Bucher, "https://BÜCHER.example:8443/b?q=1", "NTLM", true)]
        [InlineData(Bucher, "https://xn--bcher-kva.example:8443/", "negotiate", true)]
        [InlineData(Bucher, "https://bücher.example:8443/", "Basic", false)]
        [InlineData(Bucher, "https://bücher.example:8443/", "Digest", false)]
        [InlineData(Bucher, "https://bücher.example:8443/", "Kerberos", false)]
        [InlineData(Bucher, "https://bücher.example:8443/", null, false)]
        [InlineData(Bucher, "https://bücher.example/", "Negotiate", false)]
        [InlineData(Bucher, "http://bücher.example:8443/", "Negotiate", false)]
        [InlineData(Bucher, "https://bücher.example.:8443/", "Negotiate", false)]
        [InlineData(Bucher, "https://other.example:8443/", "Negotiate", false)]
        [InlineData("https://а\u04CF.example", "https://а\u04CF.example/", "Negotiate", true)]
        [InlineData("https://а\u04CF.example", "https://а\u04C0.example/", "Negotiate", false)]
        public void TheCredentials_AnswerOnlyTheConsentedOriginsNegotiateAndNtlm(string consent, string url, string scheme, bool answered)
        {
            var credentials = new ReplayWindowsCredentials(Origin(consent));

            NetworkCredential credential = credentials.GetCredential(new Uri(url), scheme);

            Assert.Equal(answered, credential != null);
            if (answered)
            {
                Assert.Same(CredentialCache.DefaultNetworkCredentials, credential);
            }

            Assert.Null(credentials.GetCredential(null, "Negotiate"));
            Assert.Null(credentials.GetCredential(new Uri("/item", UriKind.Relative), "Negotiate"));
            Assert.Throws<ArgumentNullException>(() => new ReplayWindowsCredentials(null));
        }

        // The handler of a Windows-authentication sender answers only for its origin, never ahead of a challenge, never
        // through a proxy or to another endpoint; a direct handler has no credentials. A sender's setup holds one origin:
        // with a client certificate too, both must have been allowed for it, and a transport without Windows
        // authentication takes none from its consent.
        [Fact]
        public void TheHandler_IsMadeForTheOneOrigin_AndNothingElse()
        {
            ReplayCredentialOrigin origin = Origin("https://a.test");
            Assert.True(ReplayProxy.TryParse("http://127.0.0.1:3128", out ReplayProxy proxy));
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:443", out ReplayConnectTo endpoint));
            using X509Certificate2 client = _authority.IssueClient();
            using var credential = new ReplayClientCredential(new RecordingCertificateNative(), client, origin);
            var windows = new ReplayTransportOptions(WindowsAuth: true);

            using SocketsHttpHandler handler = ReplaySender.CreateHandler(new ReplaySenderSetup(CredentialOrigin: origin, WindowsAuth: true));
            using SocketsHttpHandler direct = ReplaySender.CreateHandler(ReplaySenderSetup.Direct);
            ReplaySenderSetup setup = ReplaySenderSetup.For(windows, windowsAuthOrigin: origin);
            ReplaySenderSetup both = ReplaySenderSetup.For(windows, credential, windowsAuthOrigin: Origin("https://A.test:443/"));

            Assert.IsType<ReplayWindowsCredentials>(handler.Credentials);
            Assert.Equal((false, false), (handler.PreAuthenticate, handler.UseProxy));
            Assert.NotNull(handler.Credentials.GetCredential(new Uri("https://A.test/x"), "Negotiate"));
            Assert.Null(handler.Credentials.GetCredential(new Uri("https://b.test/x"), "Negotiate"));
            Assert.Null(direct.Credentials);
            Assert.Throws<ArgumentException>(() => ReplaySender.CreateHandler(new ReplaySenderSetup(WindowsAuth: true)));
            Assert.Throws<ArgumentException>(
                () => ReplaySender.CreateHandler(new ReplaySenderSetup(Proxy: proxy, CredentialOrigin: origin, WindowsAuth: true)));
            Assert.Throws<ArgumentException>(
                () => ReplaySender.CreateHandler(new ReplaySenderSetup(ConnectTo: endpoint, CredentialOrigin: origin, WindowsAuth: true)));
            Assert.Equal((true, origin), (setup.WindowsAuth, setup.CredentialOrigin));
            Assert.Equal((true, origin, client), (both.WindowsAuth, both.CredentialOrigin, both.ClientCertificate));
            Assert.Throws<ArgumentException>(() => ReplaySenderSetup.For(windows, credential, windowsAuthOrigin: Origin("https://b.test")));
            Assert.Null(ReplaySenderSetup.For(new ReplayTransportOptions(), windowsAuthOrigin: origin).CredentialOrigin);
        }

        // After each hop, whatever the HTTP stack put in the live message for Windows authentication is removed, also from a
        // response's own request message; only the scheme label is kept, as "Negotiate" or "NTLM", and nothing else, and
        // the request is recorded as replay built it.
        [Theory]
        [InlineData("Negotiate " + Canary, "Negotiate")]
        [InlineData("NTLM " + Canary, "NTLM")]
        [InlineData("nEgOtIaTe " + Canary, "Negotiate")]
        [InlineData("Basic " + Canary, null)]
        [InlineData(null, null)]
        public async Task AfterEachHop_TheLiveMessageLosesItsAuthorization_AndOnlyTheLabelIsKept(string added, string label)
        {
            HttpRequestMessage live = null;
            using var other = new HttpRequestMessage();
            var answering = new Answering(request =>
            {
                live = request;
                if (added != null)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", added);
                }

                other.Headers.TryAddWithoutValidation("Authorization", "Negotiate " + Canary);
                HttpResponseMessage response = Ok(HttpVersion.Version11);
                response.RequestMessage = other;
                return response;
            });
            using ReplaySender sender = WindowsAuthSender(answering);

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = Url }, Options(), CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, label), (exchange.Outcome, exchange.AuthHeaderSchemeObserved));
            Assert.False(live.Headers.Contains("Authorization"), "The live request kept its Authorization header.");
            Assert.False(other.Headers.Contains("Authorization"), "The response's request message kept its Authorization header.");
            Assert.False(Text(exchange.Request).Contains(Canary, StringComparison.Ordinal), "The recorded request holds the token.");
        }

        // A Windows-authentication send that fails, whatever failed it, keeps no exception, only its code and the label of
        // its hop, and its live message loses its Authorization too; a direct send that fails keeps its exception, as it
        // always did, and no label.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AFailedWindowsAuthSend_KeepsNoException(bool httpFailure)
        {
            Exception Failing(string text) => httpFailure ? new HttpRequestException(text) : new InvalidOperationException(text);
            HttpRequestMessage live = null;
            using ReplaySender sender = WindowsAuthSender(new Answering(request =>
            {
                live = request;
                request.Headers.TryAddWithoutValidation("Authorization", "NTLM " + Canary);
                throw Failing("failed " + Canary);
            }));
            using var direct = new ReplaySender(
                new HttpMessageInvoker(new Answering(_ => throw Failing("direct"))), ownsInvoker: true);

            ReplayExchange exchange = await sender.SendAsync(new RequestReplayDraft { Url = Url }, Options(), CancellationToken.None);
            ReplayExchange failed = await direct.SendAsync(new RequestReplayDraft { Url = Url }, Options(), CancellationToken.None);

            Assert.Equal(
                (ReplayOutcome.Failed, ReplaySender.SendFailedCode, "NTLM", true),
                (exchange.Outcome, exchange.Code, exchange.AuthHeaderSchemeObserved, exchange.Error == null));
            Assert.False(live.Headers.Contains("Authorization"), "The failed request kept its Authorization header.");
            Assert.Equal((ReplayOutcome.Failed, true, null), (failed.Outcome, failed.Error != null, failed.AuthHeaderSchemeObserved));
        }

        // The label is the final hop's own: a hop whose request carried none after one that did reports none. A redirect is
        // built from what replay made, so it never carries the token that the stack added to the hop before it.
        [Fact]
        public async Task TheLabel_IsTheFinalHopsOwn_AndARedirectCarriesNoStaleToken()
        {
            var arrived = new List<bool>();
            int calls = 0;
            using ReplaySender sender = WindowsAuthSender(new Answering(request =>
            {
                lock (arrived)
                {
                    arrived.Add(request.Headers.NonValidated.Contains("Authorization"));
                }

                if (Interlocked.Increment(ref calls) > 1)
                {
                    return Ok(HttpVersion.Version11);
                }

                request.Headers.TryAddWithoutValidation("Authorization", "NTLM " + Canary);
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("/next", UriKind.Relative);
                return redirect;
            }));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = Url }, Options(maxRedirects: 1), CancellationToken.None);

            Assert.Equal((200, 1, null), (exchange.StatusCode, exchange.Redirects, exchange.AuthHeaderSchemeObserved));
            Assert.Equal(new[] { false, false }, arrived);
        }

        // A Windows-authentication send records each WWW-Authenticate and Proxy-Authenticate value with its Negotiate,
        // NTLM and Kerberos challenges reduced to their schemes, leaves out a value that is not a challenge list, and keeps
        // every other header as it came; a direct send records them all as they came.
        [Fact]
        public async Task TheResponsesChallenges_AreRecordedWithoutTheirTokens_InWindowsAuthRunsOnly()
        {
            static HttpResponseMessage Challenges(HttpRequestMessage request)
            {
                HttpResponseMessage response = Ok(HttpVersion.Version11);
                response.StatusCode = HttpStatusCode.Unauthorized;
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Negotiate " + Canary);
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Basic realm=\"r\", NTLM " + Canary);
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Negotiate \"" + Canary);
                response.Headers.TryAddWithoutValidation("Proxy-Authenticate", "Kerberos " + Canary);
                response.Headers.TryAddWithoutValidation("X-Other", Canary);
                return response;
            }

            using ReplaySender sender = WindowsAuthSender(new Answering(Challenges));
            using var direct = new ReplaySender(new HttpMessageInvoker(new Answering(Challenges)), ownsInvoker: true);

            ReplayExchange windows = await sender.SendAsync(new RequestReplayDraft { Url = Url }, Options(), CancellationToken.None);
            ReplayExchange plain = await direct.SendAsync(new RequestReplayDraft { Url = Url }, Options(), CancellationToken.None);

            Assert.Equal(new[] { "Negotiate", "Basic realm=\"r\", NTLM" }, Values(windows.Response, "WWW-Authenticate"));
            Assert.Equal(new[] { "Kerberos" }, Values(windows.Response, "Proxy-Authenticate"));
            Assert.Equal(new[] { Canary }, Values(windows.Response, "X-Other"));
            Assert.Equal(3, Values(plain.Response, "WWW-Authenticate").Length);
            Assert.All(Values(plain.Response, "WWW-Authenticate"), value => Assert.Contains(Canary, value, StringComparison.Ordinal));
        }

        // An allowed run answers its origin's Negotiate or NTLM challenge on one connection, as the server observes it
        // request by request (no Authorization, then the label twice), at exactly HTTP/1.1, and reports the label. Nothing
        // that it records holds a token or the server's final Negotiate tail: not the request or response, nor its archive
        // session, whose flags say only that Windows authentication was configured.
        [Theory]
        [InlineData("Negotiate", "Negotiate", "Negotiate")]
        [InlineData("NTLM", "NTLM", "NTLM")]
        [InlineData("Negotiate|NTLM", "Negotiate", "Negotiate")]
        public async Task AnAllowedRun_AnswersTheOriginsChallenge_AndRecordsNoToken(string offered, string package, string label)
        {
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackAuthServer(
                new LoopbackTls(certificate), new LoopbackAuthOptions(offered.Split('|'), package, FinalTail: "Negotiate " + Canary));

            (ReplayItemResult item, ReplayExchange exchange) = await RunAsync(server.Origin + "/ok", Origin(server.Origin));

            Assert.Equal(
                (ReplayOutcome.Completed, 200, "1.1", true, label),
                (item.Outcome, item.StatusCode, item.HttpVersion, item.WindowsAuthConfigured, item.AuthHeaderSchemeObserved));
            Assert.Equal(new (int, string)[] { (1, null), (1, label), (1, label) }, server.Requests.Select(seen => (seen.Connection, seen.AuthScheme)));
            Assert.Equal(new[] { (1, "NTLM/NTLM") }, server.Authenticated);
            Assert.Equal(new[] { "Negotiate" }, Values(exchange.Response, "WWW-Authenticate"));
            SazSession session = ReplaySaz.ToSession(item, exchange);
            string[] recorded =
            {
                Text(exchange.Request),
                Text(exchange.Response),
                string.Join("\n", session.Request.Fields.Concat(session.Response.Fields).Concat(session.Flags)),
            };
            Assert.All(recorded, text => Assert.False(text.Contains(Canary, StringComparison.Ordinal), "A recorded message holds the tail."));
            Assert.False(recorded[0].Contains("Authorization", StringComparison.OrdinalIgnoreCase), "The recorded request has an Authorization header.");
            Assert.Contains(new KeyValuePair<string, string>(ReplaySaz.WindowsAuthFlag, "true"), session.Flags);
        }

        // Without consent for the server's origin, or with consent for another, the run is refused and the server never
        // sees a connection; so is HTTP/2 only.
        [Theory]
        [InlineData("none", ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData("other-port", ReplayOperation.WindowsAuthPolicyCode)]
        [InlineData("http2", ReplayOperation.WindowsAuthHttp2Code)]
        public async Task ARefusedRun_NeverReachesTheServer(string shape, string code)
        {
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackAuthServer(new LoopbackTls(certificate), new LoopbackAuthOptions(new[] { "Negotiate" }, "Negotiate"));
            var operation = new ReplayOperation { ServerChainPolicyFactory = _authority.Policy };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = server.Origin + "/ok" }),
                    new ReplayOptions(
                        Transport: new ReplayTransportOptions(shape == "http2" ? ReplayHttpVersion.Http2 : ReplayHttpVersion.Auto, WindowsAuth: true),
                        AllowWindowsAuth: shape switch
                        {
                            "none" => null,
                            "other-port" => Origin("https://127.0.0.1:" + (server.Port == 1 ? 2 : 1)),
                            _ => Origin(server.Origin),
                        })),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(0, server.Connections);
        }

        // A Basic challenge, and a 407 with proxy challenges from the server itself, are never answered: no request carries
        // Authorization, the challenge is the result, and its Windows challenges are recorded without their tokens.
        [Theory]
        [InlineData("basic")]
        [InlineData("proxy")]
        public async Task OtherChallenges_AreNeverAnswered(string shape)
        {
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackAuthServer(
                new LoopbackTls(certificate),
                shape == "basic"
                    ? new LoopbackAuthOptions(new[] { "Basic realm=\"x\"" }, Package: null)
                    : new LoopbackAuthOptions(new[] { "Negotiate" }, "Negotiate", ProxyChallenge: new[] { "Negotiate " + Canary, "Basic realm=\"p\"" }));

            (ReplayItemResult item, ReplayExchange exchange) = await RunAsync(server.Origin + "/ok", Origin(server.Origin));

            Assert.Equal((shape == "basic" ? 401 : 407, null), (item.StatusCode, item.AuthHeaderSchemeObserved));
            Assert.NotEmpty(server.Requests);
            Assert.All(server.Requests, seen => Assert.Null(seen.AuthScheme));
            Assert.Empty(server.Authenticated);
            Assert.Equal(
                shape == "basic" ? new[] { "Basic realm=\"x\"" } : new[] { "Negotiate", "Basic realm=\"p\"" },
                Values(exchange.Response, shape == "basic" ? "WWW-Authenticate" : "Proxy-Authenticate"));
        }

        // Windows authentication, alone or with a client certificate, over TLS 1.2 and 1.3, stays in its origin. A redirect
        // within the origin is followed on the connection that authenticated, without a token. One to the same host on
        // another port, or to another host, both servers that would ask for a certificate and answer Windows
        // authentication, is not followed, and that server never sees a connection. The origin observes the certificate,
        // when the run has one, and the Windows exchange on the same connection; a direct send afterwards arrives on a new
        // connection, with neither, and gets the challenge. Each case has a loopback address of its own: a client that may
        // resume TLS sessions offers one that an earlier case made with the same name, and a TLS 1.3-only server answers a
        // TLS 1.2 session with a ProtocolVersion alert (observed).
        [Theory]
        [MemberData(nameof(Containment))]
        public async Task WindowsAuth_AloneOrWithACertificate_StaysInItsOrigin_AndADirectSendAfterwardsCarriesNeither(
            SslProtocols protocol, bool withCertificate, string target)
        {
            IPAddress address = IPAddress.Parse(
                $"127.0.{(protocol == SslProtocols.Tls12 ? 12 : 13)}.{(withCertificate ? 20 : 10) + Array.IndexOf(Targets, target)}");
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.Issue(address: address);
            using X509Certificate2 ipv6Certificate = _authority.IssueServer("::1", Array.Empty<string>(), new[] { IPAddress.IPv6Loopback });
            var tls = new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Optional, SslProtocols: protocol);
            var answering = new LoopbackAuthOptions(new[] { "Negotiate" }, "Negotiate");
            await using var elsewhere = target == "other-host"
                ? new LoopbackAuthServer(tls with { Certificate = ipv6Certificate }, answering, IPAddress.IPv6Loopback)
                : new LoopbackAuthServer(tls, answering, address);
            string location = target == "same-origin" ? "/b" : elsewhere.Origin + "/b";
            await using var origin = new LoopbackAuthServer(
                tls, answering with { Redirects = new Dictionary<string, string> { ["/a"] = location } }, address);
            using var direct = new ReplaySender(new ReplaySenderSetup(ChainPolicyFactory: _authority.Policy));

            (ReplayItemResult item, _) = await RunAsync(
                origin.Origin + "/a", Origin(origin.Origin), maxRedirects: 3, client: withCertificate ? client : null);
            ReplayExchange after = await direct.SendAsync(
                new RequestReplayDraft { Url = origin.Origin + "/a" }, Options(), CancellationToken.None);

            bool followed = target == "same-origin";
            Assert.Equal(
                followed ? (200, 1, (string)null, (string)null) : (307, 0, ReplaySender.RedirectOriginChangeCode, "Negotiate"),
                (item.StatusCode, item.Redirects, item.Code, item.AuthHeaderSchemeObserved));
            var expected = new List<(int, string, string)> { (1, "/a", null), (1, "/a", "Negotiate"), (1, "/a", "Negotiate") };
            if (followed)
            {
                expected.Add((1, "/b", null));
            }

            expected.Add((2, "/a", null));
            Assert.Equal(expected, origin.Requests.Select(seen => (seen.Connection, seen.Target, seen.AuthScheme)));
            Assert.Equal(new[] { (1, "NTLM/NTLM") }, origin.Authenticated);
            Assert.Equal(withCertificate ? new[] { (1, client.Thumbprint) } : Array.Empty<(int, string)>(), origin.ClientCertificates);
            Assert.Equal(401, after.StatusCode);
            Assert.Equal(0, elsewhere.Connections);
        }

        // Sends one GET with Windows authentication allowed for consent, and with a client certificate for the URL's
        // origin when given one, through the production sender trusting the test authority.
        private async Task<(ReplayItemResult Item, ReplayExchange Exchange)> RunAsync(
            string url, ReplayCredentialOrigin consent, int maxRedirects = 0, X509Certificate2 client = null)
        {
            var sink = new Sink();
            var operation = new ReplayOperation { ServerChainPolicyFactory = _authority.Policy };
            ReplayClientCertificate certificate = null;
            if (client != null)
            {
                operation.CertificateNative = new RecordingCertificateNative(client);
                Assert.True(ReplayClientCertificate.TryParse(client.Thumbprint, out certificate));
            }

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = url }),
                    new ReplayOptions(
                        Timeout: TimeSpan.FromSeconds(20),
                        MaxRedirects: maxRedirects,
                        Transport: new ReplayTransportOptions(ClientCertificate: certificate, WindowsAuth: true),
                        AllowClientCertificate: client == null ? null : ReplayCredentialOrigin.Of(new Uri(url)),
                        AllowWindowsAuth: consent),
                    Sink: sink),
                OperationContext.None,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
            return Assert.Single(sink.Sent);
        }

        private static ReplayOperation Operation(RecordingCertificateNative native, List<ReplaySenderSetup> setups) => new()
        {
            CertificateNative = native,
            SenderFactory = () => throw new InvalidOperationException("A Windows-authentication run has a sender of its own."),
            OwnSenderFactory = setup =>
            {
                lock (setups)
                {
                    setups.Add(setup);
                }

                return new ReplaySender(new HttpMessageInvoker(new Answering(_ => Ok(HttpVersion.Version11))), ownsInvoker: true, setup);
            },
        };

        private static ReplaySender WindowsAuthSender(HttpMessageHandler handler) =>
            new(new HttpMessageInvoker(handler), ownsInvoker: true, new ReplaySenderSetup(CredentialOrigin: Origin("https://a.test:8443"), WindowsAuth: true));

        private static ReplaySendOptions Options(int maxRedirects = 0) =>
            new(TimeSpan.FromSeconds(20), ReplaySendOptions.DefaultMaxResponseBytes, maxRedirects);

        private static string UrlOf(string shape) => shape switch
        {
            "http" or "http2-http" or "certificate-http" => "http://a.test:8443/item",
            "zone" or "certificate-zone" => "https://[fe80::1%253]/item",
            "numeric" => "https://127\u30021/item",
            _ => Url,
        };

        private static string HeadersOf(string shape) => shape switch
        {
            "authorization" => "Authorization: Basic eDp5",
            "authorization-empty" => "authorization:",
            "authorization-repeated" => "Authorization: a\r\nAUTHORIZATION: b",
            "authorization-nominated" => "Connection: Authorization\r\nAuthorization: Basic eDp5",
            "connection-only" => "Connection: Authorization",
            _ => "",
        };

        private static ReplayCredentialOrigin Origin(string text)
        {
            Assert.True(ReplayCredentialOrigin.TryParse(text, out ReplayCredentialOrigin origin));
            return origin;
        }

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        private static HttpResponseMessage Ok(Version version) =>
            new(HttpStatusCode.OK) { Version = version, Content = new ByteArrayContent("ok"u8.ToArray()) };

        private static string Text(ReplayMessage message) => Encoding.Latin1.GetString(message.ToBytes());

        private static string[] Values(ReplayMessage message, string name) =>
            message.Headers.Concat(message.ContentHeaders)
                .Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
                .ToArray();

        // Answers every request in memory as answer says; an answer that throws fails the request as an HTTP stack would.
        private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                try
                {
                    return Task.FromResult(answer(request));
                }
                catch (HttpRequestException ex)
                {
                    return Task.FromException<HttpResponseMessage>(ex);
                }
            }
        }

        private sealed class Sink : IReplayExchangeSink
        {
            private readonly List<(ReplayItemResult, ReplayExchange)> _sent = new();

            internal IReadOnlyList<(ReplayItemResult Item, ReplayExchange Exchange)> Sent => _sent;

            public void Add(ReplayItemResult result, ReplayExchange exchange) => _sent.Add((result, exchange));
        }
    }
}
