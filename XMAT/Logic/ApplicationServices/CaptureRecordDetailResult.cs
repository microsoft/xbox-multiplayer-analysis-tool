// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureRecordDetailResult(IQueryResult source) : IQueryResult
    {
        public Guid Id => source.Id;
        public Guid SnapshotId => source.SnapshotId;
        public IReadOnlyList<QueryColumn> Columns { get; } =
            Array.AsReadOnly(new[] { new QueryColumn(0, "field", null, null, null), new QueryColumn(1, "value", null, null, null) });
        public long RowCount => source.Columns.Count;
        public long StorageBytes => source.StorageBytes;

        public async Task<QueryPage> ReadPageAsync(
            long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            if (source.RowCount != 1)
                throw new CaptureDataException(CaptureDataError.DetailNotFound);
            var sourcePage = await source.ReadPageAsync(0, 1, 1024 * 1024, cancellationToken);
            var sourceRow = sourcePage.Rows.Single();
            var rows = new List<QueryRow>();
            long used = 768;
            var preview = Math.Min(4096, Math.Max(0, (maximumBytes - 1536) / 12));
            for (var index = offset; index < RowCount && rows.Count < maximumRows; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = source.Columns[(int)index].Name;
                var value = sourceRow.Cells[(int)index];
                object projected = value.Value;
                var truncated = value.IsTruncated;
                if (value.Value is string text && Encoding.UTF8.GetByteCount(text) > preview)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    projected = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, preview)));
                    truncated = true;
                }
                else if (value.Value is byte[] bytes && bytes.Length > preview)
                {
                    projected = bytes.AsSpan(0, preview).ToArray();
                    truncated = true;
                }
                var length = projected switch
                {
                    string previewText => 6L * Encoding.UTF8.GetByteCount(previewText),
                    byte[] bytes => 4L * ((bytes.Length + 2L) / 3),
                    _ => 32
                };
                var cost = 448L + 6L * Encoding.UTF8.GetByteCount(name) + length;
                if (used + cost > maximumBytes)
                {
                    if (rows.Count == 0)
                        throw new QueryException(QueryError.PageTooSmall, "The detail fields exceed the response budget.");
                    break;
                }
                rows.Add(new(index, Array.AsReadOnly(new[]
                {
                    new QueryCell(QueryValueKind.Text, name, Encoding.UTF8.GetByteCount(name), false),
                    new QueryCell(value.Kind, projected, value.ByteLength, truncated)
                })));
                used += cost;
            }
            return new(offset, RowCount, Columns, rows.AsReadOnly(), offset + rows.Count < RowCount);
        }

        public Task<QueryValueChunk> ReadValueAsync(
            long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
            if (column != 1)
                throw new QueryException(QueryError.InvalidValue, "Value chunks use the value column.");
            return source.ReadValueAsync(0, checked((int)row), byteOffset, maximumBytes, cancellationToken);
        }

        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
