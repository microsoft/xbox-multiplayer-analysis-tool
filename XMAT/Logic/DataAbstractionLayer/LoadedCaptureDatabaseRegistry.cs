// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    // Tracks which read-only capture tab owns each loaded capture database. Tabs reused by a reload share the newly
    // opened database, and the database (with its file handle) is disposed as soon as its last owning tab releases it.
    internal static class LoadedCaptureDatabaseRegistry
    {
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
