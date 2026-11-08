// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // Raw mode's requests (slice L, contract 2.2 to 2.4, tests 2.12 items 2 to 5): every rule with its code and line, the
    // refusal order, each limit at its value and one past it, Prepare's line breaks, ParseForEditor, Fix Content-Length,
    // and the image's immutability.
    public sealed class RawRequestTests
    {
        private const string Host = "Host: example.test\r\n";

        private static readonly ReplayRawTarget Target = ParseTarget("http://example.test");

        public static TheoryData<string, string, int?> Refusals => new()
        {
            // Head syntax, with the line.
            { "GET / HTTP/1.1\nHost: example.test\r\n\r\n", RawCodes.Invalid, 1 },
            { "GET / HTTP/1.1\r\nHost: exa\rmple.test\r\n\r\n", RawCodes.Invalid, 2 },
            { "GET / HTTP/1.1\r\n" + Host + "X-A: caf\u00e9\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-A: \u007f\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-A: a\u0001b\r\n\r\n", RawCodes.Invalid, 3 },
            { "\u00ef\u00bb\u00bfGET / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "\r\nGET / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "GET  / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "GET / HTTP/1.1 \r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { " GET / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "GET /\tx HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "GET /\r\n" + Host + "\r\n", RawCodes.Invalid, 1 },
            { "GET / HTTP/1.1\r\n" + Host + " folded\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "\tfolded\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-A value\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-A : v\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + ": v\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X(A): v\r\n\r\n", RawCodes.Invalid, 3 },
            { "GET / HTTP/1.1\r\n" + Host, RawCodes.Invalid, 3 },
            { string.Empty, RawCodes.Invalid, 1 },

            // Method.
            { "CONNECT example.test:80 HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "connect / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "get / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "Post / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "patch / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "GE\"T / HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },

            // Target.
            { "OPTIONS * HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET example.test:80 HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET a HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a#f HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET //a HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a%2 HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a%zz HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a\"b HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a[b] HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET /a\\b HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://user@example.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://other.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET https://example.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://example.test:8080/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://example.test HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://example.test//a HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://example.test?a HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },

            // The scheme and authority exactly as the target writes them, lower case (F-L27).
            { "GET HTTP://example.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET Http://example.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://EXAMPLE.test/ HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET http://example.TEST:80/ HTTP/1.1\r\nHost: example.TEST:80\r\n\r\n", RawCodes.Target, 1 },

            // Version.
            { "GET / HTTP/1.0\r\n" + Host + "\r\n", RawCodes.Version, 1 },
            { "GET / HTTP/2\r\n" + Host + "\r\n", RawCodes.Version, 1 },
            { "GET / http/1.1\r\n" + Host + "\r\n", RawCodes.Version, 1 },

            // Duplicates, ignoring ASCII case: the line of the repeat.
            { "GET / HTTP/1.1\r\n" + Host + "X-A: 1\r\nx-a: 2\r\n\r\n", RawCodes.DuplicateField, 4 },
            { "GET / HTTP/1.1\r\n" + Host + "host: example.test\r\n\r\n", RawCodes.DuplicateField, 3 },

            // Control headers.
            { "GET / HTTP/1.1\r\n" + Host + "X-OverrideHost: other.test\r\n\r\n", ReplayControlHeaders.Code, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "fiddler-host: other.test\r\n\r\n", ReplayControlHeaders.Code, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-IgnoreCertCNMismatch: 1\r\n\r\n", ReplayControlHeaders.Code, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "X-OverrideGateway: g\r\n\r\n", ReplayControlHeaders.Code, 3 },

            // Host: one, naming the target, with absolute-form too.
            { "GET / HTTP/1.1\r\nX-A: 1\r\n\r\n", RawCodes.Host, null },
            { "GET http://example.test/ HTTP/1.1\r\nX-A: 1\r\n\r\n", RawCodes.Host, null },
            { "GET / HTTP/1.1\r\nX-A: 1\r\nHost: other.test\r\n\r\n", RawCodes.Host, 3 },
            { "GET / HTTP/1.1\r\nHost: example.test:8080\r\n\r\n", RawCodes.Host, 2 },
            { "GET / HTTP/1.1\r\nHost: user@example.test\r\n\r\n", RawCodes.Host, 2 },
            { "GET / HTTP/1.1\r\nHost:\r\n\r\n", RawCodes.Host, 2 },

            // With absolute-form, Host is exactly the authority written there, port form included (RFC 9112 3.2, F-L27).
            { "GET http://example.test/a HTTP/1.1\r\nHost: Example.test\r\n\r\n", RawCodes.Host, 2 },
            { "GET http://example.test/a HTTP/1.1\r\nHost: example.test:80\r\n\r\n", RawCodes.Host, 2 },
            { "GET http://example.test:80/a HTTP/1.1\r\nHost: example.test\r\n\r\n", RawCodes.Host, 2 },
            { "GET http://example.test:80/a HTTP/1.1\r\nX-A: 1\r\nHost: EXAMPLE.TEST:80\r\n\r\n", RawCodes.Host, 3 },

            // Connection: absent or exactly close.
            { "GET / HTTP/1.1\r\n" + Host + "Connection: keep-alive\r\n\r\n", RawCodes.Connection, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Connection: Close\r\n\r\n", RawCodes.Connection, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Connection: close, x\r\n\r\n", RawCodes.Connection, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Keep-Alive: 5\r\n\r\n", RawCodes.Connection, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Proxy-Connection: close\r\n\r\n", RawCodes.Connection, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "TE: trailers\r\n\r\n", RawCodes.Connection, 3 },

            // Expect and Upgrade.
            { "POST / HTTP/1.1\r\n" + Host + "Expect: 100-continue\r\nContent-Length: 0\r\n\r\n", RawCodes.Expect, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Upgrade: websocket\r\n\r\n", RawCodes.Upgrade, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "HTTP2-Settings: AAA\r\n\r\n", RawCodes.Upgrade, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Sec-WebSocket-Key: x\r\n\r\n", RawCodes.Upgrade, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Sec-WebSocket-Version: 13\r\n\r\n", RawCodes.Upgrade, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Sec-WebSocket-Protocol: x\r\n\r\n", RawCodes.Upgrade, 3 },
            { "GET / HTTP/1.1\r\n" + Host + "Sec-WebSocket-Extensions: x\r\n\r\n", RawCodes.Upgrade, 3 },

            // Trailer.
            { "GET / HTTP/1.1\r\n" + Host + "Trailer: X-A\r\n\r\n", RawCodes.Trailer, 3 },

            // Framing.
            { Post("Content-Length: abc", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: +1", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: -1", string.Empty), RawCodes.Framing, 3 },
            { Post("Content-Length: 01", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 1, 1", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 1 1", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 00000000000000000001", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 99999999999999999999", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 2", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 0", "x"), RawCodes.Framing, 3 },
            { Post("Content-Length: 1\r\nTransfer-Encoding: chunked", "1\r\nx\r\n0\r\n\r\n"), RawCodes.Framing, 4 },
            { Post("Transfer-Encoding: chunked\r\nContent-Length: 1", "1\r\nx\r\n0\r\n\r\n"), RawCodes.Framing, 4 },
            { Post("Transfer-Encoding: Chunked", "0\r\n\r\n"), RawCodes.Framing, 3 },
            { Post("Transfer-Encoding: gzip, chunked", "0\r\n\r\n"), RawCodes.Framing, 3 },
            { Post("Transfer-Encoding: identity", string.Empty), RawCodes.Framing, 3 },
            { Post("X-A: 1", "x"), RawCodes.Framing, null },
            { Chunked("05\r\nhello\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("000000001\r\nx\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("123456789\r\nx\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("g\r\nx\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked(" 5\r\nhello\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("5 \r\nhello\r\n0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("5\r\nhelloX0\r\n\r\n"), RawCodes.Framing, null },
            { Chunked("5\r\nhel"), RawCodes.Framing, null },
            { Chunked("5\r\nhello\r\n"), RawCodes.Framing, null },
            { Chunked("0\r\n"), RawCodes.Framing, null },
            { Chunked("0\r\n\r\nx"), RawCodes.Framing, null },
            { Chunked("5;x=y\r\nhello\r\n0\r\n\r\n"), RawCodes.Trailer, null },
            { Chunked("0;x\r\n\r\n"), RawCodes.Trailer, null },
            { Chunked("0\r\nX-A: 1\r\n\r\n"), RawCodes.Trailer, null },

            // Methods and bodies.
            { "HEAD / HTTP/1.1\r\n" + Host + "Content-Length: 1\r\n\r\nx", RawCodes.BodyNotAllowed, 1 },
            { "HEAD / HTTP/1.1\r\n" + Host + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n", RawCodes.BodyNotAllowed, 1 },
            { "TRACE / HTTP/1.1\r\n" + Host + "Content-Length: 1\r\n\r\nx", RawCodes.BodyNotAllowed, 1 },
            { "TRACE / HTTP/1.1\r\n" + Host + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n", RawCodes.BodyNotAllowed, 1 },
        };

        // Two faults at adjacent stages of the refusal order: the earlier stage's code is the one reported.
        public static TheoryData<string, string, int?> RefusalOrder => new()
        {
            { "CONNECT / HTTP/1.1\r\nX-A: a\rb\r\n" + Host + "\r\n", RawCodes.Invalid, 2 },
            { "CONNECT * HTTP/1.1\r\n" + Host + "\r\n", RawCodes.Method, 1 },
            { "GET * HTTP/1.0\r\n" + Host + "\r\n", RawCodes.Target, 1 },
            { "GET / HTTP/1.0\r\n" + Host + Host + "\r\n", RawCodes.Version, 1 },
            { "GET / HTTP/1.1\r\n" + Host + Host + "X-OverrideHost: x\r\n\r\n", RawCodes.DuplicateField, 3 },
            { "GET / HTTP/1.1\r\nX-OverrideHost: x\r\n\r\n", ReplayControlHeaders.Code, 2 },
            { "GET / HTTP/1.1\r\nHost: other.test\r\nConnection: keep-alive\r\n\r\n", RawCodes.Host, 2 },
            { "GET / HTTP/1.1\r\n" + Host + "Expect: 100-continue\r\nConnection: keep-alive\r\n\r\n", RawCodes.Connection, 4 },
            { "GET / HTTP/1.1\r\n" + Host + "Trailer: x\r\nUpgrade: h2c\r\n\r\n", RawCodes.Upgrade, 4 },
            { "POST / HTTP/1.1\r\n" + Host + "Content-Length: 5\r\nTrailer: x\r\n\r\nhi", RawCodes.Trailer, 4 },
            { "HEAD / HTTP/1.1\r\n" + Host + "Content-Length: 5\r\n\r\nhi", RawCodes.Framing, 3 },
        };

        [Theory]
        [MemberData(nameof(Refusals))]
        [MemberData(nameof(RefusalOrder))]
        public void FromBytes_RefusesEachRule_WithItsCodeAndLine(string request, string code, int? line)
        {
            AssertRefused(FromBytes(request), code, line);
        }

        [Theory]
        [InlineData("GET / HTTP/1.1\r\nhost: EXAMPLE.TEST:80\r\n\r\n", "GET", "/", "None", 0, false)]
        [InlineData("GET http://example.test/a HTTP/1.1\r\n" + Host + "\r\n", "GET", "http://example.test/a", "None", 0, false)]
        [InlineData("GET http://example.test:80/a?b HTTP/1.1\r\nHost: \texample.test:80 \r\n\r\n", "GET", "http://example.test:80/a?b", "None", 0, false)]
        [InlineData("GET /!$&'()*+,;=:@-._~%2Fa/b?c/d?e HTTP/1.1\r\n" + Host + "\r\n", "GET", "/!$&'()*+,;=:@-._~%2Fa/b?c/d?e", "None", 0, false)]
        [InlineData("GET / HTTP/1.1\r\n" + Host + "Connection: close\r\n\r\n", "GET", "/", "None", 0, false)]
        [InlineData("GET / HTTP/1.1\r\n" + Host + "X-Empty:\r\nX-Tab: a\tb\r\n\r\n", "GET", "/", "None", 0, false)]
        [InlineData("PURGE / HTTP/1.1\r\n" + Host + "\r\n", "PURGE", "/", "None", 0, true)]
        [InlineData("purge / HTTP/1.1\r\n" + Host + "\r\n", "purge", "/", "None", 0, true)]
        [InlineData("HEAD / HTTP/1.1\r\n" + Host + "Content-Length: 0\r\n\r\n", "HEAD", "/", "ContentLength", 0, false)]
        [InlineData("GET / HTTP/1.1\r\n" + Host + "Content-Length: 0\r\n\r\n", "GET", "/", "ContentLength", 0, false)]
        [InlineData("GET / HTTP/1.1\r\n" + Host + "Content-Length: 1\r\n\r\nx", "GET", "/", "ContentLength", 1, true)]
        [InlineData("OPTIONS / HTTP/1.1\r\n" + Host + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "OPTIONS", "/", "Chunked", 0, true)]
        [InlineData("POST / HTTP/1.1\r\n" + Host + "Content-Length: 5\r\n\r\nhello", "POST", "/", "ContentLength", 5, true)]
        [InlineData("POST / HTTP/1.1\r\n" + Host + "content-length: 4\r\n\r\n\u0000\u00ff\r\n", "POST", "/", "ContentLength", 4, true)]
        [InlineData("POST / HTTP/1.1\r\n" + Host + "Transfer-Encoding: chunked\r\n\r\n5\r\nhello\r\nA\r\n0123456789\r\na\r\n0123456789\r\n0\r\n\r\n", "POST", "/", "Chunked", 25, true)]
        public void FromBytes_AdmitsAValidRequest(
            string request, string method, string requestTarget, string framing, long entityLength, bool unsafeMethod)
        {
            byte[] bytes = Latin1(request);

            RawRequestImage image = AssertAdmitted(FromBytes(request));

            Assert.Equal((method, requestTarget, Enum.Parse<RawFraming>(framing)), (image.Method, image.RequestTarget, image.Framing));
            Assert.Equal((entityLength, unsafeMethod), (image.EntityLength, image.UsesUnsafeMethod));
            Assert.Equal(bytes.Length, image.Length);
            Assert.Equal(bytes, image.CopyHead().Concat(image.CopyWireBody()));
            Assert.Equal(Hex(SHA256.HashData(bytes)), image.Sha256);
            Assert.Equal(0, image.ConvertedLineFeeds);
        }

        [Fact]
        public void FromBytes_DerivesEveryMember_FromItsCopy()
        {
            const string head = "POST /a?b=1 HTTP/1.1\r\nHost: example.test\r\nX-A: \t one two \t\r\nContent-Length: 5\r\n\r\n";
            byte[] bytes = Latin1(head + "hello");

            RawRequestImage image = AssertAdmitted(RawRequest.FromBytes(Target, bytes, false, CancellationToken.None));

            Assert.Same(Target, image.Target);
            Assert.Equal((bytes.Length, head.Length, 3), (image.Length, image.HeadLength, image.FieldCount));
            Assert.Equal(
                new[] { Field("Host", "example.test"), Field("X-A", "one two"), Field("Content-Length", "5") },
                image.Fields);
            Assert.True(((ICollection<KeyValuePair<string, string>>)image.Fields).IsReadOnly);
            Assert.Equal("http://example.test/a?b=1", image.RequestUri.AbsoluteUri);
            Assert.Equal(Latin1(head), image.CopyHead());
            Assert.Equal(Latin1("hello"), image.CopyWireBody());
            Assert.Equal(Latin1("hello"), image.CopyEntity());
            Assert.Equal(image.Sha256, image.ComputeSha256(CancellationToken.None));
        }

        [Fact]
        public void RequestUri_JoinsTheTargetsOriginAndTheOriginForm_WithoutCanonicalizing()
        {
            RawRequestImage image = AssertAdmitted(FromBytes("GET /a/./b/../c%2f?q=%41/.. HTTP/1.1\r\n" + Host + "\r\n"));
            Assert.Equal("http://example.test/a/./b/../c%2f?q=%41/..", image.RequestUri.OriginalString);

            RawRequestImage absolute = AssertAdmitted(FromBytes("GET http://example.test:80/x HTTP/1.1\r\nHost: example.test:80\r\n\r\n"));
            Assert.Equal("http://example.test:80/x", absolute.RequestTarget);
            Assert.Equal("http://example.test/x", absolute.RequestUri.OriginalString);

            ReplayRawTarget ipv6 = ParseTarget("https://[::1]:8443");
            RawRequestImage literal = AssertAdmitted(RawRequest.FromBytes(
                ipv6, Latin1("GET /x HTTP/1.1\r\nHost: [::1]:8443\r\n\r\n"), false, CancellationToken.None));
            Assert.Equal("https://[::1]:8443/x", literal.RequestUri.OriginalString);
        }

        [Fact]
        public void ToMessage_ReplacesChunkedFramingInPlace_WithTheEntitysLength()
        {
            RawRequestImage chunked = AssertAdmitted(FromBytes(
                "POST /p?q HTTP/1.1\r\n" + Host + "Transfer-Encoding: chunked\r\nX-A:  a  \r\n\r\n" +
                "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n"));

            ReplayMessage message = chunked.ToMessage();

            Assert.Equal("POST /p?q HTTP/1.1", message.StartLine);
            Assert.Equal(
                new[] { Field("Host", "example.test"), Field("Content-Length", "11"), Field("X-A", "a") },
                message.Headers);
            Assert.Empty(message.ContentHeaders);
            Assert.Equal(Latin1("hello world"), message.Body);

            RawRequestImage plain = AssertAdmitted(FromBytes(Post("Content-Length: 2", "hi")));
            Assert.Equal(new[] { Field("Host", "example.test"), Field("Content-Length", "2") }, plain.ToMessage().Headers);
            Assert.Equal(Latin1("hi"), plain.ToMessage().Body);
        }

        [Fact]
        public void Limits_Head_RequestLine_FieldLines_AndMethod_AtTheirValues_AndOnePast()
        {
            AssertAdmitted(FromBytes(HeadOfLength(RawRequest.MaxHeadBytes)));
            AssertRefused(FromBytes(HeadOfLength(RawRequest.MaxHeadBytes + 1)), RawCodes.InputTooLarge, null);

            AssertAdmitted(FromBytes(RequestLineOfLength(RawRequest.MaxRequestLineBytes)));
            AssertRefused(FromBytes(RequestLineOfLength(RawRequest.MaxRequestLineBytes + 1)), RawCodes.InputTooLarge, null);

            AssertAdmitted(FromBytes(WithFieldLines(RawRequest.MaxFieldLines)));
            AssertRefused(FromBytes(WithFieldLines(RawRequest.MaxFieldLines + 1)), RawCodes.InputTooLarge, null);

            AssertAdmitted(FromBytes(new string('M', RawRequest.MaxMethodChars) + " / HTTP/1.1\r\n" + Host + "\r\n"));
            AssertRefused(
                FromBytes(new string('M', RawRequest.MaxMethodChars + 1) + " / HTTP/1.1\r\n" + Host + "\r\n"),
                RawCodes.InputTooLarge,
                null);
        }

        [Fact]
        public void Limits_TheBodyBudgets_AtTheirValues_AndOnePast()
        {
            var entity = new RawImageLimits(entityBytes: 10);
            AssertAdmitted(FromBytes(Post("Content-Length: 10", "0123456789"), entity));
            AssertRefused(FromBytes(Post("Content-Length: 11", "0123456789A"), entity), RawCodes.InputTooLarge, null);
            AssertAdmitted(FromBytes(Chunked("5\r\n01234\r\n5\r\n56789\r\n0\r\n\r\n"), entity));
            AssertRefused(FromBytes(Chunked("5\r\n01234\r\n6\r\n56789A\r\n0\r\n\r\n"), entity), RawCodes.InputTooLarge, null);

            // One chunk of 5: its line (3), its CRLF (2), the last chunk's line (3) and the empty line (2).
            const string body = "5\r\nhello\r\n0\r\n\r\n";
            AssertAdmitted(FromBytes(Chunked(body), new RawImageLimits(chunkFramingBytes: 10)));
            AssertRefused(FromBytes(Chunked(body), new RawImageLimits(chunkFramingBytes: 9)), RawCodes.InputTooLarge, null);

            string sixteen = Chunked("10\r\n0123456789abcdef\r\n0\r\n\r\n");
            AssertAdmitted(FromBytes(sixteen, new RawImageLimits(chunkLineBytes: 4)));
            AssertRefused(FromBytes(sixteen, new RawImageLimits(chunkLineBytes: 3)), RawCodes.InputTooLarge, null);

            // The whole request: a head, an entity and all of its framing at most.
            var small = new RawImageLimits(entityBytes: 4, chunkFramingBytes: 1);
            string head = "GET / HTTP/1.1\r\n" + Host + "\r\n";
            int room = RawRequest.MaxHeadBytes + 5 - head.Length;
            AssertRefused(FromBytes(head + new string('x', room), small), RawCodes.Framing, null);
            AssertRefused(FromBytes(head + new string('x', room + 1), small), RawCodes.InputTooLarge, null);
        }

        [Fact]
        public void Limits_ComeFirst_WheneverTheyCanBeRead()
        {
            // A head over its limit, whatever else is wrong with it.
            AssertRefused(
                FromBytes("GET / HTTP/1.1\n" + Host + "X-Pad: " + new string('a', RawRequest.MaxHeadBytes) + "\r\n\r\n"),
                RawCodes.InputTooLarge,
                null);

            // A well-formed head's one framing field declares the body that the budgets apply to, before every later
            // stage.
            var entity = new RawImageLimits(entityBytes: 4);
            AssertRefused(
                FromBytes("CONNECT * HTTP/1.1\r\n" + Host + Host + "Content-Length: 1\r\n\r\nhello", entity),
                RawCodes.InputTooLarge,
                null);
            AssertRefused(
                FromBytes("POST / HTTP/1.1\r\n" + Host + "Expect: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n", entity),
                RawCodes.InputTooLarge,
                null);

            // A head that is not well formed declares no framing, so its syntax is refused.
            AssertRefused(
                FromBytes("POST / HTTP/1.1\r\nX-A: a\rb\r\n" + Host + "Content-Length: 5\r\n\r\nhello", entity),
                RawCodes.Invalid,
                2);

            // So does a head that declares two framings: framing refuses it, in its turn.
            AssertRefused(
                FromBytes(Post("Content-Length: 5\r\nTransfer-Encoding: chunked", "hello"), entity),
                RawCodes.Framing,
                4);

            // A chunked body's budgets are read up to its first structural fault, which stage 12 then refuses.
            AssertRefused(
                FromBytes(Chunked("g\r\n" + "0123456789" + "\r\n0\r\n\r\n"), entity),
                RawCodes.Framing,
                null);
        }

        [Theory]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test", 0)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\r\n", 0)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n", 0)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n\r\n", 0)]
        [InlineData("GET / HTTP/1.1\nHost: example.test", 1)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\n", 2)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\n\n", 3)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\n\n\n", 3)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\n\n\n\n\n", 3)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\r\n\n", 1)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\n\r\n", 1)]
        [InlineData("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n\n", 0)]
        public void Prepare_WritesEveryLineBreakAsCrLf_AndCountsTheLineFeedsItConverts(string head, int lineFeeds)
        {
            RawRequestImage image = AssertAdmitted(RawRequest.Prepare(head, default, Target, CancellationToken.None));

            Assert.Equal(Latin1("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n"), image.CopyHead());
            Assert.Equal(lineFeeds, image.ConvertedLineFeeds);
            RawHeadCounts counts = RawRequest.CountHead(head);
            Assert.Equal((image.HeadLength, lineFeeds), ((int)counts.HeadBytes, counts.LineFeedsToConvert));
        }

        [Theory]
        [InlineData("GET / HTTP/1.1\rHost: example.test", 1)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\r", 2)]
        [InlineData("GET / HTTP/1.1\n\nHost: example.test", 2)]
        [InlineData("GET / HTTP/1.1\r\n\r\nHost: example.test", 2)]
        [InlineData("\nGET / HTTP/1.1\nHost: example.test", 1)]
        [InlineData("GET / HTTP/1.1\nHost: ex\u00e4mple.test", 2)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nX-A: \u2028", 3)]
        [InlineData("\uFEFFGET / HTTP/1.1\nHost: example.test", 1)]
        [InlineData("", 1)]
        [InlineData("\n\n", 1)]
        public void Prepare_RefusesWhatItWouldHaveToRepair(string head, int line)
        {
            AssertRefused(RawRequest.Prepare(head, default, Target, CancellationToken.None), RawCodes.Invalid, line);
        }

        [Fact]
        public void Prepare_SendsTheBodyUnchanged_AndRefusesAnOversizedBody_BeforeTheHead()
        {
            byte[] body = Latin1("a\nb\r\u00ff");
            RawRequestImage image = AssertAdmitted(RawRequest.Prepare(
                "POST / HTTP/1.1\nHost: example.test\nContent-Length: 5", body, Target, CancellationToken.None));
            Assert.Equal(body, image.CopyWireBody());
            Assert.Equal(2, image.ConvertedLineFeeds);
            Assert.True(image.UsesUnsafeMethod);

            var small = new RawImageLimits(entityBytes: 4, chunkFramingBytes: 1);
            AssertRefused(
                RawRequest.Prepare("not even a head\r", new byte[6], Target, CancellationToken.None, small),
                RawCodes.InputTooLarge,
                null);
            AssertRefused(
                RawRequest.Prepare("POST / HTTP/1.1\nHost: example.test\nContent-Length: 5", new byte[5], Target, CancellationToken.None, small),
                RawCodes.InputTooLarge,
                null);
        }

        [Fact]
        public void EveryRawRequestMethod_StopsWhenCancelled_BeforeItStarts_AndAfterTheCopy()
        {
            const string head = "POST / HTTP/1.1\nHost: example.test\nContent-Length: 2";
            byte[] request = Latin1("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 2\r\n\r\nhi");
            byte[] body = Latin1("hi");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.Throws<OperationCanceledException>(() => RawRequest.FromBytes(Target, request, false, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => RawRequest.FromBytes(Target, request, true, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => RawRequest.Prepare(head, body, Target, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => RawRequest.ParseForEditor(head, body, Target, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => RawRequest.FixContentLength(head, 2, cancelled.Token));

            using var fromBytes = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => RawRequestImage.FromBytes(
                Target, request, true, RawImageLimits.Production, fromBytes.Token, fromBytes.Cancel));
            using var prepare = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => RawRequestImage.Prepare(
                head, body, Target, RawImageLimits.Production, prepare.Token, prepare.Cancel));
        }

        // The scans of a head's text check their token inside their loops, every CheckChars characters, so a cancellation
        // during a long scan stops it there: here, a valid head followed by a long run of line breaks, which are dropped
        // and must all be read. A check that only needs the limits also stops at the first limit passed.
        [Fact]
        public void TheHeadScans_StopWhenCancelledInTheirLoops_AndAtTheFirstLimitPassed()
        {
            string head = "GET / HTTP/1.1\nHost: example.test" + new string('\n', 8 * RawRequest.CheckChars);
            Assert.Null(RawRequest.Prepare(head, default, Target, CancellationToken.None).Code);
            Assert.Equal(RawCodes.FixContentLength, RawRequest.FixContentLength(head, 0, CancellationToken.None).Code);

            using var prepare = new CancellationTokenSource();
            var prepareChecks = new List<long>();
            Assert.Throws<OperationCanceledException>(() => RawRequestImage.Prepare(
                head, default, Target, RawImageLimits.Production, prepare.Token, checkpoint: read =>
                {
                    prepareChecks.Add(read);
                    prepare.Cancel();
                }));
            Assert.Equal(new long[] { RawRequest.CheckChars }, prepareChecks);

            using var fix = new CancellationTokenSource();
            var fixChecks = new List<long>();
            Assert.Throws<OperationCanceledException>(() => RawRequest.FixContentLength(head, 0, fix.Token, read =>
            {
                fixChecks.Add(read);
                fix.Cancel();
            }));
            Assert.Equal(new long[] { RawRequest.CheckChars }, fixChecks);

            // The checks count every character read, the trailing breaks' first: here the first check falls in the lines.
            string crossing = "GET / HTTP/1.1\nHost: example.test" + new string('\n', RawRequest.CheckChars - 10);
            var crossingChecks = new List<long>();
            Assert.Null(RawRequestImage.Prepare(
                crossing, default, Target, RawImageLimits.Production, CancellationToken.None, checkpoint: crossingChecks.Add).Code);
            Assert.Equal(new long[] { RawRequest.CheckChars }, crossingChecks);

            string over = "GET / HTTP/1.1\n" + string.Concat(Enumerable.Repeat("X-A: 1\n", 200_000));
            var checks = new List<long>();
            RawHeadCounts counts = RawRequest.CountHead(over, RawImageLimits.Production, CancellationToken.None, checks.Add);
            Assert.True(counts.OverLimit);
            Assert.Empty(checks);
            Assert.True(RawRequest.CountHead(over).OverLimit);
            Assert.Equal(RawCodes.InputTooLarge, RawRequest.Prepare(over, default, Target, CancellationToken.None).Code);
        }

        [Theory]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nX-A: 1\nx-a: 2", "")]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nExpect: 100-continue\nContent-Length: 0", "")]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nUpgrade: websocket\nConnection: Upgrade", "")]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nConnection: keep-alive\nKeep-Alive: 5\nTE: trailers", "")]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nTrailer: X-A", "")]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nX-OverrideHost: other.test", "")]
        [InlineData("GET / HTTP/1.1\nX-A: 1", "")]
        [InlineData("CONNECT / HTTP/1.1\nHost: example.test", "")]
        [InlineData("get / HTTP/1.1\nHost: example.test", "")]
        [InlineData("HEAD / HTTP/1.1\nHost: example.test\nContent-Length: 1", "x")]
        public void ParseForEditor_AcceptsWhatParsedModeChecksInItsOwnWords(string head, string body)
        {
            byte[] bytes = Latin1(body);
            Assert.NotNull(RawRequest.Prepare(head, bytes, Target, CancellationToken.None).Code);

            RawEditorParseResult result = RawRequest.ParseForEditor(head, bytes, Target, CancellationToken.None);

            Assert.Equal((null, (int?)null), (result.Code, result.Line));
            Assert.NotNull(result.Parse);
        }

        [Theory]
        [InlineData("GET / HTTP/1.1\nHost: other.test", "", RawCodes.Host, 2)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nhost: other.test", "", RawCodes.Host, 3)]
        [InlineData("GET http://other.test/ HTTP/1.1\nHost: example.test", "", RawCodes.Target, 1)]
        [InlineData("GET HTTP://example.test/ HTTP/1.1\nHost: example.test", "", RawCodes.Target, 1)]
        [InlineData("GET http://example.test/ HTTP/1.1\nHost: EXAMPLE.test", "", RawCodes.Host, 2)]
        [InlineData("GET http://example.test:80/ HTTP/1.1\nHost: example.test", "", RawCodes.Host, 2)]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nContent-Length: 1\nTransfer-Encoding: chunked", "0\r\n\r\n", RawCodes.Framing, 4)]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nContent-Length: 1\ncontent-length: 1", "x", RawCodes.Framing, 4)]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nTransfer-Encoding: chunked", "1;x\r\na\r\n0\r\n\r\n", RawCodes.Trailer, null)]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nTransfer-Encoding: chunked", "0\r\nX-A: 1\r\n\r\n", RawCodes.Trailer, null)]
        [InlineData("POST / HTTP/1.1\nHost: example.test\nContent-Length: 2", "x", RawCodes.Framing, 3)]
        [InlineData("GET / HTTP/1.0\nHost: example.test", "", RawCodes.Version, 1)]
        // The version is checked before the target (design 1.3), unlike send admission.
        [InlineData("GET * HTTP/1.0\nHost: example.test", "", RawCodes.Version, 1)]
        [InlineData("GET / HTTP/1.1\nHost: example.test\nX-A: a\u0001", "", RawCodes.Invalid, 3)]
        public void ParseForEditor_RefusesRoutingFramingAndSyntax_AsPrepareDoes(
            string head, string body, string code, int? line)
        {
            byte[] bytes = Latin1(body);
            Assert.NotNull(RawRequest.Prepare(head, bytes, Target, CancellationToken.None).Code);

            RawEditorParseResult result = RawRequest.ParseForEditor(head, bytes, Target, CancellationToken.None);

            Assert.Null(result.Parse);
            Assert.Equal((code, line), (result.Code, result.Line));
        }

        [Fact]
        public void ParseForEditor_DecodesAChunkedBody_AndKeepsWhatWasWritten()
        {
            const string head =
                "POST http://example.test:80/a?b HTTP/1.1\nHost: example.test:80\nX-A:  spaced  \nTransfer-Encoding: chunked";
            byte[] body = Latin1("5\r\nhello\r\n1\r\n \r\n5\r\nworld\r\n0\r\n\r\n");

            RawEditorParse parse = RawRequest.ParseForEditor(head, body, Target, CancellationToken.None).Parse;

            Assert.Same(Target, parse.Target);
            Assert.Equal(("POST", "http://example.test:80/a?b"), (parse.Method, parse.RequestTarget));
            Assert.Equal((true, "/a?b"), (parse.AbsoluteForm, parse.OriginForm));
            Assert.Equal(
                new[] { Field("Host", "example.test:80"), Field("X-A", "spaced"), Field("Transfer-Encoding", "chunked") },
                parse.Fields);
            Assert.Equal(
                new[] { "Host: example.test:80", "X-A:  spaced  ", "Transfer-Encoding: chunked" },
                parse.FieldText);
            Assert.Equal(RawFraming.Chunked, parse.Framing);
            Assert.Equal(Latin1("hello world"), parse.Body.ToArray());
            Assert.Equal(11L, parse.EntityLength);
            Assert.Equal(new[] { 5, 1, 5 }, parse.ChunkSizes);
            Assert.Equal(3, parse.ConvertedLineFeeds);

            RawEditorParse plain = RawRequest.ParseForEditor(
                "PUT /x HTTP/1.1\r\nHost: example.test\r\nContent-Length: 3\r\n", Latin1("a\rb"), Target, CancellationToken.None).Parse;
            Assert.Equal((false, "/x", RawFraming.ContentLength), (plain.AbsoluteForm, plain.OriginForm, plain.Framing));
            Assert.Equal(Latin1("a\rb"), plain.Body.ToArray());
            Assert.Empty(plain.ChunkSizes);
            Assert.Equal(0, plain.ConvertedLineFeeds);
        }

        [Fact]
        public void OnlyTheChecksMakeAnImage_AndNoApiButTheConversionTakesAnEditorParse()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly;
            Assert.All(typeof(RawRequestImage).GetConstructors(all), constructor => Assert.True(constructor.IsPrivate));
            Assert.All(typeof(RawEditorParse).GetConstructors(all), constructor => Assert.True(constructor.IsPrivate));

            MethodBase[] members = typeof(RawRequest).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                .ToArray();
            // An auto-property's accessor only hands back an image that a maker made; any other method is a maker.
            string[] makers = members
                .Where(member => member is MethodInfo method &&
                    (method.ReturnType == typeof(RawRequestImage) || method.ReturnType == typeof(RawImageResult)) &&
                    member.DeclaringType != typeof(RawImageResult) &&
                    !method.IsDefined(typeof(CompilerGeneratedAttribute), false))
                .Select(Describe)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "RawRequest.FromBytes", "RawRequest.Prepare", "RawRequestImage.Admit", "RawRequestImage.FromBytes",
                    "RawRequestImage.Prepare", "RawRequestImage.Refused",
                },
                makers);

            string[] takers = members
                .Where(member => member.GetParameters().Any(parameter => parameter.ParameterType == typeof(RawEditorParse)) &&
                    member.DeclaringType != typeof(RawEditorParseResult))
                .Select(Describe)
                .ToArray();
            Assert.All(takers, taker => Assert.Equal("RawConversion.ToDraft", taker));
        }

        [Theory]
        [InlineData("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 7\r\n", 5, "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5\r\n")]
        [InlineData("POST / HTTP/1.1\nHost: example.test\ncontent-length:\t 0 \t\nX-A: 1", 12, "POST / HTTP/1.1\nHost: example.test\ncontent-length:\t 12 \t\nX-A: 1")]
        [InlineData("POST / HTTP/1.1\nContent-Length: abc", 0, "POST / HTTP/1.1\nContent-Length: 0")]
        [InlineData("POST / HTTP/1.1\nContent-Length:", 3, "POST / HTTP/1.1\nContent-Length:3")]
        [InlineData("POST / HTTP/1.1\nContent-Length: 1\n\nContent-Length: 1", 2, "POST / HTTP/1.1\nContent-Length: 2\n\nContent-Length: 1")]
        public void FixContentLength_ReplacesOnlyTheValue(string head, long length, string expected)
        {
            RawTextResult result = RawRequest.FixContentLength(head, length, CancellationToken.None);

            Assert.Equal((expected, null, (int?)null), (result.Text, result.Code, result.Line));
        }

        [Theory]
        [InlineData("POST / HTTP/1.1\nHost: example.test")]
        [InlineData("POST / HTTP/1.1\nContent-Length: 1\ncontent-length: 1")]
        [InlineData("POST / HTTP/1.1\nContent-Length: 1\nTransfer-Encoding: chunked")]
        [InlineData("POST / HTTP/1.1\nTransfer-Encoding: chunked")]
        [InlineData("Content-Length: 1\nHost: example.test")]
        [InlineData("")]
        public void FixContentLength_NeedsExactlyOneContentLength_AndNoTransferEncoding(string head)
        {
            RawTextResult result = RawRequest.FixContentLength(head, 1, CancellationToken.None);

            Assert.Equal((null, RawCodes.FixContentLength, (int?)null), (result.Text, result.Code, result.Line));
        }

        [Fact]
        public void FixContentLength_RefusesAHeadOverTheLimits_AndANegativeLength()
        {
            string head = "POST / HTTP/1.1\nContent-Length: 1\nX-Pad: " + new string('a', RawRequest.MaxHeadBytes);
            Assert.Equal(RawCodes.InputTooLarge, RawRequest.FixContentLength(head, 1, CancellationToken.None).Code);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RawRequest.FixContentLength("POST / HTTP/1.1\nContent-Length: 1", -1, CancellationToken.None));
        }

        [Fact]
        public void FromBytes_FixesContentLength_OnItsCopy_ThenChecksTheResultInFull()
        {
            byte[] source = Latin1("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length:  99 \r\n\r\nhello");
            byte[] original = (byte[])source.Clone();

            RawRequestImage image = AssertAdmitted(RawRequest.FromBytes(Target, source, true, CancellationToken.None));

            Assert.Equal(Latin1("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length:  5 \r\n\r\n"), image.CopyHead());
            Assert.Equal(Latin1("hello"), image.CopyEntity());
            Assert.Equal(original, source);
            Assert.Equal(Hex(SHA256.HashData(image.CopyHead().Concat(image.CopyWireBody()).ToArray())), image.Sha256);

            AssertRefused(
                FromBytes("POST /p HTTP/1.1\r\n" + Host + "X-A: 1\r\nX-A: 2\r\nContent-Length: 0\r\n\r\nhi", fix: true),
                RawCodes.DuplicateField,
                4);
            AssertRefused(FromBytes("POST /p HTTP/1.1\r\n" + Host + "\r\nhi", fix: true), RawCodes.FixContentLength, null);
            AssertRefused(FromBytes("POST /p HTTP/1.1\r\n" + Host + "Content-Length: 0\r\n", fix: true), RawCodes.Invalid, 4);
        }

        [Fact]
        public void Image_IsAPrivateCopy_ThatTheCallersBufferCannotChange()
        {
            byte[] buffer = Latin1("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5\r\n\r\nhello");
            byte[] original = (byte[])buffer.Clone();
            string hash = Hex(SHA256.HashData(original));

            RawRequestImage during = AssertAdmitted(RawRequestImage.FromBytes(
                Target, buffer, false, RawImageLimits.Production, CancellationToken.None, () => buffer.AsSpan().Fill((byte)'X')));
            Assert.Equal((hash, hash), (during.Sha256, during.ComputeSha256(CancellationToken.None)));
            Assert.Equal(original, during.CopyHead().Concat(during.CopyWireBody()));

            original.CopyTo(buffer, 0);
            RawRequestImage after = AssertAdmitted(RawRequest.FromBytes(Target, buffer, false, CancellationToken.None));
            buffer.AsSpan().Fill((byte)'Y');
            Assert.Equal((hash, hash), (after.Sha256, after.ComputeSha256(CancellationToken.None)));
            Assert.Equal(original, after.CopyHead().Concat(after.CopyWireBody()));

            byte[] body = Latin1("hello");
            RawRequestImage prepared = AssertAdmitted(RawRequestImage.Prepare(
                "POST /p HTTP/1.1\nHost: example.test\nContent-Length: 5", body, Target, RawImageLimits.Production,
                CancellationToken.None, () => body.AsSpan().Fill((byte)'Z')));
            Assert.Equal(Latin1("hello"), prepared.CopyEntity());
            Assert.Equal(hash, prepared.Sha256);

            byte[] copy = prepared.CopyEntity();
            copy[0] = (byte)'J';
            Assert.Equal(Latin1("hello"), prepared.CopyEntity());
            Assert.NotSame(prepared.CopyHead(), prepared.CopyHead());
            Assert.Equal(hash, prepared.ComputeSha256(CancellationToken.None));
        }

        [Fact]
        public async Task WriteTo_HandsTheStreamScratchBuffers_NeverTheImage()
        {
            byte[] request = Latin1("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length: 40000\r\n\r\n")
                .Concat(Enumerable.Range(0, 40_000).Select(index => (byte)(index % 251)))
                .ToArray();
            RawRequestImage image = AssertAdmitted(RawRequest.FromBytes(Target, request, false, CancellationToken.None));
            var privateArray = (byte[])typeof(RawRequestImage)
                .GetField("_image", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(image);

            var stream = new RetainingStream();
            var progress = new List<long>();
            await image.WriteToAsync(stream, new SyncProgress(progress.Add), CancellationToken.None);

            Assert.Equal(request, stream.Received.ToArray());
            Assert.Equal(new long[] { 16_384, 32_768, request.Length }, progress);
            Assert.All(stream.Buffers, kept =>
            {
                Assert.NotSame(privateArray, kept);
                Assert.True(kept.Length <= RawRequestImage.WriteBytes);
            });

            foreach (byte[] kept in stream.Buffers)
            {
                kept.AsSpan().Fill(0xEE);
            }

            Assert.Equal(image.Sha256, image.ComputeSha256(CancellationToken.None));
            var again = new RetainingStream();
            image.WriteTo(again);
            Assert.Equal(request, again.Received.ToArray());
            Assert.All(again.Buffers, kept => Assert.NotSame(privateArray, kept));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var none = new RetainingStream();
            await Assert.ThrowsAsync<OperationCanceledException>(() => image.WriteToAsync(none, null, cancelled.Token));
            Assert.Equal(0, none.Received.Length);
        }

        [Fact]
        public void CountHead_CountsWhatPrepareWrites_AndSaysWhenALimitIsExceeded()
        {
            const string head = "PATCH /abc HTTP/1.1\nHost: example.test\r\nX-A: 1\n\n";
            RawHeadCounts counts = RawRequest.CountHead(head);
            RawRequestImage image = AssertAdmitted(RawRequest.Prepare(head, default, Target, CancellationToken.None));

            Assert.Equal(image.HeadLength, counts.HeadBytes);
            Assert.Equal(("PATCH /abc HTTP/1.1\r\n".Length, 2, 5L), (counts.RequestLineBytes, counts.FieldLines, counts.MethodChars));
            Assert.Equal((image.ConvertedLineFeeds, 3), (counts.LineFeedsToConvert, counts.LineFeedsToConvert));
            Assert.False(counts.OverLimit);

            Assert.False(RawRequest.CountHead(HeadOfLength(RawRequest.MaxHeadBytes)).OverLimit);
            Assert.True(RawRequest.CountHead(HeadOfLength(RawRequest.MaxHeadBytes + 1)).OverLimit);
            Assert.False(RawRequest.CountHead(RequestLineOfLength(RawRequest.MaxRequestLineBytes)).OverLimit);
            Assert.True(RawRequest.CountHead(RequestLineOfLength(RawRequest.MaxRequestLineBytes + 1)).OverLimit);
            Assert.False(RawRequest.CountHead(WithFieldLines(RawRequest.MaxFieldLines)).OverLimit);
            Assert.True(RawRequest.CountHead(WithFieldLines(RawRequest.MaxFieldLines + 1)).OverLimit);
            Assert.False(RawRequest.CountHead(new string('M', RawRequest.MaxMethodChars) + " / HTTP/1.1").OverLimit);
            Assert.True(RawRequest.CountHead(new string('M', RawRequest.MaxMethodChars + 1) + " / HTTP/1.1").OverLimit);
        }

        [Fact]
        public void CountHead_AllocatesNothingInProportionToTheText()
        {
            string text = "GET / HTTP/1.1\n" + string.Concat(Enumerable.Repeat("X-A: 1\n", 200_000));
            RawRequest.CountHead("GET / HTTP/1.1\n");

            long before = GC.GetAllocatedBytesForCurrentThread();
            RawHeadCounts counts = RawRequest.CountHead(text);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(counts.OverLimit);
            Assert.InRange(allocated, 0, 1024);
        }

        [Fact]
        public void RawImageLimits_OnlyLowerTheProductionLimits()
        {
            Assert.Equal(
                (65_536, 16_384, 256, 64, 67_108_864, 1_048_576, 8_192),
                (RawRequest.MaxHeadBytes, RawRequest.MaxRequestLineBytes, RawRequest.MaxFieldLines, RawRequest.MaxMethodChars,
                    RawRequest.MaxEntityBytes, RawRequest.MaxChunkFramingBytes, RawRequest.MaxChunkLineBytes));
            Assert.Equal(65_536L + 67_108_864L + 1_048_576L, RawRequest.MaxFileBytes);
            RawImageLimits production = RawImageLimits.Production;
            Assert.Equal(
                (RawRequest.MaxHeadBytes, RawRequest.MaxRequestLineBytes, RawRequest.MaxFieldLines, RawRequest.MaxMethodChars,
                    RawRequest.MaxEntityBytes, RawRequest.MaxChunkFramingBytes, RawRequest.MaxChunkLineBytes),
                (production.HeadBytes, production.RequestLineBytes, production.FieldLines, production.MethodChars,
                    production.EntityBytes, production.ChunkFramingBytes, production.ChunkLineBytes));

            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(headBytes: RawRequest.MaxHeadBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(requestLineBytes: RawRequest.MaxRequestLineBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(fieldLines: RawRequest.MaxFieldLines + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(methodChars: RawRequest.MaxMethodChars + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(entityBytes: RawRequest.MaxEntityBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(chunkFramingBytes: RawRequest.MaxChunkFramingBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(chunkLineBytes: RawRequest.MaxChunkLineBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(headBytes: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawImageLimits(entityBytes: -1));
        }

        // Section 3's fixed text for each raw code (O8), replay.raw.host's as F-L27 words it. The GUI's en-us mirror test
        // compares with RawRequest.Message.
        private static readonly (string Code, string Text)[] SectionThree =
        {
            (RawCodes.InputTooLarge, "The raw request is larger than Raw mode allows (head 64 KiB, request line 16 KiB, 256 field lines, method 64 characters, body 64 MiB, chunk framing 1 MiB in all, chunk lines 8 KiB); nothing was sent."),
            (RawCodes.FileUnreadable, "The raw request file could not be read; nothing was sent."),
            (RawCodes.Invalid, "The raw request is not a valid HTTP/1.1 request head: it must be ASCII with CRLF line ends, no folded lines, single spaces in the request line and NAME: VALUE fields. Nothing was sent."),
            (RawCodes.Method, "The method must be a token of 1 to 64 characters, and Raw mode does not send CONNECT, or a standard method written in another case; nothing was sent."),
            (RawCodes.Target, "The request target must be a path, or an absolute URL of the target origin, without a fragment; nothing was sent."),
            (RawCodes.Version, "Raw mode sends HTTP/1.1 requests only; nothing was sent."),
            (RawCodes.DuplicateField, "A field appears more than once in the raw request, which Raw mode does not send; nothing was sent."),
            (RawCodes.Host, "The raw request needs one Host field naming the target origin; with absolute-form, its value must exactly match the authority written in the request target. Nothing was sent."),
            (RawCodes.Connection, "Raw mode allows only Connection: close, and no Keep-Alive, Proxy-Connection or TE field; nothing was sent."),
            (RawCodes.Expect, "Raw mode does not send an Expect field; nothing was sent."),
            (RawCodes.Upgrade, "Raw mode does not send a protocol upgrade or a WebSocket handshake; nothing was sent."),
            (RawCodes.Trailer, "Raw mode does not send trailers, a Trailer field or chunk extensions; nothing was sent."),
            (RawCodes.Framing, "The raw request's framing does not match its body: use one Content-Length equal to the body, or Transfer-Encoding: chunked with complete chunks, never both. Nothing was sent."),
            (RawCodes.BodyNotAllowed, "A HEAD or TRACE request cannot have a body in Raw mode; nothing was sent."),
            (RawCodes.FixContentLength, "Fix Content-Length needs exactly one Content-Length field and no Transfer-Encoding; nothing was changed."),
            (RawCodes.Changed, "The prepared raw request changed after it was checked; nothing was sent."),
            (RawCodes.TargetDenied, "The target, or every address that its name resolves to, is an unspecified, multicast or broadcast address; nothing was sent."),
            (RawCodes.Proxy, "Raw mode does not send through a proxy; nothing was sent."),
            (RawCodes.ClientCert, "Raw mode does not offer a client certificate; nothing was sent."),
            (RawCodes.WindowsAuth, "Raw mode does not use Windows authentication; nothing was sent."),
            (RawCodes.HttpVersion, "Raw mode sends HTTP/1.1 only, and the run must ask for exactly HTTP/1.1; nothing was sent."),
            (RawCodes.Redirects, "Raw mode does not follow redirects; nothing was sent."),
            (RawCodes.UnsupportedOption, "Raw mode sends one prepared request, without verification, sequences or edits; nothing was sent."),
            (RawCodes.PolicyRaw, "A raw request is sent as its prepared bytes: as checked, after any line-break conversion that XMAT showed and any Content-Length fix that was asked for, without the normalization that other sends get. A front end and the server behind it can read it differently, and it can affect other requests on a connection that they share. Sending this raw request to this target was not allowed; nothing was sent."),
            (RawCodes.PolicyRawUnsafe, "The raw request uses a method that can change server state, or is a GET or OPTIONS request with a body, which a server that does not expect one may read as another request; unsafe requests were not allowed, so nothing was sent."),
            (RawCodes.Alpn, "The server chose a protocol other than HTTP/1.1 for the connection; nothing was sent."),
            (RawCodes.ResponseInvalid, "The response is not a valid HTTP/1.x response within Raw mode's limits; the connection was closed."),
            (RawCodes.ResponseCoding, "The response uses a transfer coding other than chunked alone, which Raw mode does not read; the connection was closed."),
            (RawCodes.ResponseIncomplete, "The connection ended before XMAT finished reading the response's body; the response may be incomplete."),
            (RawCodes.SendCancelled, "The send was cancelled; part of the request may already have been written."),
        };

        [Fact]
        public void Message_IsSectionThreesTextForEachRawCode_AndNullForAnyOther()
        {
            string[] codes = typeof(RawCodes)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (string)field.GetRawConstantValue())
                .ToArray();

            Assert.Equal(codes.Order(StringComparer.Ordinal), SectionThree.Select(row => row.Code).Order(StringComparer.Ordinal));
            Assert.All(SectionThree, row => Assert.Equal(row.Text, RawRequest.Message(row.Code)));
            Assert.Null(RawRequest.Message("replay.send.failed"));
            Assert.Null(RawRequest.Message(ReplayControlHeaders.Code));
            Assert.Null(RawRequest.Message(null));
        }

        private static ReplayRawTarget ParseTarget(string text)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));
            return target;
        }

        private static RawImageResult FromBytes(string request, RawImageLimits limits = null, bool fix = false) =>
            RawRequest.FromBytes(Target, Latin1(request), fix, CancellationToken.None, limits);

        private static RawRequestImage AssertAdmitted(RawImageResult result)
        {
            Assert.True(result.Code == null, $"refused: {result.Code} at line {result.Line}");
            Assert.NotNull(result.Image);
            return result.Image;
        }

        private static void AssertRefused(RawImageResult result, string code, int? line)
        {
            Assert.Null(result.Image);
            Assert.Equal((code, line), (result.Code, result.Line));
        }

        private static string Post(string fields, string body) =>
            "POST / HTTP/1.1\r\n" + Host + fields + "\r\n\r\n" + body;

        private static string Chunked(string body) => Post("Transfer-Encoding: chunked", body);

        // A GET head of exactly length bytes, padded in one field.
        private static string HeadOfLength(int length)
        {
            const string start = "GET / HTTP/1.1\r\n" + Host + "X-Pad: ";
            const string end = "\r\n\r\n";
            return start + new string('a', length - start.Length - end.Length) + end;
        }

        // A request whose request line, its CRLF included, is exactly length bytes.
        private static string RequestLineOfLength(int length)
        {
            const string start = "GET /";
            const string end = " HTTP/1.1\r\n";
            return start + new string('a', length - start.Length - end.Length) + end + Host + "\r\n";
        }

        // A request with exactly count field lines, Host included.
        private static string WithFieldLines(int count)
        {
            var text = new StringBuilder("GET / HTTP/1.1\r\n").Append(Host);
            for (int index = 1; index < count; index++)
            {
                text.Append("X-").Append(index).Append(": v\r\n");
            }

            return text.Append("\r\n").ToString();
        }

        private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

        private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

        private static KeyValuePair<string, string> Field(string name, string value) => new(name, value);

        private static string Describe(MethodBase member) => member.DeclaringType.Name + "." + member.Name;

        private sealed class SyncProgress(Action<long> report) : IProgress<long>
        {
            public void Report(long value) => report(value);
        }

        // A stream that keeps every buffer that it is handed, as a stream that queues writes might.
        private sealed class RetainingStream : Stream
        {
            internal List<byte[]> Buffers { get; } = new();

            internal MemoryStream Received { get; } = new();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Buffers.Add(buffer);
                Received.Write(buffer, offset, count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Assert.True(MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment));
                Buffers.Add(segment.Array);
                Received.Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
