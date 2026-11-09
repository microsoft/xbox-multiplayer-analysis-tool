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

    internal readonly record struct ImportedCaptureSource(
        string SourcePath,
        SqLtFileIdentity Identity);

    // Tracks which read-only capture tab owns each loaded capture database. Tabs reused by a reload share the newly
    // opened database, which is disposed with its file handle when its last owning tab releases it; imported SAZ tabs
    // retain their source path and identity without an archive handle.
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
        private static readonly Dictionary<
            IDeviceCaptureController,
            ImportedCaptureSource> ImportedSources =
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
                ImportedSources.Remove(controller);
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
                    dispose = RemoveSqlOwner(controller);
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

        internal static void AssignImported(
            IDeviceCaptureController controller,
            ImportedCaptureSource source)
        {
            ArgumentNullException.ThrowIfNull(controller);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);
            SqLtReadonlyDatabase dispose;
            lock (Sync)
            {
                ImportedSources[controller] = source;
                dispose = RemoveSqlOwner(controller);
            }
            DisposeReleased(dispose);
        }

        internal static void Release(
            IDeviceCaptureController controller)
        {
            ArgumentNullException.ThrowIfNull(controller);
            SqLtReadonlyDatabase dispose;
            lock (Sync)
            {
                ImportedSources.Remove(controller);
                dispose = RemoveSqlOwner(controller);
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

        internal static ImportedCaptureSource? ImportedSourceFor(
            IDeviceCaptureController controller)
        {
            if (controller == null)
            {
                return null;
            }
            lock (Sync)
            {
                return ImportedSources.TryGetValue(
                    controller,
                    out ImportedCaptureSource source)
                        ? source
                        : null;
            }
        }

        internal static IDisposable AcquireSaveDestination(
            string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string normalized = Path.GetFullPath(path);
            SqLtFileIdentity? identity;
            try
            {
                identity = File.Exists(normalized)
                    ? SqLtFileIdentity.FromPath(normalized)
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
                if (IsProtectedDestination(normalized, identity))
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
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string normalized = Path.GetFullPath(path);
            SqLtFileIdentity? identity = File.Exists(normalized)
                ? SqLtFileIdentity.FromPath(normalized)
                : null;
            lock (Sync)
            {
                return IsProtectedDestination(normalized, identity);
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

        private static bool IsProtectedDestination(
            string normalized,
            SqLtFileIdentity? identity)
        {
            if (ImportedSources.Values.Any(source =>
                    string.Equals(source.SourcePath, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (identity is not SqLtFileIdentity resolved)
            {
                return false;
            }
            return Databases.Keys.Any(database => database.Identity == resolved) ||
                ImportedSources.Values.Any(source => source.Identity == resolved);
        }

        private static SqLtReadonlyDatabase RemoveSqlOwner(
            IDeviceCaptureController controller)
        {
            if (!Controllers.Remove(
                    controller,
                    out Entry entry) ||
                --entry.References != 0)
            {
                return null;
            }
            Databases.Remove(entry.Database);
            return entry.Database;
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
