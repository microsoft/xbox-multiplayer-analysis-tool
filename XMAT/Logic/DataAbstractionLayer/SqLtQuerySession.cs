// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal sealed class SqLtQuerySession : IQuerySession
    {
        private readonly string _workspace;
        private readonly SqliteConnection _connection;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly strdelegate_authorizer _authorizer;
        private readonly int _maximumColumns;
        private bool _disposed;

        public Guid SnapshotId { get; } = Guid.NewGuid();
        public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;

        private SqLtQuerySession(string workspace, SqliteConnection connection)
        {
            _workspace = workspace;
            _connection = connection;
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA temp_store=FILE";
            command.ExecuteNonQuery();
            raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
            _maximumColumns = raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_COLUMN, -1) - 1;
            _authorizer = Authorize;
            SqLtQuerySupport.Check(raw.sqlite3_set_authorizer(connection.Handle, _authorizer, null),
                connection, CancellationToken.None);
        }

        internal static Task<IQuerySession> CreateAsync(
            SqliteConnection source,
            CancellationToken cancellationToken)
        {
            return Task.Run<IQuerySession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspace = SqLtQuerySupport.CreateWorkspace();
                var path = Path.Combine(workspace, "snapshot.sqlite");
                try
                {
                    lock (SqLtConnectionGate.For(source))
                    {
                        using var destination = SqLtQuerySupport.Open(path, false);
                        using var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
                        if (backup == null || backup.IsInvalid)
                        {
                            throw new QueryException(QueryError.SourceBusy, "Could not acquire a capture snapshot.");
                        }
                        int result;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            result = raw.sqlite3_backup_step(backup, 128);
                            SqLtQuerySupport.Check(result, destination, cancellationToken, QueryError.SourceBusy);
                        } while (result != raw.SQLITE_DONE);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var connection = SqLtQuerySupport.Open(path, true);
                    try
                    { return new SqLtQuerySession(workspace, connection); }
                    catch { connection.Dispose(); throw; }
                }
                catch
                {
                    SqLtQuerySupport.DeleteWorkspace(workspace);
                    throw;
                }
            }, cancellationToken);
        }

        public async Task<IQueryResult> ExecuteAsync(
            string sql,
            QueryExecutionOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            ArgumentNullException.ThrowIfNull(options);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(options.Timeout);
                try
                {
                    return await Task.Run(() => Execute(sql, options, deadline.Token), deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new QueryException(QueryError.Timeout, "The query exceeded its execution deadline.");
                }
            }
            finally { _gate.Release(); }
        }

        private IQueryResult Execute(string sql, QueryExecutionOptions options, CancellationToken cancellationToken)
        {
            if (Encoding.UTF8.GetByteCount(sql) > 1024 * 1024 || sql.Contains('\0'))
            {
                throw new QueryException(QueryError.InvalidQuery, "The SQL text is too large or contains a NUL character.");
            }
            raw.sqlite3_limit(_connection.Handle, raw.SQLITE_LIMIT_LENGTH, (int)options.MaximumResultBytes);
            raw.sqlite3_limit(_connection.Handle, raw.SQLITE_LIMIT_COLUMN, _maximumColumns);
            delegate_progress progress = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
            raw.sqlite3_progress_handler(_connection.Handle, 1000, progress, null);
            try
            {
                using var statement = PrepareSingle(sql, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return SqLtQueryResult.Materialize(_connection, statement, SnapshotId, options, cancellationToken);
            }
            finally
            {
                raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
                GC.KeepAlive(progress);
            }
        }

        private sqlite3_stmt PrepareSingle(string sql, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(sql);
            ReadOnlySpan<byte> remaining = bytes;
            sqlite3_stmt first = null;
            try
            {
                while (!remaining.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = raw.sqlite3_prepare_v2(_connection.Handle, remaining, out var current, out var tail);
                    if (result != raw.SQLITE_OK)
                    {
                        current?.Dispose();
                        SqLtQuerySupport.Check(result, _connection, cancellationToken);
                    }
                    remaining = tail;
                    if (current == null || current.IsInvalid)
                    {
                        current?.Dispose();
                        continue;
                    }
                    if (first != null)
                    {
                        current.Dispose();
                        throw new QueryException(QueryError.SingleStatementRequired, "Execute exactly one read-only statement.");
                    }
                    first = current;
                    if (raw.sqlite3_stmt_readonly(first) == 0 || raw.sqlite3_column_count(first) == 0)
                    {
                        throw new QueryException(QueryError.ReadOnlyRequired, "Only read-only result-producing statements are allowed.");
                    }
                    if (raw.sqlite3_bind_parameter_count(first) != 0)
                    {
                        throw new QueryException(QueryError.InvalidQuery, "Use literal values; unbound parameters are not supported.");
                    }
                }
                return first ?? throw new QueryException(QueryError.InvalidQuery, "Enter a result-producing SQL statement.");
            }
            catch { first?.Dispose(); throw; }
        }

        private static int Authorize(object state, int action, string first, string second, string database, string trigger)
        {
            if (action is raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_RECURSIVE)
            {
                return raw.SQLITE_OK;
            }
            if (action == raw.SQLITE_FUNCTION &&
                second is not null &&
                !second.Equals("load_extension", StringComparison.OrdinalIgnoreCase) &&
                !second.Equals("readfile", StringComparison.OrdinalIgnoreCase) &&
                !second.Equals("writefile", StringComparison.OrdinalIgnoreCase) &&
                !second.Equals("eval", StringComparison.OrdinalIgnoreCase) &&
                !second.Equals("fts3_tokenizer", StringComparison.OrdinalIgnoreCase))
            {
                return raw.SQLITE_OK;
            }
            return raw.SQLITE_DENY;
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _connection.Dispose();
                    SqLtQuerySupport.DeleteWorkspace(_workspace);
                }
            }
            finally { _gate.Release(); }
        }
    }
}
