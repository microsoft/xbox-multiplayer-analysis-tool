// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Text;
using System.IO;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDatabase
    {
        private IEnumerable<IDataTable> GetDataTables()
        {
            return _dataTables.Values;
        }

        private IEnumerable<string> GetTableNames()
        {
            return _dataTables.Keys;
        }

        private IDataTable GetTableByName(string name)
        {
            return _dataTables[name];
        }

        private IDataTable CreateTableInternal(string name, IEnumerable<IField> fields)
        {
            _commandBuilder.Clear();
            _commandBuilder.Append("DROP TABLE IF EXISTS ");
            _commandBuilder.Append(name);

            _command.CommandText = _commandBuilder.ToString();
            _command.ExecuteNonQuery();

            var newTable = new SqLtDataTable(_connection, name, fields);
            _dataTables[name] = newTable;
            return newTable;
        }

        private void InitializeInternal()
        {
            if (_connection != null)
            {
                throw new InvalidOperationException("Database is being initialized more than once.");
            }

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            _command = new SqliteCommand();
            _command.Connection = _connection;
        }

        private void ShutdownInternal()
        {
            Dispose();
        }

        private void SaveToFileInternal(string filepath)
        {
            File.Delete(filepath);

            var connectionString = $"Data Source={filepath}";
            var saveDbConnection = new SqliteConnection(connectionString);
            _connection.BackupDatabase(saveDbConnection);
        }

        private void DisposeInternal()
        {
            _command = null;

            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
            }
        }

        internal SqLtDatabase()
        {
            _commandBuilder = new StringBuilder();
        }

        private SqliteConnection _connection;
        private SqliteCommand _command;
        private StringBuilder _commandBuilder;

        private readonly Dictionary<string, SqLtDataTable> _dataTables = new Dictionary<string, SqLtDataTable>();
    }
}
