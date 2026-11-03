// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtDatabaseBackup
    {
        internal static void SaveToFile(SqliteConnection source, string filepath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
            if (!string.IsNullOrEmpty(source.DataSource) && source.DataSource != ":memory:" &&
                Path.GetFullPath(source.DataSource).Equals(Path.GetFullPath(filepath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A capture backup cannot replace its source file.", nameof(filepath));

            File.Delete(filepath);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = filepath,
                Pooling = false
            }.ToString();
            using var destination = new SqliteConnection(connectionString);
            destination.Open();
            lock (SqLtConnectionGate.For(source))
            {
                source.BackupDatabase(destination);
            }
        }
    }
}
