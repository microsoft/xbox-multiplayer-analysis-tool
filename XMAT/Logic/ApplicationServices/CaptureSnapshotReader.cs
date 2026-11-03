// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureSnapshotReader(IQueryResult result)
    {
        private readonly Dictionary<string, int> _ordinals = result.Columns.ToDictionary(column => column.Name, column => column.Ordinal);

        internal QueryCell Cell(QueryRow row, string name) => row.Cells[_ordinals[name]];
        internal long Id(QueryRow row) => (long)Cell(row, "id").Value;
        internal string Text(QueryRow row, string name) => StringValue(Cell(row, name).Value);
        internal long? Number(QueryRow row, string name)
        {
            var value = Cell(row, name).Value;
            return value == null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        internal async IAsyncEnumerable<QueryRow> RowsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long offset = 0;
            while (offset < result.RowCount)
            {
                var page = await result.ReadPageAsync(offset, 1000, 1024 * 1024, cancellationToken);
                if (page.Rows.Count == 0)
                    throw new QueryException(QueryError.InvalidValue, "The snapshot reader made no progress.");
                foreach (var row in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return row;
                }
                offset += page.Rows.Count;
            }
        }

        internal async Task<IDataRecord> ReadRecordAsync(QueryRow row, CancellationToken cancellationToken)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var column in result.Columns)
            {
                var cell = row.Cells[column.Ordinal];
                if (cell.IsTruncated)
                {
                    if (cell.Kind != QueryValueKind.Text)
                        throw new QueryException(QueryError.InvalidValue, "Capture fields must use their declared storage type.");
                    var decoder = Encoding.UTF8.GetDecoder();
                    var text = new StringBuilder();
                    var characters = new char[Encoding.UTF8.GetMaxCharCount(64 * 1024)];
                    long position = 0;
                    QueryValueChunk chunk;
                    do
                    {
                        chunk = await result.ReadValueAsync(row.Ordinal, column.Ordinal, position, 64 * 1024, cancellationToken);
                        var count = decoder.GetChars(chunk.Bytes, characters, flush: !chunk.HasMore);
                        text.Append(characters, 0, count);
                        position += chunk.Bytes.Length;
                    } while (chunk.HasMore);
                    values.Add(column.Name, text.ToString());
                }
                else
                    values.Add(column.Name, cell.Value);
            }
            return new Record(checked((int)Id(row)), values);
        }

        private static string StringValue(object value) => value switch
        {
            null => null,
            string text => text,
            long or double => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => throw new QueryException(QueryError.InvalidValue, "Capture fields must use their declared storage type.")
        };

        private sealed class Record(int id, Dictionary<string, object> values) : IDataRecord
        {
            public int RowId => id;
            public long Int(string fieldName) => values.TryGetValue(fieldName, out var value) && value != null
                ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
            public string Str(string fieldName) => values.TryGetValue(fieldName, out var value) ? StringValue(value) : null;
        }
    }
}
