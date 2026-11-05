// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace XMAT.Operations.Inspection
{
    // The work that one preview may still do (BodyPreviewLimits.MaxWork) and how much of the stored body it read. Every stage
    // spends what it processes before it moves on; cancellation is checked at the same points.
    internal sealed class BodyPreviewWork
    {
        private readonly long _maxWork;
        private readonly CancellationToken _cancellationToken;
        private long _spent;

        internal BodyPreviewWork(long maxWork, CancellationToken cancellationToken)
        {
            _maxWork = maxWork;
            _cancellationToken = cancellationToken;
        }

        internal long Inspected { get; private set; }

        // The work left before MaxWork.
        internal long Remaining => _maxWork - _spent;

        // Throws BodyPreviewWorkLimitException once the work is over the limit.
        internal void Spend(long units)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _spent += units;
            if (_spent > _maxWork)
            {
                throw new BodyPreviewWorkLimitException();
            }
        }

        // Stored bytes read, which are work too.
        internal void Inspect(int bytes)
        {
            Inspected += bytes;
            Spend(bytes);
        }
    }

    // MaxWork was reached: the preview stops where it is and is never retried.
    internal sealed class BodyPreviewWorkLimitException : Exception
    {
    }

    // Chunk framing that does not parse. Not an InvalidDataException, so it is never taken for invalid compressed data.
    internal sealed class BodyPreviewFramingException : Exception
    {
    }

    // The stored bytes as a read-only stream, without copying them.
    internal sealed class BodyPreviewInputStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _input;
        private readonly BodyPreviewWork _work;
        private int _position;

        internal BodyPreviewInputStream(ReadOnlyMemory<byte> input, BodyPreviewWork work)
        {
            _input = input;
            _work = work;
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

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, _input.Length - _position);
            if (count <= 0)
            {
                return 0;
            }

            _input.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            _work.Inspect(count);
            return count;
        }

        public override void Flush()
        {
            // Read-only: nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The data of a chunk-framed body, read straight from the stored bytes. A stored prefix may end anywhere, which then
    // just ends the data; a complete body must end with the last chunk and its trailers, and anything else that does not
    // parse throws BodyPreviewFramingException.
    internal sealed class BodyPreviewChunkedStream : Stream
    {
        private const int MaxLineBytes = 8192;

        private readonly ReadOnlyMemory<byte> _input;
        private readonly bool _prefix;
        private readonly BodyPreviewWork _work;
        private int _position;
        private int _remaining;
        private bool _delimiterDue;
        private bool _done;

        internal BodyPreviewChunkedStream(ReadOnlyMemory<byte> input, bool prefix, BodyPreviewWork work)
        {
            _input = input;
            _prefix = prefix;
            _work = work;
        }

        // Whether the stored prefix ended before the last chunk.
        internal bool EndedEarly { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (!_done)
            {
                if (_remaining > 0)
                {
                    int available = _input.Length - _position;
                    if (available == 0)
                    {
                        return EndEarly();
                    }

                    int count = Math.Min(Math.Min(_remaining, available), buffer.Length);
                    _input.Span.Slice(_position, count).CopyTo(buffer);
                    _position += count;
                    _remaining -= count;
                    _delimiterDue = _remaining == 0;
                    _work.Inspect(count);
                    return count;
                }

                if (_delimiterDue)
                {
                    if (_input.Length - _position < 2)
                    {
                        if (_input.Length - _position == 1 && _input.Span[_position] != (byte)'\r')
                        {
                            throw new BodyPreviewFramingException();
                        }

                        return EndEarly();
                    }

                    if (_input.Span[_position] != (byte)'\r' || _input.Span[_position + 1] != (byte)'\n')
                    {
                        throw new BodyPreviewFramingException();
                    }

                    _position += 2;
                    _work.Inspect(2);
                    _delimiterDue = false;
                }

                if (!TryReadLine(out ReadOnlySpan<byte> line))
                {
                    return EndEarly();
                }

                int size = ParseSize(line);
                if (size == 0)
                {
                    ReadTrailers();
                    _done = true;
                    return 0;
                }

                _remaining = size;
            }

            return 0;
        }

        public override void Flush()
        {
            // Read-only: nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // A size line: hexadecimal digits, then optional extensions after ';', which the preview ignores.
        private static int ParseSize(ReadOnlySpan<byte> line)
        {
            int extension = line.IndexOf((byte)';');
            ReadOnlySpan<byte> digits = (extension < 0 ? line : line[..extension]).TrimEnd(" \t"u8);
            if (digits.IsEmpty || digits.Length > 8)
            {
                throw new BodyPreviewFramingException();
            }

            foreach (byte digit in digits)
            {
                if (!char.IsAsciiHexDigit((char)digit))
                {
                    throw new BodyPreviewFramingException();
                }
            }

            uint size = uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return size > int.MaxValue ? throw new BodyPreviewFramingException() : (int)size;
        }

        // The trailer lines up to the empty line that ends the body; then nothing may follow.
        private void ReadTrailers()
        {
            while (true)
            {
                if (!TryReadLine(out ReadOnlySpan<byte> trailer))
                {
                    EndEarly();
                    return;
                }

                if (trailer.IsEmpty)
                {
                    if (_position != _input.Length)
                    {
                        throw new BodyPreviewFramingException();
                    }

                    return;
                }

                if (trailer.IndexOf((byte)':') <= 0)
                {
                    throw new BodyPreviewFramingException();
                }
            }
        }

        // The next CRLF-terminated line, at most MaxLineBytes long; false when the stored prefix ends first.
        private bool TryReadLine(out ReadOnlySpan<byte> line)
        {
            ReadOnlySpan<byte> rest = _input.Span[_position..];
            int end = rest.IndexOf("\r\n"u8);
            if (end < 0 || end > MaxLineBytes)
            {
                if (end > MaxLineBytes || rest.Length > MaxLineBytes + 1)
                {
                    throw new BodyPreviewFramingException();
                }

                line = default;
                return false;
            }

            line = rest[..end];
            _position += end + 2;
            _work.Inspect(end + 2);
            return true;
        }

        // The data ends here: a prefix may end anywhere, a complete body may not.
        private int EndEarly()
        {
            if (!_prefix)
            {
                throw new BodyPreviewFramingException();
            }

            EndedEarly = true;
            _done = true;
            return 0;
        }
    }
}
