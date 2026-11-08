// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // The raw send's response reader (slice L, contract 2.7, tests 2.12 item 6): every framing rule in its order, interim
    // responses, chunked extensions and trailers, each budget, and where an end of stream or a failed read ends a response.
    // Each case is read whole, one byte at a time, and split at every boundary, with the same result.
    public sealed class RawResponseReaderTests
    {
        private const long Limit = 1024;

        public static TheoryData<string, int, string, string, string, string, int> Responses => new()
        {
            // response, status, framing, body, fields ("|" between them), reason, trailer fields
            { "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", 200, "content-length", "hello", "Content-Length: 5", "OK", 0 },
            { "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nokEXTRA", 200, "content-length", "ok", "Content-Length: 2", "OK", 0 },
            { "HTTP/1.1 200 OK\r\nContent-Length: 002\r\nContent-Length: 2, 2\r\n\r\nok", 200, "content-length", "ok", "Content-Length: 002|Content-Length: 2, 2", "OK", 0 },
            { "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nX-A: 1\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", 200, "chunked", "hello world", "X-A: 1|Content-Length: 11", "OK", 0 },
            { "HTTP/1.1 200 OK\r\ntransfer-ENCODING: CHUNKED\r\nTrailer: X-T\r\nX-A: 1\r\nTRAILER: X-U\r\n\r\n0005 ;a=b; c = \"q\\\"x\"\r\nhello\r\n000\r\nX-T: 1\r\nX-U: 2\r\n\r\n", 200, "chunked", "hello", "X-A: 1|Content-Length: 5", "OK", 2 },
            { "HTTP/1.1 200 OK\r\nX-A: 1\r\n\r\nall the rest", 200, "close", "all the rest", "X-A: 1", "OK", 0 },
            { "HTTP/1.0 200 OK\r\n\r\nbody", 200, "close", "body", "", "OK", 0 },
            { "HTTP/1.1 204 No Content\r\nContent-Length: 999999\r\n\r\n", 204, "none", "", "Content-Length: 999999", "No Content", 0 },
            { "HTTP/1.1 304 Not Modified\r\nTransfer-Encoding: chunked\r\n\r\n", 304, "none", "", "Transfer-Encoding: chunked", "Not Modified", 0 },
            { "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 103 Early Hints\r\nLink: </a>\r\n\r\nHTTP/1.1 201 Created\r\nContent-Length: 2\r\n\r\nok", 201, "content-length", "ok", "Content-Length: 2", "Created", 0 },
            { "HTTP/1.1 200 \u00e9t\u00e9\r\nX-A:  caf\u00e9 \t\r\nContent-Length: 0\r\n\r\n", 200, "content-length", "", "X-A: caf\u00e9|Content-Length: 0", "\u00e9t\u00e9", 0 },
            { "HTTP/1.1 200 \r\nContent-Length: 0\r\n\r\n", 200, "content-length", "", "Content-Length: 0", "", 0 },
        };

        public static TheoryData<string, string> Failures => new()
        {
            // Before a complete final status line: replay.send.failed.
            { string.Empty, ReplaySender.SendFailedCode },
            { "HTTP/1.1 20", ReplaySender.SendFailedCode },
            { "HTTP/1.1 100 Continue\r\n", ReplaySender.SendFailedCode },
            { "HTTP/1.1 100 Continue\r\nX-A: 1\r\n", ReplaySender.SendFailedCode },
            { "HTTP/1.1 100 Continue\r\n\r\n", ReplaySender.SendFailedCode },

            // Inside the final head.
            { "HTTP/1.1 200 OK\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A: 1\r\n", RawCodes.ResponseInvalid },

            // Inside a framed body.
            { "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhel", RawCodes.ResponseIncomplete },
            { Chunked("5\r\nhel"), RawCodes.ResponseIncomplete },
            { Chunked("5\r\nhello\r\n"), RawCodes.ResponseIncomplete },
            { Chunked("5\r\nhello"), RawCodes.ResponseIncomplete },
            { Chunked("0\r\n"), RawCodes.ResponseIncomplete },
            { Chunked("0\r\nX-T: 1\r\n"), RawCodes.ResponseIncomplete },

            // Status lines.
            { "HTTP/2.0 200 OK\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/0.9 200 OK\r\n\r\n", RawCodes.ResponseInvalid },
            { "http/1.1 200 OK\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 600 X\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 099 X\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1  200 OK\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 2000 OK\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 O\u0001K\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 101 Switching Protocols\r\nUpgrade: x\r\n\r\n", RawCodes.ResponseInvalid },

            // Interim heads get the framing checks that do not depend on a body (L3-A1).
            { "HTTP/1.1 100 Continue\r\nTransfer-Encoding: chunked\r\nContent-Length: 1\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.0 103 Early Hints\r\nTransfer-Encoding: chunked\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", RawCodes.ResponseInvalid },

            // Final framing, in order.
            { "HTTP/1.0 204 No Content\r\nTransfer-Encoding: chunked\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\nhello", RawCodes.ResponseInvalid },
            { "HTTP/1.1 304 Not Modified\r\nContent-Length: 1\r\nTransfer-Encoding: x\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", RawCodes.ResponseCoding },
            { "HTTP/1.1 200 OK\r\nTransfer-Encoding: identity\r\n\r\n", RawCodes.ResponseCoding },
            { "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", RawCodes.ResponseCoding },
            { "HTTP/1.1 200 OK\r\nContent-Length: abc\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 1.0\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 5, 6\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 5,\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 00000000000000000001\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nContent-Length: 9999999999999999999\r\n\r\n", RawCodes.ResponseInvalid },

            // Fields.
            { "HTTP/1.1 200 OK\r\nX-A: 1\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A: 1\r\n folded\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX A: 1\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A : 1\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\n: 1\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A 1\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A: a\u0001b\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A: a\u007fb\r\n\r\n", RawCodes.ResponseInvalid },
            { "HTTP/1.1 200 OK\r\nX-A: a\rb\r\n\r\n", RawCodes.ResponseInvalid },

            // Chunk lines and data.
            { Chunked("g\r\nx\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked(" 5\r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5 \r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5;\r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5;a=\r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5;a \r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5;a=\"x\r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5;a=b c\r\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5\r\nhelloXX0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("5\nhello\r\n0\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("FFFFFFFFFFFFFFFFF\r\n"), RawCodes.ResponseInvalid },
            { Chunked("0\r\n folded\r\n\r\n"), RawCodes.ResponseInvalid },
            { Chunked("0\r\nX T: 1\r\n\r\n"), RawCodes.ResponseInvalid },
        };

        [Theory]
        [MemberData(nameof(Responses))]
        public async Task ReadsAResponse_TheSameWayHoweverItsBytesArrive(
            string response, int status, string framing, string body, string fields, string reason, int trailers)
        {
            foreach (ScriptedStream stream in Splits(response))
            {
                RawReadResult result = await new RawResponseReader(stream, Limit, false).ReadAsync(CancellationToken.None);

                Assert.True(result.Kind == RawReadKind.Response, $"{result.Kind} {result.Code} ({stream})");
                RawResponse read = result.Response;
                Assert.Equal((status, framing, reason, trailers, false), (read.StatusCode, read.Framing, read.ReasonPhrase, read.TrailerFields, read.TooLarge));
                Assert.Equal(Latin1(body), read.Body);
                Assert.Equal(fields, string.Join("|", read.Fields.Select(field => field.Key + ": " + field.Value)));
                int final = response.LastIndexOf("HTTP/1.", StringComparison.Ordinal);
                Assert.Equal(response[final..response.IndexOf("\r\n", final, StringComparison.Ordinal)], read.StatusLine);
            }
        }

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task RefusesAResponse_WithItsCode_HoweverItsBytesArrive(string response, string code)
        {
            foreach (ScriptedStream stream in Splits(response))
            {
                RawReadResult result = await new RawResponseReader(stream, Limit, false).ReadAsync(CancellationToken.None);

                Assert.Equal((RawReadKind.ReaderFailure, code), (result.Kind, result.Code));
                Assert.Null(result.Response);
            }
        }

        [Fact]
        public async Task KeepsTheVersionAsReceived_AndReadsHttp10WithItsOwnSemantics()
        {
            RawResponse minor = await ReadAsync("HTTP/1.2 200 OK\r\nContent-Length: 0\r\n\r\n");
            Assert.Equal(new Version(1, 2), minor.Version);
            Assert.Equal("HTTP/1.2 200 OK", minor.ToMessage().StartLine);

            RawResponse old = await ReadAsync("HTTP/1.0 200 OK\r\nContent-Length: 2\r\n\r\nok");
            Assert.Equal((new Version(1, 0), "content-length"), (old.Version, old.Framing));
        }

        [Fact]
        public async Task AHeadRequestsResponse_HasNoBody_WhateverItDeclares()
        {
            var stream = new ScriptedStream(Latin1("HTTP/1.1 200 OK\r\nContent-Length: 999999999\r\n\r\n"), int.MaxValue);

            RawReadResult result = await new RawResponseReader(stream, 10, true).ReadAsync(CancellationToken.None);

            Assert.Equal((RawReadKind.Response, "none", false), (result.Kind, result.Response.Framing, result.Response.TooLarge));
            Assert.Empty(result.Response.Body);
        }

        [Fact]
        public async Task InterimResponses_AtMostEight()
        {
            string eight = string.Concat(Enumerable.Repeat("HTTP/1.1 100 Continue\r\n\r\n", 8));
            Assert.Equal(200, (await ReadAsync(eight + "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")).StatusCode);

            RawReadResult nine = await ReadResultAsync(eight + "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            Assert.Equal((RawReadKind.ReaderFailure, RawCodes.ResponseInvalid), (nine.Kind, nine.Code));
        }

        [Fact]
        public async Task TheHeadBudgets_AtTheirValues_AndOnePast()
        {
            Assert.Equal(200, (await ReadAsync(HeadOfLength(RawResponseReader.MaxHeadBytes))).StatusCode);
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(HeadOfLength(RawResponseReader.MaxHeadBytes + 1))).Code);

            // The budget is every head together, interim ones included.
            string interim = "HTTP/1.1 100 Continue\r\n\r\n";
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(interim + HeadOfLength(RawResponseReader.MaxHeadBytes - interim.Length + 1))).Code);
            Assert.Equal(200, (await ReadAsync(interim + HeadOfLength(RawResponseReader.MaxHeadBytes - interim.Length))).StatusCode);

            Assert.Equal(200, (await ReadAsync(WithFields(RawResponseReader.MaxFieldLines))).StatusCode);
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(WithFields(RawResponseReader.MaxFieldLines + 1))).Code);
        }

        [Fact]
        public async Task TheChunkBudgets_AtTheirValues_AndOnePast()
        {
            // A chunk line of exactly 8 KiB, its CRLF included, padded in an extension.
            string line = "1;a=" + new string('b', RawResponseReader.MaxChunkLineBytes - 6) + "\r\n";
            Assert.Equal("x", Encoding.ASCII.GetString((await ReadAsync(Chunked(line + "x\r\n0\r\n\r\n"))).Body));
            string longer = "1;a=" + new string('b', RawResponseReader.MaxChunkLineBytes - 5) + "\r\n";
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(Chunked(longer + "x\r\n0\r\n\r\n"))).Code);

            // Chunks of one byte: 3 bytes of line and 2 of CRLF each, then the last chunk's 3.
            int chunks = (RawResponseReader.MaxChunkFramingBytes - 3) / 5;
            string framed = string.Concat(Enumerable.Repeat("1\r\nx\r\n", chunks));
            var limit = new RawResponseReader(new ScriptedStream(Latin1(Chunked(framed + "0\r\n\r\n")), int.MaxValue), long.MaxValue, false);
            Assert.Equal(RawReadKind.Response, (await limit.ReadAsync(CancellationToken.None)).Kind);
            string over = string.Concat(Enumerable.Repeat("1\r\nx\r\n", chunks + 1));
            var past = new RawResponseReader(new ScriptedStream(Latin1(Chunked(over + "0\r\n\r\n")), int.MaxValue), long.MaxValue, false);
            Assert.Equal(RawCodes.ResponseInvalid, (await past.ReadAsync(CancellationToken.None)).Code);

            // The trailer section: 64 fields, and 16 KiB with its empty line.
            string fields = string.Concat(Enumerable.Range(0, RawResponseReader.MaxTrailerFields).Select(i => $"X-{i}: v\r\n"));
            Assert.Equal(64, (await ReadAsync(Chunked("0\r\n" + fields + "\r\n"))).TrailerFields);
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(Chunked("0\r\n" + fields + "X-64: v\r\n\r\n"))).Code);
            string big = "X-T: " + new string('v', RawResponseReader.MaxTrailerBytes - 9) + "\r\n";
            Assert.Equal(1, (await ReadAsync(Chunked("0\r\n" + big + "\r\n"))).TrailerFields);
            string bigger = "X-T: " + new string('v', RawResponseReader.MaxTrailerBytes - 8) + "\r\n";
            Assert.Equal(RawCodes.ResponseInvalid, (await ReadResultAsync(Chunked("0\r\n" + bigger + "\r\n"))).Code);
        }

        [Theory]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nhello world", "content-length")]
        [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n6\r\nhello \r\n5\r\nworld\r\n0\r\n\r\n", "chunked")]
        [InlineData("HTTP/1.1 200 OK\r\n\r\nhello world", "close")]
        public async Task ABodyOverTheLimit_IsTooLarge_WithItsHeadAsReceived(string response, string framing)
        {
            RawReadResult over = await new RawResponseReader(new ScriptedStream(Latin1(response), 3), 10, false).ReadAsync(CancellationToken.None);

            Assert.Equal((RawReadKind.Response, true, framing), (over.Kind, over.Response.TooLarge, over.Response.Framing));
            Assert.Empty(over.Response.Body);
            int statusEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
            int headEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string[] fields = headEnd <= statusEnd ? Array.Empty<string>() : response[(statusEnd + 2)..headEnd].Split("\r\n");
            Assert.Equal(fields, over.Response.Fields.Select(field => field.Key + ": " + field.Value));

            RawReadResult at = await new RawResponseReader(new ScriptedStream(Latin1(response.Replace("11", "10").Replace("hello world", "helloworld").Replace("6\r\nhello ", "5\r\nhello")), 3), 10, false)
                .ReadAsync(CancellationToken.None);
            Assert.False(at.Response?.TooLarge ?? true, $"{at.Kind} {at.Code}");
        }

        [Theory]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", 5, ReplaySender.SendFailedCode)]
        [InlineData("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", 30, ReplaySender.SendFailedCode)]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", 20, RawCodes.ResponseInvalid)]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", 40, RawCodes.ResponseIncomplete)]
        [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n", 50, RawCodes.ResponseIncomplete)]
        [InlineData("HTTP/1.1 200 OK\r\n\r\nhello", 21, RawCodes.ResponseIncomplete)]
        public async Task AFailedRead_IsClassifiedWhereItHappens(string response, int failAt, string code)
        {
            var stream = new ScriptedStream(Latin1(response), 1, failAt: failAt);

            RawReadResult result = await new RawResponseReader(stream, Limit, false).ReadAsync(CancellationToken.None);

            Assert.Equal((RawReadKind.ReadFailure, code), (result.Kind, result.Code));
            Assert.Null(result.Response);
        }

        [Fact]
        public async Task ACloseDelimitedBody_EndsWithTheStream()
        {
            RawResponse response = await ReadAsync("HTTP/1.1 200 OK\r\n\r\n");

            Assert.Equal(("close", 0), (response.Framing, response.Body.Length));
        }

        private static async Task<RawResponse> ReadAsync(string response)
        {
            RawReadResult result = await ReadResultAsync(response);
            Assert.True(result.Kind == RawReadKind.Response, $"{result.Kind} {result.Code}");
            return result.Response;
        }

        private static Task<RawReadResult> ReadResultAsync(string response) =>
            new RawResponseReader(new ScriptedStream(Latin1(response), 4096), Limit, false).ReadAsync(CancellationToken.None);

        // The bytes whole, one at a time, and split in two at every boundary.
        private static IEnumerable<ScriptedStream> Splits(string response)
        {
            byte[] bytes = Latin1(response);
            yield return new ScriptedStream(bytes, int.MaxValue);
            yield return new ScriptedStream(bytes, 1);
            for (int cut = 1; cut < bytes.Length; cut++)
            {
                yield return new ScriptedStream(bytes, int.MaxValue, firstRead: cut);
            }
        }

        private static string Chunked(string body) => "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" + body;

        // A response whose head is exactly length bytes, padded in one field.
        private static string HeadOfLength(int length)
        {
            const string start = "HTTP/1.1 200 OK\r\nX-Pad: ";
            const string end = "\r\nContent-Length: 0\r\n\r\n";
            return start + new string('a', length - start.Length - end.Length) + end;
        }

        private static string WithFields(int count) =>
            "HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Range(1, count - 1).Select(i => $"X-{i}: v\r\n")) +
            "Content-Length: 0\r\n\r\n";

        private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

        // Serves its bytes in reads of at most maxRead (the first of at most firstRead), and fails every read from failAt.
        private sealed class ScriptedStream(byte[] data, int maxRead, int firstRead = 0, int failAt = -1) : Stream
        {
            private int _position;
            private bool _first = true;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Serve(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(Serve(buffer.Span));

            public override string ToString() => $"maxRead {maxRead}, firstRead {firstRead}";

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private int Serve(Span<byte> buffer)
            {
                if (failAt >= 0 && _position >= failAt)
                {
                    throw new IOException("The connection failed.");
                }

                int count = Math.Min(buffer.Length, data.Length - _position);
                count = Math.Min(count, _first && firstRead > 0 ? firstRead : maxRead);
                if (failAt >= 0)
                {
                    count = Math.Min(count, failAt - _position);
                }

                _first = false;
                data.AsSpan(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
        }
    }
}
