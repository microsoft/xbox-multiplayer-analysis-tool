// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class RequestReplayDraftTests
    {
        [Fact]
        public async Task FromCapture_CreatesIndependentEditableSnapshot()
        {
            var capture = Capture("POST", "/sessions?name=one", "original"u8.ToArray(),
                "Content-Type: application/json\r\nX-Test: first\r\nX-Test: second\r\n");
            var draft = RequestReplayDraft.FromCapture(capture);

            Assert.Equal("https://example.com:8443/sessions?name=one", draft.Url);
            Assert.Equal("POST", draft.Method);
            Assert.Equal("original", draft.BodyText);
            Assert.False(draft.BodyIsBase64);

            draft.Url = "https://other.example/edited";
            draft.BodyText = "edited";
            using var request = draft.CreateRequest();

            Assert.Equal(["first", "second"], request.Headers.GetValues("X-Test"));
            Assert.Equal("edited", await request.Content.ReadAsStringAsync());
            Assert.Equal("/sessions?name=one", capture.Path);
            Assert.Equal("original"u8.ToArray(), capture.RequestBody);
            Assert.Equal("second", capture.RequestHeaders["X-Test"]);
        }

        [Theory]
        [InlineData("GET", false)]
        [InlineData("HEAD", false)]
        [InlineData("OPTIONS", false)]
        [InlineData("POST", true)]
        [InlineData("PUT", true)]
        [InlineData("DELETE", true)]
        [InlineData("PATCH", true)]
        [InlineData("CUSTOM", true)]
        public void RequiresConfirmation_OnlyExemptsSafeMethods(string method, bool expected)
        {
            Assert.Equal(expected, new RequestReplayDraft { Method = method }.RequiresConfirmation);
        }

        [Fact]
        public void RequiresConfirmation_AlsoGuardsChangingTheCapturedOrigin()
        {
            var draft = RequestReplayDraft.FromCapture(Capture("GET", "/", []));
            Assert.False(draft.RequiresConfirmation);
            draft.Url = "http://different.example/";
            Assert.True(draft.RequiresConfirmation);
        }

        [Fact]
        public async Task CreateRequest_RebuildsAuthorityAndFramingAndRemovesConnectionHeaders()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "https://new.example/path",
                HeadersText = "Host: old.example\r\nContent-Length: 999\r\n" +
                    "Connection: keep-alive, X-Connection-Only\r\nX-Connection-Only: private\r\n" +
                    "Transfer-Encoding: chunked\r\nProxy-Authorization: secret\r\n" +
                    "Content-Type: application/json\r\nAuthorization: Bearer explicit\r\n",
                BodyText = "{\"updated\":true}"
            };

            using var request = draft.CreateRequest();

            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            Assert.Null(request.Headers.Host);
            Assert.False(request.Headers.Contains("Connection"));
            Assert.False(request.Headers.Contains("X-Connection-Only"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            Assert.False(request.Headers.TransferEncodingChunked ?? false);
            Assert.Equal("Bearer explicit", request.Headers.Authorization.ToString());
            Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);
            Assert.Equal(Encoding.UTF8.GetByteCount(draft.BodyText), request.Content.Headers.ContentLength);
            Assert.Equal(draft.BodyText, await request.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("not a url")]
        [InlineData("/relative")]
        [InlineData("file:///C:/capture")]
        [InlineData("ftp://example.com/file")]
        [InlineData("https://user:password@example.com/")]
        [InlineData("https://example.com/#fragment")]
        public void CreateRequest_RejectsInvalidOrAmbiguousUrls(string url)
        {
            var draft = new RequestReplayDraft { Url = url };
            Assert.Throws<ArgumentException>(() => draft.CreateRequest());
        }

        [Theory]
        [InlineData("Header without colon")]
        [InlineData("Bad Name: value")]
        [InlineData(": pseudo-header")]
        [InlineData("X-Test: first\rInjected: second")]
        [InlineData("X-Test: value\0bad")]
        [InlineData("Content-Length: not-a-number\nbroken")]
        public void CreateRequest_RejectsMalformedHeaders(string headers)
        {
            var draft = new RequestReplayDraft { Url = "https://example.com/", HeadersText = headers };
            Assert.Throws<FormatException>(() => draft.CreateRequest());
        }

        [Theory]
        [InlineData("CONNECT")]
        [InlineData("GET / HTTP/1.1")]
        [InlineData("")]
        public void CreateRequest_RejectsUnsupportedOrInvalidMethod(string method)
        {
            var draft = new RequestReplayDraft { Method = method, Url = "https://example.com/" };
            Assert.ThrowsAny<ArgumentException>(() => draft.CreateRequest());
        }

        [Fact]
        public void CreateRequest_RejectsWebSocketUpgrade()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/",
                HeadersText = "Connection: Upgrade\r\nUpgrade: websocket"
            };
            Assert.Throws<ArgumentException>(() => draft.CreateRequest());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FromCapture_PreservesBinaryBodyUsingBase64(bool staleChunkedHeader)
        {
            byte[] body = [0, 255, 128, 13, 10];
            var draft = RequestReplayDraft.FromCapture(Capture("POST", "/", body,
                staleChunkedHeader ? "Transfer-Encoding: chunked\r\n" : ""));

            Assert.True(draft.BodyIsBase64);
            Assert.Equal(Convert.ToBase64String(body), draft.BodyText);
            using var request = draft.CreateRequest();
            Assert.Equal(body, await request.Content.ReadAsByteArrayAsync());
        }

        [Fact]
        public void FromCapture_UsesBase64ForEncodedBodyEvenWhenBytesAreValidUtf8()
        {
            var draft = RequestReplayDraft.FromCapture(
                Capture("POST", "/", "encoded"u8.ToArray(), "Content-Encoding: custom\r\n"));
            Assert.True(draft.BodyIsBase64);
        }

        [Fact]
        public async Task SetBodyFormat_RoundTripsUtf8WithoutChangingBytes()
        {
            var draft = new RequestReplayDraft { Url = "https://example.com/", BodyText = "caf\u00e9\ntext" };
            draft.SetBodyFormat(true);
            Assert.True(draft.BodyIsBase64);
            using var request = draft.CreateRequest();
            Assert.Equal(Encoding.UTF8.GetBytes("caf\u00e9\ntext"), await request.Content.ReadAsByteArrayAsync());

            draft.SetBodyFormat(false);
            Assert.False(draft.BodyIsBase64);
            Assert.Equal("caf\u00e9\ntext", draft.BodyText);
        }

        [Fact]
        public void SetBodyFormat_RejectsLossyBinaryConversionWithoutChangingDraft()
        {
            var draft = RequestReplayDraft.FromCapture(Capture("POST", "/", [255]));
            string original = draft.BodyText;
            Assert.Throws<DecoderFallbackException>(() => draft.SetBodyFormat(false));
            Assert.True(draft.BodyIsBase64);
            Assert.Equal(original, draft.BodyText);
        }

        [Fact]
        public void CreateRequest_RejectsInvalidBase64()
        {
            var draft = new RequestReplayDraft
            {
                Url = "https://example.com/",
                BodyIsBase64 = true,
                BodyText = "not base64!"
            };
            Assert.Throws<FormatException>(() => draft.CreateRequest());
        }

        [Fact]
        public void CanReplay_DisallowsConnectAndWebSocketsButAllowsImportedHttp()
        {
            Assert.False(RequestReplayDraft.CanReplay(null));
            Assert.False(RequestReplayDraft.CanReplay(Capture("CONNECT", "example.com:443", [])));
            Assert.False(RequestReplayDraft.CanReplay(Capture("GET", "/", [], "Upgrade: websocket\r\n")));
            Assert.True(RequestReplayDraft.CanReplay(Capture("POST", "/", [])));
        }

        [Fact]
        public void FromCapture_PreservesAbsoluteUriAndCreateRequestAcceptsIpv6()
        {
            var absolute = Capture("GET", "https://example.com:8443/absolute?q=1", []);
            Assert.Equal(absolute.Path, RequestReplayDraft.FromCapture(absolute).Url);

            var draft = new RequestReplayDraft { Url = "http://[::1]:8080/test" };
            using var request = draft.CreateRequest();
            Assert.Equal("[::1]", request.RequestUri.Host);
        }

        [Theory]
        [InlineData("/sessions?name=one")]
        [InlineData("/sessions%2Fone?name=one%26two&next=%2Fpath")]
        [InlineData("/sessions")]
        public void FromCapture_PreservesImportedSazRequestTarget(string target)
        {
            var capture = ImportedCapture(target, []);
            var draft = RequestReplayDraft.FromCapture(capture);
            using var request = draft.CreateRequest();

            Assert.Equal(target, capture.Path);
            Assert.Equal("https://example.com:8443" + target, draft.Url);
            Assert.Equal(draft.Url, request.RequestUri.AbsoluteUri);
        }

        [Theory]
        [InlineData("chunked", "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n", "Wikipedia")]
        [InlineData("ChUnKeD", "4;name=\"one;two\"\r\nWiki\r\n0;done\r\nX-Trailer: ignored\r\n\r\n", "Wiki")]
        [InlineData("identity, chunked", "A\r\n0123456789\r\n0\r\n\r\n", "0123456789")]
        [InlineData("chunked", "0\r\n\r\n", "")]
        public async Task FromCapture_DecodesImportedChunkedBodyWithoutChangingCapture(
            string transferEncoding, string framedBody, string expected)
        {
            byte[] body = Encoding.ASCII.GetBytes(framedBody);
            var capture = ImportedCapture("/", body, $"Transfer-Encoding: {transferEncoding}\r\n");
            string headers = capture.RequestLineAndHeaders;

            var draft = RequestReplayDraft.FromCapture(capture);
            using var request = draft.CreateRequest();

            Assert.Equal(expected, draft.BodyText);
            Assert.False(draft.BodyIsBase64);
            Assert.Equal(Encoding.ASCII.GetBytes(expected), await request.Content.ReadAsByteArrayAsync());
            Assert.Equal(expected.Length, request.Content.Headers.ContentLength);
            Assert.False(request.Headers.Contains("Transfer-Encoding"));
            Assert.Equal(Encoding.ASCII.GetBytes(framedBody), capture.RequestBody);
            Assert.Equal(headers, capture.RequestLineAndHeaders);
            Assert.Equal(transferEncoding, capture.RequestHeaders["Transfer-Encoding"]);
        }

        [Fact]
        public async Task FromCapture_DecodesBinaryChunksWithoutDecodingContentEncoding()
        {
            byte[] body = [.. "3\r\n"u8, 0, 255, 128, .. "\r\n0\r\n\r\n"u8];
            var capture = ImportedCapture("/", body,
                "Transfer-Encoding: chunked\r\nContent-Encoding: custom\r\n");

            var draft = RequestReplayDraft.FromCapture(capture);
            using var request = draft.CreateRequest();

            Assert.True(draft.BodyIsBase64);
            Assert.Equal(new byte[] { 0, 255, 128 }, await request.Content.ReadAsByteArrayAsync());
            Assert.Contains("custom", request.Content.Headers.ContentEncoding);
            Assert.Equal(body, capture.RequestBody);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Wikipedia")]
        [InlineData("{\r\n\"name\":\"one\"\r\n}")]
        [InlineData("plain\r\ntext\r\n")]
        [InlineData("123")]
        public async Task FromCapture_PreservesDecodedBodiesWithStaleChunkedHeader(string text)
        {
            var capture = ImportedCapture("/", Encoding.UTF8.GetBytes(text), "Transfer-Encoding: chunked\r\n");
            var draft = RequestReplayDraft.FromCapture(capture);
            using var request = draft.CreateRequest();

            Assert.Equal(text, await request.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("")]
        [InlineData("gzip")]
        [InlineData("chunked, gzip")]
        public async Task FromCapture_DoesNotDecodeWithoutFinalChunkedCoding(string transferEncoding)
        {
            byte[] body = "4\r\nWiki\r\n0\r\n\r\n"u8.ToArray();
            var capture = ImportedCapture("/", body,
                transferEncoding.Length == 0 ? "" : $"Transfer-Encoding: {transferEncoding}\r\n");
            var draft = RequestReplayDraft.FromCapture(capture);
            using var request = draft.CreateRequest();

            Assert.Equal(body, await request.Content.ReadAsByteArrayAsync());
        }

        [Theory]
        [InlineData("4\r\nWiki\r\n")]
        [InlineData("4\nWiki\n0\n\n")]
        [InlineData("4\r\nWik\r\n0\r\n\r\n")]
        [InlineData("4\r\nWikiX\r\n0\r\n\r\n")]
        [InlineData("0\r\n")]
        [InlineData("0\r\n\r\nextra")]
        [InlineData("FFFFFFFFFFFFFFFF\r\nx\r\n0\r\n\r\n")]
        [InlineData("4;=bad\r\nWiki\r\n0\r\n\r\n")]
        [InlineData("4\r\nWiki\r\n0\r\nBad-Trailer\r\n\r\n")]
        [InlineData("4\r\nWiki\r\n0\r\nBad Name: value\r\n\r\n")]
        [InlineData("4\r\nWiki\r\n0\r\nX-Trailer: value\nbad\r\n\r\n")]
        [InlineData("4\r\nWiki\r\nZ\r\n\r\n")]
        [InlineData("Z\r\nWiki\r\n0\r\n\r\n")]
        public void FromCapture_RejectsRecognizedMalformedChunkFraming(string text)
        {
            byte[] body = Encoding.ASCII.GetBytes(text);
            var capture = ImportedCapture("/", body, "Transfer-Encoding: chunked\r\n");

            var exception = Assert.Throws<FormatException>(() => RequestReplayDraft.FromCapture(capture));

            Assert.Contains("malformed chunked framing", exception.Message);
            Assert.Equal(body, capture.RequestBody);
            Assert.Equal("chunked", capture.RequestHeaders["Transfer-Encoding"]);
        }

        // With a bound that its body fits, FromCapture is FromCapture: the same method, URL, origin, header lines and
        // body, shown the same way, for text, binary, encoded, chunked and empty bodies.
        [Theory]
        [InlineData("POST", "/text", "plain text", "")]
        [InlineData("POST", "/binary", "\u0001\u0002", "")]
        [InlineData("POST", "/encoded", "zipped", "Content-Encoding: gzip\r\n")]
        [InlineData("GET", "/empty", "", "X-Test: one\r\nX-Test: two\r\n")]
        public void FromCapture_WithABoundItsBodyFits_IsFromCapture(string method, string path, string body, string headers)
        {
            ProxyConnectionModel capture = Capture(method, path, Encoding.UTF8.GetBytes(body), headers);

            RequestReplayDraft unbounded = RequestReplayDraft.FromCapture(capture);
            RequestReplayDraft bounded = RequestReplayDraft.FromCapture(capture, 64 * 1024, CancellationToken.None);

            Assert.Equal(
                (unbounded.Method, unbounded.Url, unbounded.HeadersText, unbounded.BodyText, unbounded.BodyIsBase64),
                (bounded.Method, bounded.Url, bounded.HeadersText, bounded.BodyText, bounded.BodyIsBase64));
            Assert.Equal(unbounded.GetBodyBytes(), bounded.GetBodyBytes());
            bounded.Url = "https://other.example/";
            Assert.True(bounded.ChangesOrigin);

            RequestReplayDraft chunked = RequestReplayDraft.FromCapture(
                ImportedCapture("/", "4\r\nWiki\r\n0\r\n\r\n"u8.ToArray(), "Transfer-Encoding: chunked\r\n"), 64 * 1024, CancellationToken.None);
            Assert.Equal(("Wiki", false), (chunked.BodyText, chunked.BodyIsBase64));
        }

        // A body larger than the bound stays its exact bytes, decoded from chunked framing as always, and no text or
        // Base64 is made of it: drafting an 8 MiB text body allocates far less than its Base64 or text would take.
        [Fact]
        public void FromCapture_WithABound_HoldsALargerBodyAsItsBytes()
        {
            byte[] body = Encoding.ASCII.GetBytes(new string('a', 8 * 1024 * 1024));
            ProxyConnectionModel capture = Capture("PUT", "/large", body, "Content-Type: text/plain\r\n");
            RequestReplayDraft.FromCapture(Capture("PUT", "/warm", "warm"u8.ToArray()), 1, CancellationToken.None);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RequestReplayDraft draft = RequestReplayDraft.FromCapture(capture, 64 * 1024, CancellationToken.None);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 1024 * 1024, $"Drafting the large body allocated {allocated} bytes.");
            Assert.True(draft.BodyIsBase64);
            Assert.Same(body, draft.GetBodyBytes());
            Assert.Equal("https://example.com:8443/large", draft.Url);

            byte[] decoded = Encoding.ASCII.GetBytes(new string('b', 70_000));
            byte[] framed = [.. Encoding.ASCII.GetBytes($"{decoded.Length:X}\r\n"), .. decoded, .. "\r\n0\r\n\r\n"u8];
            RequestReplayDraft chunked = RequestReplayDraft.FromCapture(
                ImportedCapture("/", framed, "Transfer-Encoding: chunked\r\n"), 64 * 1024, CancellationToken.None);
            Assert.True(chunked.BodyIsBase64);
            Assert.Equal(decoded, chunked.GetBodyBytes());
        }

        // A cancelled draft stops with OperationCanceledException, whatever its body: text, encoded, empty, or larger
        // than the bound and held as bytes.
        [Fact]
        public void FromCapture_WithABound_StopsWhenCancelled()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            ProxyConnectionModel[] captures =
            {
                Capture("POST", "/text", "body"u8.ToArray()),
                Capture("POST", "/encoded", "body"u8.ToArray(), "Content-Encoding: gzip\r\n"),
                Capture("GET", "/empty", []),
                Capture("PUT", "/held", new byte[100]),
            };

            Assert.All(captures, capture => Assert.Throws<OperationCanceledException>(
                () => RequestReplayDraft.FromCapture(capture, 64, cancellation.Token)));
        }

        // A draft cancelled before it starts stops before its body is decoded. Malformed chunked framing, which
        // decoding refuses, gives OperationCanceledException, not FormatException, and a large chunked body is never
        // decoded.
        [Fact]
        public void FromCapture_WithABound_StopsWhenCancelled_BeforeDecodingTheBody()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            ProxyConnectionModel malformed = ImportedCapture("/", "4\r\nWiki\r\nZ\r\n\r\n"u8.ToArray(), "Transfer-Encoding: chunked\r\n");

            Assert.Throws<OperationCanceledException>(() => RequestReplayDraft.FromCapture(malformed, 64, cancellation.Token));

            byte[] decoded = Encoding.ASCII.GetBytes(new string('c', 4 * 1024 * 1024));
            byte[] framed = [.. Encoding.ASCII.GetBytes($"{decoded.Length:X}\r\n"), .. decoded, .. "\r\n0\r\n\r\n"u8];
            ProxyConnectionModel large = ImportedCapture("/", framed, "Transfer-Encoding: chunked\r\n");
            Assert.Throws<OperationCanceledException>(() => RequestReplayDraft.FromCapture(large, 64, cancellation.Token));

            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<OperationCanceledException>(() => RequestReplayDraft.FromCapture(large, 64, cancellation.Token));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 1024 * 1024, $"A cancelled draft allocated {allocated} bytes.");
            Assert.Equal(framed, large.RequestBody);
        }

        // The authority that a preview shows is the one that FromCapture sends to: the port only when it is not the
        // scheme's default, and an IPv6 host in brackets.
        [Theory]
        [InlineData("https", "example.com:8443", "https://example.com:8443")]
        [InlineData("https", "example.com:443", "https://example.com")]
        [InlineData("http", "example.test:80", "http://example.test")]
        [InlineData("http", "example.test", "http://example.test")]
        [InlineData("http", "[::1]:8080", "http://[::1]:8080")]
        [InlineData("http", "[2001:db8::1]", "http://[2001:db8::1]")]
        public void CaptureAuthority_IsTheAuthorityThatFromCaptureSendsTo(string scheme, string authority, string expected)
        {
            var capture = new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.Zero, scheme, authority, "GET", "/path?q=1",
                "GET /path?q=1 HTTP/1.1\r\n\r\n", [], "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");

            Assert.Equal(expected, RequestReplayDraft.CaptureAuthority(capture));
            Assert.Equal(expected + "/path?q=1", RequestReplayDraft.FromCapture(capture).Url);
        }

        private static ProxyConnectionModel ImportedCapture(string target, byte[] body, string headers = "")
        {
            using var stream = new MemoryStream();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
            var entry = archive.CreateEntry("raw/1_c.txt");
            using (var requestStream = entry.Open())
            {
                requestStream.Write(Encoding.ASCII.GetBytes(
                    $"POST https://example.com:8443{target} HTTP/1.1\r\nHost: example.com:8443\r\n{headers}\r\n"));
                requestStream.Write(body);
            }
            return FiddlerSazHandler.FromFiddlerFrame(1, entry, null, null);
        }

        internal static ProxyConnectionModel Capture(
            string method, string path, byte[] body, string headers = "")
        {
            return new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                "https", "example.com:8443", method, path,
                $"{method} {path} HTTP/1.1\r\n{headers}\r\n", body,
                "HTTP/1.1 200 OK\r\n\r\n", "old response"u8.ToArray(), "127.0.0.1");
        }
    }
}
