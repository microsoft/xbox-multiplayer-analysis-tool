// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    public sealed record QuerySchemaColumn(
        int Ordinal, string Name, string DeclaredType, bool Nullable, bool PrimaryKey, bool Hidden);

    public sealed record QueryTable(string Name, string Kind, IReadOnlyList<QuerySchemaColumn> Columns);

    public sealed record QueryColumn(
        int Ordinal, string Name, string DeclaredType, string TableName, string OriginName);

    public enum QueryValueKind { Null, Integer, Real, Text, Blob }
    public enum QueryExportFormat { Csv, Json }

    // Value is restricted to null, Int64, Double, String, or Byte[] by the DAL.
    public sealed record QueryCell(QueryValueKind Kind, object Value, long ByteLength, bool IsTruncated);
    public sealed record QueryRow(long Ordinal, IReadOnlyList<QueryCell> Cells);
    public sealed record QueryPage(long Offset, long TotalRows, IReadOnlyList<QueryColumn> Columns,
        IReadOnlyList<QueryRow> Rows, bool HasMore);
    public sealed record QueryValueChunk(QueryValueKind Kind, long ByteOffset, long TotalBytes, byte[] Bytes, bool HasMore);

    public sealed record QueryExecutionOptions
    {
        public TimeSpan Timeout { get; }
        public long MaximumResultBytes { get; }

        public QueryExecutionOptions(TimeSpan timeout, long maximumResultBytes = 512L * 1024 * 1024)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.FromMilliseconds(1));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, TimeSpan.FromMilliseconds(uint.MaxValue - 1L));
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumResultBytes, 16 * 1024);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResultBytes, int.MaxValue);
            Timeout = timeout;
            MaximumResultBytes = maximumResultBytes;
        }
    }

    public enum QueryError
    {
        InvalidQuery,
        ReadOnlyRequired,
        SingleStatementRequired,
        ResultLimit,
        Timeout,
        SourceBusy,
        SourceChanged,
        PageTooSmall,
        InvalidValue
    }

    public sealed class QueryException(QueryError error, string detail) : Exception(detail)
    {
        public QueryError Error { get; } = error;
    }
}
