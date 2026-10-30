// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtSchemaPreflight
    {
        private const byte InteriorTablePage = 0x05;
        private const byte LeafTablePage = 0x0d;
        private static readonly byte[] Header =
            Encoding.ASCII.GetBytes("SQLite format 3\0");
        private static readonly UTF8Encoding StrictUtf8 =
            new(false, true);

        internal static SqLtValidatedSchema Validate(
            Stream input,
            SqLtSchemaProfile profile)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(profile);
            if (!input.CanRead ||
                !input.CanSeek)
            {
                throw Invalid();
            }

            try
            {
                return new Reader(
                    input,
                    profile).Read();
            }
            catch (SqLtSchemaException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw Invalid(ex);
            }
        }

        private static SqLtSchemaException Invalid(
            Exception innerException = null) =>
            new(
                SqLtSchemaFailure.Invalid,
                innerException);

        private static SqLtSchemaException Unsupported() =>
            new(SqLtSchemaFailure.Unsupported);

        private static SqLtSchemaException Limit() =>
            new(SqLtSchemaFailure.Limit);

        private sealed record RawSchemaRecord(
            string Type,
            string Name,
            string TableName,
            long RootPage,
            string Sql);

        private sealed class Reader
        {
            private readonly Stream _input;
            private readonly SqLtSchemaProfile _profile;
            private readonly HashSet<uint> _visitedPages = new();
            private readonly List<RawSchemaRecord> _records = new();
            private int _pageSize;
            private int _usablePageSize;
            private uint _pageCount;
            private int _totalPayloadBytes;

            internal Reader(
                Stream input,
                SqLtSchemaProfile profile)
            {
                _input = input;
                _profile = profile;
            }

            internal SqLtValidatedSchema Read()
            {
                ReadHeader();
                ReadBtreePage(
                    1,
                    0);
                var tables =
                    new Dictionary<string, SqLtValidatedTable>(
                        StringComparer.Ordinal);
                foreach (RawSchemaRecord record in _records)
                {
                    if (!record.Type.Equals(
                            "table",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(record.Name) ||
                        string.IsNullOrEmpty(record.TableName) ||
                        record.Sql == null ||
                        record.RootPage <= 0 ||
                        record.RootPage > _pageCount ||
                        !record.Name.Equals(
                            record.TableName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unsupported();
                    }

                    SqLtExpectedTable expected =
                        SqLtSchemaProfile.GetKnownTable(
                            record.Name);
                    if (expected == null)
                    {
                        throw Unsupported();
                    }
                    if (StrictUtf8.GetByteCount(record.Sql) >
                        SqLtSchemaProfile.MaxDdlBytes)
                    {
                        throw Limit();
                    }

                    SqLtValidatedTable table =
                        SqLtCreateTableParser.Parse(
                            record.Sql,
                            record.Name,
                            checked((int)record.RootPage),
                            expected);
                    if (!tables.TryAdd(
                            table.CanonicalName,
                            table))
                    {
                        throw Invalid();
                    }
                }

                foreach (string required in
                    _profile.RequiredTables)
                {
                    if (!tables.ContainsKey(required))
                    {
                        throw Unsupported();
                    }
                }

                return new SqLtValidatedSchema(tables);
            }

            private void ReadHeader()
            {
                if (_input.Length < 512)
                {
                    throw Invalid();
                }

                var header = new byte[100];
                ReadExactly(
                    0,
                    header);
                if (!header.AsSpan(
                        0,
                        Header.Length)
                    .SequenceEqual(Header))
                {
                    throw Invalid();
                }

                int encodedPageSize =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        header.AsSpan(
                            16,
                            2));
                _pageSize = encodedPageSize == 1
                    ? 65_536
                    : encodedPageSize;
                if (_pageSize < 512 ||
                    _pageSize > 65_536 ||
                    (_pageSize &
                        (_pageSize - 1)) != 0 ||
                    _input.Length % _pageSize != 0)
                {
                    throw Invalid();
                }
                if (header[18] != 1 ||
                    header[19] != 1)
                {
                    throw Unsupported();
                }

                int reserved = header[20];
                _usablePageSize =
                    _pageSize - reserved;
                if (_usablePageSize < 480)
                {
                    throw Invalid();
                }

                long physicalPages =
                    _input.Length / _pageSize;
                uint declaredPages =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        header.AsSpan(
                            28,
                            4));
                uint changeCounter =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        header.AsSpan(
                            24,
                            4));
                uint versionValidFor =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        header.AsSpan(
                            92,
                            4));
                if (physicalPages <= 0 ||
                    physicalPages > uint.MaxValue)
                {
                    throw Invalid();
                }
                if (declaredPages != 0 &&
                    changeCounter == versionValidFor)
                {
                    if (declaredPages >
                        physicalPages)
                    {
                        throw Invalid();
                    }
                    _pageCount = declaredPages;
                }
                else
                {
                    _pageCount =
                        checked((uint)physicalPages);
                }

                uint encoding =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        header.AsSpan(
                            56,
                            4));
                if (encoding != 1)
                {
                    throw Unsupported();
                }
            }

            private void ReadBtreePage(
                uint pageNumber,
                int depth)
            {
                if (depth >
                    SqLtSchemaProfile.MaxSchemaTreeDepth)
                {
                    throw Limit();
                }
                byte[] page = ReadPage(pageNumber);
                int headerOffset =
                    pageNumber == 1
                        ? 100
                        : 0;
                if (headerOffset + 8 >
                    _usablePageSize)
                {
                    throw Invalid();
                }

                byte pageType = page[headerOffset];
                bool interior =
                    pageType == InteriorTablePage;
                if (!interior &&
                    pageType != LeafTablePage)
                {
                    throw Unsupported();
                }
                int headerSize = interior
                    ? 12
                    : 8;
                int cellCount =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        page.AsSpan(
                            headerOffset + 3,
                            2));
                if (cellCount >
                    SqLtSchemaProfile.MaxCellsPerSchemaPage)
                {
                    throw Limit();
                }
                int pointerStart =
                    headerOffset + headerSize;
                int pointerEnd = checked(
                    pointerStart +
                    cellCount * 2);
                if (pointerEnd >
                    _usablePageSize)
                {
                    throw Invalid();
                }

                for (var index = 0;
                    index < cellCount;
                    index++)
                {
                    int cellOffset =
                        BinaryPrimitives.ReadUInt16BigEndian(
                            page.AsSpan(
                                pointerStart +
                                index * 2,
                                2));
                    if (cellOffset <
                            pointerEnd ||
                        cellOffset >=
                            _usablePageSize)
                    {
                        throw Invalid();
                    }

                    if (interior)
                    {
                        if (cellOffset + 4 >
                            _usablePageSize)
                        {
                            throw Invalid();
                        }
                        uint child =
                            BinaryPrimitives.ReadUInt32BigEndian(
                                page.AsSpan(
                                    cellOffset,
                                    4));
                        int keyOffset =
                            cellOffset + 4;
                        _ = ReadVarint(
                            page,
                            ref keyOffset,
                            _usablePageSize);
                        ReadBtreePage(
                            child,
                            depth + 1);
                    }
                    else
                    {
                        ReadLeafCell(
                            page,
                            cellOffset);
                    }
                }

                if (interior)
                {
                    uint rightMost =
                        BinaryPrimitives.ReadUInt32BigEndian(
                            page.AsSpan(
                                headerOffset + 8,
                                4));
                    ReadBtreePage(
                        rightMost,
                        depth + 1);
                }
            }

            private void ReadLeafCell(
                byte[] page,
                int cellOffset)
            {
                int offset = cellOffset;
                ulong payloadSize =
                    ReadVarint(
                        page,
                        ref offset,
                        _usablePageSize);
                _ = ReadVarint(
                    page,
                    ref offset,
                    _usablePageSize);
                if (payloadSize >
                    SqLtSchemaProfile.MaxSchemaRecordBytes)
                {
                    throw Limit();
                }
                if (_records.Count >=
                    SqLtSchemaProfile.MaxSchemaRecords)
                {
                    throw Limit();
                }
                _totalPayloadBytes = checked(
                    _totalPayloadBytes +
                    (int)payloadSize);
                if (_totalPayloadBytes >
                    SqLtSchemaProfile.MaxTotalSchemaBytes)
                {
                    throw Limit();
                }

                byte[] payload = ReadPayload(
                    page,
                    offset,
                    checked((int)payloadSize));
                _records.Add(
                    ParseRecord(payload));
            }

            private byte[] ReadPayload(
                byte[] page,
                int offset,
                int payloadSize)
            {
                int maxLocal =
                    _usablePageSize - 35;
                int minLocal =
                    ((_usablePageSize - 12) *
                        32 /
                        255) -
                    23;
                int local = payloadSize;
                if (payloadSize > maxLocal)
                {
                    local = minLocal +
                        (payloadSize - minLocal) %
                        (_usablePageSize - 4);
                    if (local > maxLocal)
                    {
                        local = minLocal;
                    }
                }

                int required =
                    local +
                    (local < payloadSize
                        ? 4
                        : 0);
                int localEnd = checked(
                    offset + required);
                if (offset < 0 ||
                    localEnd >
                        _usablePageSize)
                {
                    throw Invalid();
                }

                var payload =
                    new byte[payloadSize];
                page.AsSpan(
                        offset,
                        local)
                    .CopyTo(payload);
                int written = local;
                if (written == payloadSize)
                {
                    return payload;
                }

                uint overflow =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        page.AsSpan(
                            offset + local,
                            4));
                var chainLength = 0;
                while (written < payloadSize)
                {
                    if (++chainLength >
                        SqLtSchemaProfile
                            .MaxOverflowPagesPerRecord)
                    {
                        throw Limit();
                    }
                    byte[] overflowPage =
                        ReadPage(overflow);
                    uint next =
                        BinaryPrimitives.ReadUInt32BigEndian(
                            overflowPage.AsSpan(
                                0,
                                4));
                    int copy = Math.Min(
                        payloadSize - written,
                        _usablePageSize - 4);
                    overflowPage.AsSpan(
                            4,
                            copy)
                        .CopyTo(
                            payload.AsSpan(written));
                    written += copy;
                    if (written < payloadSize &&
                        next == 0)
                    {
                        throw Invalid();
                    }
                    overflow = next;
                }
                if (overflow != 0)
                {
                    throw Invalid();
                }
                return payload;
            }

            private byte[] ReadPage(uint pageNumber)
            {
                if (pageNumber == 0 ||
                    pageNumber > _pageCount ||
                    !_visitedPages.Add(pageNumber))
                {
                    throw Invalid();
                }
                if (_visitedPages.Count >
                    SqLtSchemaProfile.MaxSchemaPagesVisited)
                {
                    throw Limit();
                }

                long offset = checked(
                    ((long)pageNumber - 1) *
                    _pageSize);
                var page = new byte[_pageSize];
                ReadExactly(
                    offset,
                    page);
                return page;
            }

            private void ReadExactly(
                long offset,
                byte[] buffer)
            {
                _input.Seek(
                    offset,
                    SeekOrigin.Begin);
                var read = 0;
                while (read < buffer.Length)
                {
                    int count = _input.Read(
                        buffer,
                        read,
                        buffer.Length - read);
                    if (count == 0)
                    {
                        throw Invalid();
                    }
                    read += count;
                }
            }

            private static RawSchemaRecord ParseRecord(
                byte[] payload)
            {
                int headerOffset = 0;
                ulong headerSizeValue =
                    ReadVarint(
                        payload,
                        ref headerOffset,
                        payload.Length);
                if (headerSizeValue >
                    (ulong)payload.Length ||
                    headerSizeValue <
                    (ulong)headerOffset)
                {
                    throw Invalid();
                }
                int headerSize =
                    checked((int)headerSizeValue);
                var serialTypes = new List<ulong>();
                while (headerOffset < headerSize)
                {
                    serialTypes.Add(
                        ReadVarint(
                            payload,
                            ref headerOffset,
                            headerSize));
                }
                if (headerOffset != headerSize ||
                    serialTypes.Count != 5)
                {
                    throw Invalid();
                }

                int dataOffset = headerSize;
                var values =
                    new object[serialTypes.Count];
                for (var index = 0;
                    index < serialTypes.Count;
                    index++)
                {
                    values[index] = ReadValue(
                        payload,
                        ref dataOffset,
                        serialTypes[index]);
                }
                if (dataOffset != payload.Length ||
                    values[0] is not string type ||
                    values[1] is not string name ||
                    values[2] is not string tableName ||
                    values[3] is not long rootPage ||
                    (values[4] != null &&
                     values[4] is not string))
                {
                    throw Invalid();
                }
                return new RawSchemaRecord(
                    type,
                    name,
                    tableName,
                    rootPage,
                    values[4] as string);
            }

            private static object ReadValue(
                byte[] payload,
                ref int offset,
                ulong serialType)
            {
                if (serialType == 0)
                {
                    return null;
                }
                if (serialType is >= 1 and <= 6)
                {
                    int integerLength = serialType switch
                    {
                        1 => 1,
                        2 => 2,
                        3 => 3,
                        4 => 4,
                        5 => 6,
                        _ => 8,
                    };
                    return ReadInteger(
                        payload,
                        ref offset,
                        integerLength);
                }
                if (serialType == 8)
                {
                    return 0L;
                }
                if (serialType == 9)
                {
                    return 1L;
                }
                if (serialType < 12 ||
                    (serialType & 1) == 0)
                {
                    throw Invalid();
                }

                ulong lengthValue =
                    (serialType - 13) /
                    2;
                if (lengthValue >
                    int.MaxValue)
                {
                    throw Limit();
                }
                int length =
                    checked((int)lengthValue);
                int end = checked(
                    offset + length);
                if (offset < 0 ||
                    end >
                        payload.Length)
                {
                    throw Invalid();
                }
                string text;
                try
                {
                    text = StrictUtf8.GetString(
                        payload,
                        offset,
                        length);
                }
                catch (DecoderFallbackException ex)
                {
                    throw Invalid(ex);
                }
                offset += length;
                return text;
            }

            private static long ReadInteger(
                byte[] payload,
                ref int offset,
                int length)
            {
                int end = checked(
                    offset + length);
                if (offset < 0 ||
                    end >
                        payload.Length)
                {
                    throw Invalid();
                }
                long value =
                    (payload[offset] & 0x80) != 0
                        ? -1
                        : 0;
                for (var index = 0;
                    index < length;
                    index++)
                {
                    value = (value << 8) |
                        payload[offset + index];
                }
                offset += length;
                return value;
            }

            private static ulong ReadVarint(
                byte[] bytes,
                ref int offset,
                int end)
            {
                ulong value = 0;
                for (var index = 0;
                    index < 9;
                    index++)
                {
                    if (offset >= end)
                    {
                        throw Invalid();
                    }
                    byte current =
                        bytes[offset++];
                    if (index == 8)
                    {
                        return (value << 8) |
                            current;
                    }
                    value = (value << 7) |
                        (uint)(current & 0x7f);
                    if ((current & 0x80) == 0)
                    {
                        return value;
                    }
                }
                throw Invalid();
            }
        }
    }
}
