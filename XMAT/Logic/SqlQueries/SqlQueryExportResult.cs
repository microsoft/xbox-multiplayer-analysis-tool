// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.SqlQueries
{
    // A non-owning ordinal selection over a completed result. Values still come from that one execution.
    internal sealed class SqlQueryExportResult : IQueryResult
    {
        private readonly IQueryResult _result;
        private readonly long[] _selection;
        private readonly IProgress<long> _progress;

        internal SqlQueryExportResult(IQueryResult result, IEnumerable<long> selection, IProgress<long> progress = null)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            _selection = selection?.Distinct().Order().ToArray();
            if (_selection?.Any(row => row < 0 || row >= result.RowCount) == true)
            {
                throw new ArgumentOutOfRangeException(nameof(selection));
            }
            _progress = progress;
        }

        public Guid Id => _result.Id;
        public Guid SnapshotId => _result.SnapshotId;
        public IReadOnlyList<QueryColumn> Columns => _result.Columns;
        public long RowCount => _selection?.LongLength ?? _result.RowCount;
        public long StorageBytes => _result.StorageBytes;

        public async Task<QueryPage> ReadPageAsync(
            long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            if (_selection == null)
            {
                var page = await _result.ReadPageAsync(offset, maximumRows, maximumBytes, cancellationToken);
                _progress?.Report(offset + page.Rows.Count);
                return page;
            }
            var rows = new List<QueryRow>();
            var usedBytes = 0L;
            while (offset + rows.Count < RowCount && rows.Count < maximumRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = checked((int)(offset + rows.Count));
                var contiguous = 1;
                while (index + contiguous < _selection.Length && rows.Count + contiguous < maximumRows &&
                    _selection[index + contiguous] == _selection[index] + contiguous)
                {
                    contiguous++;
                }
                var page = await _result.ReadPageAsync(_selection[index], contiguous, maximumBytes, cancellationToken);
                if (page.Rows.Count == 0)
                    throw new InvalidDataException("A result page made no progress.");
                foreach (var row in page.Rows)
                {
                    var rowBytes = 64L + row.Cells.Sum(cell => 128L + (cell.Value switch
                    {
                        string text => text.Length * 6L,
                        byte[] bytes => bytes.Length * 2L,
                        _ => 32L
                    }));
                    if (rows.Count != 0 && usedBytes + rowBytes > maximumBytes)
                    {
                        _progress?.Report(offset + rows.Count);
                        return new(offset, RowCount, Columns, rows.AsReadOnly(), true);
                    }
                    rows.Add(row);
                    usedBytes += rowBytes;
                }
                if (usedBytes >= maximumBytes / 2L)
                    break;
            }
            _progress?.Report(offset + rows.Count);
            return new(offset, RowCount, Columns, rows.AsReadOnly(), offset + rows.Count < RowCount);
        }

        public Task<QueryValueChunk> ReadValueAsync(
            long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default) =>
            _result.ReadValueAsync(row, column, byteOffset, maximumBytes, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
