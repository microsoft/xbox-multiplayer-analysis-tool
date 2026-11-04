// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal sealed class SqLtQueryResult : IQueryResult
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly AsyncOperationTracker _readers = new();
        private readonly object _disposeGate = new();
        private Task _disposing;

        public Guid Id { get; } = Guid.NewGuid();
        public Guid SnapshotId { get; }
        public IReadOnlyList<QueryColumn> Columns { get; }
        public long RowCount { get; }
        public long StorageBytes { get; }

        private SqLtQueryResult(string workspace, Guid snapshotId, QueryColumn[] columns, long rowCount)
        {
            _workspace = workspace;
            _path = Path.Combine(workspace, "result.sqlite");
            SnapshotId = snapshotId;
            Columns = Array.AsReadOnly(columns);
            RowCount = rowCount;
            StorageBytes = new FileInfo(_path).Length;
            _readers.StartAccepting();
        }

        internal static IQueryResult Materialize(
            SqliteConnection source,
            sqlite3_stmt statement,
            Guid snapshotId,
            QueryExecutionOptions options,
            CancellationToken cancellationToken)
        {
            var count = raw.sqlite3_column_count(statement);
            var columns = Enumerable.Range(0, count).Select(index => new QueryColumn(
                index,
                raw.sqlite3_column_name(statement, index).utf8_to_string(),
                raw.sqlite3_column_decltype(statement, index).utf8_to_string(),
                raw.sqlite3_column_table_name(statement, index).utf8_to_string(),
                raw.sqlite3_column_origin_name(statement, index).utf8_to_string())).ToArray();
            var workspace = SqLtQuerySupport.CreateWorkspace();
            long rowCount = 0;
            try
            {
                using (var destination = SqLtQuerySupport.Open(Path.Combine(workspace, "result.sqlite"), false))
                {
                    using var command = destination.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF";
                    command.ExecuteNonQuery();
                    command.CommandText = "PRAGMA page_size";
                    var pageSize = (long)command.ExecuteScalar();
                    var maximumPages = options.MaximumResultBytes / pageSize;
                    command.CommandText = $"PRAGMA max_page_count={maximumPages}";
                    if ((long)command.ExecuteScalar() > maximumPages)
                    {
                        throw new QueryException(QueryError.ResultLimit, "The result-store budget is too small.");
                    }
                    var fields = string.Join(",", Enumerable.Range(0, count).Select(index => $"c{index}"));
                    command.CommandText = $"CREATE TABLE Rows(ordinal INTEGER PRIMARY KEY,{fields})";
                    command.ExecuteNonQuery();
                    using var transaction = destination.BeginTransaction();
                    SqLtQuerySupport.Check(raw.sqlite3_prepare_v2(destination.Handle,
                        $"INSERT INTO Rows VALUES({string.Join(",", Enumerable.Range(0, count + 1).Select(_ => "?"))})",
                        out var insert), destination, cancellationToken);
                    using (insert)
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var step = raw.sqlite3_step(statement);
                            SqLtQuerySupport.Check(step, source, cancellationToken);
                            if (step == raw.SQLITE_DONE)
                            {
                                break;
                            }
                            SqLtQuerySupport.Check(raw.sqlite3_bind_int64(insert, 1, rowCount), destination, cancellationToken);
                            for (var index = 0; index < count; index++)
                            {
                                var kind = raw.sqlite3_column_type(statement, index);
                                int binding;
                                switch (kind)
                                {
                                    case raw.SQLITE_NULL:
                                        binding = raw.sqlite3_bind_null(insert, index + 2);
                                        break;
                                    case raw.SQLITE_INTEGER:
                                        binding = raw.sqlite3_bind_int64(insert, index + 2, raw.sqlite3_column_int64(statement, index));
                                        break;
                                    case raw.SQLITE_FLOAT:
                                        binding = raw.sqlite3_bind_double(insert, index + 2, raw.sqlite3_column_double(statement, index));
                                        break;
                                    case raw.SQLITE_TEXT:
                                        var text = raw.sqlite3_column_blob(statement, index);
                                        binding = text.IsEmpty
                                            ? raw.sqlite3_bind_text(insert, index + 2, string.Empty)
                                            : raw.sqlite3_bind_text(insert, index + 2, text);
                                        break;
                                    case raw.SQLITE_BLOB:
                                        var blob = raw.sqlite3_column_blob(statement, index);
                                        binding = blob.IsEmpty
                                            ? raw.sqlite3_bind_zeroblob(insert, index + 2, 0)
                                            : raw.sqlite3_bind_blob(insert, index + 2, blob);
                                        break;
                                    default:
                                        throw new QueryException(QueryError.InvalidValue, "Unsupported SQLite value.");
                                }
                                SqLtQuerySupport.Check(binding, destination, cancellationToken);
                            }
                            SqLtQuerySupport.Check(raw.sqlite3_step(insert), destination, cancellationToken);
                            SqLtQuerySupport.Check(raw.sqlite3_reset(insert), destination, cancellationToken);
                            SqLtQuerySupport.Check(raw.sqlite3_clear_bindings(insert), destination, cancellationToken);
                            rowCount++;
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                return new SqLtQueryResult(workspace, snapshotId, columns, rowCount);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is raw.SQLITE_FULL or raw.SQLITE_TOOBIG)
            {
                SqLtQuerySupport.DeleteWorkspace(workspace);
                throw new QueryException(QueryError.ResultLimit, "The result-store budget was exhausted.");
            }
            catch
            {
                SqLtQuerySupport.DeleteWorkspace(workspace);
                throw;
            }
        }

        public Task<QueryPage> ReadPageAsync(
            long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            return ReadAsync(() => ReadPage(offset, maximumRows, maximumBytes, cancellationToken), cancellationToken);
        }

        private QueryPage ReadPage(long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken)
        {
            var cost = 512L + Columns.Sum(column => 256L +
                6L * Encoding.UTF8.GetByteCount(column.Name ?? string.Empty) +
                6L * Encoding.UTF8.GetByteCount(column.DeclaredType ?? string.Empty) +
                6L * Encoding.UTF8.GetByteCount(column.TableName ?? string.Empty) +
                6L * Encoding.UTF8.GetByteCount(column.OriginName ?? string.Empty));
            var minimumRowCost = 64L + Columns.Count * 128L;
            if (cost + minimumRowCost > maximumBytes)
            {
                throw new QueryException(QueryError.PageTooSmall, "Select fewer columns or increase the response budget.");
            }
            var preview = (int)Math.Min(4096, Math.Max(0, (maximumBytes - cost - minimumRowCost) /
                Math.Max(1L, Columns.Count * 6L)));
            using var connection = SqLtQuerySupport.Open(_path, true);
            // Each value needs three projected fields; batch wide results within SQLite's native limit.
            var batchSize = (raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_COLUMN, -1) - 1) / 3;
            var commands = new List<SqliteCommand>();
            var readers = new List<SqliteDataReader>();
            try
            {
                for (var first = 0; first < Columns.Count; first += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var command = connection.CreateCommand();
                    commands.Add(command);
                    command.CommandText = "SELECT ordinal," + string.Join(",", Columns.Skip(first).Take(batchSize).Select(column =>
                        $"typeof(c{column.Ordinal}),length(CAST(c{column.Ordinal} AS BLOB))," +
                        $"CASE WHEN typeof(c{column.Ordinal}) IN ('text','blob') THEN substr(CAST(c{column.Ordinal} AS BLOB),1,$preview) ELSE c{column.Ordinal} END")) +
                        " FROM Rows WHERE ordinal >= $offset ORDER BY ordinal LIMIT $rows";
                    command.Parameters.AddWithValue("$preview", preview);
                    command.Parameters.AddWithValue("$offset", offset);
                    command.Parameters.AddWithValue("$rows", maximumRows);
                    readers.Add(command.ExecuteReader());
                }
                var rows = new List<QueryRow>();
                while (readers[0].Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ordinal = readers[0].GetInt64(0);
                    foreach (var reader in readers.Skip(1))
                    {
                        if (!reader.Read() || reader.GetInt64(0) != ordinal)
                            throw new InvalidDataException("The result column batches do not identify the same row.");
                    }
                    var cells = new List<QueryCell>();
                    long rowCost = 64;
                    for (var index = 0; index < Columns.Count; index++)
                    {
                        var reader = readers[index / batchSize];
                        var start = index % batchSize * 3 + 1;
                        var kind = ParseKind(reader.GetString(start));
                        var length = reader.IsDBNull(start + 1) ? 0 : reader.GetInt64(start + 1);
                        object value = kind switch
                        {
                            QueryValueKind.Null => null,
                            QueryValueKind.Integer => reader.GetInt64(start + 2),
                            QueryValueKind.Real => reader.GetDouble(start + 2),
                            QueryValueKind.Text => length == 0 ? string.Empty :
                                DecodePreview(reader.GetFieldValue<byte[]>(start + 2), length > preview),
                            QueryValueKind.Blob => length == 0 ? Array.Empty<byte>() : reader.GetFieldValue<byte[]>(start + 2),
                            _ => throw new QueryException(QueryError.InvalidValue, "Unsupported SQLite value.")
                        };
                        cells.Add(new(kind, value, length,
                            kind is QueryValueKind.Text or QueryValueKind.Blob && length > preview));
                        rowCost += 128 + (value switch
                        {
                            string => 6L * Math.Min(length, preview),
                            byte[] bytes => 4L * ((bytes.Length + 2L) / 3),
                            _ => 32
                        });
                    }
                    if (cost + rowCost > maximumBytes)
                    {
                        if (rows.Count == 0)
                            throw new QueryException(QueryError.PageTooSmall, "This row does not fit the response budget.");
                        break;
                    }
                    cost += rowCost;
                    rows.Add(new(ordinal, cells.AsReadOnly()));
                }
                return new(offset, RowCount, Columns, rows.AsReadOnly(), offset + rows.Count < RowCount);
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
                foreach (var command in commands)
                    command.Dispose();
            }
        }

        public Task<QueryValueChunk> ReadValueAsync(
            long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns.Count);
            ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 64 * 1024);
            return ReadAsync(() =>
            {
                using var connection = SqLtQuerySupport.Open(_path, true);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT typeof(c{column}),length(CAST(c{column} AS BLOB)) FROM Rows WHERE ordinal=$row";
                command.Parameters.AddWithValue("$row", row);
                using var reader = command.ExecuteReader();
                reader.Read();
                var kind = ParseKind(reader.GetString(0));
                if (kind is not (QueryValueKind.Text or QueryValueKind.Blob))
                {
                    throw new QueryException(QueryError.InvalidValue, "Only text and BLOB values have byte chunks.");
                }
                var length = reader.GetInt64(1);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, length);
                reader.Close();
                using var blob = new SqliteBlob(connection, "Rows", $"c{column}", row, readOnly: true);
                blob.Position = byteOffset;
                var bytes = new byte[(int)Math.Min(maximumBytes, length - byteOffset)];
                var read = 0;
                while (read < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = blob.Read(bytes, read, bytes.Length - read);
                    if (next == 0)
                        throw new EndOfStreamException();
                    read += next;
                }
                return new QueryValueChunk(kind, byteOffset, length, bytes, byteOffset + bytes.Length < length);
            }, cancellationToken);
        }

        private async Task<T> ReadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            IDisposable lease;
            lock (_disposeGate)
            {
                ObjectDisposedException.ThrowIf(_disposing != null, this);
                var accepted = _readers.TryBegin(out lease);
                ObjectDisposedException.ThrowIf(!accepted, this);
            }
            using (lease)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(action, cancellationToken);
            }
        }

        private static string DecodePreview(byte[] bytes, bool truncated)
        {
            if (truncated)
            {
                var strict = new UTF8Encoding(false, true);
                for (var length = bytes.Length; length >= Math.Max(0, bytes.Length - 3); length--)
                {
                    try
                    { return strict.GetString(bytes, 0, length); }
                    catch (DecoderFallbackException) { }
                }
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static QueryValueKind ParseKind(string kind) => kind switch
        {
            "null" => QueryValueKind.Null,
            "integer" => QueryValueKind.Integer,
            "real" => QueryValueKind.Real,
            "text" => QueryValueKind.Text,
            "blob" => QueryValueKind.Blob,
            _ => throw new QueryException(QueryError.InvalidValue, "Unsupported SQLite storage class.")
        };

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposing != null)
                    return new(_disposing);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposing = completion.Task;
                _ = DisposeCoreAsync(completion);
                return new(completion.Task);
            }
        }

        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                await _readers.StopAcceptingAndWaitAsync();
                SqLtQuerySupport.DeleteWorkspace(_workspace);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                lock (_disposeGate)
                {
                    if (ReferenceEquals(_disposing, completion.Task))
                        _disposing = null;
                }
                completion.TrySetException(exception);
            }
        }
    }
}
