// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;

namespace XMAT.Tests
{
    /// <summary>
    /// Tests for gzip/deflate compression and decompression logic.
    /// Note: PublicUtilities methods cannot be tested directly because
    /// its static constructor requires a WPF Application context.
    /// These tests verify the compression roundtrip independently.
    /// </summary>
    public class CompressionTests
    {
        [Fact]
        public void GzipRoundtrip_ProducesOriginalData()
        {
            string original = "Hello, this is a test of gzip compression!";
            byte[] compressed = GzipCompress(Encoding.UTF8.GetBytes(original));
            byte[] decompressed = GzipDecompress(compressed);
            Assert.Equal(original, Encoding.UTF8.GetString(decompressed));
        }

        [Fact]
        public void DeflateRoundtrip_ProducesOriginalData()
        {
            string original = "Hello, this is a test of deflate compression!";
            byte[] compressed = DeflateCompress(Encoding.UTF8.GetBytes(original));
            byte[] decompressed = DeflateDecompress(compressed);
            Assert.Equal(original, Encoding.UTF8.GetString(decompressed));
        }

        [Fact]
        public void GzipRoundtrip_HandlesEmptyContent()
        {
            byte[] compressed = GzipCompress(Array.Empty<byte>());
            byte[] decompressed = GzipDecompress(compressed);
            Assert.Empty(decompressed);
        }

        [Fact]
        public void DeflateRoundtrip_HandlesEmptyContent()
        {
            byte[] compressed = DeflateCompress(Array.Empty<byte>());
            byte[] decompressed = DeflateDecompress(compressed);
            Assert.Empty(decompressed);
        }

        [Fact]
        public void GzipRoundtrip_HandlesLargeContent()
        {
            string original = new string('A', 100_000);
            byte[] compressed = GzipCompress(Encoding.UTF8.GetBytes(original));
            byte[] decompressed = GzipDecompress(compressed);
            Assert.Equal(original, Encoding.UTF8.GetString(decompressed));
        }

        [Fact]
        public void DecompressZipEntryToMemory_ReturnsContent()
        {
            string content = "zip entry content";
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);

            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("test.txt");
                using var writer = entry.Open();
                writer.Write(contentBytes);
            }
            zipStream.Seek(0, SeekOrigin.Begin);

            using var readArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var readEntry = readArchive.GetEntry("test.txt");

            // Use the same decompression pattern as PublicUtilities.DecompressZipEntryToMemory
            var memory = new MemoryStream();
            using (Stream zip = readEntry.Open())
            {
                zip.CopyTo(memory);
            }
            memory.Seek(0, SeekOrigin.Begin);

            string resultText = Encoding.UTF8.GetString(memory.ToArray());
            Assert.Equal(content, resultText);
            memory.Dispose();
        }

        [Fact]
        public void BodyDecoding_HandlesGzipContentEncoding()
        {
            string original = "gzipped body content";
            byte[] body = GzipCompress(Encoding.UTF8.GetBytes(original));

            // Simulate BodyAsText logic for gzip
            byte[] decoded = GzipDecompress(body);
            string result = Encoding.ASCII.GetString(decoded);
            Assert.Equal(original, result);
        }

        [Fact]
        public void BodyDecoding_HandlesDeflateContentEncoding()
        {
            string original = "deflated body content";
            byte[] body = DeflateCompress(Encoding.UTF8.GetBytes(original));

            byte[] decoded = DeflateDecompress(body);
            string result = Encoding.ASCII.GetString(decoded);
            Assert.Equal(original, result);
        }

        [Fact]
        public void BodyDecoding_ReturnsUtf8_ForJsonContent()
        {
            string original = "{ \"key\": \"value\" }";
            byte[] body = Encoding.UTF8.GetBytes(original);

            // Simulate BodyAsText for JSON content type
            string result = Encoding.UTF8.GetString(body);
            Assert.Equal(original, result);
        }

        [Fact]
        public void BodyDecoding_ReturnsAscii_ForNonJsonContent()
        {
            string original = "plain text content";
            byte[] body = Encoding.ASCII.GetBytes(original);

            string result = Encoding.ASCII.GetString(body);
            Assert.Equal(original, result);
        }

        [Fact]
        public void BodyDecoding_HandlesNullBody()
        {
            byte[] body = null;
            if (body == null)
                body = new byte[0];
            string result = Encoding.ASCII.GetString(body);
            Assert.Equal("", result);
        }

        #region Helpers

        private static byte[] GzipCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private static byte[] GzipDecompress(byte[] data)
        {
            byte[] buffer = new byte[8192];
            using var stream = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var memory = new MemoryStream();
            int count;
            do
            {
                count = stream.Read(buffer, 0, buffer.Length);
                if (count > 0)
                    memory.Write(buffer, 0, count);
            } while (count > 0);
            return memory.ToArray();
        }

        private static byte[] DeflateCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionMode.Compress))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private static byte[] DeflateDecompress(byte[] data)
        {
            byte[] buffer = new byte[8192];
            using var stream = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var memory = new MemoryStream();
            int count;
            do
            {
                count = stream.Read(buffer, 0, buffer.Length);
                if (count > 0)
                    memory.Write(buffer, 0, count);
            } while (count > 0);
            return memory.ToArray();
        }

        #endregion
    }
}
