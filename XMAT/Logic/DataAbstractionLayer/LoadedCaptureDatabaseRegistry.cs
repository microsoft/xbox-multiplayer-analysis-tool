// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal enum CaptureSaveFailure
    {
        Refused,
        DestinationLocked,
        Failed,
    }

    internal sealed class CaptureSaveException :
        IOException
    {
        internal CaptureSaveException(
            CaptureSaveFailure failure,
            Exception innerException = null)
            : base(
                failure switch
                {
                    CaptureSaveFailure.Refused =>
                        "The save destination is currently loaded.",
                    CaptureSaveFailure.DestinationLocked =>
                        "The save destination is locked.",
                    _ =>
                        "The capture could not be saved.",
                },
                innerException)
        {
            Failure = failure;
        }

        internal CaptureSaveFailure Failure { get; }
    }

    // Tracks which read-only capture tab owns each loaded capture database. Tabs reused by a reload share the newly
    // opened database, and the database (with its file handle) is disposed as soon as its last owning tab releases it.
    internal static class LoadedCaptureDatabaseRegistry
    {
        private sealed class SaveDestinationLease :
            IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                Monitor.Exit(Sync);
            }
        }

        private sealed class Entry
        {
            internal Entry(
                SqLtReadonlyDatabase database)
            {
                Database = database;
            }

            internal SqLtReadonlyDatabase Database { get; }

            internal int References { get; set; }
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<
            SqLtReadonlyDatabase,
            Entry> Databases =
            new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<
            IDeviceCaptureController,
            Entry> Controllers =
            new(ReferenceEqualityComparer.Instance);

        internal static void Assign(
            IDeviceCaptureController controller,
            SqLtReadonlyDatabase database)
        {
            ArgumentNullException.ThrowIfNull(controller);
            ArgumentNullException.ThrowIfNull(database);
            SqLtReadonlyDatabase dispose = null;
            lock (Sync)
            {
                if (Controllers.TryGetValue(
                        controller,
                        out Entry existing))
                {
                    if (ReferenceEquals(
                            existing.Database,
                            database))
                    {
                        return;
                    }
                    Controllers.Remove(controller);
                    if (--existing.References == 0)
                    {
                        Databases.Remove(
                            existing.Database);
                        dispose = existing.Database;
                    }
                }

                if (!Databases.TryGetValue(
                        database,
                        out Entry replacement))
                {
                    replacement = new Entry(database);
                    Databases.Add(
                        database,
                        replacement);
                }
                replacement.References++;
                Controllers.Add(
                    controller,
                    replacement);
            }
            DisposeReleased(dispose);
        }

        internal static void Release(
            IDeviceCaptureController controller)
        {
            ArgumentNullException.ThrowIfNull(controller);
            SqLtReadonlyDatabase dispose = null;
            lock (Sync)
            {
                if (!Controllers.Remove(
                        controller,
                        out Entry entry))
                {
                    return;
                }
                if (--entry.References == 0)
                {
                    Databases.Remove(
                        entry.Database);
                    dispose = entry.Database;
                }
            }
            DisposeReleased(dispose);
        }

        internal static SqLtReadonlyDatabase DatabaseFor(
            IDeviceCaptureController controller)
        {
            if (controller == null)
            {
                return null;
            }
            lock (Sync)
            {
                return Controllers.TryGetValue(
                    controller,
                    out Entry entry)
                        ? entry.Database
                        : null;
            }
        }

        internal static IDisposable AcquireSaveDestination(
            string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            SqLtFileIdentity? identity;
            try
            {
                identity = File.Exists(path)
                    ? SqLtFileIdentity.FromPath(path)
                    : null;
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
            Monitor.Enter(Sync);
            try
            {
                if (identity.HasValue &&
                    Databases.Keys.Any(
                        database =>
                            database.Identity ==
                            identity.Value))
                {
                    throw new CaptureSaveException(
                        CaptureSaveFailure.Refused);
                }
                return new SaveDestinationLease();
            }
            catch
            {
                Monitor.Exit(Sync);
                throw;
            }
        }

        internal static bool IsLoadedPath(
            string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            SqLtFileIdentity identity =
                SqLtFileIdentity.FromPath(path);
            lock (Sync)
            {
                return Databases.Keys.Any(
                    database =>
                        database.Identity ==
                        identity);
            }
        }

        internal static int ReferenceCount(
            SqLtReadonlyDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            lock (Sync)
            {
                return Databases.TryGetValue(
                    database,
                    out Entry entry)
                        ? entry.References
                        : 0;
            }
        }

        private static void DisposeReleased(
            SqLtReadonlyDatabase database)
        {
            if (database == null)
            {
                return;
            }
            try
            {
                database.Dispose();
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(
                    exception))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Loaded capture disposal failed: {exception}");
            }
        }
    }
}
