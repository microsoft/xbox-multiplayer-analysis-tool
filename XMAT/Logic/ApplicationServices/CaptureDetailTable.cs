// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureDetailTable : IQueryResult
    {
        internal sealed record Utf8Text(byte[] Bytes);

        private readonly Value[][] _rows;
        public Guid Id { get; } = Guid.NewGuid();
        public Guid SnapshotId { get; }
        public IReadOnlyList<QueryColumn> Columns { get; }
        public long RowCount => _rows.LongLength;
        public long StorageBytes { get; }

        internal CaptureDetailTable(Guid snapshotId, string[] columns, IEnumerable<object[]> rows)
        {
            SnapshotId = snapshotId;
            Columns = Array.AsReadOnly(columns.Select((name, ordinal) =>
                new QueryColumn(ordinal, name, null, null, null)).ToArray());
            _rows = rows.Select(row => row.Select(value => new Value(value)).ToArray()).ToArray();
            if (_rows.Any(row => row.Length != columns.Length))
                throw new ArgumentException("The detail fields do not match their schema.", nameof(rows));
            StorageBytes = 512 + _rows.Sum(row => 64L + row.Sum(value => 128 + value.Length));
        }

        public Task<QueryPage> ReadPageAsync(
            long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var cost = 512L + Columns.Sum(column => 128L + 6L * Encoding.UTF8.GetByteCount(column.Name));
            var minimum = 64L + Columns.Count * 160L;
            if (cost + minimum > maximumBytes && offset < RowCount)
                throw new QueryException(QueryError.PageTooSmall, "The detail page exceeds the response budget.");
            var preview = (int)Math.Min(4096, Math.Max(0, (maximumBytes - cost - minimum) / Math.Max(1L, Columns.Count * 6L)));
            var rows = new List<QueryRow>();
            for (var index = offset; index < RowCount && rows.Count < maximumRows; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = _rows[index].Select(value => value.Preview(preview)).ToArray();
                var size = 64 + cells.Sum(cell => 160L + (cell.Value switch
                {
                    string text => 6L * Encoding.UTF8.GetByteCount(text),
                    byte[] bytes => 4L * ((bytes.Length + 2L) / 3),
                    _ => 32
                }));
                if (cost + size > maximumBytes)
                {
                    if (rows.Count == 0)
                        throw new QueryException(QueryError.PageTooSmall, "The detail row exceeds the response budget.");
                    break;
                }
                rows.Add(new(index, Array.AsReadOnly(cells)));
                cost += size;
            }
            return Task.FromResult(new QueryPage(offset, RowCount, Columns, rows.AsReadOnly(), offset + rows.Count < RowCount));
        }

        public Task<QueryValueChunk> ReadValueAsync(
            long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns.Count);
            ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 64 * 1024);
            var value = _rows[row][column];
            if (value.Bytes == null)
                throw new QueryException(QueryError.InvalidValue, "Only text and binary fields have byte chunks.");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, value.Length);
            var bytes = value.Bytes.AsSpan(checked((int)byteOffset),
                (int)Math.Min(maximumBytes, value.Length - byteOffset)).ToArray();
            return Task.FromResult(new QueryValueChunk(value.Kind, byteOffset, value.Length, bytes, byteOffset + bytes.Length < value.Length));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Value
        {
            internal QueryValueKind Kind { get; }
            internal byte[] Bytes { get; }
            internal long Length { get; }
            private readonly object _scalar;

            internal Value(object value)
            {
                switch (value)
                {
                    case null:
                        Kind = QueryValueKind.Null;
                        break;
                    case string text:
                        Kind = QueryValueKind.Text;
                        Bytes = Encoding.UTF8.GetBytes(text);
                        break;
                    case Utf8Text utf8:
                        Kind = QueryValueKind.Text;
                        Bytes = utf8.Bytes;
                        break;
                    case byte[] bytes:
                        Kind = QueryValueKind.Blob;
                        Bytes = (byte[])bytes.Clone();
                        break;
                    case double or float or decimal:
                        Kind = QueryValueKind.Real;
                        _scalar = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        break;
                    case bool boolean:
                        Kind = QueryValueKind.Integer;
                        _scalar = boolean ? 1L : 0L;
                        break;
                    case ulong number when number > long.MaxValue:
                        Kind = QueryValueKind.Text;
                        Bytes = Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture));
                        break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong:
                        Kind = QueryValueKind.Integer;
                        _scalar = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException("The detail projector returned an unsupported value.", nameof(value));
                }
                Length = Bytes?.LongLength ?? (Kind == QueryValueKind.Null ? 0 : 8);
            }

            internal QueryCell Preview(int maximum)
            {
                var truncated = Bytes != null && Bytes.Length > maximum;
                var bytes = Bytes?.AsSpan(0, Math.Min(Bytes.Length, maximum)).ToArray();
                object value = Kind switch
                {
                    QueryValueKind.Text => Encoding.UTF8.GetString(bytes),
                    QueryValueKind.Blob => bytes,
                    _ => _scalar
                };
                return new(Kind, value, Length, truncated);
            }
        }
    }
}
