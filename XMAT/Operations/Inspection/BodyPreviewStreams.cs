// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace XMAT.Operations.Inspection
{
    // The work that one preview may still do (BodyPreviewLimits.MaxWork) and how far into the stored body it read. A stage
    // asks how much it may do (Allow) or whether a whole pass fits (Require) before it works, then spends what it did;
    // cancellation is checked at the same points.
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

        // How far into the stored body any stage has read.
        internal long Inspected { get; private set; }

        // The work left before MaxWork.
        internal long Remaining => _maxWork - _spent;

        // How much of wanted may be done now; throws BodyPreviewWorkLimitException when no work is left.
        internal int Allow(int wanted)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_spent >= _maxWork)
            {
                throw new BodyPreviewWorkLimitException();
            }

            return (int)Math.Min(wanted, _maxWork - _spent);
        }

        // Throws BodyPreviewWorkLimitException unless units of work are left, before a pass that may do that much.
        internal void Require(long units)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_maxWork - _spent < units)
            {
                throw new BodyPreviewWorkLimitException();
            }
        }

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

        // Records that the stored body was read up to offset.
        internal void Reach(long offset) => Inspected = Math.Max(Inspected, offset);
    }

    // MaxWork was reached: the preview stops where it is and is never retried.
    internal sealed class BodyPreviewWorkLimitException : Exception
    {
    }

    // Chunk framing that does not parse. Not an InvalidDataException, so it is never taken for invalid compressed data.
    internal sealed class BodyPreviewFramingException : Exception
    {
    }

    // What a decoder reads from: its last eight bytes (a gzip trailer), and whether the decoder asked for more after the data
    // had ended, which a deflate or Brotli decoder does only when its compressed data is incomplete.
    internal sealed class BodyPreviewSourceStream : Stream
    {
        private const int TailBytes = 8;

        private readonly Stream _inner;
        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;

        internal BodyPreviewSourceStream(Stream inner)
        {
            _inner = inner;
        }

        internal bool EndRequested { get; private set; }

        internal ReadOnlySpan<byte> Tail => _tail.AsSpan(0, _tailLength);

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

            int read = _inner.Read(buffer);
            if (read == 0)
            {
                EndRequested = true;
                return 0;
            }

            ReadOnlySpan<byte> data = buffer[..read];
            if (data.Length >= TailBytes)
            {
                data[^TailBytes..].CopyTo(_tail);
                _tailLength = TailBytes;
            }
            else
            {
                int keep = Math.Min(_tailLength, TailBytes - data.Length);
                _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
                data.CopyTo(_tail.AsSpan(keep));
                _tailLength = keep + data.Length;
            }

            return read;
        }

        public override void Flush()
        {
            // Read-only: nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
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

            count = _work.Allow(count);
            _input.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            _work.Spend(count);
            _work.Reach(_position);
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

                    int count = _work.Allow(Math.Min(Math.Min(_remaining, available), buffer.Length));
                    _input.Span.Slice(_position, count).CopyTo(buffer);
                    _position += count;
                    _remaining -= count;
                    _delimiterDue = _remaining == 0;
                    _work.Spend(count);
                    _work.Reach(_position);
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
                    _work.Spend(2);
                    _work.Reach(_position);
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

        // The next CRLF-terminated line, at most MaxLineBytes long, looked for in no more bytes than that; false when the
        // stored prefix ends first.
        private bool TryReadLine(out ReadOnlySpan<byte> line)
        {
            ReadOnlySpan<byte> rest = _input.Span[_position..];
            int end = rest[..Math.Min(rest.Length, MaxLineBytes + 2)].IndexOf("\r\n"u8);
            if (end < 0)
            {
                if (rest.Length >= MaxLineBytes + 2)
                {
                    throw new BodyPreviewFramingException();
                }

                line = default;
                return false;
            }

            line = rest[..end];
            _position += end + 2;
            _work.Spend(end + 2);
            _work.Reach(_position);
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
