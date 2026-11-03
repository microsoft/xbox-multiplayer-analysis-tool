// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtReadonlyDatabase : IReadonlyDatabase, IDisposable
    {
        public Task<IReadOnlyList<QueryTable>> GetQuerySchemaAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() => SqLtQuerySupport.ReadSchema(_connection, cancellationToken), cancellationToken);

        public Task<IQuerySession> CreateQuerySessionAsync(CancellationToken cancellationToken = default) =>
            SqLtQuerySession.CreateAsync(_connection, cancellationToken);

        public void SaveToFile(string filepath) => SqLtDatabaseBackup.SaveToFile(_connection, filepath);

        public IEnumerable<string> TableNames()
        {
            return _dataTables.Keys;
        }

        public IReadonlyDataTable TableByName(string name)
        {
            return _dataTables.GetValueOrDefault(name);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        internal SqLtReadonlyDatabase(string filepath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = filepath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            _connection = new SqliteConnection(connectionString);
            try
            {
                _connection.Open();
                InitializeTables();
            }
            catch
            {
                _connection.Dispose();
                throw;
            }
        }

        private void InitializeTables()
        {
            var names = new List<string>();
            using var command = new SqliteCommand(
                "SELECT name,sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'", _connection);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1).StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A native capture cannot contain virtual tables.");
                    names.Add(reader.GetString(0));
                }
            }
            foreach (var name in names)
                InitializeTableFields(name);
        }

        private void InitializeTableFields(string tableName)
        {
            using var command = new SqliteCommand(
                "SELECT cid,name,type,hidden,pk FROM pragma_table_xinfo($name) ORDER BY cid", _connection);
            command.Parameters.AddWithValue("$name", tableName);
            var fieldDefinitions = new List<IFieldDefinition>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var index = reader.GetInt32(0);
                    var name = reader.GetString(1);
                    var declared = reader.GetString(2).ToUpperInvariant();
                    if (reader.GetInt64(3) != 0 ||
                        (declared != "INTEGER" && declared != "TEXT"))
                        throw new InvalidDataException("A native capture requires ordinary integer/text columns.");
                    if (index == 0)
                    {
                        if (!name.Equals("id", StringComparison.OrdinalIgnoreCase) || declared != "INTEGER" ||
                            reader.GetInt64(4) != 1)
                            throw new InvalidDataException("A native capture table must start with its record identity.");
                        continue;
                    }
                    if (reader.GetInt64(4) != 0)
                        throw new InvalidDataException("A native capture requires a single-column record identity.");
                    fieldDefinitions.Add(FieldDefinition.CreateFromSchema(name,
                        declared == "INTEGER" ? typeof(long) : typeof(string), index));
                }
            }
            if (fieldDefinitions.Count == 0)
                throw new InvalidDataException("A native capture table requires data columns.");
            _dataTables[tableName] = new SqLtReadonlyDataTable(_connection, tableName, fieldDefinitions.ToArray());
        }

        private SqliteConnection _connection;

        private readonly Dictionary<string, SqLtReadonlyDataTable> _dataTables =
            new Dictionary<string, SqLtReadonlyDataTable>();
    }
}
