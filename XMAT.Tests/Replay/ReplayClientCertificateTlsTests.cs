// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // A client certificate on the wire (slice K2), as loopback servers observe it over TLS 1.2 and 1.3: it is offered only
    // to a server that asks for one and that passes, before the certificate leaves, the validation that the handler makes
    // anyway (the run's trust, server authentication and the native name check, for the consented origin's host), never
    // while the server's certificate is unknown, never through a resumed session, and never to another origin by a redirect.
    // The certificate is resolved by the production calls from a PFX-imported key; only the store is the test's list. The
    // servers' certificates come from an in-memory authority that only the run's trust includes; no store is ever written.
    public sealed class ReplayClientCertificateTlsTests : IDisposable
    {
        private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
        private readonly TestCertificateAuthority _authority = new();

        public static TheoryData<SslProtocols> Protocols => new() { SslProtocols.Tls12, SslProtocols.Tls13 };

        public void Dispose() => _authority.Dispose();

        // A server that this PC accepts for the origin receives the run's certificate when it asks for one, whether it
        // requires one or only asks; one that does not ask receives none, and the send goes on.
        [Theory]
        [InlineData(SslProtocols.Tls12, "Required", true)]
        [InlineData(SslProtocols.Tls13, "Required", true)]
        [InlineData(SslProtocols.Tls12, "Optional", true)]
        [InlineData(SslProtocols.Tls13, "Optional", true)]
        [InlineData(SslProtocols.Tls12, "NotAsked", false)]
        [InlineData(SslProtocols.Tls13, "NotAsked", false)]
        public async Task AnAcceptedServerThatAsks_ReceivesTheCertificate(
            SslProtocols protocol, string asks, bool delivered)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate, ClientCertificate: Enum.Parse<LoopbackClientCertificate>(asks), SslProtocols: protocol));

            ReplayItemResult item = await SendAsync(client, server.Origin + "/");

            Assert.Equal((ReplayOutcome.Completed, 200), (item.Outcome, item.StatusCode));
            Assert.Equal(delivered ? new[] { client.Thumbprint } : Array.Empty<string>(), server.ClientCertificates);
        }

        // A server whose certificate this PC does not trust, or that names another host, or that is valid only for client
        // authentication, never receives it: the check withholds it, then the handler's own validation refuses the server.
        [Theory]
        [InlineData(SslProtocols.Tls12, "untrusted")]
        [InlineData(SslProtocols.Tls13, "untrusted")]
        [InlineData(SslProtocols.Tls12, "other-name")]
        [InlineData(SslProtocols.Tls13, "other-name")]
        [InlineData(SslProtocols.Tls12, "client-auth-only")]
        [InlineData(SslProtocols.Tls13, "client-auth-only")]
        public async Task AServerThatThisPcDoesNotAcceptForTheOrigin_NeverReceivesIt(SslProtocols protocol, string kind)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using var stranger = new TestCertificateAuthority();
            using X509Certificate2 certificate = kind switch
            {
                "untrusted" => stranger.Issue(address: IPAddress.Loopback),
                "other-name" => _authority.Issue(address: IPAddress.Parse("127.0.0.2")),
                _ => _authority.IssueServer(
                    "127.0.0.1", Array.Empty<string>(), new[] { IPAddress.Loopback }, TestCertificateAuthority.ClientAuthenticationOid),
            };
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Optional, SslProtocols: protocol));

            ReplayItemResult item = await SendAsync(client, server.Origin + "/");

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (item.Outcome, item.Code));
            Assert.Empty(server.ClientCertificates);
            Assert.Empty(server.Requests);
        }

        // A server that sends an intermediate with its certificate is accepted, so it receives the certificate.
        [Theory]
        [MemberData(nameof(Protocols))]
        public async Task AServerSendingAnIntermediate_ReceivesIt(SslProtocols protocol)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 intermediate = _authority.CreateIntermediate();
            using X509Certificate2 certificate = _authority.IssueServer(
                "127.0.0.1", Array.Empty<string>(), new[] { IPAddress.Loopback }, issuer: intermediate);
            using X509Certificate2 sent = X509CertificateLoader.LoadCertificate(intermediate.RawData);
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"),
                new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Required, SslProtocols: protocol, Intermediates: new X509Certificate2Collection(sent)));

            ReplayItemResult item = await SendAsync(client, server.Origin + "/");

            Assert.Equal((ReplayOutcome.Completed, 200), (item.Outcome, item.StatusCode));
            Assert.Equal(new[] { client.Thumbprint }, server.ClientCertificates);
        }

        // With a connect-to endpoint, the server must still prove that it is the URL's host before the certificate leaves.
        [Theory]
        [MemberData(nameof(Protocols))]
        public async Task ThroughAConnectToEndpoint_TheUrlsHostIsStillChecked(SslProtocols protocol)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.IssueServer("xmat-k2.test", new[] { "xmat-k2.test" });
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Required, SslProtocols: protocol));

            ReplayItemResult named = await SendAsync(client, $"https://xmat-k2.test:{server.Port}/", Endpoint(server.Port));
            ReplayItemResult other = await SendAsync(client, $"https://other.test:{server.Port}/", Endpoint(server.Port));

            Assert.Equal((ReplayOutcome.Completed, 200, ReplayTransportOptions.ConnectToName), (named.Outcome, named.StatusCode, named.Transport));
            Assert.Equal(ReplayOutcome.Failed, other.Outcome);
            Assert.Equal(new[] { client.Thumbprint }, server.ClientCertificates);
        }

        // The certificate reaches a server exactly when the handler's own validation accepts it, for every name shape that
        // the native name check and MatchesHostname judge differently (ka1): '*.test' is withheld, a partial wildcard and an
        // IP address written as a DNS name are delivered.
        [Theory]
        [MemberData(nameof(NameShapes))]
        public async Task EveryNameShape_GetsTheCertificate_ExactlyWhenTheHandlerAcceptsTheServer(
            SslProtocols protocol, string commonName, string dnsNames, string address, string host, bool delivered)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.IssueServer(
                commonName,
                dnsNames.Split(',', StringSplitOptions.RemoveEmptyEntries),
                address == null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(address) });
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Optional, SslProtocols: protocol));

            ReplayItemResult item = await SendAsync(client, $"https://{host}:{server.Port}/", Endpoint(server.Port));

            Assert.Equal(delivered ? ReplayOutcome.Completed : ReplayOutcome.Failed, item.Outcome);
            Assert.Equal(delivered ? new[] { client.Thumbprint } : Array.Empty<string>(), server.ClientCertificates);
        }

        public static TheoryData<SslProtocols, string, string, string, string, bool> NameShapes()
        {
            var shapes = new (string CommonName, string DnsNames, string Address, string Host, bool Delivered)[]
            {
                ("x", "a.example.test", null, "a.example.test", true),
                ("x", "*.example.test", null, "a.example.test", true),
                ("x", "*.example.test", null, "b.a.example.test", false),
                ("x", "*.example.test", null, "example.test", false),
                ("x", "a*.example.test", null, "ab.example.test", true),
                ("x", "*.test", null, "a.test", false),
                ("a.example.test", "", null, "a.example.test", true),
                ("a.example.test", "other.test", null, "a.example.test", false),
                ("x", "", "127.0.0.1", "127.0.0.1", true),
                ("x", "127.0.0.1", null, "127.0.0.1", true),
                ("127.0.0.1", "", null, "127.0.0.1", true),
                ("x", "xn--bcher-kva.example", null, "bücher.example", true),
                ("x", "A.EXAMPLE.TEST", null, "a.example.test", true),
                ("x", "a.example.test", null, "a.example.test.", true),
            };
            var data = new TheoryData<SslProtocols, string, string, string, string, bool>();
            foreach (SslProtocols protocol in new[] { SslProtocols.Tls12, SslProtocols.Tls13 })
            {
                foreach ((string commonName, string dnsNames, string address, string host, bool delivered) in shapes)
                {
                    data.Add(protocol, commonName, dnsNames, address, host, delivered);
                }
            }

            return data;
        }

        // A run with a certificate never resumes a session, so when it follows a direct send to the same server, which
        // carries no certificate, it still makes the full handshake in which the server asks and receives the certificate;
        // the direct sends, over the composer's pooled handler, never carry it.
        [Theory]
        [MemberData(nameof(Protocols))]
        public async Task RunsWithAndWithoutTheCertificate_NeverShareASession(SslProtocols protocol)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"), new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Optional, SslProtocols: protocol));
            SocketsHttpHandler pooled = ReplaySender.CreateHandler();
            _authority.Trust(pooled);
            using var direct = new ReplaySender(new HttpMessageInvoker(pooled), ownsInvoker: true);
            var draft = new RequestReplayDraft { Url = server.Origin + "/" };

            ReplayItemResult first = await SendAsync(client, server.Origin + "/");
            ReplayExchange second = await direct.SendAsync(draft, ReplaySendOptions.Default, CancellationToken.None);
            ReplayItemResult third = await SendAsync(client, server.Origin + "/");
            ReplayExchange fourth = await direct.SendAsync(draft, ReplaySendOptions.Default, CancellationToken.None);

            Assert.All(new[] { first.StatusCode, second.StatusCode, third.StatusCode, fourth.StatusCode }, status => Assert.Equal(200, status));
            Assert.Equal(4, server.Requests.Count);
            Assert.Equal(new[] { client.Thumbprint, client.Thumbprint }, server.ClientCertificates);
        }

        // A redirect is followed only within the consented origin: to the same host on another port, or to another host,
        // both servers that would ask for and accept the certificate, nothing connects; within the origin, the redirect is
        // followed and the certificate goes with it.
        [Theory]
        [InlineData(SslProtocols.Tls12, "other-port")]
        [InlineData(SslProtocols.Tls13, "other-port")]
        [InlineData(SslProtocols.Tls12, "other-host")]
        [InlineData(SslProtocols.Tls13, "other-host")]
        [InlineData(SslProtocols.Tls12, "same-origin")]
        [InlineData(SslProtocols.Tls13, "same-origin")]
        public async Task ARedirect_IsFollowedOnlyWithinTheConsentedOrigin(SslProtocols protocol, string target)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.Issue(address: IPAddress.Loopback);
            using X509Certificate2 ipv6Certificate = _authority.IssueServer("::1", Array.Empty<string>(), new[] { IPAddress.IPv6Loopback });
            var tls = new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Required, SslProtocols: protocol);
            await using var elsewhere = target == "other-host"
                ? new LoopbackHttpServer(_ => LoopbackResponse.Ok("elsewhere"), tls with { Certificate = ipv6Certificate }, IPAddress.IPv6Loopback)
                : new LoopbackHttpServer(_ => LoopbackResponse.Ok("elsewhere"), tls);
            string location = target == "same-origin" ? "/b" : elsewhere.Origin + "/b";
            await using var origin = new LoopbackHttpServer(
                request => request.Target == "/a" ? LoopbackResponse.Redirect(307, location) : LoopbackResponse.Ok("b"), tls);

            ReplayItemResult item = await SendAsync(client, origin.Origin + "/a", maxRedirects: 3);

            if (target == "same-origin")
            {
                Assert.Equal((ReplayOutcome.Completed, 200, 1), (item.Outcome, item.StatusCode, item.Redirects));
                Assert.Equal(new[] { client.Thumbprint, client.Thumbprint }, origin.ClientCertificates);
            }
            else
            {
                Assert.Equal((ReplayOutcome.Completed, 307, ReplaySender.RedirectOriginChangeCode), (item.Outcome, item.StatusCode, item.Code));
                Assert.Equal(new[] { client.Thumbprint }, origin.ClientCertificates);
            }

            Assert.Equal(0, elsewhere.Connections);
        }

        // A redirect to the run's host spelled another way that the legacy comparison tells apart (Unicode and punycode,
        // ADJ-1), or to a host whose canonical form differs while its authority matches ignoring case (Greek final sigma,
        // K2-F1), is not followed, in both directions; the server's certificate is valid for both names, so nothing but the
        // origin checks stops the second hop, and the certificate goes only to the first.
        [Theory]
        [InlineData(SslProtocols.Tls12, "bücher.example", "xn--bcher-kva.example")]
        [InlineData(SslProtocols.Tls13, "xn--bcher-kva.example", "bücher.example")]
        [InlineData(SslProtocols.Tls12, "ασ.example", "ας.example")]
        [InlineData(SslProtocols.Tls13, "ας.example", "ΑΣ.example")]
        public async Task ARedirectToAnAliasOfTheHost_IsNotFollowed(SslProtocols protocol, string from, string to)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 certificate = _authority.IssueServer(
                "aliases", new[] { new Uri("https://" + from + "/").IdnHost, new Uri("https://" + to + "/").IdnHost });
            int port = 0;
            await using var server = new LoopbackHttpServer(
                request => request.Target == "/a"
                    ? new LoopbackResponse(307, "Redirect", "Location: " + Utf8($"https://{to}:{Volatile.Read(ref port)}/b") + "\r\n")
                    : LoopbackResponse.Ok("b"),
                new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Required, SslProtocols: protocol));
            Volatile.Write(ref port, server.Port);

            ReplayItemResult item = await SendAsync(client, $"https://{from}:{server.Port}/a", Endpoint(server.Port), maxRedirects: 3);

            Assert.Equal((ReplayOutcome.Completed, 307, ReplaySender.RedirectOriginChangeCode), (item.Outcome, item.StatusCode, item.Code));
            Assert.Equal("/a", Assert.Single(server.Requests).Target);
            Assert.Equal(new[] { client.Thumbprint }, server.ClientCertificates);
        }

        // The handler of a run with a certificate offers nothing up front, never resumes a session, and its callback gives
        // none while the server's certificate is unknown, none to a server that fails the check, and the run's own
        // certificate, the very instance, only to one that passes it, and only while that certificate has its private key.
        [Fact]
        public void TheHandler_OffersTheRunsCertificate_OnlyToAServerThatPasses()
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(client.RawData);
            using X509Certificate2 server = _authority.Issue(address: IPAddress.Loopback);
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(server.RawData);
            ReplayCredentialOrigin origin = Origin("https://127.0.0.1:8443");

            using SocketsHttpHandler handler = ReplaySender.CreateHandler(
                new ReplaySenderSetup(ClientCertificate: client, CredentialOrigin: origin, ChainPolicyFactory: _authority.Policy));
            using SocketsHttpHandler keyless = ReplaySender.CreateHandler(
                new ReplaySenderSetup(ClientCertificate: publicOnly, CredentialOrigin: origin, ChainPolicyFactory: _authority.Policy));
            LocalCertificateSelectionCallback select = handler.SslOptions.LocalCertificateSelectionCallback;

            Assert.Null(handler.SslOptions.ClientCertificates);
            Assert.False(handler.SslOptions.AllowTlsResume);
            Assert.Null(select(this, "127.0.0.1", new X509CertificateCollection(), null, Array.Empty<string>()));
            Assert.Null(select(this, "127.0.0.2", new X509CertificateCollection(), remote, Array.Empty<string>()));
            Assert.Same(client, select(this, "127.0.0.1", new X509CertificateCollection(), remote, Array.Empty<string>()));
            Assert.Null(keyless.SslOptions.LocalCertificateSelectionCallback(
                this, "127.0.0.1", new X509CertificateCollection(), remote, Array.Empty<string>()));
        }

        // A sender without a certificate keeps the handler as it was: nothing selects a certificate, and sessions resume.
        [Fact]
        public void AHandlerWithoutACertificate_SelectsNone()
        {
            using SocketsHttpHandler handler = ReplaySender.CreateHandler();

            Assert.Null(handler.SslOptions.LocalCertificateSelectionCallback);
            Assert.True(handler.SslOptions.AllowTlsResume);
            Assert.Throws<ArgumentException>(() => ReplaySender.CreateHandler(new ReplaySenderSetup(ClientCertificate: _authority.Root)));
        }

        // The check passes only when the native SSL policy was called and its status is 0: a call that failed, a name that
        // does not match, any other status and an exception all withhold the certificate.
        [Theory]
        [InlineData(true, 0u, null, true)]
        [InlineData(false, 0u, null, false)]
        [InlineData(true, 0x800B010Fu, null, false)]
        [InlineData(true, 0x800B0109u, null, false)]
        [InlineData(true, 0x800B0101u, null, false)]
        [InlineData(true, 0u, "throws", false)]
        public void TheCheck_PassesOnlyWhenTheNativePolicyCallSucceedsWithStatusZero(bool called, uint status, string fails, bool passes)
        {
            using X509Certificate2 server = _authority.Issue(address: IPAddress.Loopback);
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(server.RawData);
            var check = new ReplayServerCertificateCheck(
                Origin("https://127.0.0.1"),
                _authority.Policy,
                (_, _) => fails == null ? (called, status) : throw new CryptographicException("A test failed the call."));

            Assert.Equal(passes, check.Passes("127.0.0.1", remote));
        }

        // Every check builds its chain on a policy of its own from the factory, never one that the handler or another check
        // holds; server authentication is added when the policy names no application policy, as SslStream adds it, and a
        // policy that names one is used as it is. The native call gets the chain and the normalized name.
        [Fact]
        public void EveryCheck_UsesAPolicyOfItsOwn()
        {
            using X509Certificate2 server = _authority.Issue(dnsName: "xmat-k2.test");
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(server.RawData);
            var made = new List<X509ChainPolicy>();
            var seen = new List<(X509ChainPolicy Policy, string[] ApplicationPolicies, string Name)>();
            X509ChainPolicy handlers = _authority.Policy();
            var check = new ReplayServerCertificateCheck(
                Origin("https://XMAT-K2.test"),
                () =>
                {
                    X509ChainPolicy policy = _authority.Policy();
                    made.Add(policy);
                    return policy;
                },
                (chain, name) =>
                {
                    seen.Add((chain.ChainPolicy, chain.ChainPolicy.ApplicationPolicy.Cast<Oid>().Select(oid => oid.Value).ToArray(), name));
                    return (true, 0u);
                });

            Assert.True(check.Passes("XMAT-K2.test.", remote));
            Assert.True(check.Passes("xmat-k2.test", remote));

            Assert.Equal(2, made.Count);
            Assert.Equal(made, seen.Select(entry => entry.Policy));
            Assert.DoesNotContain(handlers, made);
            Assert.All(seen, entry => Assert.Equal(new[] { ServerAuthentication }, entry.ApplicationPolicies));
            Assert.Equal(new[] { "XMAT-K2.test", "xmat-k2.test" }, seen.Select(entry => entry.Name));
            Assert.Empty(handlers.ApplicationPolicy);

            using X509Certificate2 clientAuthServer = _authority.IssueServer(
                "xmat-k2.test", new[] { "xmat-k2.test" }, extendedKeyUsage: TestCertificateAuthority.ClientAuthenticationOid);
            using X509Certificate2 clientAuthRemote = X509CertificateLoader.LoadCertificate(clientAuthServer.RawData);
            var clientOnly = new ReplayServerCertificateCheck(
                Origin("https://xmat-k2.test"),
                () =>
                {
                    X509ChainPolicy policy = _authority.Policy();
                    policy.ApplicationPolicy.Add(new Oid(TestCertificateAuthority.ClientAuthenticationOid));
                    return policy;
                },
                (chain, _) =>
                {
                    seen.Add((chain.ChainPolicy, chain.ChainPolicy.ApplicationPolicy.Cast<Oid>().Select(oid => oid.Value).ToArray(), null));
                    return (true, 0u);
                });
            Assert.True(clientOnly.Passes("xmat-k2.test", clientAuthRemote));
            Assert.Equal(new[] { TestCertificateAuthority.ClientAuthenticationOid }, seen[^1].ApplicationPolicies);
        }

        // The check withholds the certificate before any chain or native call when the remote certificate is unknown, the
        // target host is not the consented origin's, or the host has no name.
        [Theory]
        [InlineData("127.0.0.1", false, true)]
        [InlineData("127.0.0.2", true, true)]
        [InlineData("localhost", true, true)]
        [InlineData(null, true, true)]
        [InlineData("", true, true)]
        [InlineData("...", true, true)]
        public void TheCheck_WithholdsBeforeAnyCall_ForAnotherHostOrNoServerCertificate(string targetHost, bool withRemote, bool withholds)
        {
            using X509Certificate2 server = _authority.Issue(address: IPAddress.Loopback);
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(server.RawData);
            int calls = 0;
            var check = new ReplayServerCertificateCheck(
                Origin("https://127.0.0.1:8443"),
                () =>
                {
                    calls++;
                    return _authority.Policy();
                },
                (_, _) => (true, 0u));

            Assert.Equal(!withholds, check.Passes(targetHost, withRemote ? remote : null));
            Assert.Equal(0, calls);
        }

        // The name that the check uses is SslStream's, except where SslStream would use an empty name, keep a name that
        // IDNA refuses or throw: there, there is no name, and the certificate is withheld (F31).
        [Theory]
        [InlineData("a.example.test", "a.example.test")]
        [InlineData("a.example.test.", "a.example.test")]
        [InlineData("A.Example.TEST", "A.Example.TEST")]
        [InlineData("bücher.example", "xn--bcher-kva.example")]
        [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
        [InlineData("a_b.example.test", "a_b.example.test")]
        [InlineData("a b.example.test", "a b.example.test")]
        [InlineData("127.0.0.1", "127.0.0.1")]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("...", null)]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.example.test", null)]
        [InlineData("a..b.example.test", null)]
        [InlineData("xn--a.example", null)]
        [InlineData("a\u2028b.example", null)]
        public void TheCheck_NormalizesTheTargetHostAsSslStream_OrHasNoName(string host, string normalized)
        {
            Assert.Equal(normalized, ReplayServerCertificateCheck.NormalizeHostName(host));
        }

        // Sends one GET with the client certificate, resolved by the production calls from the test's store, and the
        // consent for the URL's origin; through a connect-to endpoint when given one.
        private async Task<ReplayItemResult> SendAsync(
            X509Certificate2 client, string url, ReplayConnectTo connectTo = null, int maxRedirects = 0)
        {
            var operation = new ReplayOperation
            {
                CertificateNative = new RecordingCertificateNative(client),
                ServerChainPolicyFactory = _authority.Policy,
            };
            Assert.True(ReplayClientCertificate.TryParse(client.Thumbprint, out ReplayClientCertificate certificate));
            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = url }),
                    new ReplayOptions(
                        Timeout: TimeSpan.FromSeconds(20),
                        MaxRedirects: maxRedirects,
                        AllowConnectTo: connectTo != null,
                        Transport: new ReplayTransportOptions(ConnectTo: connectTo, ClientCertificate: certificate),
                        AllowClientCertificate: ReplayCredentialOrigin.Of(new Uri(url)))),
                OperationContext.None,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.True(item.ClientCertificateConfigured);
            return item;
        }

        private static ReplayConnectTo Endpoint(int port)
        {
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:" + port, out ReplayConnectTo endpoint));
            return endpoint;
        }

        private static ReplayCredentialOrigin Origin(string text)
        {
            Assert.True(ReplayCredentialOrigin.TryParse(text, out ReplayCredentialOrigin origin));
            return origin;
        }

        // The UTF-8 bytes of text, as the Latin-1 characters that the loopback server writes byte for byte.
        private static string Utf8(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
    }
}
