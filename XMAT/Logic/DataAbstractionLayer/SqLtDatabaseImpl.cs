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
            ArgumentException.ThrowIfNullOrWhiteSpace(
                filepath);
            try
            {
                SaveToFileCore(filepath);
            }
            catch (CaptureSaveException)
            {
                throw;
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(
                    exception))
            {
                throw new CaptureSaveException(
                    CaptureSaveFailure.Failed,
                    exception);
            }
        }

        private void SaveToFileCore(string filepath)
        {
            string destination =
                Path.GetFullPath(filepath);
            string directory =
                Path.GetDirectoryName(destination) ??
                throw new InvalidOperationException(
                    "The save destination has no directory.");
            using IDisposable destinationLease =
                LoadedCaptureDatabaseRegistry
                    .AcquireSaveDestination(
                        destination);
            string temporaryPath =
                CreateSaveTemporaryFile(
                    directory,
                    Path.GetFileName(destination));
            var committed = false;
            try
            {
                var connectionString =
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = temporaryPath,
                        // Dispose must close the file instead of returning it to the pool.
                        Pooling = false,
                    }.ToString();
                using (var saveDbConnection =
                    new SqliteConnection(
                        connectionString))
                {
                    saveDbConnection.Open();
                    BackupFailureInjection?.Invoke(
                        temporaryPath);
                    _connection.BackupDatabase(
                        saveDbConnection);
                }

                MoveFailureInjection?.Invoke(
                    temporaryPath,
                    destination);
                // The temp and destination share a directory, so MOVEFILE_REPLACE_EXISTING is an atomic same-volume
                // rename rather than a delete followed by a move.
                try
                {
                    File.Move(
                        temporaryPath,
                        destination,
                        true);
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException)
                {
                    throw new CaptureSaveException(
                        CaptureSaveFailure
                            .DestinationLocked,
                        exception);
                }
                committed = true;
            }
            finally
            {
                if (!committed &&
                    File.Exists(temporaryPath))
                {
                    DeleteOwnedTemporary(
                        temporaryPath);
                }
            }
        }

        private string CreateSaveTemporaryFile(
            string directory,
            string destinationFileName)
        {
            int hexLength = Math.Clamp(
                destinationFileName.Length - 1,
                1,
                8);
            for (var attempt = 0;
                attempt < 10;
                attempt++)
            {
                string hex =
                    TemporaryHex(hexLength);
                if (!IsLowerHex(
                        hex,
                        hexLength))
                {
                    throw new InvalidOperationException(
                        "The save temporary name source returned an invalid value.");
                }
                string candidateName =
                    destinationFileName.Length == 1
                        ? hex
                        : "." + hex;
                if (ConflictsWithDestination(
                        candidateName,
                        destinationFileName))
                {
                    continue;
                }
                string path = Path.Combine(
                    directory,
                    candidateName);
                if (HasExistingSqliteCompanion(path))
                {
                    continue;
                }
                try
                {
                    bool companionAppeared;
                    using (var reservation =
                        new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.None))
                    {
                        TemporaryReserved?.Invoke(path);
                        companionAppeared =
                            HasExistingSqliteCompanion(path);
                    }
                    if (companionAppeared)
                    {
                        DeleteOwnedTemporary(path);
                        continue;
                    }
                    // A companion can still appear before SQLite opens the reserved temp; that narrow external race is
                    // accepted. Every companion visible before or immediately after reservation is left untouched.
                    return path;
                }
                catch (Exception exception) when (
                    (exception is IOException or
                        UnauthorizedAccessException) &&
                    Path.Exists(path))
                {
                }
            }
            throw new IOException(
                "A unique save temporary file could not be created.");
        }

        private static bool HasExistingSqliteCompanion(
            string path) =>
            Path.Exists(path + "-journal") ||
            Path.Exists(path + "-wal") ||
            Path.Exists(path + "-shm");

        private void DeleteOwnedTemporary(
            string path)
        {
            try
            {
                TemporaryDelete(path);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(
                    exception))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    "Capture save temporary-file cleanup failed (" +
                    exception.GetType().FullName +
                    ").");
            }
        }

        private static bool ConflictsWithDestination(
            string candidateName,
            string destinationFileName) =>
            string.Equals(
                candidateName,
                destinationFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                candidateName + "-journal",
                destinationFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                candidateName + "-wal",
                destinationFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                candidateName + "-shm",
                destinationFileName,
                StringComparison.OrdinalIgnoreCase);

        private static bool IsLowerHex(
            string value,
            int length)
        {
            if (value == null ||
                value.Length != length)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (character is not (
                        >= '0' and <= '9') &&
                    character is not (
                        >= 'a' and <= 'f'))
                {
                    return false;
                }
            }
            return true;
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

        internal Action<string> BackupFailureInjection { get; set; }

        internal Action<string, string> MoveFailureInjection { get; set; }

        internal Action<string> TemporaryDelete { get; set; } =
            File.Delete;

        internal Func<int, string> TemporaryHex { get; set; } =
            static length =>
                RandomNumberGenerator.GetHexString(
                    length,
                    true);

        internal Action<string> TemporaryReserved { get; set; }

        private SqliteConnection _connection;
        private SqliteCommand _command;
        private StringBuilder _commandBuilder;

        private readonly Dictionary<string, SqLtDataTable> _dataTables = new Dictionary<string, SqLtDataTable>();
    }
}
