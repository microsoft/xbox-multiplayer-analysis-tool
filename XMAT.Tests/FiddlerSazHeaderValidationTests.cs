// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Net;
using System.Text;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class FiddlerSazHeaderValidationTests
    {
        [Fact]
        public void HeaderValidator_AgreesWithWebHeaderCollectionForEveryUtf16CodeUnit()
        {
            for (var value = 0; value <= char.MaxValue; value++)
            {
                char character = (char)value;
                AssertAgreement($"a{character}b:value");
                AssertAgreement($"Name:a{character}b");
            }
        }

        [Theory]
        [MemberData(nameof(StructuredHeaderLines))]
        public void HeaderValidator_AgreesWithWebHeaderCollectionForStructuredCases(
            string line)
        {
            AssertAgreement(line);
        }

        [Fact]
        public void HeaderValidator_AgreesWithWebHeaderCollectionForSeededRandomLines()
        {
            char[] alphabet =
            {
                'a', 'Z', '0', ':', ':', ' ', '\t', '\r', '\n', '\v', '\f',
                '(', ')', '<', '>', '@', ',', ';', '\\', '"', '\'', '/', '[',
                ']', '?', '=', '{', '}', (char)0, (char)1, (char)31, (char)127,
                '\u0080', '\u00e9', '\u0100', '\u0109', '\u010a', '\u010d',
                '\u0120', '\u017f', '\u20ac',
            };
            var random = new Random(0x48454144);
            for (var sample = 0; sample < 100_000; sample++)
            {
                var characters = new char[random.Next(0, 65)];
                for (var index = 0; index < characters.Length; index++)
                {
                    characters[index] =
                        alphabet[random.Next(alphabet.Length)];
                }
                AssertAgreement(new string(characters));
            }
        }

        [Fact]
        public void HeaderValidator_UsesTheLowByteForValuesButRequiresAsciiNames()
        {
            const string FoldedValue =
                "Name:a\u010D\u010A\u0120b";
            const string NonAsciiName =
                "N\u00E9:v";

            Assert.True(
                FiddlerSazHandler.IsValidHeaderLine(
                    FoldedValue));
            Assert.False(
                FiddlerSazHandler.IsValidHeaderLine(
                    NonAsciiName));
            AssertAgreement(FoldedValue);
            AssertAgreement(NonAsciiName);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Host:\r\n")]
        [InlineData("Host:   \r\n")]
        public void MissingOrBlankHost_IsKeptWithoutInferringTheRequestTargetHost(
            string hostHeader)
        {
            string request =
                "GET http://absolute.example.test/path HTTP/1.1\r\n" +
                hostHeader +
                "User-Agent: XboxServicesAPI\r\n\r\n";
            string response = BuildMessageWithHeaderLines(
                response: true,
                headerLines: 0,
                malformed: false);

            ProxyConnectionModel record = ParseMessages(
                request,
                response,
                out int skipped);

            Assert.NotNull(record);
            Assert.Equal(string.Empty, record.Host);
            Assert.Equal(string.Empty, record.Port);
            Assert.Null(record.ActionableHost);
            Assert.Equal("/path", record.Path);
            Assert.Equal(0, skipped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HeaderLineLimit_AllowsTheBoundaryAndRejectsTheNextLine(
            bool response)
        {
            string atLimit = BuildMessageWithHeaderLines(
                response,
                FiddlerSazHandler.MaxHeaderLinesPerMessage,
                malformed: false);
            string overLimit = BuildMessageWithHeaderLines(
                response,
                FiddlerSazHandler.MaxHeaderLinesPerMessage + 1,
                malformed: false);

            Assert.NotNull(
                ParseMessage(
                    response,
                    atLimit,
                    out int skipped));
            Assert.Equal(0, skipped);
            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => ParseMessage(
                    response,
                    overLimit,
                    out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RawHeaderByteLimit_AllowsTheBoundaryAndRejectsAnOversizedLine(
            bool response)
        {
            string atLimit = BuildMessageWithRawHeaderBytes(
                response,
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage);
            string overLimit = BuildMessageWithRawHeaderBytes(
                response,
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage + 1);

            Assert.Equal(
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage,
                RawHeaderBytes(atLimit));
            Assert.Equal(
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage + 1,
                RawHeaderBytes(overLimit));
            Assert.NotNull(
                ParseMessage(
                    response,
                    atLimit,
                    out int skipped));
            Assert.Equal(0, skipped);
            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => ParseMessage(
                    response,
                    overLimit,
                    out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MalformedHeaderLines_CountTowardTheLineLimit(
            bool response)
        {
            string atLimit = BuildMessageWithHeaderLines(
                response,
                FiddlerSazHandler.MaxHeaderLinesPerMessage,
                malformed: true);
            string overLimit = BuildMessageWithHeaderLines(
                response,
                FiddlerSazHandler.MaxHeaderLinesPerMessage + 1,
                malformed: true);

            Assert.NotNull(
                ParseMessage(
                    response,
                    atLimit,
                    out int skipped));
            Assert.Equal(
                FiddlerSazHandler.MaxHeaderLinesPerMessage -
                    (response ? 0 : 1),
                skipped);
            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => ParseMessage(
                    response,
                    overLimit,
                    out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MalformedHeaderBytes_CountTowardTheRawByteLimit(
            bool response)
        {
            string atLimit = BuildMessageWithRawHeaderBytes(
                response,
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage,
                malformed: true);
            string overLimit = BuildMessageWithRawHeaderBytes(
                response,
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage + 1,
                malformed: true);

            Assert.NotNull(
                ParseMessage(
                    response,
                    atLimit,
                    out int skipped));
            Assert.Equal(1, skipped);
            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => ParseMessage(
                    response,
                    overLimit,
                    out _));
        }

        [Fact]
        public void DenseHeadersDistributedAcrossMessages_StayWithinPerMessageLimits()
        {
            const int Frames = 32;
            const int LinesPerDirection = 1024;
            string request = BuildMessageWithHeaderLines(
                response: false,
                headerLines: LinesPerDirection,
                malformed: true);
            string response = BuildMessageWithHeaderLines(
                response: true,
                headerLines: LinesPerDirection,
                malformed: true);
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);

            for (uint frame = 1; frame <= Frames; frame++)
            {
                using var requestStream =
                    new MemoryStream(requestBytes, writable: false);
                using var responseStream =
                    new MemoryStream(responseBytes, writable: false);
                ProxyConnectionModel record =
                    FiddlerSazHandler.FromFiddlerFrame(
                        frame,
                        requestStream,
                        null,
                        responseStream,
                        out int skipped);

                Assert.NotNull(record);
                Assert.Equal(
                    LinesPerDirection * 2 - 1,
                    skipped);
            }
        }

        [Fact]
        public void OversizedHeaderLine_StopsReadingAtTheRawByteLimit()
        {
            string request = BuildMessageWithRawHeaderBytes(
                response: false,
                rawHeaderBytes:
                    FiddlerSazHandler
                        .MaxRawHeaderBytesPerMessage + 1);
            string response = BuildMessageWithHeaderLines(
                response: true,
                headerLines: 0,
                malformed: false);
            using var requestStream = new MemoryStream(
                Encoding.ASCII.GetBytes(request),
                writable: false);
            using var responseStream = new MemoryStream(
                Encoding.ASCII.GetBytes(response),
                writable: false);

            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => FiddlerSazHandler.FromFiddlerFrame(
                    1,
                    requestStream,
                    null,
                    responseStream,
                    out _));

            Assert.InRange(
                requestStream.Position,
                0L,
                (long)FiddlerSazHandler.MaxRawHeaderBytesPerMessage);
        }

        [Fact]
        public void ExcessHeaderLines_StopAtTheFirstByteOfLine16385()
        {
            string request = BuildMessageWithHeaderLines(
                response: false,
                headerLines: 100_000,
                malformed: false);
            string response = BuildMessageWithHeaderLines(
                response: true,
                headerLines: 0,
                malformed: false);
            using var requestStream = new MemoryStream(
                Encoding.ASCII.GetBytes(request),
                writable: false);
            using var responseStream = new MemoryStream(
                Encoding.ASCII.GetBytes(response),
                writable: false);
            long line16385 =
                Encoding.ASCII.GetByteCount(
                    StartLine(response: false) +
                    "\r\nHost: example.test\r\n") +
                (FiddlerSazHandler.MaxHeaderLinesPerMessage - 1L) *
                Encoding.ASCII.GetByteCount(
                    "X-Test: value\r\n");

            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => FiddlerSazHandler.FromFiddlerFrame(
                    1,
                    requestStream,
                    null,
                    responseStream,
                    out _));

            Assert.InRange(
                requestStream.Position,
                0L,
                line16385 + 1);
        }

        [Fact]
        public void RequestAndResponseBudgets_AreIndependentAtBothExactCaps()
        {
            string request = BuildMessageAtBothLimits(
                response: false);
            string response = BuildMessageAtBothLimits(
                response: true);

            Assert.Equal(
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage,
                RawHeaderBytes(request));
            Assert.Equal(
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage,
                RawHeaderBytes(response));
            Assert.Equal(
                FiddlerSazHandler.MaxHeaderLinesPerMessage,
                HeaderLines(request));
            Assert.Equal(
                FiddlerSazHandler.MaxHeaderLinesPerMessage,
                HeaderLines(response));
            Assert.NotNull(
                ParseMessages(
                    request,
                    response,
                    out int skipped));
            Assert.Equal(0, skipped);

            string oversizedResponse = BuildMessageAtBothLimits(
                response: true,
                extraRawBytes: 1);
            Assert.Throws<FiddlerSazHeaderLimitException>(
                () => ParseMessages(
                    request,
                    oversizedResponse,
                    out _));
        }

        public static IEnumerable<object[]> StructuredHeaderLines()
        {
            yield return new object[] { null };
            yield return new object[] { string.Empty };
            yield return new object[] { ":x" };
            yield return new object[] { "Name:" };
            yield return new object[] { "Name:value" };
            yield return new object[] { " Name:value" };
            yield return new object[] { "Name :value" };
            yield return new object[] { "Name:\t value \r\n" };
            yield return new object[] { "Name:value\r\n more" };
            yield return new object[] { "Name:value\n\tmore" };
            yield return new object[] { "Name:value\rmore" };
            yield return new object[] { "Name:value\nmore" };
            yield return new object[] { "Name:value\u007f" };
            yield return new object[] { "Name:value\r\n" };
            yield return new object[] { "Bad Name:value" };
            yield return new object[] { "No colon" };
        }

        private static void AssertAgreement(string line)
        {
            bool accepted;
            try
            {
                new WebHeaderCollection().Add(line);
                accepted = true;
            }
            catch (ArgumentException)
            {
                accepted = false;
            }

            if (accepted != FiddlerSazHandler.IsValidHeaderLine(line))
            {
                Assert.Fail(
                    line == null
                        ? "Header line: <null>"
                        : "Header line UTF-16: " +
                            Convert.ToHexString(
                                Encoding.Unicode.GetBytes(line)));
            }
        }

        private static ProxyConnectionModel ParseMessage(
            bool response,
            string message,
            out int skippedMalformedHeaderLines)
        {
            string requestText = response
                ? BuildMessageWithHeaderLines(
                    response: false,
                    headerLines: 0,
                    malformed: false)
                : message;
            string responseText = response
                ? message
                : BuildMessageWithHeaderLines(
                    response: true,
                    headerLines: 0,
                    malformed: false);
            return ParseMessages(
                requestText,
                responseText,
                out skippedMalformedHeaderLines);
        }

        private static ProxyConnectionModel ParseMessages(
            string requestText,
            string responseText,
            out int skippedMalformedHeaderLines)
        {
            using var requestStream = new MemoryStream(
                Encoding.ASCII.GetBytes(requestText),
                writable: false);
            using var responseStream = new MemoryStream(
                Encoding.ASCII.GetBytes(responseText),
                writable: false);
            return FiddlerSazHandler.FromFiddlerFrame(
                1,
                requestStream,
                null,
                responseStream,
                out skippedMalformedHeaderLines);
        }

        private static string BuildMessageWithHeaderLines(
            bool response,
            int headerLines,
            bool malformed)
        {
            var builder = new StringBuilder();
            builder.Append(StartLine(response))
                .Append("\r\n");
            int generatedLines = headerLines;
            if (!response)
            {
                builder.Append("Host: example.test\r\n");
                generatedLines = Math.Max(
                    0,
                    headerLines - 1);
            }
            for (var index = 0; index < generatedLines; index++)
            {
                if (malformed)
                {
                    builder.Append("Malformed header ")
                        .Append(index)
                        .Append("\r\n");
                }
                else
                {
                    builder.Append("X-Test: value\r\n");
                }
            }
            return builder.Append("\r\nbody")
                .ToString();
        }

        private static string BuildMessageWithRawHeaderBytes(
            bool response,
            int rawHeaderBytes,
            bool malformed = false)
        {
            string startLine = StartLine(response);
            string prefix = malformed
                ? "Malformed"
                : "X:";
            string requiredHeaders = response
                ? string.Empty
                : "Host: example.test\r\n";
            int fixedBytes =
                startLine.Length +
                "\r\n\r\n\r\n".Length +
                requiredHeaders.Length +
                prefix.Length;
            int valueBytes = rawHeaderBytes - fixedBytes;
            Assert.True(valueBytes >= 0);
            return startLine +
                "\r\n" +
                requiredHeaders +
                prefix +
                new string('v', valueBytes) +
                "\r\n\r\nbody";
        }

        private static int RawHeaderBytes(string message)
        {
            int separator = message.IndexOf(
                "\r\n\r\n",
                StringComparison.Ordinal);
            return Encoding.ASCII.GetByteCount(
                message.Substring(
                    0,
                    separator + 4));
        }

        private static int HeaderLines(string message)
        {
            int separator = message.IndexOf(
                "\r\n\r\n",
                StringComparison.Ordinal);
            return message.Substring(
                    0,
                    separator)
                .Split(
                    "\r\n",
                    StringSplitOptions.None)
                .Length - 1;
        }

        private static string BuildMessageAtBothLimits(
            bool response,
            int extraRawBytes = 0)
        {
            var builder = new StringBuilder();
            builder.Append(StartLine(response))
                .Append("\r\n");
            int lines = 0;
            if (!response)
            {
                builder.Append("Host: example.test\r\n");
                lines++;
            }
            while (lines <
                FiddlerSazHandler.MaxHeaderLinesPerMessage - 1)
            {
                builder.Append("X-Test: value\r\n");
                lines++;
            }
            builder.Append("X-Fill:");
            int fixedRawBytes =
                Encoding.ASCII.GetByteCount(builder.ToString()) +
                "\r\n\r\n".Length;
            int fillerBytes =
                FiddlerSazHandler.MaxRawHeaderBytesPerMessage +
                extraRawBytes -
                fixedRawBytes;
            Assert.True(fillerBytes >= 0);
            return builder.Append(
                    'f',
                    fillerBytes)
                .Append("\r\n\r\nbody")
                .ToString();
        }

        private static string StartLine(bool response) =>
            response
                ? "HTTP/1.1 200 OK"
                : "GET / HTTP/1.1";

        // GUI imports parse requests and responses while their ZIP entries decompress. Every frame must parse as it
        // does from a seekable stream: the same headers, bodies and skipped lines, and the same limit failures.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void FromFiddlerFrame_ParsesForwardOnlyStreamsLikeSeekableStreams(
            int scenario)
        {
            (string request, string response) = scenario switch
            {
                0 => (
                    BuildMessageWithHeaderLines(false, 3, false),
                    BuildMessageWithHeaderLines(true, 2, false)),
                1 => (
                    BuildMessageWithHeaderLines(false, 5, true),
                    BuildMessageWithHeaderLines(true, 4, true)),
                2 => (
                    BuildMessageAtBothLimits(false),
                    BuildMessageAtBothLimits(true)),
                3 => (
                    BuildMessageAtBothLimits(false, extraRawBytes: 1),
                    BuildMessageWithHeaderLines(true, 0, false)),
                4 => (
                    "GET / HTTP/1.1\r\nHost: example.test\r\n",
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"),
                _ => (
                    BuildMessageWithHeaderLines(
                        false,
                        FiddlerSazHandler.MaxHeaderLinesPerMessage + 1,
                        false),
                    BuildMessageWithHeaderLines(true, 0, false)),
            };
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            byte[] responseBytes = Encoding.ASCII.GetBytes(response);

            string seekable = DescribeFrame(
                new MemoryStream(requestBytes, writable: false),
                new MemoryStream(responseBytes, writable: false));
            string forwardOnly = DescribeFrame(
                new LookaheadReadStream(
                    new ForwardOnlyStream(
                        requestBytes,
                        maxBytesPerRead: 7)),
                new LookaheadReadStream(
                    new ForwardOnlyStream(
                        responseBytes,
                        maxBytesPerRead: 7)));

            Assert.Equal(seekable, forwardOnly);
        }

        // Whole-entry decompression would allocate at least the entry before the header limit applied; the GUI import
        // stops reading an unterminated header at the raw byte limit.
        [Fact]
        public void HeaderLimit_StopsAZipEntryBeforeItIsFullyDecompressed()
        {
            const int EntryBytes = 64 * 1024 * 1024;
            string directory = CreateTestDirectory();
            try
            {
                string path = Path.Combine(
                    directory,
                    "unterminated-header.saz");
                using (var archive = ZipFile.Open(
                    path,
                    ZipArchiveMode.Create))
                {
                    ZipArchiveEntry request = archive.CreateEntry(
                        "raw/1_c.txt",
                        CompressionLevel.Fastest);
                    using (Stream stream = request.Open())
                    {
                        stream.Write(
                            Encoding.ASCII.GetBytes(
                                "GET / HTTP/1.1\r\nX-Unterminated: "));
                        var filler = new byte[64 * 1024];
                        Array.Fill(filler, (byte)'v');
                        for (var written = 0;
                            written < EntryBytes;
                            written += filler.Length)
                        {
                            stream.Write(filler);
                        }
                    }
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                Assert.Throws<FiddlerSazHeaderLimitException>(
                    () => FiddlerSazHandler.ImportSazFileWithDiagnostics(
                        path,
                        CancellationToken.None));
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.InRange(
                    allocated,
                    0L,
                    (long)EntryBytes);
            }
            finally
            {
                Directory.Delete(
                    directory,
                    true);
            }
        }

        // XMAT can record a message whose headers exceed the import limits and export it, plainly or sanitized; such an
        // archive is refused on import rather than truncated.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExportsAboveTheHeaderLineLimit_AreRefusedOnImportRatherThanTruncated(
            bool sanitized)
        {
            var headers = new StringBuilder(
                "GET http://example.test/limit HTTP/1.1\r\nHost: example.test\r\n");
            for (var line = 0;
                line < FiddlerSazHandler.MaxHeaderLinesPerMessage;
                line++)
            {
                headers.Append("X-Line-")
                    .Append(line)
                    .Append(": v\r\n");
            }
            headers.Append("\r\n");
            var record = new ProxyConnectionModel(
                1,
                "200",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(5),
                "http",
                "example.test:80",
                "GET",
                "/limit",
                headers.ToString(),
                Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
                Array.Empty<byte>(),
                "10.0.0.1");
            string directory = CreateTestDirectory();
            try
            {
                string path = Path.Combine(
                    directory,
                    "over-limit.saz");
                if (sanitized)
                {
                    FiddlerSazHandler.CreateSanitizedSazFile(
                        path,
                        new[] { record });
                }
                else
                {
                    FiddlerSazHandler.CreateSazFile(
                        path,
                        new[] { record });
                }

                Assert.Throws<FiddlerSazHeaderLimitException>(
                    () => FiddlerSazHandler.ImportSazFileWithDiagnostics(
                        path,
                        CancellationToken.None));
            }
            finally
            {
                Directory.Delete(
                    directory,
                    true);
            }
        }

        private static string DescribeFrame(
            Stream request,
            Stream response)
        {
            try
            {
                ProxyConnectionModel model =
                    FiddlerSazHandler.FromFiddlerFrame(
                        1,
                        request,
                        null,
                        response,
                        out int skipped);
                return model == null
                    ? $"skipped frame|{skipped}"
                    : string.Join(
                        "|",
                        model.Method,
                        model.Path,
                        model.Host,
                        model.Port,
                        model.Status,
                        model.RequestLineAndHeaders,
                        Convert.ToHexString(model.RequestBody),
                        model.ResponseLineAndHeaders,
                        Convert.ToHexString(model.ResponseBody),
                        skipped);
            }
            catch (FiddlerSazHeaderLimitException)
            {
                return "header limit";
            }
            finally
            {
                request.Dispose();
                response.Dispose();
            }
        }

        private static string CreateTestDirectory()
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory,
                "SazTestArtifacts",
                $"XMAT-headers-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
