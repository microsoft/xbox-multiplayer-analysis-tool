// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtDatabaseBackup
    {
        internal static void SaveToFile(
            SqliteConnection source, string filepath, SqLtDatabase hooks = null, SqLtFileIdentity? sourceIdentity = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
            if (sourceIdentity.HasValue && File.Exists(filepath) &&
                sourceIdentity.Value == SqLtFileIdentity.FromPath(filepath))
                throw new ArgumentException("A capture backup cannot replace its source file.", nameof(filepath));
            try
            { SaveCore(source, filepath, hooks); }
            catch (CaptureSaveException) { throw; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { throw new CaptureSaveException(CaptureSaveFailure.Failed, exception); }
        }

        private static void SaveCore(SqliteConnection source, string filepath, SqLtDatabase hooks)
        {
            var destinationPath = Path.GetFullPath(filepath);
            var directory = Path.GetDirectoryName(destinationPath) ??
                throw new InvalidOperationException("The save destination has no directory.");
            using var destinationLease = LoadedCaptureDatabaseRegistry.AcquireSaveDestination(destinationPath);
            var temporary = CreateTemporary(directory, Path.GetFileName(destinationPath), hooks);
            var committed = false;
            try
            {
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Pooling = false }.ToString()))
                {
                    destination.Open();
                    hooks?.BackupFailureInjection?.Invoke(temporary);
                    lock (SqLtConnectionGate.For(source))
                    { source.BackupDatabase(destination); }
                }
                hooks?.MoveFailureInjection?.Invoke(temporary, destinationPath);
                try
                { File.Move(temporary, destinationPath, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { throw new CaptureSaveException(CaptureSaveFailure.DestinationLocked, exception); }
                committed = true;
            }
            finally
            {
                if (!committed && File.Exists(temporary))
                    DeleteTemporary(temporary, hooks);
            }
        }

        private static string CreateTemporary(string directory, string destinationName, SqLtDatabase hooks)
        {
            var length = Math.Clamp(destinationName.Length - 1, 1, 8);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var hex = hooks?.TemporaryHex != null
                    ? hooks.TemporaryHex(length) : RandomNumberGenerator.GetHexString(length, true);
                if (hex == null || hex.Length != length || !hex.All(character =>
                    character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                    throw new InvalidOperationException("The save temporary name source returned an invalid value.");
                var name = destinationName.Length == 1 ? hex : "." + hex;
                if (new[] { name, name + "-journal", name + "-wal", name + "-shm" }.Any(candidate =>
                    candidate.Equals(destinationName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var path = Path.Combine(directory, name);
                if (HasCompanion(path))
                    continue;
                try
                {
                    bool companion;
                    using (var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        hooks?.TemporaryReserved?.Invoke(path);
                        companion = HasCompanion(path);
                    }
                    if (companion)
                    { DeleteTemporary(path, hooks); continue; }
                    return path;
                }
                catch (Exception exception) when (
                    (exception is IOException or UnauthorizedAccessException) && Path.Exists(path))
                { }
            }
            throw new IOException("A unique save temporary file could not be created.");
        }

        private static bool HasCompanion(string path) =>
            Path.Exists(path + "-journal") || Path.Exists(path + "-wal") || Path.Exists(path + "-shm");

        private static void DeleteTemporary(string path, SqLtDatabase hooks)
        {
            try
            {
                if (hooks?.TemporaryDelete != null)
                    hooks.TemporaryDelete(path);
                else
                    File.Delete(path);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { PublicUtilities.AppLog(LogLevel.ERROR, $"Capture save temporary-file cleanup failed ({exception.GetType().FullName})."); }
        }
    }
}
