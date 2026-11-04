// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed record ProxyRequestReadPolicy(
        int MaxHeaderBytes,
        int MaxHeaderFields,
        TimeSpan HeaderDeadline,
        int MaxBodyBytes,
        TimeSpan BodyInactivityTimeout,
        TimeSpan BodyTotalTimeout,
        long MaxAggregateBufferedBytes,
        int MaxChunkLineBytes,
        int MaxTrailerBytes,
        int MaxTrailerFields,
        int MaxChunkCount)
    {
        internal static ProxyRequestReadPolicy Default { get; } =
            new(
                MaxHeaderBytes: 32 * 1024,
                MaxHeaderFields: 100,
                HeaderDeadline: TimeSpan.FromSeconds(30),
                MaxBodyBytes: 32 * 1024 * 1024,
                BodyInactivityTimeout: TimeSpan.FromSeconds(30),
                BodyTotalTimeout: TimeSpan.FromSeconds(120),
                MaxAggregateBufferedBytes: 128L * 1024 * 1024,
                MaxChunkLineBytes: 8 * 1024,
                MaxTrailerBytes: 32 * 1024,
                MaxTrailerFields: 100,
                MaxChunkCount: 100_000);

        internal void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxHeaderBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxHeaderFields);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxBodyBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxAggregateBufferedBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxChunkLineBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxTrailerBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxTrailerFields);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                MaxChunkCount);
            if (HeaderDeadline <= TimeSpan.Zero ||
                BodyInactivityTimeout <= TimeSpan.Zero ||
                BodyTotalTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(HeaderDeadline));
            }
        }
    }

    internal sealed class ProxyRequestBufferBudget
    {
        private readonly long _maximumBytes;
        private long _reservedBytes;
        private long _peakReservedBytes;
        private long _rejectedReservations;

        internal static ProxyRequestBufferBudget Shared { get; } =
            new(
                ProxyRequestReadPolicy.Default);

        public ProxyRequestBufferBudget(
            ProxyRequestReadPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            policy.Validate();
            _maximumBytes =
                policy.MaxAggregateBufferedBytes;
        }

        internal long MaximumBytes =>
            _maximumBytes;

        internal long ReservedBytes =>
            Interlocked.Read(
                ref _reservedBytes);

        internal long PeakReservedBytes =>
            Interlocked.Read(
                ref _peakReservedBytes);

        internal long RejectedReservations =>
            Interlocked.Read(
                ref _rejectedReservations);

        internal ProxyRequestBufferLease CreateLease() =>
            new(this);

        internal bool TryReserve(
            long bytes,
            out ProxyRequestBufferLease lease)
        {
            lease = CreateLease();
            if (lease.TryGrow(bytes))
            {
                return true;
            }
            lease.Dispose();
            lease = null;
            return false;
        }

        internal bool TryAcquire(
            long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                bytes);
            if (bytes == 0)
            {
                return true;
            }

            while (true)
            {
                long current =
                    Interlocked.Read(
                        ref _reservedBytes);
                if (bytes >
                    _maximumBytes -
                    current)
                {
                    Interlocked.Increment(
                        ref _rejectedReservations);
                    return false;
                }
                long updated =
                    current +
                    bytes;
                if (Interlocked.CompareExchange(
                        ref _reservedBytes,
                        updated,
                        current) ==
                    current)
                {
                    UpdatePeak(updated);
                    return true;
                }
            }
        }

        internal void Release(
            long bytes)
        {
            if (bytes == 0)
            {
                return;
            }
            _ = Interlocked.Add(
                ref _reservedBytes,
                -bytes);
        }

        private void UpdatePeak(
            long value)
        {
            while (true)
            {
                long peak =
                    Interlocked.Read(
                        ref _peakReservedBytes);
                if (value <= peak ||
                    Interlocked.CompareExchange(
                        ref _peakReservedBytes,
                        value,
                        peak) ==
                    peak)
                {
                    return;
                }
            }
        }
    }

    internal sealed class ProxyRequestBufferLease :
        IDisposable
    {
        private ProxyRequestBufferBudget _budget;
        private long _bytes;

        internal ProxyRequestBufferLease(
            ProxyRequestBufferBudget budget)
        {
            _budget = budget;
        }

        internal bool TryGrow(
            long bytes)
        {
            ProxyRequestBufferBudget budget =
                _budget;
            ObjectDisposedException.ThrowIf(
                budget == null,
                this);
            if (!budget.TryAcquire(bytes))
            {
                return false;
            }
            _bytes =
                checked(
                    _bytes +
                    bytes);
            return true;
        }

        internal long ReservedBytes =>
            Interlocked.Read(
                ref _bytes);

        internal bool TryEnsure(
            long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                bytes);
            long current =
                Interlocked.Read(
                    ref _bytes);
            return bytes <= current ||
                TryGrow(
                    bytes -
                    current);
        }

        internal void Shrink(
            long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                bytes);
            if (bytes == 0)
            {
                return;
            }
            long remaining =
                Interlocked.Add(
                    ref _bytes,
                    -bytes);
            if (remaining < 0)
            {
                _ = Interlocked.Add(
                    ref _bytes,
                    bytes);
                throw new InvalidOperationException(
                    "The request-buffer reservation was over-released.");
            }
            _budget.Release(bytes);
        }

        public void Dispose()
        {
            ProxyRequestBufferBudget budget =
                Interlocked.Exchange(
                    ref _budget,
                    null);
            if (budget == null)
            {
                return;
            }
            long bytes =
                Interlocked.Exchange(
                    ref _bytes,
                    0);
            budget.Release(bytes);
        }
    }

    internal enum ProxyRequestReadFailure
    {
        None,
        EndOfStream,
        Malformed,
        HeaderTooLarge,
        BodyTooLarge,
        BufferExhausted,
        Timeout,
    }

    internal sealed class ProxyRequestReadResult
    {
        private ProxyRequestReadResult(
            ClientRequest request,
            ProxyRequestReadFailure failure)
        {
            Request = request;
            Failure = failure;
        }

        internal ClientRequest Request { get; }

        internal ProxyRequestReadFailure Failure { get; }

        internal static ProxyRequestReadResult Success(
            ClientRequest request) =>
            new(
                request,
                ProxyRequestReadFailure.None);

        internal static ProxyRequestReadResult Failed(
            ProxyRequestReadFailure failure) =>
            new(
                null,
                failure);
    }

    internal sealed class InitialProxyRequestReader
    {
        private static readonly byte[] HeaderTerminator =
            "\r\n\r\n"u8.ToArray();
        private static readonly byte[] LineTerminator =
            "\r\n"u8.ToArray();
        private const int StringObjectBytes = 24;
        private const int ArrayObjectBytes = 24;
        private const int HeaderFieldBookkeepingBytes = 128;
        // Covers bracketed-IPv6 parser objects and short substrings beyond
        // the authority-sized copies charged separately.
        private const int HostAuthorityBookkeepingBytes = 512;
        private const int HostAuthorityCopyCount = 3;
        // ClientRequest, ParsedRequest, StringBuilder and its initial buffer,
        // plus two HeaderCollection and Dictionary objects.
        private const int RetainedRequestBookkeepingBytes = 512;
        // One dictionary slot/bucket and a one-value List<string> with its
        // backing array. The parser stores one value per retained field.
        private const int RetainedHeaderBookkeepingBytes = 224;
        private const int BodySegmentBookkeepingBytes = 32;
        private const int BodyReaderBookkeepingBytes = 128;
        private const int BodyLineBookkeepingBytes = 32;
        private const int MinPooledBodySegmentBytes =
            4 * 1024;
        private const int MaxPooledBodySegmentBytes =
            64 * 1024;

        private readonly ProxyRequestReadPolicy _policy;
        private readonly ProxyRequestBufferBudget _budget;

        public InitialProxyRequestReader(
            ProxyRequestReadPolicy policy,
            ProxyRequestBufferBudget budget)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(budget);
            policy.Validate();
            _policy = policy;
            _budget = budget;
        }

        internal ProxyRequestBufferBudget BufferBudget =>
            _budget;

        internal Action<WeakReference<byte[]>>
            RawHeaderLifetimeObserver { get; set; }

        internal async Task<ProxyRequestReadResult> ReadAsync(
            PipeReader reader,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            using ProxyRequestBufferLease headerLease =
                _budget.CreateLease();
            try
            {
                ParsedRequest parsed =
                    ParseHeader(
                        await ReadHeaderAsync(
                                reader,
                                headerLease,
                                cancellationToken)
                            .ConfigureAwait(false),
                        headerLease);
                RetainParsedRequest(
                    parsed.Request,
                    headerLease);

                BodyRead body =
                    parsed.Framing.Kind switch
                    {
                        BodyKind.ContentLength =>
                            await ReadContentLengthBodyAsync(
                                reader,
                                parsed.Framing.ContentLength,
                                cancellationToken)
                                .ConfigureAwait(false),
                        BodyKind.Chunked =>
                            await ReadChunkedBodyAsync(
                                reader,
                                cancellationToken)
                                .ConfigureAwait(false),
                        _ => BodyRead.Empty,
                    };
                try
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    parsed.Request.BodyBytes =
                        body.Bytes;
                    // Parsed headers and the exact body now belong to ClientRequest.
                    return
                        ProxyRequestReadResult.Success(
                            parsed.Request);
                }
                finally
                {
                    body.Lease?.Dispose();
                }
            }
            catch (ProxyRequestReadException ex)
            {
                return ProxyRequestReadResult.Failed(
                    ex.Failure);
            }
        }

        private async Task<byte[]> ReadHeaderAsync(
            PipeReader reader,
            ProxyRequestBufferLease headerLease,
            CancellationToken cancellationToken)
        {
            using var deadline =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);
            deadline.CancelAfter(
                _policy.HeaderDeadline);
            long searchedLength = 0;

            while (true)
            {
                ReadResult result;
                try
                {
                    result =
                        await reader.ReadAsync(
                            deadline.Token)
                            .ConfigureAwait(false);
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    if (deadline
                        .IsCancellationRequested)
                    {
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.Timeout);
                    }
                }
                catch (OperationCanceledException) when (
                    !cancellationToken
                        .IsCancellationRequested)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.Timeout);
                }

                ReadOnlySequence<byte> buffer =
                    result.Buffer;
                long bufferedHeaderBytes =
                    Math.Min(
                        buffer.Length,
                        _policy.MaxHeaderBytes);
                if (!headerLease.TryEnsure(
                        bufferedHeaderBytes))
                {
                    reader.AdvanceTo(
                        buffer.End);
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.BufferExhausted);
                }
                long searchStart =
                    Math.Max(
                        0,
                        searchedLength -
                        (HeaderTerminator.Length - 1));
                if (TryFindDelimiter(
                        buffer,
                        searchStart,
                        HeaderTerminator,
                        out long bodyOffset))
                {
                    if (bodyOffset >
                        _policy.MaxHeaderBytes)
                    {
                        reader.AdvanceTo(
                            buffer.End);
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.HeaderTooLarge);
                    }
                    long headerLength =
                        bodyOffset -
                        HeaderTerminator.Length;
                    long headerArrayBytes =
                        EstimateByteArrayBytes(
                            headerLength);
                    if (!headerLease.TryGrow(
                            headerArrayBytes))
                    {
                        reader.AdvanceTo(
                            buffer.End);
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.BufferExhausted);
                    }
                    byte[] headerBytes =
                        buffer.Slice(
                                0,
                                headerLength)
                            .ToArray();
                    SequencePosition bodyStart =
                        buffer.GetPosition(
                            bodyOffset);
                    reader.AdvanceTo(
                        bodyStart,
                        bodyStart);
                    headerLease.Shrink(
                        bufferedHeaderBytes);
                    return headerBytes;
                }

                if (buffer.Length >=
                    _policy.MaxHeaderBytes)
                {
                    reader.AdvanceTo(
                        buffer.End);
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.HeaderTooLarge);
                }
                if (result.IsCompleted)
                {
                    bool empty =
                        buffer.IsEmpty;
                    reader.AdvanceTo(
                        buffer.End);
                    throw new ProxyRequestReadException(
                        empty
                            ? ProxyRequestReadFailure.EndOfStream
                            : ProxyRequestReadFailure.Malformed);
                }

                searchedLength =
                    buffer.Length;
                reader.AdvanceTo(
                    buffer.Start,
                    buffer.End);
            }
        }

        private ParsedRequest ParseHeader(
            byte[] headerBytes,
            ProxyRequestBufferLease headerLease)
        {
            long maximumTextBytes =
                EstimateStringBytes(
                    Encoding.UTF8.GetMaxCharCount(
                        headerBytes.Length));
            if (!headerLease.TryGrow(
                    maximumTextBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            string text =
                Encoding.UTF8.GetString(
                    headerBytes);
            long actualTextBytes =
                EstimateStringBytes(
                    text.Length);
            headerLease.Shrink(
                maximumTextBytes -
                actualTextBytes);
            int fieldCount =
                CountHeaderFields(text);
            if (fieldCount >
                _policy.MaxHeaderFields)
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.HeaderTooLarge);
            }
            // Charge line copies, name/value/request-line substrings and the
            // worst-case retained request graph before any of them are built.
            long parsingBytes =
                checked(
                    checked(
                        EstimateStringBytes(
                            text.Length) *
                        2) +
                    RetainedRequestBookkeepingBytes +
                    ((long)(fieldCount + 1) *
                     HeaderFieldBookkeepingBytes) +
                    ((long)fieldCount *
                     RetainedHeaderBookkeepingBytes));
            if (!headerLease.TryGrow(
                    parsingBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            string[] lines =
                text.Split(
                    "\r\n",
                    StringSplitOptions.None);
            if (lines.Length == 0)
            {
                throw Malformed();
            }

            if (!TryParseRequestLine(
                    lines[0],
                    out string method,
                    out string path,
                    out string version))
            {
                throw Malformed();
            }

            var headers =
                new List<RawHeader>(
                    Math.Max(
                        0,
                        lines.Length - 1));
            for (var index = 1;
                index < lines.Length;
                index++)
            {
                headers.Add(
                    ParseHeaderLine(
                        lines[index]));
            }

            BodyFraming framing =
                ParseFraming(headers);
            var request =
                new ClientRequest
                {
                    Method = method,
                    Path = path,
                    Version = version,
                };

            var hostCount = 0;
            string hostAuthority = null;
            foreach (RawHeader header in headers)
            {
                if (header.Name.Equals(
                        "Host",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hostCount++;
                    if (hostCount > 1)
                    {
                        throw Malformed();
                    }
                    hostAuthority =
                        header.Value;
                }
                if (header.Name.Equals(
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals(
                        "Transfer-Encoding",
                        StringComparison.OrdinalIgnoreCase) ||
                    (framing.Kind ==
                        BodyKind.Chunked &&
                     header.Name.Equals(
                        "Trailer",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (IsContentHeader(
                        header.Name.AsSpan()))
                {
                    request.ContentHeaders[
                        header.Name] =
                        header.Value;
                }
                else
                {
                    request.Headers[
                        header.Name] =
                        header.Value;
                }
            }
            if (framing.Kind ==
                BodyKind.ContentLength)
            {
                request.ContentHeaders[
                    "Content-Length"] =
                    framing.ContentLength.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrEmpty(
                    hostAuthority))
            {
                ApplyAuthority(
                    request,
                    hostAuthority,
                    defaultPort: -1,
                    headerLease);
            }
            else if (request.Method == "CONNECT")
            {
                ApplyAuthority(
                    request,
                    request.Path,
                    defaultPort: 443,
                    headerLease);
            }

            var parsed =
                new ParsedRequest(
                    request,
                    framing);
            RawHeaderLifetimeObserver?.Invoke(
                new WeakReference<byte[]>(
                    headerBytes));
            return parsed;
        }

        private static void ApplyAuthority(
            ClientRequest request,
            string authority,
            int defaultPort,
            ProxyRequestBufferLease headerLease)
        {
            long parsingBytes =
                EstimateAuthorityParsingBytes(
                    authority.Length);
            if (!headerLease.TryGrow(
                    parsingBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            if (!HostAuthority.TryParse(
                    authority,
                    out string host,
                    out int? port))
            {
                throw Malformed();
            }
            request.Host = host;
            request.Port =
                port ??
                defaultPort;
        }

        internal static long EstimateAuthorityParsingBytes(
            int authorityLength) =>
            checked(
                HostAuthorityBookkeepingBytes +
                (HostAuthorityCopyCount *
                 EstimateStringBytes(
                     authorityLength)));

        private static bool TryParseRequestLine(
            string line,
            out string method,
            out string path,
            out string version)
        {
            method = null;
            path = null;
            version = null;
            Span<int> starts =
                stackalloc int[3];
            Span<int> lengths =
                stackalloc int[3];
            var count = 0;
            var index = 0;
            while (index <
                line.Length)
            {
                while (index <
                        line.Length &&
                    line[index] == ' ')
                {
                    index++;
                }
                if (index ==
                    line.Length)
                {
                    break;
                }
                if (count ==
                    starts.Length)
                {
                    return false;
                }
                int start = index;
                while (index <
                        line.Length &&
                    line[index] != ' ')
                {
                    index++;
                }
                starts[count] = start;
                lengths[count] =
                    index -
                    start;
                count++;
            }
            if (count != 3)
            {
                return false;
            }

            ReadOnlySpan<char> methodSpan =
                line.AsSpan(
                    starts[0],
                    lengths[0]);
            ReadOnlySpan<char> pathSpan =
                line.AsSpan(
                    starts[1],
                    lengths[1]);
            ReadOnlySpan<char> versionSpan =
                line.AsSpan(
                    starts[2],
                    lengths[2]);
            if (!IsToken(methodSpan) ||
                pathSpan.IsEmpty ||
                (!versionSpan.SequenceEqual(
                        "HTTP/1.0") &&
                 !versionSpan.SequenceEqual(
                        "HTTP/1.1")))
            {
                return false;
            }

            method =
                line.Substring(
                    starts[0],
                    lengths[0]);
            path =
                line.Substring(
                    starts[1],
                    lengths[1]);
            version =
                line.Substring(
                    starts[2],
                    lengths[2]);
            return true;
        }

        // Parsing keeps the raw copy, decoded text and split structures charged
        // through completion. Only the reachable request graph remains while
        // the body is read; duplicate string references are counted separately.
        private static void RetainParsedRequest(
            ClientRequest request,
            ProxyRequestBufferLease headerLease)
        {
            long retainedBytes =
                EstimateRetainedRequestBytes(
                    request);
            if (!headerLease.TryEnsure(
                    retainedBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            headerLease.Shrink(
                headerLease.ReservedBytes -
                retainedBytes);
        }

        internal static long EstimateRetainedRequestBytes(
            ClientRequest request)
        {
            long bytes =
                RetainedRequestBookkeepingBytes;
            bytes = AddRetainedString(
                bytes,
                request.Method);
            bytes = AddRetainedString(
                bytes,
                request.Path);
            bytes = AddRetainedString(
                bytes,
                request.Version);
            bytes = AddRetainedString(
                bytes,
                request.Host);
            bytes = AddRetainedHeaders(
                bytes,
                request.Headers);
            return AddRetainedHeaders(
                bytes,
                request.ContentHeaders);
        }

        private static long AddRetainedHeaders(
            long bytes,
            HeaderCollection headers)
        {
            foreach (KeyValuePair<
                string,
                IEnumerable<string>> header in
                headers)
            {
                bytes =
                    checked(
                        bytes +
                        RetainedHeaderBookkeepingBytes);
                bytes = AddRetainedString(
                    bytes,
                    header.Key);
                foreach (string value in
                    header.Value)
                {
                    bytes = AddRetainedString(
                        bytes,
                        value);
                }
            }
            return bytes;
        }

        private static long AddRetainedString(
            long bytes,
            string value) =>
            value == null
                ? bytes
                : checked(
                    bytes +
                    EstimateStringBytes(
                        value.Length));

        private static int CountHeaderFields(
            string text)
        {
            var count = 0;
            var start = 0;
            while (true)
            {
                int separator =
                    text.IndexOf(
                        "\r\n",
                        start,
                        StringComparison.Ordinal);
                if (separator < 0)
                {
                    return count;
                }
                count++;
                start =
                    separator +
                    2;
            }
        }

        private static long EstimateStringBytes(
            int characters)
        {
            long bytes =
                checked(
                    StringObjectBytes +
                    ((long)characters * 2) +
                    2);
            return checked(
                (bytes + 7) &
                ~7L);
        }

        private static long EstimateByteArrayBytes(
            long length)
        {
            long bytes =
                checked(
                    ArrayObjectBytes +
                    length);
            return checked(
                (bytes + 7) &
                ~7L);
        }

        private static int PooledCapacityFor(
            int length)
        {
            var capacity = 16;
            while (capacity < length)
            {
                capacity =
                    checked(
                        capacity * 2);
            }
            return capacity;
        }

        private static void EnsureSegmentCapacity(
            List<BodySegment> segments,
            ProxyRequestBufferLease lease)
        {
            if (segments.Count <
                segments.Capacity)
            {
                return;
            }
            int oldCapacity =
                segments.Capacity;
            int newCapacity =
                oldCapacity == 0
                    ? 4
                    : checked(
                        oldCapacity * 2);
            long oldBytes =
                oldCapacity == 0
                    ? 0
                    : checked(
                        ArrayObjectBytes +
                        ((long)oldCapacity *
                         IntPtr.Size));
            long newBytes =
                checked(
                    ArrayObjectBytes +
                    ((long)newCapacity *
                     IntPtr.Size));
            if (!lease.TryGrow(newBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            try
            {
                segments.Capacity =
                    newCapacity;
            }
            catch
            {
                lease.Shrink(newBytes);
                throw;
            }
            lease.Shrink(oldBytes);
        }

        private BodyFraming ParseFraming(
            IReadOnlyList<RawHeader> headers)
        {
            var hasContentLength = false;
            var hasTransferEncoding = false;
            foreach (RawHeader header in headers)
            {
                if (header.Name.Equals(
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasContentLength = true;
                }
                else if (header.Name.Equals(
                        "Transfer-Encoding",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasTransferEncoding = true;
                }
            }

            if (hasContentLength &&
                hasTransferEncoding)
            {
                throw Malformed();
            }
            if (hasTransferEncoding)
            {
                var codingCount = 0;
                foreach (RawHeader header in
                    headers)
                {
                    if (!header.Name.Equals(
                            "Transfer-Encoding",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    ReadOnlySpan<char> remaining =
                        header.Value.AsSpan();
                    while (true)
                    {
                        int separator =
                            remaining.IndexOf(',');
                        ReadOnlySpan<char> coding =
                            (separator < 0
                                ? remaining
                                : remaining[..separator])
                            .Trim();
                        codingCount++;
                        if (coding.Length == 0 ||
                            coding.Contains(
                                ';') ||
                            codingCount > 1 ||
                            !coding.Equals(
                                "chunked",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw Malformed();
                        }
                        if (separator < 0)
                        {
                            break;
                        }
                        remaining =
                            remaining[
                                (separator + 1)..];
                    }
                }
                if (codingCount != 1)
                {
                    throw Malformed();
                }
                return BodyFraming.Chunked;
            }
            if (!hasContentLength)
            {
                return BodyFraming.None;
            }

            long? expected = null;
            foreach (RawHeader header in
                headers)
            {
                if (!header.Name.Equals(
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ReadOnlySpan<char> remaining =
                    header.Value.AsSpan();
                while (true)
                {
                    int separator =
                        remaining.IndexOf(',');
                    ReadOnlySpan<char> lengthText =
                        (separator < 0
                            ? remaining
                            : remaining[..separator])
                        .Trim();
                    if (lengthText.Length == 0 ||
                        !IsAsciiDigits(
                            lengthText) ||
                        !ulong.TryParse(
                            lengthText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong parsed))
                    {
                        throw Malformed();
                    }
                    if (parsed >
                        (ulong)_policy.MaxBodyBytes)
                    {
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.BodyTooLarge);
                    }
                    long length =
                        (long)parsed;
                    if (expected.HasValue &&
                        expected.Value != length)
                    {
                        throw Malformed();
                    }
                    expected = length;
                    if (separator < 0)
                    {
                        break;
                    }
                    remaining =
                        remaining[
                            (separator + 1)..];
                }
            }
            return new BodyFraming(
                BodyKind.ContentLength,
                checked((int)expected.GetValueOrDefault()));
        }

        private async Task<BodyRead>
            ReadContentLengthBodyAsync(
                PipeReader reader,
                int contentLength,
                CancellationToken cancellationToken)
        {
            if (contentLength == 0)
            {
                return BodyRead.Empty;
            }
            using ProxyRequestBufferLease segmentsLease =
                _budget.CreateLease();
            if (!segmentsLease.TryGrow(
                    BodyReaderBookkeepingBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            using var bodyReader =
                new BodyPipeReader(
                    reader,
                    _policy,
                    _budget,
                    cancellationToken);
            using var body =
                new PooledBodyBuilder(
                    _budget,
                    segmentsLease);
            await body.ReadExactlyAsync(
                    bodyReader,
                    contentLength,
                    cancellationToken)
                .ConfigureAwait(false);
            return body.CreateBodyRead(
                cancellationToken);
        }

        private async Task<BodyRead>
            ReadChunkedBodyAsync(
                PipeReader reader,
                CancellationToken cancellationToken)
        {
            using ProxyRequestBufferLease chunksLease =
                _budget.CreateLease();
            if (!chunksLease.TryGrow(
                    BodyReaderBookkeepingBytes))
            {
                throw new ProxyRequestReadException(
                    ProxyRequestReadFailure.BufferExhausted);
            }
            using var bodyReader =
                new BodyPipeReader(
                    reader,
                    _policy,
                    _budget,
                    cancellationToken);
            using var body =
                new PooledBodyBuilder(
                    _budget,
                    chunksLease);
            var chunkCount = 0;
            while (true)
            {
                using BodyLine line =
                    await bodyReader.ReadLineAsync(
                            _policy.MaxChunkLineBytes,
                            ProxyRequestReadFailure.HeaderTooLarge)
                        .ConfigureAwait(false);
                int chunkSize =
                    ParseChunkSize(line);
                if (chunkSize == 0)
                {
                    await ReadTrailersAsync(
                            bodyReader)
                        .ConfigureAwait(false);
                    break;
                }
                chunkCount++;
                if (chunkCount >
                    _policy.MaxChunkCount ||
                    chunkSize >
                    _policy.MaxBodyBytes -
                    body.Length)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.BodyTooLarge);
                }
                await body.ReadExactlyAsync(
                        bodyReader,
                        chunkSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                await bodyReader.ExpectCrlfAsync()
                    .ConfigureAwait(false);
            }

            return body.CreateBodyRead(
                cancellationToken);
        }

        private async Task ReadTrailersAsync(
            BodyPipeReader reader)
        {
            var bytes = 0;
            var fields = 0;
            while (true)
            {
                using BodyLine line =
                    await reader.ReadLineAsync(
                            _policy.MaxTrailerBytes,
                            ProxyRequestReadFailure.HeaderTooLarge)
                        .ConfigureAwait(false);
                bytes =
                    checked(
                        bytes +
                        line.Bytes.Length +
                        LineTerminator.Length);
                if (bytes >
                    _policy.MaxTrailerBytes)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.HeaderTooLarge);
                }
                if (line.Bytes.Length == 0)
                {
                    return;
                }
                fields++;
                if (fields >
                    _policy.MaxTrailerFields)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.HeaderTooLarge);
                }

                RawHeader trailer =
                    ParseHeaderLine(
                        line.GetAsciiString());
                if (trailer.Name.Equals(
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase) ||
                    trailer.Name.Equals(
                        "Transfer-Encoding",
                        StringComparison.OrdinalIgnoreCase) ||
                    trailer.Name.Equals(
                        "Host",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw Malformed();
                }
            }
        }

        private int ParseChunkSize(
            BodyLine line)
        {
            string text =
                line.GetAsciiString();
            int extension =
                text.IndexOf(';');
            string sizeText =
                extension < 0
                    ? text
                    : text.Substring(
                        0,
                        extension);
            ulong size = 0;
            if (sizeText.Length == 0 ||
                sizeText.Length > 16 ||
                !ulong.TryParse(
                    sizeText,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out size) ||
                size >
                    (ulong)_policy.MaxBodyBytes)
            {
                throw new ProxyRequestReadException(
                    size >
                        (ulong)_policy.MaxBodyBytes
                        ? ProxyRequestReadFailure.BodyTooLarge
                        : ProxyRequestReadFailure.Malformed);
            }
            if (extension >= 0)
            {
                for (var index = extension + 1;
                    index < text.Length;
                    index++)
                {
                    char character = text[index];
                    if ((character < 0x20 &&
                         character != '\t') ||
                        character == 0x7f)
                    {
                        throw Malformed();
                    }
                }
            }
            return checked((int)size);
        }

        private static RawHeader ParseHeaderLine(
            string line)
        {
            if (string.IsNullOrEmpty(line) ||
                char.IsWhiteSpace(line[0]))
            {
                throw Malformed();
            }
            int separator =
                line.IndexOf(':');
            if (separator <= 0)
            {
                throw Malformed();
            }
            if (!IsToken(
                    line.AsSpan(
                        0,
                        separator)))
            {
                throw Malformed();
            }
            string name =
                line.Substring(
                    0,
                    separator);
            int valueStart =
                separator +
                1;
            while (valueStart <
                    line.Length &&
                line[valueStart] is
                    ' ' or '\t')
            {
                valueStart++;
            }
            int valueEnd =
                line.Length;
            while (valueEnd >
                    valueStart &&
                line[valueEnd - 1] is
                    ' ' or '\t')
            {
                valueEnd--;
            }
            string value =
                line.Substring(
                    valueStart,
                    valueEnd -
                    valueStart);
            foreach (char character in value)
            {
                if ((character < 0x20 &&
                     character != '\t') ||
                    character == 0x7f)
                {
                    throw Malformed();
                }
            }
            return new RawHeader(
                name,
                value);
        }

        private static bool IsToken(
            ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!(char.IsAsciiLetterOrDigit(character) ||
                      character is
                          '!' or '#' or '$' or '%' or '&' or
                          '\'' or '*' or '+' or '-' or '.' or
                          '^' or '_' or '`' or '|' or '~'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsAsciiDigits(
            ReadOnlySpan<char> value)
        {
            foreach (char character in value)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }
            return true;
        }

        // Matches ProxyHeaderUtilities without allocating a lowercase copy
        // while the bounded parser is still accounting its peak.
        internal static bool IsContentHeader(
            ReadOnlySpan<char> name) =>
            name.Equals(
                "allow",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-disposition",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-encoding",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-language",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-length",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-location",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-md5",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-range",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "content-type",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "expires",
                StringComparison.OrdinalIgnoreCase) ||
            name.Equals(
                "last-modified",
                StringComparison.OrdinalIgnoreCase);

        private static bool TryFindDelimiter(
            ReadOnlySequence<byte> buffer,
            long searchStart,
            ReadOnlySpan<byte> delimiter,
            out long consumedLength)
        {
            var reader =
                new SequenceReader<byte>(
                    buffer.Slice(searchStart));
            if (reader.TryReadTo(
                    out ReadOnlySequence<byte> _,
                    delimiter,
                    advancePastDelimiter: true))
            {
                consumedLength =
                    checked(
                        searchStart +
                        reader.Consumed);
                return true;
            }
            consumedLength = 0;
            return false;
        }

        private static ProxyRequestReadException
            Malformed() =>
            new(
                ProxyRequestReadFailure.Malformed);

        private enum BodyKind
        {
            None,
            ContentLength,
            Chunked,
        }

        private readonly record struct BodyFraming(
            BodyKind Kind,
            int ContentLength)
        {
            internal static BodyFraming None =>
                new(
                    BodyKind.None,
                    0);

            internal static BodyFraming Chunked =>
                new(
                    BodyKind.Chunked,
                    0);
        }

        private sealed record ParsedRequest(
            ClientRequest Request,
            BodyFraming Framing);

        private sealed record RawHeader(
            string Name,
            string Value);

        private sealed record BodyRead(
            byte[] Bytes,
            ProxyRequestBufferLease Lease)
        {
            internal static BodyRead Empty { get; } =
                new(
                    Array.Empty<byte>(),
                    null);
        }

        private sealed class BodySegment
        {
            internal BodySegment(
                byte[] buffer)
            {
                Buffer = buffer;
            }

            internal byte[] Buffer { get; }

            internal int Count { get; set; }
        }

        private sealed class PooledBodyBuilder :
            IDisposable
        {
            private readonly ProxyRequestBufferBudget _budget;
            private readonly ProxyRequestBufferLease _segmentsLease;
            private readonly List<BodySegment> _segments =
                new();

            internal PooledBodyBuilder(
                ProxyRequestBufferBudget budget,
                ProxyRequestBufferLease segmentsLease)
            {
                _budget = budget;
                _segmentsLease = segmentsLease;
            }

            internal int Length { get; private set; }

            internal async Task ReadExactlyAsync(
                BodyPipeReader reader,
                int count,
                CancellationToken cancellationToken)
            {
                var remaining = count;
                while (remaining > 0)
                {
                    int read =
                        await reader.ReadIntoAsync(
                                this,
                                remaining,
                                cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= read;
                }
            }

            internal void Append(
                ReadOnlySequence<byte> source,
                CancellationToken cancellationToken)
            {
                foreach (ReadOnlyMemory<byte> memory in
                    source)
                {
                    ReadOnlySpan<byte> remaining =
                        memory.Span;
                    while (!remaining.IsEmpty)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        BodySegment segment =
                            WritableSegment(
                                remaining.Length);
                        int writable =
                            Math.Min(
                                remaining.Length,
                                segment.Buffer.Length -
                                segment.Count);
                        remaining.Slice(
                                0,
                                writable)
                            .CopyTo(
                                segment.Buffer.AsSpan(
                                    segment.Count,
                                    writable));
                        segment.Count += writable;
                        Length =
                            checked(
                                Length +
                                writable);
                        remaining =
                            remaining.Slice(
                                writable);
                    }
                }
            }

            internal BodyRead CreateBodyRead(
                CancellationToken cancellationToken)
            {
                if (Length == 0)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    return BodyRead.Empty;
                }
                if (!_budget.TryReserve(
                        EstimateByteArrayBytes(
                            Length),
                        out ProxyRequestBufferLease finalLease))
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.BufferExhausted);
                }
                try
                {
                    byte[] body =
                        GC.AllocateUninitializedArray<byte>(
                            Length);
                    var offset = 0;
                    foreach (BodySegment segment in
                        _segments)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        segment.Buffer.AsSpan(
                                0,
                                segment.Count)
                            .CopyTo(
                                body.AsSpan(
                                    offset,
                                    segment.Count));
                        offset += segment.Count;
                    }
                    return new BodyRead(
                        body,
                        finalLease);
                }
                catch
                {
                    finalLease.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (BodySegment segment in
                    _segments)
                {
                    ArrayPool<byte>
                        .Shared.Return(
                            segment.Buffer);
                }
                _segments.Clear();
            }

            private BodySegment WritableSegment(
                int remaining)
            {
                if (_segments.Count > 0)
                {
                    BodySegment current =
                        _segments[^1];
                    if (current.Count <
                        current.Buffer.Length)
                    {
                        return current;
                    }
                }

                EnsureSegmentCapacity(
                    _segments,
                    _segmentsLease);
                int requested =
                    Math.Min(
                        Math.Max(
                            remaining,
                            MinPooledBodySegmentBytes),
                        MaxPooledBodySegmentBytes);
                int expectedCapacity =
                    PooledCapacityFor(
                        requested);
                long expectedBytes =
                    checked(
                        EstimateByteArrayBytes(
                            expectedCapacity) +
                        BodySegmentBookkeepingBytes);
                if (!_segmentsLease.TryGrow(
                        expectedBytes))
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.BufferExhausted);
                }

                byte[] buffer =
                    ArrayPool<byte>
                        .Shared.Rent(
                            requested);
                long actualBytes =
                    checked(
                        EstimateByteArrayBytes(
                            buffer.Length) +
                        BodySegmentBookkeepingBytes);
                if (actualBytes >
                    expectedBytes)
                {
                    if (!_segmentsLease.TryGrow(
                            actualBytes -
                            expectedBytes))
                    {
                        ArrayPool<byte>
                            .Shared.Return(
                                buffer);
                        _segmentsLease.Shrink(
                            expectedBytes);
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.BufferExhausted);
                    }
                }
                else if (actualBytes <
                    expectedBytes)
                {
                    _segmentsLease.Shrink(
                        expectedBytes -
                        actualBytes);
                }

                try
                {
                    var segment =
                        new BodySegment(
                            buffer);
                    _segments.Add(segment);
                    return segment;
                }
                catch
                {
                    ArrayPool<byte>
                        .Shared.Return(
                            buffer);
                    _segmentsLease.Shrink(
                        actualBytes);
                    throw;
                }
            }
        }

        private sealed class BodyLine :
            IDisposable
        {
            private ProxyRequestBufferLease _lease;
            private long _reservedBytes;
            private string _text;

            internal BodyLine(
                byte[] bytes,
                ProxyRequestBufferLease lease,
                long reservedBytes)
            {
                Bytes = bytes;
                _lease = lease;
                _reservedBytes = reservedBytes;
            }

            internal byte[] Bytes { get; }

            internal string GetAsciiString()
            {
                if (_text != null)
                {
                    return _text;
                }
                long textBytes =
                    EstimateStringBytes(
                        Bytes.Length);
                if (!_lease.TryGrow(
                        textBytes))
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.BufferExhausted);
                }
                _reservedBytes =
                    checked(
                        _reservedBytes +
                        textBytes);
                _text =
                    Encoding.ASCII.GetString(
                        Bytes);
                return _text;
            }

            public void Dispose()
            {
                ProxyRequestBufferLease lease =
                    Interlocked.Exchange(
                        ref _lease,
                        null);
                lease?.Shrink(
                    _reservedBytes);
            }
        }

        private sealed class BodyPipeReader :
            IDisposable
        {
            private readonly PipeReader _reader;
            private readonly ProxyRequestReadPolicy _policy;
            private readonly ProxyRequestBufferBudget _budget;
            private readonly CancellationToken _callerToken;
            private readonly CancellationTokenSource _total;
            private readonly CancellationTokenSource _inactivity;
            private readonly byte[] _ending =
                new byte[2];

            internal BodyPipeReader(
                PipeReader reader,
                ProxyRequestReadPolicy policy,
                ProxyRequestBufferBudget budget,
                CancellationToken callerToken)
            {
                _reader = reader;
                _policy = policy;
                _budget = budget;
                _callerToken = callerToken;
                _total =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            callerToken);
                _total.CancelAfter(
                    policy.BodyTotalTimeout);
                _inactivity =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            _total.Token);
            }

            internal async Task ReadExactlyAsync(
                Memory<byte> destination)
            {
                var offset = 0;
                while (offset <
                    destination.Length)
                {
                    ReadResult result =
                        await ReadAsync()
                            .ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer =
                        result.Buffer;
                    int count =
                        checked(
                            (int)Math.Min(
                                destination.Length -
                                offset,
                                buffer.Length));
                    if (count > 0)
                    {
                        buffer.Slice(
                                0,
                                count)
                            .CopyTo(
                                destination.Span.Slice(
                                    offset,
                                    count));
                    }
                    SequencePosition consumed =
                        buffer.GetPosition(count);
                    _reader.AdvanceTo(
                        consumed,
                        consumed);
                    offset += count;
                    if (offset <
                            destination.Length &&
                        result.IsCompleted)
                    {
                        throw Malformed();
                    }
                }
            }

            internal async Task<int> ReadIntoAsync(
                PooledBodyBuilder destination,
                int maximumBytes,
                CancellationToken cancellationToken)
            {
                while (true)
                {
                    ReadResult result =
                        await ReadAsync()
                            .ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer =
                        result.Buffer;
                    int count =
                        checked(
                            (int)Math.Min(
                                maximumBytes,
                                buffer.Length));
                    if (count == 0)
                    {
                        _reader.AdvanceTo(
                            buffer.Start,
                            buffer.End);
                        if (result.IsCompleted)
                        {
                            throw Malformed();
                        }
                        continue;
                    }

                    SequencePosition consumed =
                        buffer.GetPosition(count);
                    try
                    {
                        destination.Append(
                            buffer.Slice(
                                0,
                                count),
                            cancellationToken);
                    }
                    catch
                    {
                        _reader.AdvanceTo(
                            buffer.End);
                        throw;
                    }
                    _reader.AdvanceTo(
                        consumed,
                        consumed);
                    return count;
                }
            }

            internal async Task<BodyLine> ReadLineAsync(
                int maximumBytes,
                ProxyRequestReadFailure limitFailure)
            {
                ProxyRequestBufferLease lineLease =
                    _budget.CreateLease();
                long searchedLength = 0;
                try
                {
                    while (true)
                    {
                        ReadResult result =
                            await ReadAsync()
                                .ConfigureAwait(false);
                        ReadOnlySequence<byte> buffer =
                            result.Buffer;
                        long bufferedLineBytes =
                            Math.Min(
                                buffer.Length,
                                maximumBytes +
                                (LineTerminator.Length - 1));
                        if (!lineLease.TryEnsure(
                                bufferedLineBytes))
                        {
                            _reader.AdvanceTo(
                                buffer.End);
                            throw new ProxyRequestReadException(
                                ProxyRequestReadFailure.BufferExhausted);
                        }
                        long searchStart =
                            Math.Max(
                                0,
                                searchedLength -
                                (LineTerminator.Length - 1));
                        if (TryFindDelimiter(
                                buffer,
                                searchStart,
                                LineTerminator,
                                out long consumedLength))
                        {
                            long lineLength =
                                consumedLength -
                                LineTerminator.Length;
                            if (lineLength >
                                maximumBytes)
                            {
                                _reader.AdvanceTo(
                                    buffer.End);
                                throw new ProxyRequestReadException(
                                    limitFailure);
                            }
                            long reservation =
                                checked(
                                    EstimateByteArrayBytes(
                                        lineLength) +
                                    BodyLineBookkeepingBytes);
                            if (!lineLease.TryGrow(
                                    reservation))
                            {
                                _reader.AdvanceTo(
                                    buffer.End);
                                throw new ProxyRequestReadException(
                                    ProxyRequestReadFailure.BufferExhausted);
                            }
                            byte[] line =
                                buffer.Slice(
                                        0,
                                        lineLength)
                                    .ToArray();
                            SequencePosition consumed =
                                buffer.GetPosition(
                                    consumedLength);
                            _reader.AdvanceTo(
                                consumed,
                                consumed);
                            lineLease.Shrink(
                                bufferedLineBytes);
                            var resultLine =
                                new BodyLine(
                                    line,
                                    lineLease,
                                    reservation);
                            lineLease = null;
                            return resultLine;
                        }

                        if (buffer.Length >
                            maximumBytes +
                            (LineTerminator.Length - 1))
                        {
                            _reader.AdvanceTo(
                                buffer.End);
                            throw new ProxyRequestReadException(
                                limitFailure);
                        }
                        if (result.IsCompleted)
                        {
                            _reader.AdvanceTo(
                                buffer.End);
                            throw Malformed();
                        }
                        searchedLength =
                            buffer.Length;
                        _reader.AdvanceTo(
                            buffer.Start,
                            buffer.End);
                    }
                }
                catch
                {
                    lineLease?.Dispose();
                    throw;
                }
            }

            internal async Task ExpectCrlfAsync()
            {
                await ReadExactlyAsync(_ending)
                    .ConfigureAwait(false);
                if (!_ending.AsSpan().SequenceEqual(
                        LineTerminator))
                {
                    throw Malformed();
                }
            }

            public void Dispose()
            {
                _inactivity.Dispose();
                _total.Dispose();
            }

            private async ValueTask<ReadResult>
                ReadAsync()
            {
                _callerToken
                    .ThrowIfCancellationRequested();
                if (_total.IsCancellationRequested)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.Timeout);
                }
                _inactivity.CancelAfter(
                    _policy.BodyInactivityTimeout);
                try
                {
                    ReadResult result =
                        await _reader.ReadAsync(
                                _inactivity.Token)
                            .ConfigureAwait(false);
                    _callerToken
                        .ThrowIfCancellationRequested();
                    if (_total.IsCancellationRequested)
                    {
                        throw new ProxyRequestReadException(
                            ProxyRequestReadFailure.Timeout);
                    }
                    return result;
                }
                catch (OperationCanceledException) when (
                    !_callerToken
                        .IsCancellationRequested)
                {
                    throw new ProxyRequestReadException(
                        ProxyRequestReadFailure.Timeout);
                }
                finally
                {
                    if (!_inactivity.IsCancellationRequested)
                    {
                        _inactivity.CancelAfter(
                            Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        private sealed class ProxyRequestReadException :
            Exception
        {
            internal ProxyRequestReadException(
                ProxyRequestReadFailure failure)
            {
                Failure = failure;
            }

            internal ProxyRequestReadFailure Failure { get; }
        }
    }
}
