// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests
{
    public class BinaryReadingExtensionsTests
    {
        #region ConvertToString

        [Fact]
        public void ConvertToString_ReturnsFullString_WhenLengthExceedsBufferCount()
        {
            IList<byte> buffer = Encoding.ASCII.GetBytes("Hello");
            string result = buffer.ConvertToString(100);
            Assert.Equal("Hello", result);
        }

        [Fact]
        public void ConvertToString_ReturnsTruncatedString_WhenLengthIsSmaller()
        {
            IList<byte> buffer = Encoding.ASCII.GetBytes("Hello World");
            string result = buffer.ConvertToString(5);
            Assert.Equal("Hello", result);
        }

        [Fact]
        public void ConvertToString_ReturnsEmptyString_ForEmptyBuffer()
        {
            IList<byte> buffer = Array.Empty<byte>();
            string result = buffer.ConvertToString();
            Assert.Equal("", result);
        }

        #endregion

        #region FindFirstMatch

        [Fact]
        public void FindFirstMatch_ReturnsIndex_WhenPatternFound()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello World Test");
            byte[] pattern = Encoding.ASCII.GetBytes("World");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(6, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenPatternNotFound()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello World");
            byte[] pattern = Encoding.ASCII.GetBytes("xyz");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenSourceIsNull()
        {
            IList<byte> source = null;
            byte[] pattern = Encoding.ASCII.GetBytes("test");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenPatternIsNull()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello");
            int result = source.FindFirstMatch(null);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenSourceIsEmpty()
        {
            IList<byte> source = Array.Empty<byte>();
            byte[] pattern = Encoding.ASCII.GetBytes("test");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenPatternIsEmpty()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello");
            byte[] pattern = Array.Empty<byte>();
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenPatternLargerThanSource()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hi");
            byte[] pattern = Encoding.ASCII.GetBytes("Hello World");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindFirstMatch_ReturnsFirstOccurrence_WhenMultipleMatches()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("abcabc");
            byte[] pattern = Encoding.ASCII.GetBytes("abc");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(0, result);
        }

        [Fact]
        public void FindFirstMatch_FindsPatternAtStart()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello");
            byte[] pattern = Encoding.ASCII.GetBytes("He");
            int result = source.FindFirstMatch(pattern);
            Assert.Equal(0, result);
        }

        #endregion

        #region IsMatch

        [Fact]
        public void IsMatch_ReturnsTrue_WhenPatternMatchesAtPosition()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello World");
            byte[] pattern = Encoding.ASCII.GetBytes("World");
            bool result = BinaryReadingExtensions.IsMatch(source, 6, pattern);
            Assert.True(result);
        }

        [Fact]
        public void IsMatch_ReturnsFalse_WhenPatternDoesNotMatch()
        {
            IList<byte> source = Encoding.ASCII.GetBytes("Hello World");
            byte[] pattern = Encoding.ASCII.GetBytes("xyz");
            bool result = BinaryReadingExtensions.IsMatch(source, 0, pattern);
            Assert.False(result);
        }

        #endregion

        #region BinaryReader extensions

        [Fact]
        public void ReadLine_ReadsUpToNewline()
        {
            byte[] data = Encoding.ASCII.GetBytes("First line\r\nSecond line\r\n");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            string line = reader.ReadLine();
            Assert.Equal("First line", line);
        }

        [Fact]
        public void ReadLine_ReadsEntireContent_WhenNoNewline()
        {
            byte[] data = Encoding.ASCII.GetBytes("No newline here");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            string line = reader.ReadLine();
            Assert.Equal("No newline here", line);
        }

        [Fact]
        public void ReadLine_PreservesTerminalCrLfAsContent()
        {
            byte[] data = Encoding.ASCII.GetBytes("terminal\r\n");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal("terminal\r\n", reader.ReadLine());
            Assert.Equal(stream.Length, stream.Position);
        }

        [Fact]
        public void ReadLine_CrLfAcrossBufferBoundary_WithNoTrailingByteIsContent()
        {
            byte[] data = Encoding.ASCII.GetBytes(
                new string('a', 4095) + "\r\n");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal(
                new string('a', 4095) + "\r\n",
                reader.ReadLine());
            Assert.Equal(stream.Length, stream.Position);
        }

        [Fact]
        public void ReadLine_CrLfAcrossBufferBoundary_WithTrailingByteEndsTheLine()
        {
            byte[] data = Encoding.ASCII.GetBytes(
                new string('a', 4095) + "\r\nz");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal(new string('a', 4095), reader.ReadLine());
            Assert.Equal(4097L, stream.Position);
            Assert.Equal(new byte[] { (byte)'z' }, reader.ReadToEnd());
        }

        [Fact]
        public void ReadLine_EmptyStreamReturnsEmptyString()
        {
            using var stream = new MemoryStream();
            using var reader = new BinaryReader(stream);

            Assert.Equal(string.Empty, reader.ReadLine());
            Assert.Equal(0L, stream.Position);
        }

        [Fact]
        public void ReadLine_UsesTheSecondCarriageReturnInDoubleCarriageReturnLineFeed()
        {
            byte[] data = Encoding.ASCII.GetBytes("\r\r\nx");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal("\r", reader.ReadLine());
            Assert.Equal(3L, stream.Position);
        }

        [Fact]
        public void ReadLine_KeepsBareCarriageReturnsAndLineFeedsInsideTheLine()
        {
            byte[] data = Encoding.ASCII.GetBytes("a\rb\nc\r\nd");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal("a\rb\nc", reader.ReadLine());
            Assert.Equal(new byte[] { (byte)'d' }, reader.ReadToEnd());
        }

        [Fact]
        public void ReadToEnd_AfterReadLineReturnsTheExactBinaryBody()
        {
            byte[] body =
            {
                0x80,
                (byte)'a',
                (byte)'\r',
                (byte)'\n',
                0xff,
            };
            byte[] data = Encoding.ASCII.GetBytes("header\r\n")
                .Concat(body)
                .ToArray();
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.Equal("header", reader.ReadLine());
            Assert.Equal(body, reader.ReadToEnd());
        }

        // A decompressing ZIP entry cannot seek, so the line reader looks one byte ahead instead; it must read every
        // line, limit and remaining body exactly as it does from a seekable stream.
        [Fact]
        public void ReadLine_OverAForwardOnlyStreamMatchesASeekableStream()
        {
            var random = new Random(0x4c4f4f4b);
            byte[] alphabet =
            {
                (byte)'a',
                (byte)' ',
                (byte)'\r',
                (byte)'\n',
                (byte)'\r',
                (byte)'\n',
                0x80,
            };
            int?[] limits = { null, 0, 1, 2, 3, 5, 8 };

            for (var length = 0; length <= 48; length++)
            {
                for (var sample = 0; sample < 16; sample++)
                {
                    var data = new byte[length];
                    for (var index = 0; index < data.Length; index++)
                    {
                        data[index] = alphabet[random.Next(alphabet.Length)];
                    }

                    foreach (int? limit in limits)
                    {
                        Assert.Equal(
                            ReadTranscript(
                                new MemoryStream(data),
                                limit),
                            ReadTranscript(
                                new LookaheadReadStream(
                                    new ForwardOnlyStream(
                                        data,
                                        maxBytesPerRead: 1 + (sample % 3))),
                                limit));
                    }
                }
            }
        }

        [Fact]
        public void LookaheadReadStream_KeepsThePeekedByteForTheNextRead()
        {
            byte[] data = Encoding.ASCII.GetBytes("ab");
            using var stream = new LookaheadReadStream(
                new ForwardOnlyStream(data));

            Assert.True(stream.HasMoreBytes());
            Assert.True(stream.HasMoreBytes());
            Assert.Equal((int)'a', stream.ReadByte());
            var buffer = new byte[4];
            Assert.True(stream.HasMoreBytes());
            Assert.Equal(1, stream.Read(buffer, 0, buffer.Length));
            Assert.Equal((byte)'b', buffer[0]);
            Assert.False(stream.HasMoreBytes());
            Assert.Equal(-1, stream.ReadByte());
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
            Assert.False(stream.CanSeek);
        }

        // Each line (with its byte count when limited), a limit failure, and finally the bytes that remain.
        private static List<string> ReadTranscript(
            Stream stream,
            int? limit)
        {
            var transcript = new List<string>();
            using var reader = new BinaryReader(stream);
            while (stream.HasMoreBytes())
            {
                try
                {
                    if (limit is int maxBytes)
                    {
                        string line = reader.ReadLine(
                            maxBytes,
                            out int bytesRead);
                        transcript.Add($"{line}|{bytesRead}");
                    }
                    else
                    {
                        transcript.Add(reader.ReadLine());
                    }
                }
                catch (BinaryReadLimitException)
                {
                    transcript.Add("<limit>");
                    break;
                }
            }
            transcript.Add(
                Convert.ToHexString(reader.ReadToEnd()));
            return transcript;
        }

        [Fact]
        public void ReadLine_MatchesTheOldImplementationAroundBufferBoundaries()
        {
            var random = new Random(0x584d4154);
            byte[] alphabet =
            {
                (byte)'a',
                (byte)'b',
                (byte)' ',
                (byte)'\r',
                (byte)'\n',
                (byte)'\r',
                (byte)'\n',
                0x80,
                0xff,
            };
            int[] lengths = Enumerable.Range(4094, 5)
                .Concat(Enumerable.Range(8190, 5))
                .Concat(Enumerable.Range(12286, 5))
                .ToArray();

            foreach (int length in lengths)
            {
                var data = new byte[length];
                for (var index = 0; index < data.Length; index++)
                {
                    data[index] = alphabet[random.Next(alphabet.Length)];
                }

                using var expectedStream = new MemoryStream(data);
                using var actualStream = new MemoryStream(data);
                using var expectedReader = new BinaryReader(expectedStream);
                using var actualReader = new BinaryReader(actualStream);
                while (expectedStream.Position < expectedStream.Length ||
                    actualStream.Position < actualStream.Length)
                {
                    Assert.Equal(
                        ReadLineWithOldImplementation(expectedReader),
                        actualReader.ReadLine());
                    Assert.Equal(
                        expectedStream.Position,
                        actualStream.Position);
                }

                Assert.Equal(
                    ReadLineWithOldImplementation(expectedReader),
                    actualReader.ReadLine());
                Assert.Equal(
                    expectedStream.Position,
                    actualStream.Position);
            }
        }

        [Fact]
        public void ReadLine_DoesNotReadShortLinesMoreThanOnce()
        {
            byte[] data = Encoding.ASCII.GetBytes(
                string.Concat(
                    Enumerable.Repeat("x\r\n", 10_000)) +
                "tail");
            using var stream = new CountingStream(data);
            using var reader = new BinaryReader(stream);

            while (!reader.IsEndOfStream())
            {
                _ = reader.ReadLine();
            }

            Assert.InRange(
                stream.BytesRead,
                (long)data.Length,
                (long)data.Length + 1);
        }

        [Fact]
        public void ReadToEnd_ReadsAllBytes()
        {
            byte[] data = new byte[] { 1, 2, 3, 4, 5 };
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            byte[] result = reader.ReadToEnd();
            Assert.Equal(data, result);
        }

        [Fact]
        public void ReadToEnd_ReturnsEmptyArray_ForEmptyStream()
        {
            using var stream = new MemoryStream(Array.Empty<byte>());
            using var reader = new BinaryReader(stream);

            byte[] result = reader.ReadToEnd();
            Assert.Empty(result);
        }

        [Fact]
        public void IsEndOfStream_ReturnsTrue_AtEnd()
        {
            byte[] data = new byte[] { 1 };
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();

            Assert.True(reader.IsEndOfStream());
        }

        [Fact]
        public void IsEndOfStream_ReturnsFalse_WhenNotAtEnd()
        {
            byte[] data = new byte[] { 1, 2 };
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.False(reader.IsEndOfStream());
        }

        #endregion

        // Frozen copy of ReadLine before the linear implementation. Keep this independent as the compatibility oracle.
        private static string ReadLineWithOldImplementation(
            BinaryReader reader)
        {
            byte[] pattern = Encoding.ASCII.GetBytes("\r\n");
            var bytes = new List<byte>();
            int count;
            do
            {
                byte[] buffer = reader.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }

                int match = bytes.FindFirstMatch(pattern);
                if (match != -1)
                {
                    reader.BaseStream.Seek(
                        match - bytes.Count + 2,
                        SeekOrigin.Current);
                    return bytes.ConvertToString(match);
                }
            }
            while (count == 4096);

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private sealed class CountingStream : Stream
        {
            private readonly MemoryStream _inner;

            internal CountingStream(byte[] contents)
            {
                _inner = new MemoryStream(contents, writable: false);
            }

            internal long BytesRead { get; private set; }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush()
            {
            }

            public override int Read(
                byte[] buffer,
                int offset,
                int count)
            {
                int read = _inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override int ReadByte()
            {
                int value = _inner.ReadByte();
                if (value >= 0)
                {
                    BytesRead++;
                }
                return value;
            }

            public override long Seek(
                long offset,
                SeekOrigin origin) =>
                _inner.Seek(offset, origin);

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(
                byte[] buffer,
                int offset,
                int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
