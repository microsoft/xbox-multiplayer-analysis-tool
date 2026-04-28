// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;

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
    }
}
