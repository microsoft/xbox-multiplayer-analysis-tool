// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.AgentAccess
{
    internal sealed class BoundedMcpInputStream(Stream stream, int maximumMessageBytes = 1024 * 1024) : Stream
    {
        private int _lineBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = stream.Read(buffer, offset, count);
            Check(buffer.AsSpan(offset, read));
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            Check(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            Check(buffer.Span[..read]);
            return read;
        }

        private void Check(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n')
                {
                    _lineBytes = 0;
                }
                else if (++_lineBytes > maximumMessageBytes)
                {
                    throw new IOException("MCP message limit exceeded.");
                }
            }
        }
    }
}
