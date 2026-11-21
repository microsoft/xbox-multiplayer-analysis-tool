// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtDatabaseBackup
    {
        internal static void CreateSnapshot(
            SqliteConnection source, string filepath, long maximumBytes, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            using (new FileStream(filepath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { }
            try
            {
                using (var destination = SqLtQuerySupport.Open(filepath, false))
                    Copy(source, destination, maximumBytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var durable = new FileStream(filepath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (durable.Length > maximumBytes)
                    throw new IOException("The capture backup exceeds its storage limit.");
                durable.Flush(flushToDisk: true);
            }
            catch
            {
                File.Delete(filepath);
                throw;
            }
        }

        private static void Copy(
            SqliteConnection source, SqliteConnection destination, long maximumBytes, CancellationToken cancellationToken)
        {
            lock (SqLtConnectionGate.For(source))
            {
                using var pageSize = source.CreateCommand();
                pageSize.CommandText = "PRAGMA page_size";
                var bytesPerPage = (long)pageSize.ExecuteScalar();
                using var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
                if (backup == null || backup.IsInvalid)
                    throw new IOException("A capture snapshot could not be acquired.");
                int result;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = raw.sqlite3_backup_step(backup, 128);
                    if ((long)raw.sqlite3_backup_pagecount(backup) * bytesPerPage > maximumBytes)
                        throw new IOException("The capture backup exceeds its storage limit.");
                    SqLtQuerySupport.Check(result, destination, cancellationToken);
                } while (result != raw.SQLITE_DONE);
            }
        }

        internal static void SaveToFile(
            SqliteConnection source, string filepath, SqLtDatabase hooks = null, SqLtFileIdentity? sourceIdentity = null,
            long maximumBytes = long.MaxValue, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
            if (sourceIdentity.HasValue && File.Exists(filepath) &&
                sourceIdentity.Value == SqLtFileIdentity.FromPath(filepath))
                throw new ArgumentException("A capture backup cannot replace its source file.", nameof(filepath));
            try
            { SaveCore(source, filepath, hooks, maximumBytes, cancellationToken); }
            catch (CaptureSaveException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { throw new CaptureSaveException(CaptureSaveFailure.Failed, exception); }
        }

        private static void SaveCore(
            SqliteConnection source, string filepath, SqLtDatabase hooks, long maximumBytes, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
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
                    Copy(source, destination, maximumBytes, cancellationToken);
                    if (hooks != null)
                    {
                        hooks.BackupCompleted?.Invoke(destination);
                        if (SqLtDatabase.ReadUserVersion(destination) != CaptureTextFormat.LiteralUserVersion)
                            throw new InvalidOperationException("The saved capture database did not keep its literal text marker.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                using (var durable = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (durable.Length > maximumBytes)
                        throw new IOException("The capture backup exceeds its storage limit.");
                    durable.Flush(flushToDisk: true);
                }
                hooks?.MoveFailureInjection?.Invoke(temporary, destinationPath);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!WindowsFilePublication.TryPublish(temporary, destinationPath, true, out var error))
                        throw new IOException("The prepared capture could not be published.", new Win32Exception(error));
                }
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
