// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtQueryProjection
    {
        internal static void Create(
            string inputPath, string destinationPath, IQuerySnapshotProjection projection, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projection.MaximumBytes);
            using var connection = SqLtQuerySupport.Open(inputPath, true);
            using var destination = SqLtQuerySupport.Open(destinationPath, false);
            var maximumValue = (int)Math.Min(int.MaxValue, Math.Max(16 * 1024, projection.MaximumBytes));
            raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_LENGTH, maximumValue);
            raw.sqlite3_limit(destination.Handle, raw.SQLITE_LIMIT_LENGTH, maximumValue);
            delegate_progress progress = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
            raw.sqlite3_progress_handler(connection.Handle, 1000, progress, null);
            raw.sqlite3_progress_handler(destination.Handle, 1000, progress, null);
            try
            {
                var schema = SqLtQuerySupport.ReadSchema(connection, cancellationToken);
                if (schema.Any(table => table.Kind != "table" || table.Columns.Any(column => column.Hidden)))
                    throw Unavailable();
                long retainedBytes = 0;
                using (var transaction = destination.BeginTransaction())
                {
                    foreach (var table in schema)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rowKey = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(name =>
                            !table.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) ??
                            table.Columns.FirstOrDefault(column => column.PrimaryKey &&
                                column.DeclaredType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))?.Name
                            ?? throw Unavailable();
                        // Generate only quoted table/field/type declarations, never execute source schema SQL.
                        using (var create = destination.CreateCommand())
                        {
                            create.Transaction = transaction;
                            var definitions = table.Columns.Select(column =>
                                SqLtQuerySupport.Identifier(column.Name) + " " +
                                SqLtQuerySupport.Identifier(string.IsNullOrEmpty(column.DeclaredType) ? "BLOB" : column.DeclaredType) +
                                (column.Nullable ? string.Empty : " NOT NULL")).ToList();
                            var primary = table.Columns.Where(column => column.PrimaryKey).ToArray();
                            if (primary.Length > 0)
                                definitions.Add("PRIMARY KEY(" + string.Join(",", primary.Select(column =>
                                    SqLtQuerySupport.Identifier(column.Name))) + ")");
                            create.CommandText = $"CREATE TABLE {SqLtQuerySupport.Identifier(table.Name)}({string.Join(",", definitions)})";
                            create.ExecuteNonQuery();
                        }
                        using var select = connection.CreateCommand();
                        select.CommandText = $"SELECT {SqLtQuerySupport.Identifier(rowKey)},{string.Join(",", table.Columns.Select(column =>
                            SqLtQuerySupport.Identifier(column.Name)))} FROM {SqLtQuerySupport.Identifier(table.Name)} ORDER BY {SqLtQuerySupport.Identifier(rowKey)}";
                        using var insert = destination.CreateCommand();
                        insert.Transaction = transaction;
                        var separateRowKey = !table.Columns.Any(column => column.Name.Equals(rowKey, StringComparison.OrdinalIgnoreCase));
                        insert.CommandText = $"INSERT INTO {SqLtQuerySupport.Identifier(table.Name)}(" +
                            (separateRowKey ? SqLtQuerySupport.Identifier(rowKey) + "," : string.Empty) +
                            string.Join(",", table.Columns.Select(column => SqLtQuerySupport.Identifier(column.Name))) + ") VALUES(" +
                            (separateRowKey ? "$row," : string.Empty) +
                            string.Join(",", table.Columns.Select((_, index) => "$v" + index)) + ")";
                        for (var index = 0; index < table.Columns.Count; index++)
                            insert.Parameters.AddWithValue("$v" + index, DBNull.Value);
                        if (separateRowKey)
                            insert.Parameters.AddWithValue("$row", 0L);
                        using var reader = select.ExecuteReader();
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var values = new object[table.Columns.Count];
                            for (var index = 0; index < values.Length; index++)
                                values[index] = reader.IsDBNull(index + 1) ? null : reader.GetValue(index + 1);
                            var keys = table.Columns.Select((column, index) =>
                                column.PrimaryKey ? (Index: index, Value: values[index]) : (Index: -1, Value: (object)null))
                                .Where(key => key.Index >= 0).ToArray();
                            var filtered = projection.Project(table, values, cancellationToken);
                            if (filtered == null || filtered.Length != values.Length ||
                                keys.Any(key => !Equals(key.Value, filtered[key.Index])))
                                throw Unavailable();
                            retainedBytes += 64;
                            for (var index = 0; index < filtered.Length; index++)
                            {
                                retainedBytes += filtered[index] switch
                                {
                                    null => 8,
                                    long or int or double => 16,
                                    string text => Encoding.UTF8.GetByteCount(text) + 16L,
                                    byte[] bytes => bytes.LongLength + 16,
                                    _ => throw Unavailable()
                                };
                                if (retainedBytes > projection.MaximumBytes)
                                    throw new QueryException(QueryError.ResultLimit, "The filtered snapshot exceeds its data limit.");
                                insert.Parameters[index].Value = filtered[index] ?? DBNull.Value;
                            }
                            if (separateRowKey)
                                insert.Parameters[^1].Value = reader.GetInt64(0);
                            insert.ExecuteNonQuery();
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(destinationPath).Length > projection.MaximumBytes)
                    throw new QueryException(QueryError.ResultLimit, "The filtered snapshot exceeds its storage limit.");
            }
            catch (SqliteException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Unavailable();
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
                raw.sqlite3_progress_handler(destination.Handle, 0, null, null);
                GC.KeepAlive(progress);
            }
        }

        private static QueryException Unavailable() =>
            new(QueryError.ProjectionUnavailable, "A complete filtered snapshot could not be prepared.");
    }
}
