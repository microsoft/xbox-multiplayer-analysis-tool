// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // One certificate-validation policy for every replay send (ReplayTls): Parsed sends and the check before a client
    // certificate is offered, like Raw sends (RawSenderTlsTests), download no issuer certificate that a server's certificate
    // names and check no revocation, even when a test's policy turns either on, while a server that sends its intermediate
    // certificate is still accepted. Each control shows, on the same certificates and listener, that a download or a
    // revocation check would reach the listener, so no test passes vacuously. Every authority and intermediate is new, so
    // no certificate cached by an earlier test can complete a chain.
    public sealed class ReplayTlsPolicyTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        // The server is openssl s_server, which sends only its leaf (RawSenderTlsTests says why no SChannel server can).
        [OpenSslFact]
        public async Task ParsedSends_NeverDownloadAnIssuer_EvenWhenATestPolicyTurnsDownloadsOn()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 intermediate = authority.CreateIntermediate();
            using var issuers = new RawSenderTlsTests.IssuerListener(intermediate);
            (X509Certificate2 leaf, string keyPem) = RawSenderTlsTests.Leaf(
                intermediate, IPAddress.Parse("127.0.0.15"), issuerUrl: issuers.Url);
            using (leaf)
            {
                using (RawSenderTlsTests.OpenSslServer server =
                    await RawSenderTlsTests.OpenSslServer.StartAsync(IPAddress.Parse("127.0.0.15"), leaf, keyPem))
                {
                    foreach (Func<X509ChainPolicy> trust in new Func<X509ChainPolicy>[] { authority.Policy, () => Permissive(authority) })
                    {
                        using var sender = new ReplaySender(new ReplaySenderSetup(ChainPolicyFactory: trust));

                        ReplayExchange exchange = await sender.SendAsync(
                            new RequestReplayDraft { Url = $"https://127.0.0.15:{server.Port}/" },
                            ReplaySendOptions.Default,
                            CancellationToken.None).WaitAsync(Wait);

                        Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
                        Assert.Equal(0, issuers.Requests);
                    }
                }

                AssertADownloadCompletesTheChain(authority, leaf, issuers);
            }
        }

        // A seam policy that turns revocation checking on is overridden: the leaf names a loopback CRL listener, the send
        // completes, and the listener receives no request.
        [OpenSslFact]
        public async Task ParsedSends_NeverCheckRevocation_EvenWhenATestPolicyTurnsItOn()
        {
            using var authority = new TestCertificateAuthority();
            using var revocation = new RawSenderTlsTests.IssuerListener(authority.Root);
            (X509Certificate2 leaf, string keyPem) = RawSenderTlsTests.Leaf(
                authority.Root, IPAddress.Parse("127.0.0.16"), crlUrl: revocation.Url);
            using (leaf)
            {
                using (RawSenderTlsTests.OpenSslServer server =
                    await RawSenderTlsTests.OpenSslServer.StartAsync(IPAddress.Parse("127.0.0.16"), leaf, keyPem))
                {
                    using var sender = new ReplaySender(new ReplaySenderSetup(ChainPolicyFactory: () => Permissive(authority)));

                    ReplayExchange exchange = await sender.SendAsync(
                        new RequestReplayDraft { Url = $"https://127.0.0.16:{server.Port}/" },
                        ReplaySendOptions.Default,
                        CancellationToken.None).WaitAsync(Wait);

                    Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
                    Assert.Equal(0, revocation.Requests);
                }

                using var chain = new X509Chain { ChainPolicy = authority.Policy() };
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
                chain.Build(publicLeaf);
                Assert.True(revocation.Requests > 0);
            }
        }

        // A server that sends its intermediate certificate, as the documentation asks servers to, is accepted.
        [Fact]
        public async Task ParsedSends_AcceptAServerThatSendsItsIntermediate()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 intermediate = authority.CreateIntermediate();
            using X509Certificate2 certificate = authority.IssueServer(
                "127.0.0.1", Array.Empty<string>(), new[] { IPAddress.Loopback }, issuer: intermediate);
            using X509Certificate2 sent = X509CertificateLoader.LoadCertificate(intermediate.RawData);
            await using var server = new LoopbackHttpServer(
                _ => LoopbackResponse.Ok("ok"),
                new LoopbackTls(certificate, Intermediates: new X509Certificate2Collection(sent)));
            using var sender = new ReplaySender(new ReplaySenderSetup(ChainPolicyFactory: authority.Policy));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/" },
                ReplaySendOptions.Default,
                CancellationToken.None).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
        }

        // The check before a client certificate is offered builds its chain with the replay policy too: for a server that
        // presents only its leaf, whose issuer only a download could find, it withholds the certificate and fetches nothing,
        // even when a test's policy turns downloads on. Given the intermediate, as a server that sends it gives it, the same
        // check offers the certificate, so only the missing issuer withholds it.
        [Fact]
        public void TheClientCertificateCheck_WithholdsTheCertificate_RatherThanDownloadAnIssuer()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 intermediate = authority.CreateIntermediate();
            using var issuers = new RawSenderTlsTests.IssuerListener(intermediate);
            (X509Certificate2 leaf, _) = RawSenderTlsTests.Leaf(
                intermediate, IPAddress.Parse("127.0.0.17"), issuerUrl: issuers.Url);
            using (leaf)
            using (X509Certificate2 client = authority.IssueClient())
            using (X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData))
            using (X509Certificate2 publicIntermediate = X509CertificateLoader.LoadCertificate(intermediate.RawData))
            {
                ReplayCredentialOrigin origin = ReplayCredentialOrigin.Of(new Uri("https://127.0.0.17/"));
                foreach (Func<X509ChainPolicy> trust in new Func<X509ChainPolicy>[] { authority.Policy, () => Permissive(authority) })
                {
                    Assert.Null(Offer(client, origin, trust, publicLeaf));
                    Assert.Equal(0, issuers.Requests);
                }

                Assert.Same(
                    client,
                    Offer(
                        client,
                        origin,
                        () =>
                        {
                            X509ChainPolicy policy = authority.Policy();
                            policy.ExtraStore.Add(publicIntermediate);
                            return policy;
                        },
                        publicLeaf));
                Assert.Equal(0, issuers.Requests);

                AssertADownloadCompletesTheChain(authority, leaf, issuers);
            }
        }

        // A test's factory may return the one policy that it keeps, and change it later: the handler, each check before a
        // client certificate is offered and each Raw handshake still get a copy of their own, so a check adds server
        // authentication to no other policy, and no later change turns downloads or revocation back on.
        [Fact]
        public void APolicyThatATestKeeps_ReachesNoHandlerCheckOrHandshake()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 client = authority.IssueClient();
            using X509Certificate2 server = authority.IssueServer(
                "127.0.0.1", Array.Empty<string>(), new[] { IPAddress.Loopback });
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(server.RawData);
            X509ChainPolicy kept = authority.Policy();
            ReplayCredentialOrigin origin = ReplayCredentialOrigin.Of(new Uri("https://127.0.0.1/"));
            using SocketsHttpHandler handler = ReplaySender.CreateHandler(
                new ReplaySenderSetup(ClientCertificate: client, CredentialOrigin: origin, ChainPolicyFactory: () => kept));

            for (int check = 0; check < 2; check++)
            {
                Assert.Same(
                    client,
                    handler.SslOptions.LocalCertificateSelectionCallback(
                        handler, origin.Host, new X509CertificateCollection(), remote, Array.Empty<string>()));
            }

            X509ChainPolicy raw = ReplayTls.ChainPolicy(() => kept);
            kept.DisableCertificateDownloads = false;
            kept.RevocationMode = X509RevocationMode.Online;

            Assert.Empty(kept.ApplicationPolicy);
            foreach (X509ChainPolicy policy in new[] { handler.SslOptions.CertificateChainPolicy, raw })
            {
                Assert.NotSame(kept, policy);
                Assert.Empty(policy.ApplicationPolicy);
                Assert.Equal(
                    (X509RevocationMode.NoCheck, X509RevocationFlag.ExcludeRoot, true),
                    (policy.RevocationMode, policy.RevocationFlag, policy.DisableCertificateDownloads));
            }
        }

        // What a production handler made for a client certificate offers a server that presents remote for the origin's
        // host, from its certificate selection callback, the one that SslStream calls during the handshake. The check
        // builds its chain from a policy of its own: it adds server authentication to that policy, never to the handler's.
        private static X509Certificate Offer(
            X509Certificate2 client, ReplayCredentialOrigin origin, Func<X509ChainPolicy> trust, X509Certificate2 remote)
        {
            using SocketsHttpHandler handler = ReplaySender.CreateHandler(
                new ReplaySenderSetup(ClientCertificate: client, CredentialOrigin: origin, ChainPolicyFactory: trust));
            X509Certificate offered = handler.SslOptions.LocalCertificateSelectionCallback(
                handler, origin.Host, new X509CertificateCollection(), remote, Array.Empty<string>());
            Assert.Empty(handler.SslOptions.CertificateChainPolicy.ApplicationPolicy);
            return offered;
        }

        // A test's policy that turns certificate downloads and revocation checking on, which the replay policy overrides.
        private static X509ChainPolicy Permissive(TestCertificateAuthority authority)
        {
            X509ChainPolicy policy = authority.Policy();
            policy.DisableCertificateDownloads = false;
            policy.RevocationMode = X509RevocationMode.Online;
            return policy;
        }

        // The control, on the same certificates: with downloads on, building the leaf's chain contacts the listener and
        // succeeds, so the setup works on this machine.
        private static void AssertADownloadCompletesTheChain(
            TestCertificateAuthority authority, X509Certificate2 leaf, RawSenderTlsTests.IssuerListener issuers)
        {
            using var chain = new X509Chain { ChainPolicy = authority.Policy() };
            chain.ChainPolicy.DisableCertificateDownloads = false;
            using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
            Assert.True(chain.Build(publicLeaf), string.Join(", ", chain.ChainStatus.Select(status => status.Status)));
            Assert.True(issuers.Requests > 0);
        }
    }
}
