// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // What replay takes for one origin (ReplayAuthority.SameOrigin): the authorities that Uri writes, compared ignoring
    // case, and the canonical (IDN) hosts, schemes and ports, so that hosts that differ only in letters whose upper cases
    // match, such as Greek final sigma (ς) and sigma (σ), are two origins, as DNS has them, and a host without a canonical
    // form is none. A redirect between them is never followed, an edit between them needs consent, and a connect-to run
    // cannot hold both. Loopback servers only: the HTTPS one presents a certificate valid for both names, from an in-memory
    // authority that only the senders built here trust, so the certificate check is never what stops a hop.
    public sealed class ReplayOriginTests : IDisposable
    {
        private const string Sigma = "ασ.example";
        private const string FinalSigma = "ας.example";
        private const string CapitalSigma = "ΑΣ.example";
        private const string Palochka = "а\u04CF.example";
        private const string CapitalPalochka = "а\u04C0.example";
        private const string Canary = "XMATF1CANARY";
        private readonly TestCertificateAuthority _authority = new();

        public void Dispose() => _authority.Dispose();

        [Theory]
        [InlineData("https://a.test/x", "https://A.TEST:443/y", true)]
        [InlineData("https://a.test/", "https://a.test:8443/", false)]
        [InlineData("https://a.test/", "http://a.test/", false)]
        [InlineData("https://ασ.example/", "https://ας.example/", false)]
        [InlineData("https://ΑΣ.example/", "https://ας.example/", false)]
        [InlineData("https://ΑΣ.example/", "https://ασ.example/", true)]
        [InlineData("https://bücher.example/", "https://BÜCHER.example/", true)]
        [InlineData("https://bücher.example/", "https://xn--bcher-kva.example/", false)]
        [InlineData("http://[::1]:8080/", "http://[0:0::1]:8080/", true)]
        [InlineData("https://а\u04CF.example/", "https://а\u04C0.example/", false)]
        [InlineData("http://[fe80::1%253]/a", "http://[FE80::1%253]/b", true)]
        [InlineData("http://[fe80::1%253]/", "http://[fe80::1%254]/", false)]
        [InlineData("http://[fe80::1%253]/", "http://[fe80::1]/", false)]
        [InlineData("http://[fe80::1%25A]/", "http://[fe80::1%25a]/", false)]
        public void SameOrigin_NeedsTheAuthoritiesAndTheCanonicalHostsToMatch(string first, string second, bool same)
        {
            Assert.Equal(same, ReplayAuthority.SameOrigin(new Uri(first), new Uri(second)));
            Assert.Equal(same, ReplayAuthority.SameOrigin(new Uri(second), new Uri(first)));
        }

        // A host that IDNA refuses (here with a capital Cyrillic palochka, U+04C0) has no canonical form: it is no origin,
        // not even its own.
        [Fact]
        public void AHostWithoutACanonicalForm_IsNoOrigin()
        {
            var refused = new Uri("https://" + CapitalPalochka + "/");

            Assert.False(ReplayAuthority.TryOriginKey(refused, out string key));
            Assert.Null(key);
            Assert.False(ReplayAuthority.SameOrigin(refused, refused));
            Assert.True(ReplayAuthority.TryOriginKey(new Uri("https://" + Sigma + ":8443/x"), out key));
            Assert.Equal("https://xn--mxa0b.example:8443", key);
        }

        // A redirect from one sigma to the other is to another host, whichever way it goes: it is never followed, so the
        // credentials, cookie and body that a 307 or 308 would keep reach only the host that they were sent to. The
        // Location comes as UTF-8, which the HTTP stack reads as UTF-8.
        [Theory]
        [InlineData(Sigma, FinalSigma, 307)]
        [InlineData(FinalSigma, Sigma, 307)]
        [InlineData(CapitalSigma, FinalSigma, 308)]
        [InlineData(FinalSigma, Sigma, 308)]
        public async Task ARedirectToTheOtherSigma_IsNeverFollowed(string from, string to, int status)
        {
            (LoopbackHttpServer server, ReplayExchange exchange) = await RedirectAsync(from, to, status);
            await using (server)
            {
                Assert.Equal(
                    (ReplayOutcome.Completed, status, ReplaySender.RedirectOriginChangeCode, 0),
                    (exchange.Outcome, exchange.StatusCode, exchange.Code, exchange.Redirects));
                Assert.Equal("/a", Assert.Single(server.Requests).Target);
            }
        }

        // A redirect to a host without a canonical form is to no origin: it is not followed either.
        [Fact]
        public async Task ARedirectToAHostWithoutACanonicalForm_IsNotFollowed()
        {
            (LoopbackHttpServer server, ReplayExchange exchange) = await RedirectAsync(Palochka, CapitalPalochka, 307);
            await using (server)
            {
                Assert.Equal(
                    (ReplayOutcome.Completed, 307, ReplaySender.RedirectOriginChangeCode),
                    (exchange.Outcome, exchange.StatusCode, exchange.Code));
                Assert.Equal("/a", Assert.Single(server.Requests).Target);
            }
        }

        // Within the origin, the host spelled with other capitals included, a redirect is followed, and a 307 or 308 keeps
        // the credentials, cookie and body.
        [Theory]
        [InlineData(Sigma, CapitalSigma, 307)]
        [InlineData(CapitalSigma, Sigma, 308)]
        [InlineData(FinalSigma, FinalSigma, 307)]
        public async Task ARedirectWithinTheOrigin_IsFollowed_WithWhatItKeeps(string from, string to, int status)
        {
            (LoopbackHttpServer server, ReplayExchange exchange) = await RedirectAsync(from, to, status);
            await using (server)
            {
                Assert.Equal((ReplayOutcome.Completed, 200, null, 1), (exchange.Outcome, exchange.StatusCode, exchange.Code, exchange.Redirects));
                Assert.Equal(new[] { "/a", "/b" }, server.Requests.Select(request => request.Target));
                Assert.All(server.Requests, request =>
                {
                    Assert.Equal("Bearer " + Canary, request.Header("Authorization"));
                    Assert.Equal("session=" + Canary, request.Header("Cookie"));
                    Assert.Equal(Canary + "-body", Encoding.ASCII.GetString(request.Body));
                });
            }
        }

        // An edit from one sigma to the other sends the captured headers to another origin, which needs consent; the same
        // host with capitals does not.
        [Theory]
        [InlineData("https://ασ.example/item", "https://ας.example/item", true)]
        [InlineData("https://ας.example/item", "https://ΑΣ.example/item", true)]
        [InlineData("https://ασ.example/item", "https://ΑΣ.example/item", false)]
        [InlineData("https://а\u04CF.example/item", "https://а\u04C0.example/item", true)]
        public void AnEditToTheOtherSigma_ChangesTheOrigin(string captured, string edited, bool changes)
        {
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(Row(1, captured));

            draft.Url = edited;

            Assert.Equal(changes, draft.ChangesOrigin);
        }

        [Theory]
        [InlineData("https://ασ.example/item", "https://ας.example/item", "replay.policy.origin-change")]
        [InlineData("https://ασ.example/item", "https://ΑΣ.example/item", null)]
        public async Task AnEditToTheOtherSigma_IsNotSentWithoutConsent(string captured, string edited, string code)
        {
            int sends = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new OkHandler(() => sends++)), ownsInvoker: true),
            };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { Row(1, captured) }), Edits: new ReplayEdits(Url: edited)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(code, result.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(code == null ? 1 : 0, sends);
        }

        // A draft's origin keeps its URL's IPv6 zone, which Uri's authority drops: the origin as it came, from a request file,
        // in a copy of the draft, or from the URL supplied for a file without one, is no change, with the address written in
        // another case too; another zone, the zone written another way, or none is a change.
        [Theory]
        [InlineData("http://[fe80::1%25A]/other", false)]
        [InlineData("http://[FE80::1%25A]/item", false)]
        [InlineData("http://[fe80::1%25B]/item", true)]
        [InlineData("http://[fe80::1%25a]/item", true)]
        [InlineData("http://[fe80::1]/item", true)]
        public void AnEditOfTheZone_ChangesTheOrigin_WhereverTheDraftCameFrom(string edited, bool changes)
        {
            const string original = "http://[fe80::1%25A]/item";
            RequestReplayDraft file = RequestReplayDraft.FromRequestFile(File(original));
            RequestReplayDraft completed = RequestReplayDraft.FromRequestFile(File(null));
            completed.CompleteUrl(original);
            RequestReplayDraft copied = file.WithUrl(original);
            Assert.Equal((false, false, false), (file.ChangesOrigin, completed.ChangesOrigin, copied.ChangesOrigin));

            RequestReplayDraft copy = file.WithUrl(edited);
            file.Url = edited;
            completed.Url = edited;

            Assert.Equal((changes, changes, changes), (file.ChangesOrigin, completed.ChangesOrigin, copy.ChangesOrigin));
        }

        // A request file keeps its IPv6 zone as origin data, but neither that destination nor another scoped edit is sent.
        [Theory]
        [InlineData("http://[fe80::1%25A]/item")]
        [InlineData("http://[fe80::1%25B]/item")]
        public async Task ARequestFilesZone_IsAlwaysRefusedForSending(string edited)
        {
            int sends = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new OkHandler(() => sends++)), ownsInvoker: true),
            };
            RequestReplayDraft draft = RequestReplayDraft.FromRequestFile(File("http://[fe80::1%25A]/item"));
            draft.Url = edited;

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(draft)), OperationContext.None, CancellationToken.None);

            Assert.Equal(ReplayDestination.ScopedIpv6Code, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(0, sends);
        }

        // A connect-to run serves one origin: the two sigmas are two, and a host without a canonical form is none, so a run
        // with it is refused rather than counted with another.
        [Theory]
        [InlineData("https://ασ.example/a", "https://ας.example/b", ReplayOperation.ConnectToOriginCountCode)]
        [InlineData("https://ας.example/a", "https://ΑΣ.example/b", ReplayOperation.ConnectToOriginCountCode)]
        [InlineData("https://а\u04CF.example/a", "https://а\u04C0.example/b", ReplayOperation.ConnectToOriginCountCode)]
        [InlineData("https://а\u04C0.example/a", null, ReplayOperation.ConnectToOriginCountCode)]
        [InlineData("https://ασ.example/a", "https://ΑΣ.example/b", null)]
        public async Task AConnectToRun_HoldsOneOrigin_TheSigmasCountedApart(string first, string second, string code)
        {
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:443", out ReplayConnectTo endpoint));
            int sends = 0;
            var operation = new ReplayOperation
            {
                OwnSenderFactory = setup => new ReplaySender(new HttpMessageInvoker(new OkHandler(() => sends++)), ownsInvoker: true, setup),
            };
            ProxyConnectionModel[] rows = second == null ? new[] { Row(1, first) } : new[] { Row(1, first), Row(2, second) };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromCaptures(rows),
                    new ReplayOptions(AllowConnectTo: true, Transport: new ReplayTransportOptions(ConnectTo: endpoint))),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(code, result.Diagnostics.SingleOrDefault()?.Code);
            Assert.Equal(code == null ? rows.Length : 0, sends);
        }

        // A POST with credentials, a cookie and a body, all carrying the canary, from https://FROM:PORT/a, which redirects
        // with status to https://TO:PORT/b, written as UTF-8 bytes; both names reach the one loopback server, whose
        // certificate is valid for both, through a connect-to sender that trusts it.
        private async Task<(LoopbackHttpServer Server, ReplayExchange Exchange)> RedirectAsync(string from, string to, int status)
        {
            using X509Certificate2 certificate = _authority.IssueServer(
                "both names", new[] { new Uri("https://" + from + "/").IdnHost, IdnHostOrSelf(to) });
            int port = 0;
            var server = new LoopbackHttpServer(
                request => request.Target == "/a"
                    ? new LoopbackResponse(status, "Redirect", "Location: " + Utf8($"https://{to}:{Volatile.Read(ref port)}/b") + "\r\n")
                    : LoopbackResponse.Ok("b"),
                new LoopbackTls(certificate));
            Volatile.Write(ref port, server.Port);
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:" + server.Port, out ReplayConnectTo endpoint));
            var setup = new ReplaySenderSetup(ConnectTo: endpoint);
            SocketsHttpHandler handler = ReplaySender.CreateHandler(setup);
            _authority.Trust(handler);
            using var sender = new ReplaySender(new HttpMessageInvoker(handler), ownsInvoker: true, setup);
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = $"https://{from}:{server.Port}/a",
                HeadersText = $"Authorization: Bearer {Canary}\r\nCookie: session={Canary}\r\nContent-Type: text/plain",
            };
            draft.SetBodyBytes(Encoding.ASCII.GetBytes(Canary + "-body"));

            ReplayExchange exchange = await sender.SendAsync(
                draft, new ReplaySendOptions(TimeSpan.FromSeconds(10), ReplaySendOptions.DefaultMaxResponseBytes, 5), CancellationToken.None);
            return (server, exchange);
        }

        // A name that IDNA refuses cannot be a certificate's name; the certificate then names the refused host as written,
        // which no handshake ever asks for.
        private static string IdnHostOrSelf(string host)
        {
            try
            {
                return new Uri("https://" + host + "/").IdnHost;
            }
            catch (UriFormatException)
            {
                return "unused.example";
            }
        }

        // The UTF-8 bytes of text, as the Latin-1 characters that the loopback server writes byte for byte.
        private static string Utf8(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // A content request file with a GET to url and no headers or body; a template that leaves out its URL when url is
        // null.
        private static RequestFileContent File(string url) => new(
            url == null ? RequestFileProfile.Template : RequestFileProfile.Content, "GET", url, ReplayHttpVersion.Auto,
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>(), new RequestOmissions(false, url == null, false, false));

        // Answers 200 without any network, counting the sends.
        private sealed class OkHandler(Action sent) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                sent();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ok"u8.ToArray()) });
            }
        }
    }
}
