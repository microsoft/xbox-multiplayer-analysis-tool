// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    /// <summary>
    /// Tests for the CaptureAnalysisEngine.BinaryReadingExtensions class
    /// (separate from the XMAT.BinaryReadingExtensions).
    /// </summary>
    public class AnalysisEngineBinaryExtensionsTests
    {
        #region ConvertToString

        [Fact]
        public void ConvertToString_ReturnsFullString_WhenLengthExceedsCount()
        {
            var buffer = new List<byte>(Encoding.ASCII.GetBytes("Test"));
            string result = buffer.ConvertToString(100);
            Assert.Equal("Test", result);
        }

        [Fact]
        public void ConvertToString_ReturnsTruncated_WhenLengthIsSmaller()
        {
            var buffer = new List<byte>(Encoding.ASCII.GetBytes("Hello World"));
            string result = buffer.ConvertToString(5);
            Assert.Equal("Hello", result);
        }

        [Fact]
        public void ConvertToString_DefaultLength_ReturnsFullString()
        {
            var buffer = new List<byte>(Encoding.ASCII.GetBytes("Full string"));
            string result = buffer.ConvertToString();
            Assert.Equal("Full string", result);
        }

        #endregion

        #region FindFirstMatch

        [Fact]
        public void FindFirstMatch_ReturnsCorrectIndex()
        {
            var source = new List<byte>(Encoding.ASCII.GetBytes("abcdef"));
            byte[] pattern = Encoding.ASCII.GetBytes("cde");
            Assert.Equal(2, source.FindFirstMatch(pattern));
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_ForNoMatch()
        {
            var source = new List<byte>(Encoding.ASCII.GetBytes("abcdef"));
            byte[] pattern = Encoding.ASCII.GetBytes("xyz");
            Assert.Equal(-1, source.FindFirstMatch(pattern));
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_ForNull()
        {
            List<byte> source = null;
            Assert.Equal(-1, source.FindFirstMatch(Encoding.ASCII.GetBytes("a")));
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_ForNullPattern()
        {
            var source = new List<byte>(Encoding.ASCII.GetBytes("abc"));
            Assert.Equal(-1, source.FindFirstMatch(null));
        }

        [Fact]
        public void FindFirstMatch_ReturnsNegativeOne_WhenPatternLarger()
        {
            var source = new List<byte>(Encoding.ASCII.GetBytes("ab"));
            byte[] pattern = Encoding.ASCII.GetBytes("abcdef");
            Assert.Equal(-1, source.FindFirstMatch(pattern));
        }

        #endregion

        #region BinaryReader extensions

        [Fact]
        public void ReadLine_ReadsFirstLine()
        {
            byte[] data = Encoding.ASCII.GetBytes("Line1\r\nLine2\r\n");
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            string line = reader.ReadLine();
            Assert.Equal("Line1", line);
        }

        [Fact]
        public void ReadToEnd_ReadsRemainingBytes()
        {
            byte[] data = { 10, 20, 30, 40, 50 };
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            byte[] result = reader.ReadToEnd();
            Assert.Equal(data, result);
        }

        [Fact]
        public void IsEndOfStream_WorksCorrectly()
        {
            byte[] data = { 1, 2 };
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            Assert.False(reader.IsEndOfStream());
            reader.ReadBytes(2);
            Assert.True(reader.IsEndOfStream());
        }

        #endregion
    }
}
