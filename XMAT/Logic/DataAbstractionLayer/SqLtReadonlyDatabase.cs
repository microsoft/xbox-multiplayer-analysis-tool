// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using SQLitePCL;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal sealed record SqLtAppliedLimits(
        int Length,
        int SqlLength,
        int Columns,
        int ExpressionDepth);

    internal class SqLtReadonlyDatabase : IReadonlyDatabase, IDisposable
    {
        // FILE_NAME_NORMALIZED with VOLUME_NAME_DOS, then with VOLUME_NAME_GUID for volumes without a drive letter. The
        // opened name is never used: it is not normalized, so it could still lead SQLite through a link.
        private static readonly uint[] FinalPathFlags =
        {
            0x0,
            0x1,
        };

        private readonly FileStream _snapshot;
        private readonly SqliteConnection _connection;
        private readonly Dictionary<string, SqLtReadonlyDataTable> _dataTables =
            new(StringComparer.Ordinal);
        private bool _disposed;

        internal SqLtReadonlyDatabase(string filepath)
            : this(
                filepath,
                SqLtSchemaProfile.CreateGui())
        {
        }

        internal SqLtReadonlyDatabase(
            string filepath,
            SqLtSchemaProfile profile)
            : this(
                filepath,
                profile,
                beforeSqliteOpens: null)
        {
        }

        // beforeSqliteOpens runs after the schema check and before SQLite opens the file, and finalPathUnavailable
        // makes the final-path lookup fail for the given flags; tests use them to change what happens in between.
        internal SqLtReadonlyDatabase(
            string filepath,
            SqLtSchemaProfile profile,
            Action<string> beforeSqliteOpens,
            Func<uint, bool> finalPathUnavailable = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
            ArgumentNullException.ThrowIfNull(profile);
            string databasePath = Path.GetFullPath(filepath);
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "The capture database does not exist.",
                    filepath);
            }

            try
            {
                // Without write sharing, this open fails while another handle or a mapped view can write the file, and
                // while it is held nothing can open the file for writing, rename it or delete it.
                _snapshot = new FileStream(
                    databasePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                // SQLite opens the file by the final path of this handle, which has no junction or symbolic link left
                // that could be retargeted after the check.
                string checkedPath = ToVerbatimPath(
                    GetFinalPath(
                        _snapshot.SafeFileHandle,
                        finalPathUnavailable));
                if (HasSqliteSidecar(checkedPath))
                {
                    throw new SqLtSchemaException(
                        SqLtSchemaFailure.Sidecar);
                }
                SqLtValidatedSchema schema =
                    SqLtSchemaPreflight.Validate(
                        _snapshot,
                        profile);
                beforeSqliteOpens?.Invoke(databasePath);
                var connectionString =
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = ToImmutableUri(checkedPath),
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false,
                    }.ToString();
                _connection =
                    new SqliteConnection(connectionString);
                _connection.Open();
                ThrowUnlessSqliteOpened(
                    _connection,
                    checkedPath);
                AppliedLimits = ApplyLimits(
                    _connection);
                InitializeTables(
                    schema,
                    profile);
            }
            catch
            {
                try
                {
                    _connection?.Dispose();
                }
                finally
                {
                    _snapshot?.Dispose();
                }
                throw;
            }
        }

        internal SqLtAppliedLimits AppliedLimits { get; }

        public IEnumerable<string> TableNames() =>
            _dataTables.Keys;

        public IReadonlyDataTable TableByName(string name) =>
            name != null &&
            _dataTables.TryGetValue(
                name,
                out SqLtReadonlyDataTable table)
                ? table
                : null;

        internal string ReadSqliteVersion()
        {
            using var command =
                _connection.CreateCommand();
            command.CommandText =
                "SELECT sqlite_version()";
            return command.ExecuteScalar() as string;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                foreach (SqLtReadonlyDataTable table in
                    _dataTables.Values)
                {
                    table.Dispose();
                }
                _dataTables.Clear();
            }
            finally
            {
                try
                {
                    _connection.Close();
                }
                finally
                {
                    try
                    {
                        _connection.Dispose();
                    }
                    finally
                    {
                        _snapshot.Dispose();
                        _disposed = true;
                    }
                }
            }
        }

        // A -journal, -wal or -shm file next to the capture means its last save did not finish or another program may
        // still be writing it, so the file alone may not hold the whole capture. Such captures are refused, and neither
        // the capture nor those files are changed.
        internal static bool HasSqliteSidecar(
            string databasePath) =>
            Path.Exists(databasePath + "-journal") ||
            Path.Exists(databasePath + "-wal") ||
            Path.Exists(databasePath + "-shm");

        private static string GetFinalPath(
            SafeFileHandle file,
            Func<uint, bool> finalPathUnavailable)
        {
            foreach (uint flags in FinalPathFlags)
            {
                string path =
                    finalPathUnavailable?.Invoke(flags) == true
                        ? null
                        : TryGetFinalPath(
                            file,
                            flags);
                if (path != null)
                {
                    return path;
                }
            }
            throw new SqLtSchemaException(
                SqLtSchemaFailure.UnsupportedLocation);
        }

        private static string TryGetFinalPath(
            SafeFileHandle file,
            uint flags)
        {
            var buffer = new char[512];
            while (true)
            {
                uint length =
                    NativeMethods.GetFinalPathNameByHandle(
                        file,
                        buffer,
                        (uint)buffer.Length,
                        flags);
                if (length == 0)
                {
                    return null;
                }
                if (length < buffer.Length)
                {
                    return new string(
                        buffer,
                        0,
                        (int)length);
                }
                buffer = new char[length];
            }
        }

        // The \\?\ form keeps paths longer than MAX_PATH openable and stops Windows from reinterpreting the name.
        private static string ToVerbatimPath(
            string fullPath) =>
            fullPath.StartsWith(
                    @"\\?\",
                    StringComparison.Ordinal) ||
                fullPath.StartsWith(
                    @"\\.\",
                    StringComparison.Ordinal)
                ? fullPath
                : fullPath.StartsWith(
                    @"\\",
                    StringComparison.Ordinal)
                    ? @"\\?\UNC\" + fullPath[2..]
                    : @"\\?\" + fullPath;

        // immutable=1 makes SQLite read only this file: it takes no locks and never looks for journal or write-ahead
        // log files, so sidecars created after the check above are never read. The flag is only a promise that the file
        // does not change; the read-only handle without write sharing opened above keeps that promise. SQLite decodes
        // %HH in URI file names and ends the name at '?' or '#', so those characters are escaped.
        private static string ToImmutableUri(
            string path) =>
            "file:" +
            path.Replace(
                    "%",
                    "%25",
                    StringComparison.Ordinal)
                .Replace(
                    "?",
                    "%3F",
                    StringComparison.Ordinal)
                .Replace(
                    "#",
                    "%23",
                    StringComparison.Ordinal) +
            "?immutable=1";

        // SQLite reports the decoded file name only if it parsed the URI, which is also what applies immutable=1.
        private static void ThrowUnlessSqliteOpened(
            SqliteConnection connection,
            string expectedPath)
        {
            string opened =
                raw.sqlite3_db_filename(
                        connection.Handle,
                        "main")
                    .utf8_to_string();
            if (!string.Equals(
                    opened,
                    expectedPath,
                    StringComparison.Ordinal))
            {
                throw new SqLtSchemaException(
                    SqLtSchemaFailure.Invalid);
            }
        }

        private void InitializeTables(
            SqLtValidatedSchema schema,
            SqLtSchemaProfile profile)
        {
            foreach (SqLtValidatedTable table in
                schema.Tables.Values)
            {
                if (!profile.MaterializedTables.Contains(
                        table.CanonicalName))
                {
                    continue;
                }
                SqLtExpectedTable expected =
                    SqLtSchemaProfile.GetKnownTable(
                        table.CanonicalName) ??
                    throw new SqLtSchemaException(
                        SqLtSchemaFailure.Invalid);
                IEnumerable<IFieldDefinition> fields =
                    table.Columns.Select(
                        (column, index) =>
                            FieldDefinition.CreateFromSchema(
                                column.Name,
                                column.RuntimeType,
                                index + 1,
                                isExtension:
                                    index >=
                                    expected.RequiredColumns.Count));
                _dataTables.Add(
                    table.CanonicalName,
                    new SqLtReadonlyDataTable(
                        _connection,
                        table.SqliteName,
                        fields));
            }
        }

        // SQLite's value limit is left at its default: opening a capture in XMAT has no size quota. The SQL, column and
        // expression limits bound statement parsing, and each is read back after it is set.
        private static SqLtAppliedLimits ApplyLimits(
            SqliteConnection connection)
        {
            int length = ReadLimit(
                connection,
                raw.SQLITE_LIMIT_LENGTH);
            int sqlLength = SetLimit(
                connection,
                raw.SQLITE_LIMIT_SQL_LENGTH,
                SqLtSchemaProfile.MaxSqlLength);
            int columns = SetLimit(
                connection,
                raw.SQLITE_LIMIT_COLUMN,
                SqLtSchemaProfile.MaxColumns);
            int expressionDepth = SetLimit(
                connection,
                raw.SQLITE_LIMIT_EXPR_DEPTH,
                SqLtSchemaProfile.MaxExpressionDepth);
            return new SqLtAppliedLimits(
                length,
                sqlLength,
                columns,
                expressionDepth);
        }

        private static int SetLimit(
            SqliteConnection connection,
            int id,
            int requested)
        {
            _ = raw.sqlite3_limit(
                connection.Handle,
                id,
                requested);
            int effective = raw.sqlite3_limit(
                connection.Handle,
                id,
                -1);
            if (effective != requested)
            {
                throw new SqLtSchemaException(
                    SqLtSchemaFailure.Limit);
            }
            return effective;
        }

        private static int ReadLimit(
            SqliteConnection connection,
            int id) =>
            raw.sqlite3_limit(
                connection.Handle,
                id,
                -1);

        private static class NativeMethods
        {
            [DllImport(
                "kernel32.dll",
                EntryPoint = "GetFinalPathNameByHandleW",
                SetLastError = true,
                CharSet = CharSet.Unicode,
                ExactSpelling = true)]
            [DefaultDllImportSearchPaths(
                DllImportSearchPath.System32)]
            internal static extern uint
                GetFinalPathNameByHandle(
                    SafeFileHandle file,
                    [Out] char[] path,
                    uint pathLength,
                    uint flags);
        }
    }
}
