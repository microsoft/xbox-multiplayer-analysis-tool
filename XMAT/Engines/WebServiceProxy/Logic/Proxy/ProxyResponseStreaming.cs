// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
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

        // Buffers the body until it ends (Complete) or passes the limit; the bytes read so far
        // are returned either way so a body that outgrew the limit can continue as a stream.
        internal static async Task<(byte[] Bytes, bool Complete)> ReadUpToLimitAsync(
            IdleTimeoutReader reader,
            int limit,
            long? declaredLength)
        {
            int capacity = declaredLength is long length && length <= limit ? (int)length : 0;
            using var body = new MemoryStream(capacity);
            byte[] buffer = new byte[ReadBufferSize];
            while (true)
            {
                int read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    return (body.ToArray(), true);
                body.Write(buffer, 0, read);
                if (body.Length > limit)
                    return (body.ToArray(), false);
            }
        }

        // Forwards the bytes already read, then the rest of the body as it arrives, keeping the
        // capture copy; captureFilled runs once, when the body first outgrows the capture limit.
        internal static async Task RelayBodyAsync(
            IdleTimeoutReader reader,
            byte[] received,
            CapturedResponseBody capture,
            Func<ReadOnlyMemory<byte>, Task> forward,
            Func<Task> captureFilled)
        {
            await RelayAsync(received).ConfigureAwait(false);
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
                await forward(data).ConfigureAwait(false);
                if (capture.Append(data.Span))
                    await captureFilled().ConfigureAwait(false);
            }
        }
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
        private readonly MemoryStream _bytes = new();

        internal CapturedResponseBody(int limit)
        {
            Limit = limit;
        }

        internal int Limit { get; }

        internal bool Truncated { get; private set; }

        // Returns true when this data first made the body longer than the limit.
        internal bool Append(ReadOnlySpan<byte> data)
        {
            if (Truncated)
                return false;
            int room = Limit - (int)_bytes.Length;
            if (data.Length <= room)
            {
                _bytes.Write(data);
                return false;
            }
            _bytes.Write(data[..room]);
            Truncated = true;
            return true;
        }

        internal void CopyTo(ServerResponse response)
        {
            response.BodyBytes = _bytes.ToArray();
            response.CaptureTruncatedAt = Truncated ? Limit : 0;
        }
    }
}
