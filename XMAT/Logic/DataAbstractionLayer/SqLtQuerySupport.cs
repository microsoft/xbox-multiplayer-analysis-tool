// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtQuerySupport
    {
        internal static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        internal static IReadOnlyList<QueryTable> ReadSchema(SqliteConnection connection, CancellationToken cancellationToken)
        {
            lock (SqLtConnectionGate.For(connection))
            {
                var tables = new List<(string Name, string Kind)>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name,type FROM sqlite_schema WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tables.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
                var result = new List<QueryTable>();
                foreach (var table in tables)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT cid,name,type,\"notnull\",pk,hidden FROM pragma_table_xinfo($name) ORDER BY cid";
                    command.Parameters.AddWithValue("$name", table.Name);
                    using var reader = command.ExecuteReader();
                    var columns = new List<QuerySchemaColumn>();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        columns.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                            reader.GetInt64(3) == 0 && reader.GetInt64(4) == 0,
                            reader.GetInt64(4) != 0, reader.GetInt64(5) != 0));
                    }
                    result.Add(new(table.Name, table.Kind, columns.AsReadOnly()));
                }
                return result.AsReadOnly();
            }
        }

        internal static SqliteConnection Open(string path, bool readOnly)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            try
            { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }

        internal static string CreateWorkspace()
        {
            var path = Path.Combine(Path.GetTempPath(), $"XMAT.Query.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void DeleteWorkspace(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        internal static void Check(
            int result,
            SqliteConnection connection,
            CancellationToken cancellationToken,
            QueryError fallback = QueryError.InvalidQuery)
        {
            if (result is raw.SQLITE_OK or raw.SQLITE_ROW or raw.SQLITE_DONE)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var error = result switch
            {
                raw.SQLITE_FULL or raw.SQLITE_TOOBIG or raw.SQLITE_NOMEM => QueryError.ResultLimit,
                raw.SQLITE_BUSY or raw.SQLITE_LOCKED => QueryError.SourceBusy,
                raw.SQLITE_AUTH => QueryError.ReadOnlyRequired,
                _ => fallback
            };
            throw new QueryException(error, raw.sqlite3_errmsg(connection.Handle).utf8_to_string());
        }
    }
}
