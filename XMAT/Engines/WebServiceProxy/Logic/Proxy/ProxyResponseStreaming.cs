// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    /// <summary>
    /// Response-body handling shared by the plain and intercepted (HTTPS) paths: Fiddler's
    /// buffer-then-stream model, with a capture copy bounded by the capture limit.
    /// </summary>
    internal static class ProxyResponseStreaming
    {
        internal const int ReadBufferSize = 64 * 1024;

        // HEAD, 1xx, 204 and 304 responses have no message body.
        internal static bool HasMessageBody(string method, HttpStatusCode statusCode)
        {
            return !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
                (int)statusCode >= 200 &&
                statusCode != HttpStatusCode.NoContent &&
                statusCode != HttpStatusCode.NotModified;
        }

        // A body that cannot (or will not) fit the capture limit streams from its first byte.
        internal static bool StreamsImmediately(HttpResponseMessage response, ResponseStreamingOptions options)
        {
            return options.StreamAllResponses ||
                (options.AutoStreamMediaAndEvents &&
                    ResponseStreamingOptions.IsAutoStreamedType(response.Content.Headers.ContentType)) ||
                response.Content.Headers.ContentLength > options.CaptureLimitBytes;
        }

        // Buffers into the same bounded accumulator that a body crossing the limit continues to
        // use. At most one read buffer is retained for the bytes beyond the captured prefix.
        internal static async Task<BufferedResponseBody> ReadUpToLimitAsync(
            IdleTimeoutReader reader,
            int limit,
            long? declaredLength)
        {
            _ = declaredLength;
            var capture = new CapturedResponseBody(limit);
            byte[] buffer = new byte[ReadBufferSize];
            while (true)
            {
                int read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    return new BufferedResponseBody(capture, complete: true, ReadOnlyMemory<byte>.Empty);

                int captured = Math.Min(capture.Remaining, read);
                capture.AppendPrefix(buffer.AsSpan(0, captured));
                if (captured != read)
                {
                    return new BufferedResponseBody(
                        capture,
                        complete: false,
                        buffer.AsMemory(captured, read - captured));
                }
            }
        }

        // Forwards a prefix buffered while deciding whether to stream, then the rest as it arrives.
        // captureChanged runs at exact fill and again on the first byte beyond the limit.
        internal static async Task RelayBodyAsync(
            IdleTimeoutReader reader,
            CapturedResponseBody capture,
            ReadOnlyMemory<byte> overflow,
            Func<ReadOnlyMemory<byte>, Task> forward,
            Func<Task> captureChanged)
        {
            if (capture.Length != 0)
            {
                if (!capture.IsFull)
                    throw new InvalidOperationException("Only a full buffered prefix can transition to streaming.");

                byte[] prefix = capture.Freeze();
                for (var offset = 0; offset < prefix.Length; offset += ReadBufferSize)
                {
                    await forward(prefix.AsMemory(
                        offset,
                        Math.Min(ReadBufferSize, prefix.Length - offset))).ConfigureAwait(false);
                }
                await captureChanged().ConfigureAwait(false);
            }
            await RelayAsync(overflow).ConfigureAwait(false);

            byte[] buffer = new byte[ReadBufferSize];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await RelayAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            async Task RelayAsync(ReadOnlyMemory<byte> data)
            {
                if (data.IsEmpty)
                    return;

                if (!capture.IsFull)
                {
                    int captured = Math.Min(capture.Remaining, data.Length);
                    ReadOnlyMemory<byte> prefix = data[..captured];
                    await forward(prefix).ConfigureAwait(false);
                    capture.AppendPrefix(prefix.Span);
                    data = data[captured..];
                    if (capture.IsFull)
                    {
                        capture.Freeze();
                        await captureChanged().ConfigureAwait(false);
                    }
                }

                if (data.IsEmpty)
                    return;

                await forward(data).ConfigureAwait(false);
                if (!capture.Truncated)
                {
                    capture.MarkTruncated();
                    await captureChanged().ConfigureAwait(false);
                }
            }
        }
    }

    internal sealed class BufferedResponseBody
    {
        internal BufferedResponseBody(
            CapturedResponseBody capture,
            bool complete,
            ReadOnlyMemory<byte> overflow)
        {
            Capture = capture;
            Complete = complete;
            Overflow = overflow;
        }

        internal CapturedResponseBody Capture { get; }
        internal bool Complete { get; }
        internal ReadOnlyMemory<byte> Overflow { get; }
    }

    /// <summary>
    /// Reads a response body, failing with a TimeoutException when one read waits on the origin for the idle
    /// timeout. Streams may last forever, and writing to a slow device between reads may take long, so only
    /// the time spent waiting on the origin counts.
    /// </summary>
    internal sealed class IdleTimeoutReader
    {
        private readonly Stream _body;
        private readonly TimeSpan _idleTimeout;
        private readonly CancellationToken _cancellationToken;

        internal IdleTimeoutReader(Stream body, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            _body = body;
            _idleTimeout = idleTimeout;
            _cancellationToken = cancellationToken;
        }

        internal async ValueTask<int> ReadAsync(Memory<byte> buffer)
        {
            if (_idleTimeout <= TimeSpan.Zero)
                return await _body.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false);

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            idle.CancelAfter(_idleTimeout);
            try
            {
                return await _body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The origin sent no response data for {_idleTimeout.TotalSeconds:0} seconds.");
            }
        }
    }

    /// <summary>
    /// The capture copy of a streamed body: keeps at most Limit bytes and notes when more arrived.
    /// </summary>
    internal sealed class CapturedResponseBody
    {
        private readonly List<byte[]> _segments = new();
        private byte[] _frozen;
        private int _tailLength;
        private int _length;

        internal CapturedResponseBody(int limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);
            Limit = limit;
        }

        internal int Limit { get; }

        internal int Length => _length;

        internal int Remaining => Limit - _length;

        internal bool IsFull => _length == Limit;

        internal bool Truncated { get; private set; }

        internal void AppendPrefix(ReadOnlySpan<byte> data)
        {
            if (data.Length > Remaining)
                throw new ArgumentOutOfRangeException(nameof(data));
            if (_frozen != null && !data.IsEmpty)
                throw new InvalidOperationException("The capture prefix is already immutable.");

            while (!data.IsEmpty)
            {
                if (_segments.Count == 0 || _tailLength == _segments[^1].Length)
                {
                    int segmentLength = Math.Min(ProxyResponseStreaming.ReadBufferSize, Remaining);
                    _segments.Add(new byte[segmentLength]);
                    _tailLength = 0;
                }

                byte[] tail = _segments[^1];
                int copied = Math.Min(tail.Length - _tailLength, data.Length);
                data[..copied].CopyTo(tail.AsSpan(_tailLength));
                data = data[copied..];
                _tailLength += copied;
                _length += copied;
            }
        }

        internal void MarkTruncated()
        {
            if (!IsFull)
                throw new InvalidOperationException("A capture cannot be truncated before reaching its limit.");
            Truncated = true;
        }

        internal byte[] Freeze()
        {
            if (_frozen != null)
                return _frozen;

            _frozen = GC.AllocateUninitializedArray<byte>(_length);
            int offset = 0;
            for (var index = 0; index < _segments.Count; index++)
            {
                byte[] segment = _segments[index];
                int count = index == _segments.Count - 1 ? _tailLength : segment.Length;
                segment.AsSpan(0, count).CopyTo(_frozen.AsSpan(offset));
                offset += count;
            }
            _segments.Clear();
            _tailLength = 0;
            return _frozen;
        }

        internal void CopyProgressTo(ServerResponse response)
        {
            response.BodyBytes = IsFull ? Freeze() : Array.Empty<byte>();
            response.CaptureTruncatedAt = Truncated ? Limit : 0;
        }

        internal void CopyFinalTo(ServerResponse response)
        {
            response.BodyBytes = Freeze();
            response.CaptureTruncatedAt = Truncated ? Limit : 0;
        }
    }
}
