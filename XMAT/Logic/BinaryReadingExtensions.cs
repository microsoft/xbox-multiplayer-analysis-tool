// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XMAT
{
    internal sealed class BinaryReadLimitException : Exception
    {
    }

    // A read-only view of a forward-only stream, such as a decompressing ZIP entry, that can look one byte ahead. It
    // lets a reader tell whether more data follows without seeking or reading the rest of the stream.
    internal sealed class LookaheadReadStream : Stream
    {
        private const int NoLookahead = -2;
        private readonly Stream _source;
        private int _next = NoLookahead;

        internal LookaheadReadStream(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal bool HasMoreBytes()
        {
            if (_next == NoLookahead)
            {
                _next = _source.ReadByte();
            }
            return _next >= 0;
        }

        public override int ReadByte()
        {
            if (_next == NoLookahead)
            {
                return _source.ReadByte();
            }
            int value = _next;
            if (value >= 0)
            {
                _next = NoLookahead;
            }
            return value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }
            if (_next == NoLookahead)
            {
                return _source.Read(buffer);
            }
            if (_next < 0)
            {
                return 0;
            }
            buffer[0] = (byte)_next;
            _next = NoLookahead;
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class BinaryReadingExtensions
    {
        public static String ConvertToString(this IList<byte> buffer, int length = int.MaxValue)
        {
            if (length > buffer.Count)
                return Encoding.ASCII.GetString(buffer.ToArray());

            return Encoding.ASCII.GetString(buffer.ToArray(), 0, length);
        }


        public static int FindFirstMatch(this IList<byte> source, byte[] pattern)
        {
            if (source == null || pattern == null || source.Count == 0 ||
               pattern.Length == 0 || pattern.Length > source.Count)
                return -1;

            for (int i = 0; i < source.Count - pattern.Length; ++i)
            {
                if (IsMatch(source, i, pattern))
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool IsMatch(IList<byte> source, int position, byte[] pattern)
        {
            for (int j = 0; j < pattern.Length; ++j)
            {
                if (source[position + j] != pattern[j])
                {
                    return false;
                }
            }

            return true;
        }

        public static string ReadLine(this BinaryReader obj)
        {
            return ReadLineCore(
                obj,
                null,
                out _);
        }

        internal static string ReadLine(
            this BinaryReader obj,
            int maxBytes,
            out int bytesRead)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                maxBytes);

            return ReadLineCore(
                obj,
                maxBytes,
                out bytesRead);
        }

        private static string ReadLineCore(
            BinaryReader obj,
            int? maxBytes,
            out int bytesRead)
        {
            var bytes = new List<byte>();
            bytesRead = 0;
            while (true)
            {
                if (maxBytes.HasValue &&
                    bytesRead == maxBytes.Value &&
                    obj.BaseStream.HasMoreBytes())
                {
                    throw new BinaryReadLimitException();
                }

                int value = obj.BaseStream.ReadByte();
                if (value < 0)
                {
                    return bytes.ConvertToString();
                }

                bytes.Add((byte)value);
                bytesRead++;
                // Preserve the historical quirk: a CRLF at the exact end of the stream is returned as content.
                if (value == '\n' &&
                    bytes.Count >= 2 &&
                    bytes[^2] == '\r' &&
                    obj.BaseStream.HasMoreBytes())
                {
                    return bytes.ConvertToString(bytes.Count - 2);
                }
            }
        }

        // Whether more bytes follow: a lookahead stream peeks at its next byte, and any other stream must be seekable.
        internal static bool HasMoreBytes(this Stream stream)
        {
            return stream is LookaheadReadStream lookahead
                ? lookahead.HasMoreBytes()
                : stream.Position < stream.Length;
        }

        public static byte[] ReadToEnd(this BinaryReader obj)
        {
            List<byte> bytes = new List<byte>();
            int count = 0;
            do
            {
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }
            } while (count == 4096);

            return bytes.ToArray();
        }

        public static bool IsEndOfStream(this BinaryReader obj)
        {
            return obj.BaseStream.Position == obj.BaseStream.Length;
        }
    }
}
