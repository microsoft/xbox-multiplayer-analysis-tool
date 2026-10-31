// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.Fixtures
{
    // A read-only stream that cannot seek or report its length, like a decompressing ZIP entry. It returns at most
    // the given number of bytes per read, so readers see short reads, and counts the bytes it has returned.
    internal sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxBytesPerRead;
        private int _position;

        internal ForwardOnlyStream(
            byte[] data,
            int maxBytesPerRead = int.MaxValue)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerRead);
            _data = data;
            _maxBytesPerRead = maxBytesPerRead;
        }

        internal long BytesRead => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(
                Math.Min(buffer.Length, _maxBytesPerRead),
                _data.Length - _position);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
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
    }
}
