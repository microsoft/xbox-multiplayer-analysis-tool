// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Http;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // A URL whose international host name Uri parses but gives no ASCII form (ReplayAuthority.HasIdnHost) is an invalid URL
    // wherever replay takes one, whether reading its IDN host throws or returns the name unconverted: it is refused as
    // input before any sender exists, and is never sent or failed as an internal error. An international name that Uri
    // converts to ASCII, a host name in ASCII, and an IPv6 zone of any characters, which is data that send admission
    // refuses as scoped, stay valid URLs and origins.
    public class ReplayIdnHostTests
    {
        private const string NoIdnHost = SyntheticCaptures.IdnTooLongHost;
        private const string BasicHost = SyntheticCaptures.NonAsciiBasicHost;

        // How a refused URL is described: the host rule, which names what is taken, then what else the URL must be.
        private const string UrlRule =
            "Enter an absolute HTTP or HTTPS URL with an IP address, a host name in ASCII or an international host name " +
            "that XMAT can convert to ASCII (punycode), ";

        // Hosts that Uri parses but IDNA refuses: a label longer than 63 characters in punycode, a line separator, and a
        // lone zero-width joiner. Uri.IdnHost throws UriFormatException for each.
        public static TheoryData<string> HostsWithoutAnIdnForm => new()
        {
            NoIdnHost,
            "x\u2028y.example.test",
            "\u200D",
        };

        [Theory]
        [MemberData(nameof(HostsWithoutAnIdnForm))]
        public void AUrlWhoseHostHasNoIdnForm_IsNoValidUrl_NorAnOrigin(string host)
        {
            foreach (string url in Urls(host))
            {
                Uri parsed = Parsed(url);
                Assert.Throws<UriFormatException>(() => parsed.IdnHost);
            }

            AssertNoValidUrl(host);
        }

        // A name whose first label fails Uri's preliminary DNS check is kept as a Basic (UNC) host, as written, so reading its
        // IDN host returns its Unicode text without throwing: Uri gives it no ASCII form either, and it is refused as those
        // above are.
        [Fact]
        public void AHostThatUriKeepsUnconverted_IsNoValidUrl_NorAnOrigin()
        {
            Assert.False(BasicHost.All(char.IsAscii));
            foreach (string url in Urls(BasicHost))
            {
                Uri parsed = Parsed(url);
                Assert.Equal(UriHostNameType.Basic, parsed.HostNameType);
                Assert.Equal(BasicHost, parsed.IdnHost);
            }

            AssertNoValidUrl(BasicHost);
        }

        // That check also refuses some names that IDNA converts, such as a label of 30 Chinese characters, whose punycode label
        // is 36 characters. Uri keeps such a name unconverted, so XMAT refuses it as the name above: its rule is the ASCII form
        // that Uri gives a host, not whether the name has an IDNA form.
        [Fact]
        public void AHostThatIdnaConvertsButUriKeepsUnconverted_IsNoValidUrl_NorAnOrigin()
        {
            string host = new string('\u4E00', 30) + ".example.test";
            Assert.Equal("xn--4gq" + new string('a', 29) + ".example.test", new IdnMapping().GetAscii(host));
            foreach (string url in Urls(host))
            {
                Uri parsed = Parsed(url);
                Assert.Equal(UriHostNameType.Basic, parsed.HostNameType);
                Assert.Equal(host, parsed.IdnHost);
            }

            AssertNoValidUrl(host);
        }

        [Fact]
        public void AnInternationalName_AndAZoneOfAnyCharacters_StayValidUrls()
        {
            Assert.True(RequestReplayDraft.IsValidRequestFileUrl("http://b\u00fccher.example/item/#"));
            Assert.True(ReplayOperation.TryParseOrigin("https://b\u00fccher.example", out _));
            foreach (string url in new[] { "http://[fe80::1%25\u00e9]/", "http://[fe80::1%\u00e9]/item/#" })
            {
                Assert.True(RequestReplayDraft.IsValidRequestFileUrl(url), url);
            }

            Assert.True(ReplayOperation.TryParseOrigin("http://[fe80::1%25\u00e9]", out string origin));
            Assert.Equal("http://[fe80::1%25\u00e9]", origin);

            // A host name in ASCII is taken as it is, whatever DNS's rules say of it, such as one with a 64-character label,
            // which Uri keeps as a Basic host: XMAT requires a host that it can send, not a valid DNS name.
            string label64 = "http://" + new string('a', 64) + ".example.test/item/#";
            Assert.Equal(UriHostNameType.Basic, Parsed(label64).HostNameType);
            Assert.True(RequestReplayDraft.IsValidRequestFileUrl(label64));
        }

        // A zone is data of any characters, and its origin is the zone exactly as written: the same zone, the address in
        // another case, is one origin, and the zone spelled another way, in another case or with other characters is
        // another, as for an ASCII zone.
        [Theory]
        [InlineData("http://[fe80::1%25\u00e9]/a", "http://[FE80::1%25\u00e9]/b", true)]
        [InlineData("http://[fe80::1%25\u00e9]/", "http://[fe80::1%\u00e9]/", false)]
        [InlineData("http://[fe80::1%25\u00e9]/", "http://[fe80::1%25\u00c9]/", false)]
        [InlineData("http://[fe80::1%25\u00e9]/", "http://[fe80::1%25e]/", false)]
        public void AZoneOfAnyCharacters_IsAnOrigin_AsWritten(string first, string second, bool same)
        {
            Assert.True(ReplayAuthority.SameOrigin(new Uri(first), new Uri(first)));
            Assert.Equal(same, ReplayAuthority.SameOrigin(new Uri(first), new Uri(second)));
            Assert.Equal(same, ReplayAuthority.SameOrigin(new Uri(second), new Uri(first)));
        }

        // So a request file's scoped URL is its own origin, which saving its content with it does not change.
        [Fact]
        public void ARequestFilesZoneOfAnyCharacters_IsItsOrigin()
        {
            Assert.True(ReplayAuthority.TryOriginKey(new Uri("https://[FE80::1%25\u00e9]:8443/d"), out string key));
            Assert.Equal("https://[fe80::1%25\u00e9]:8443", key);
            RequestReplayDraft file = RequestReplayDraft.FromRequestFile(File("http://[fe80::1%25\u00e9]/item"));
            Assert.False(file.ChangesOrigin);

            file.Url = "http://[fe80::1%25\u00e9]/other";
            Assert.False(file.ChangesOrigin);
            file.Url = "http://[fe80::1%25\u00c9]/item";
            Assert.True(file.ChangesOrigin);
        }

        [Theory]
        [InlineData(NoIdnHost)]
        [InlineData(BasicHost)]
        public async Task ACapturedRequestWhoseHostHasNoIdnForm_IsRefusedBeforeASender(string host)
        {
            ProxyConnectionModel capture = Capture(host + ":80", "/item");
            Assert.True(RequestReplayDraft.CanReplay(capture));
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { capture })),
                OperationContext.None,
                CancellationToken.None);
            OperationResult<ReplayExport> export = operation.PrepareForExport(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { capture })),
                CancellationToken.None);

            AssertRefused(result, RequestFileCodes.RequestInvalid);
            AssertRefused(export, RequestFileCodes.RequestInvalid);
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        // A request whose host has no IDN form, run or exported without an earlier Preview, is refused as invalid before a
        // sender exists, as Preview then refuses it.
        [Theory]
        [InlineData(NoIdnHost)]
        [InlineData(BasicHost)]
        public async Task ARequestExecutedWithoutPreview_IsRefusedAsPreviewRefusesIt(string host)
        {
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);
            ReplaySource source = ReplaySource.FromDraft(new RequestReplayDraft { Method = "GET", Url = $"http://{host}/item" });

            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source), OperationContext.None, CancellationToken.None);
            OperationResult<ReplayExport> export = operation.PrepareForExport(new ReplayRequest(source), CancellationToken.None);
            OperationResult<ReplayPlanPreview> preview = operation.Preview(source);

            AssertRefused(run, RequestFileCodes.RequestInvalid);
            AssertRefused(export, RequestFileCodes.RequestInvalid);
            AssertRefused(preview, RequestFileCodes.RequestInvalid);
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        // ExecuteAsync expands a sequence before it checks the requests, without Preview: a template whose host has no IDN
        // form is refused there as invalid, never as an internal failure, and one whose IPv6 zone has any characters as
        // scoped, as Preview refuses each.
        [Theory]
        [InlineData("http://" + NoIdnHost + "/item/#", RequestFileCodes.RequestInvalid)]
        [InlineData("http://" + BasicHost + "/item/#", RequestFileCodes.RequestInvalid)]
        [InlineData("http://[fe80::1%25\u00e9]/item/#", ReplayDestination.ScopedIpv6Code)]
        public async Task ASequenceExecutedWithoutPreview_IsRefusedAsPreviewRefusesIt(string template, string code)
        {
            Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);
            ReplaySource source = ReplaySource.FromTemplate(new RequestReplayDraft { Method = "GET", Url = template }, sequence);

            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source), OperationContext.None, CancellationToken.None);
            OperationResult<ReplayPlanPreview> preview = operation.Preview(source);

            AssertRefused(run, code);
            AssertRefused(preview, code);
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        [Theory]
        [InlineData(NoIdnHost)]
        [InlineData(BasicHost)]
        public async Task AnOriginEditWhoseHostHasNoIdnForm_IsRefusedBeforeASender(string host)
        {
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Method = "GET", Url = "http://example.test/item" }),
                    Edits: new ReplayEdits(Origin: "https://" + host)),
                OperationContext.None,
                CancellationToken.None);

            AssertRefused(result, "replay.edit.invalid-origin");
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        // Every input that takes a URL or an origin refuses one with this host: the shared predicate and the origin key, a
        // request file's URL and sequence template, building and checking a request, completing a template's URL, and an
        // origin.
        private static void AssertNoValidUrl(string host)
        {
            foreach (string url in Urls(host))
            {
                Uri parsed = Parsed(url);
                Assert.False(ReplayAuthority.HasIdnHost(parsed), url);
                Assert.False(ReplayAuthority.TryOriginKey(parsed, out _), url);
                Assert.False(RequestReplayDraft.IsValidRequestFileUrl(url), url);
            }

            var draft = new RequestReplayDraft { Method = "GET", Url = $"http://{host}/" };
            ArgumentException refused = Assert.Throws<ArgumentException>(() => draft.CreateRequest());
            Assert.Equal(UrlRule + "without embedded credentials or a fragment.", refused.Message);
            Assert.StartsWith(
                UrlRule, Assert.Throws<FormatException>(() => draft.ValidateStructure()).Message, StringComparison.Ordinal);
            Assert.StartsWith(
                UrlRule,
                Assert.Throws<ArgumentException>(() => RequestReplayDraft.FromRequestFile(File(null)).CompleteUrl(draft.Url))
                    .Message,
                StringComparison.Ordinal);
            Assert.False(ReplayOperation.TryParseOrigin($"https://{host}", out _));
        }

        // A URL, one with a port and a query, and a sequence template, with this host.
        private static string[] Urls(string host) =>
            new[] { $"http://{host}/", $"https://{host}:8443/item?x=1", $"http://{host}/item/#" };

        // The URL as Uri parses it, a sequence template's '#' read as a number.
        private static Uri Parsed(string url)
        {
            Assert.True(Uri.TryCreate(url.Replace('#', '0'), UriKind.Absolute, out Uri parsed), url);
            return parsed;
        }

        private static ReplayOperation Operation(Action sent, Action created)
        {
            ReplaySender Sender()
            {
                created();
                return new ReplaySender(new HttpMessageInvoker(new OkHandler(sent)), ownsInvoker: true);
            }

            return new ReplayOperation
            {
                SenderFactory = Sender,
                OwnSenderFactory = _ => Sender(),
            };
        }

        private static ProxyConnectionModel Capture(string authority, string path) =>
            new(
                1, "200", DateTime.UnixEpoch, TimeSpan.Zero, "http", authority, "GET", path,
                $"GET {path} HTTP/1.1\r\n\r\n", Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");

        // A content request file with a GET to url and no headers or body; a template that leaves out its URL when url is
        // null.
        private static RequestFileContent File(string url) => new(
            url == null ? RequestFileProfile.Template : RequestFileProfile.Content, "GET", url, ReplayHttpVersion.Auto,
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>(), new RequestOmissions(false, url == null, false, false));

        private static void AssertRefused<T>(OperationResult<T> result, string code)
        {
            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(OperationDiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(code, diagnostic.Code);
        }

        private sealed class OkHandler(Action sent) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                sent();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) });
            }
        }
    }
}
