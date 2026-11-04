// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The pure parts of replay: request selection, verification and the draft edits that the operation applies.
    public class ReplayCoreTests
    {
        [Fact]
        public void Selection_ReadsAllNumbersAndMergedRanges()
        {
            Assert.True(ReplaySelection.TryParse("all", out ReplaySelection all));
            Assert.True(ReplaySelection.TryParse("5,1,5,9-12,11-14", out ReplaySelection list));

            Assert.True(all.IsAll);
            Assert.True(all.Contains(123456789));
            Assert.False(all.Names(1));
            Assert.Equal(new long[] { 5, 1 }, list.Numbers);
            Assert.True(list.Names(5));
            Assert.False(list.Names(10));
            Assert.Equal(
                new[] { 1L, 5, 9, 10, 11, 12, 13, 14 },
                Enumerable.Range(0, 20).Select(number => (long)number).Where(list.Contains));
        }

        [Theory]
        [InlineData("")]
        [InlineData("All")]
        [InlineData("0")]
        [InlineData("01")]
        [InlineData("-1")]
        [InlineData("1-")]
        [InlineData("5-3")]
        [InlineData("1,,2")]
        [InlineData(" 1")]
        [InlineData("1.5")]
        [InlineData("١")]
        [InlineData("1234567890123456")]
        [InlineData("1-10001")]
        [InlineData("1-5000,6000-11000")]
        public void Selection_RejectsAnythingElse(string text)
        {
            Assert.False(ReplaySelection.TryParse(text, out ReplaySelection selection));
            Assert.Null(selection);
        }

        [Fact]
        public void Selection_AcceptsExactlyTheNumberLimit()
        {
            Assert.True(ReplaySelection.TryParse("1-10000", out _));
            Assert.True(ReplaySelection.TryParse("1-9999,20000", out _));
            Assert.False(ReplaySelection.TryParse("1-9999,20000,20001", out _));
        }

        [Fact]
        public void Verify_MatchesTheSameStatusAndContentAcrossFramingAndContentCodings()
        {
            byte[] content = "{\"ok\":true}"u8.ToArray();
            ProxyConnectionModel chunked = Original(
                "200", "Transfer-Encoding: chunked\r\nContent-Type: application/json\r\n", Encoding.ASCII.GetBytes("b\r\n{\"ok\":true}\r\n0\r\n\r\n"));
            ProxyConnectionModel gzipped = Original("200", "Content-Encoding: gzip\r\n", Gzip(content));

            Assert.True(ReplayVerifier.Compare(chunked, Replay(200, content), 1024).Matches);
            Assert.True(ReplayVerifier.Compare(gzipped, Replay(200, content), 1024).Matches);
            Assert.True(ReplayVerifier.Compare(Original("200", "", content), Replay(200, Gzip(content), "gzip"), 1024).Matches);
        }

        [Fact]
        public void Verify_ListsTheStatusAndBodyDifferences()
        {
            byte[] content = "same"u8.ToArray();

            Assert.Equal(
                new[] { ReplayVerifier.StatusDifference },
                ReplayVerifier.Compare(Original("200", "", content), Replay(404, content), 1024).Differences);
            Assert.Equal(
                new[] { ReplayVerifier.BodyDifference },
                ReplayVerifier.Compare(Original("200", "", content), Replay(200, "other"u8.ToArray()), 1024).Differences);
            Assert.Equal(
                new[] { ReplayVerifier.StatusDifference, ReplayVerifier.BodyDifference },
                ReplayVerifier.Compare(Original("", "", content), Replay(200, Array.Empty<byte>()), 1024).Differences);
        }

        [Fact]
        public void Verify_ComparesUndecodableOrOversizedContentAsItIs()
        {
            byte[] content = new byte[4096];
            byte[] compressed = Gzip(content);
            byte[] notGzip = "not gzip"u8.ToArray();

            // Within the limit the content matches; over it, the two different encodings are compared as they are.
            Assert.True(ReplayVerifier.Compare(Original("200", "Content-Encoding: gzip\r\n", compressed), Replay(200, content), 8192).Matches);
            Assert.False(ReplayVerifier.Compare(Original("200", "Content-Encoding: gzip\r\n", compressed), Replay(200, content), 1024).Matches);
            Assert.True(ReplayVerifier.Compare(Original("200", "Content-Encoding: gzip\r\n", notGzip), Replay(200, notGzip, "gzip"), 1024).Matches);
        }

        [Fact]
        public void Draft_SetsAndRemovesHeadersInPlace()
        {
            var draft = new RequestReplayDraft
            {
                HeadersText = "Accept: a\r\nIf-None-Match: \"v1\"\r\nX-Gone: 1\r\naccept: b\r\nIf-Modified-Since: x\r\nIf-Range: y\r\nX-Last: z",
            };

            draft.SetHeader("ACCEPT", " c ");
            draft.SetHeader("X-New", "n");
            draft.RemoveHeader("x-gone");
            draft.RemoveConditionalHeaders();

            Assert.Equal("ACCEPT: c\r\nX-Last: z\r\nX-New: n", draft.HeadersText);
        }

        [Theory]
        [InlineData("X-Test", "a\r\nInjected: yes")]
        [InlineData("X-Test", "a\nInjected: yes")]
        [InlineData("X-Test", "a\u007f")]
        [InlineData("Bad Name", "a")]
        [InlineData("", "a")]
        public void Draft_RejectsHeadersThatCouldInjectLines(string name, string value)
        {
            var draft = new RequestReplayDraft { HeadersText = "X-Keep: 1" };

            Assert.Throws<FormatException>(() => draft.SetHeader(name, value));
            Assert.Equal("X-Keep: 1", draft.HeadersText);
        }

        // The HTTP stack sends ASCII header values only, and refuses others after it has connected; a captured or typed
        // value outside ASCII must fail before anything is sent.
        [Theory]
        [InlineData("caf\u00e9")]
        [InlineData("line\u2028separator")]
        public void Draft_RefusesNonAsciiHeaderValuesBeforeSending(string value)
        {
            var draft = new RequestReplayDraft { Url = "https://example.test/", HeadersText = "X-Name: " + value };

            Assert.Throws<FormatException>(() => draft.CreateRequestToSend());
            using (HttpRequestMessage forCurl = draft.CreateRequest())
            {
                // Copy as cURL still exports it: curl sends the value as it is.
                Assert.Equal(value, Assert.Single(forCurl.Headers.NonValidated["X-Name"]));
            }

            draft.HeadersText = "X-Name: cafe\tau lait";
            using HttpRequestMessage request = draft.CreateRequestToSend();
            Assert.Equal("cafe\tau lait", Assert.Single(request.Headers.NonValidated["X-Name"]));
        }

        // Only tab and printable ASCII can be sent. Only spaces and tabs around a value are trimmed, so a value that ends
        // in another kind of space keeps it and is refused rather than silently changed.
        [Theory]
        [InlineData("~", true)]
        [InlineData(" !\t", true)]
        [InlineData("a\tb", true)]
        [InlineData("\u00a1", false)]
        [InlineData("a\u00a0", false)]
        [InlineData("\u00a0a", false)]
        [InlineData("\u4e2d", false)]
        public void Draft_SendsOnlyPrintableAsciiHeaderValues(string value, bool sendable)
        {
            var draft = new RequestReplayDraft { Url = "https://example.test/" };
            draft.SetHeader("X-Value", value);

            if (sendable)
            {
                using HttpRequestMessage request = draft.CreateRequestToSend();
                Assert.Equal(value.Trim(' ', '\t'), Assert.Single(request.Headers.NonValidated["X-Value"]));
            }
            else
            {
                Assert.Throws<FormatException>(() => draft.CreateRequestToSend());
            }
        }

        [Theory]
        [InlineData("\u001f")]
        [InlineData("\u007f")]
        public void Draft_RejectsControlCharactersInHeaderValues(string value)
        {
            Assert.Throws<FormatException>(() => new RequestReplayDraft().SetHeader("X-Value", value));
        }

        // A shared body serves sends as it is; editing the body in the composer replaces it with the edited bytes.
        [Fact]
        public void Draft_EditingASharedBodyKeepsExactBytes()
        {
            byte[] shared = "hello"u8.ToArray();
            var draft = new RequestReplayDraft { Url = "https://example.test/" };

            draft.SetBodyBytes(shared);
            draft.SetBodyFormat(base64: false);
            Assert.Equal(("hello", false), (draft.BodyText, draft.BodyIsBase64));
            byte[] asText = draft.GetBodyBytes();
            Assert.Equal(shared, asText);
            Assert.NotSame(shared, asText);

            draft.SetBodyFormat(base64: true);
            draft.BodyText = Convert.ToBase64String(new byte[] { 0, 255 });
            Assert.Equal(new byte[] { 0, 255 }, draft.GetBodyBytes());
            Assert.Equal("hello"u8.ToArray(), shared);
        }
        [Fact]
        public void Draft_SeparatesUnsafeMethodsFromOriginChanges()
        {
            RequestReplayDraft captured = RequestReplayDraft.FromCapture(RequestReplayDraftTests.Capture("GET", "/", []));

            Assert.False(captured.RequiresConfirmation);
            captured.Url = "https://example.com:8443/other?x=1";
            Assert.False(captured.ChangesOrigin);
            captured.Url = "https://example.net:8443/";
            Assert.True(captured.ChangesOrigin);
            Assert.False(captured.UsesUnsafeMethod);
            captured.Method = "DELETE";
            Assert.True(captured.UsesUnsafeMethod);
            Assert.True(captured.RequiresConfirmation);
            Assert.False(new RequestReplayDraft { Url = "https://example.net/" }.ChangesOrigin);
        }

        [Theory]
        [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
        [InlineData("https://example.test/", "https://example.test")]
        [InlineData("HTTPS://Example.Test:443", "https://example.test")]
        [InlineData("http://example.test/path", null)]
        [InlineData("http://example.test/?q=1", null)]
        [InlineData("http://example.test/#f", null)]
        [InlineData("http://user@example.test", null)]
        [InlineData("ftp://example.test", null)]
        [InlineData("example.test", null)]
        public void Origin_IsOnlySchemeHostAndPort(string text, string expected)
        {
            Assert.Equal(expected != null, ReplayOperation.TryParseOrigin(text, out string origin));
            Assert.Equal(expected, origin);
        }

        private static ProxyConnectionModel Original(string status, string headers, byte[] body) =>
            new(1, status, DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:1", "GET", "/", "GET / HTTP/1.1\r\n", [],
                $"HTTP/1.1 {status} X\r\n{headers}", body, "127.0.0.1");

        private static ReplayExchange Replay(int status, byte[] body, string contentEncoding = null) =>
            new()
            {
                Outcome = ReplayOutcome.Completed,
                StatusCode = status,
                Response = new ReplayMessage(
                    $"HTTP/1.1 {status} X",
                    null,
                    contentEncoding == null ? null : new[] { new KeyValuePair<string, string>("Content-Encoding", contentEncoding) },
                    body),
            };

        internal static byte[] Gzip(byte[] content)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(content);
            }

            return output.ToArray();
        }
    }
}
