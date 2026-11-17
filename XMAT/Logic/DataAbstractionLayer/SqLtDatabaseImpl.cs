// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDatabase
    {
        private IEnumerable<IDataTable> GetDataTables() => _dataTables.Values;
        private IEnumerable<string> GetTableNames() => _dataTables.Keys;
        private IDataTable GetTableByName(string name) => _dataTables[name];

        private IDataTable CreateTableInternal(string name, IEnumerable<IField> fields)
        {
            lock (SqLtConnectionGate.For(_connection))
            {
                _commandBuilder.Clear();
                _commandBuilder.Append("DROP TABLE IF EXISTS ");
                _commandBuilder.Append(name);
                _command.CommandText = _commandBuilder.ToString();
                _command.ExecuteNonQuery();
                var table = new SqLtDataTable(_connection, SqLtConnectionGate.For(_connection), name, fields, CaptureTextSource);
                _dataTables[name] = table;
                return table;
            }
        }

        private void InitializeInternal()
        {
            if (_connection != null)
                throw new InvalidOperationException("Database is being initialized more than once.");
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            WritableConnectionOpened?.Invoke(_connection);
            using (var secureDelete = _connection.CreateCommand())
            {
                secureDelete.CommandText = "PRAGMA secure_delete=ON";
                secureDelete.ExecuteNonQuery();
                secureDelete.CommandText = "PRAGMA secure_delete";
                if (Convert.ToInt64(secureDelete.ExecuteScalar()) != 1)
                    throw new InvalidOperationException("SQLite did not enable secure deletion.");
            }
            SetLiteralUserVersion(_connection);
            CaptureTextSource = global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal;
            _command = new SqliteCommand { Connection = _connection };
        }

        private void ShutdownInternal() => Dispose();

        private void SaveToFileInternal(string filepath) =>
            SqLtDatabaseBackup.SaveToFile(_connection, filepath, this);

        private void DisposeInternal()
        {
            if (_connection == null) return;
            lock (SqLtConnectionGate.For(_connection))
            {
                _command?.Dispose();
                _command = null;
                _connection.Close();
                _connection.Dispose();
                _connection = null;
            }
        }

        internal SqLtDatabase() { _commandBuilder = new StringBuilder(); }

        internal Action<string> BackupFailureInjection { get; set; }
        internal Action<string, string> MoveFailureInjection { get; set; }
        internal Action<string> TemporaryDelete { get; set; } = File.Delete;
        internal Func<int, string> TemporaryHex { get; set; } =
            static length => RandomNumberGenerator.GetHexString(length, true);
        internal Action<string> TemporaryReserved { get; set; }
        internal Action<SqliteConnection> WritableConnectionOpened { get; set; }
        internal Action<SqliteConnection> BackupCompleted { get; set; }

        private static void SetLiteralUserVersion(SqliteConnection connection)
        {
            using var set = connection.CreateCommand();
            set.CommandText = $"PRAGMA user_version={CaptureTextFormat.LiteralUserVersion}";
            set.ExecuteNonQuery();
            if (ReadUserVersion(connection) != CaptureTextFormat.LiteralUserVersion)
                throw new InvalidOperationException("The active capture database did not keep its literal text marker.");
        }

        internal static long ReadUserVersion(SqliteConnection connection)
        {
            using var read = connection.CreateCommand();
            read.CommandText = "PRAGMA user_version";
            return (long)read.ExecuteScalar();
        }

        private SqliteConnection _connection;
        private SqliteCommand _command;
        private StringBuilder _commandBuilder;
        private readonly Dictionary<string, SqLtDataTable> _dataTables = new();
    }
}
