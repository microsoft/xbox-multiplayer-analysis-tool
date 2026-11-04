// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.DataAbstractionLayer
{
    internal enum SqLtSchemaFailure
    {
        Invalid,
        Unsupported,
        Limit,
        Sidecar,
        UnsupportedLocation,
    }

    internal sealed class SqLtSchemaException : Exception
    {
        internal SqLtSchemaException(
            SqLtSchemaFailure failure,
            Exception innerException = null)
            : base(
                failure switch
                {
                    SqLtSchemaFailure.Limit =>
                        "The capture database schema exceeds a supported safety limit.",
                    SqLtSchemaFailure.Sidecar =>
                        "The capture database has SQLite journal or write-ahead log files next to it.",
                    SqLtSchemaFailure.UnsupportedLocation =>
                        "The capture database's location has no normalized path and cannot be opened safely.",
                    _ => "The capture database schema is unsupported or invalid.",
                },
                innerException)
        {
            Failure = failure;
        }

        internal SqLtSchemaFailure Failure { get; }
    }

    internal enum SqLtDeclaredType
    {
        None,
        Text,
        Integer,
    }

    internal sealed record SqLtSchemaColumn(
        string Name,
        SqLtDeclaredType DeclaredType)
    {
        internal Type RuntimeType =>
            DeclaredType == SqLtDeclaredType.Integer
                ? typeof(long)
                : typeof(string);
    }

    internal sealed record SqLtValidatedTable(
        string CanonicalName,
        string SqliteName,
        int RootPage,
        IReadOnlyList<SqLtSchemaColumn> Columns);

    internal sealed class SqLtValidatedSchema
    {
        internal SqLtValidatedSchema(
            IReadOnlyDictionary<string, SqLtValidatedTable> tables)
        {
            Tables = tables;
        }

        internal IReadOnlyDictionary<string, SqLtValidatedTable> Tables { get; }
    }

    internal sealed record SqLtExpectedColumn(
        string Name,
        SqLtDeclaredType DeclaredType);

    internal sealed record SqLtExpectedTable(
        string Name,
        IReadOnlyList<SqLtExpectedColumn> RequiredColumns);

    internal sealed class SqLtSchemaProfile
    {
        internal const int MaxSchemaRecords = 16;
        internal const int MaxSchemaRecordBytes = 64 * 1024;
        internal const int MaxTotalSchemaBytes = 256 * 1024;
        internal const int MaxSchemaPagesVisited = 512;
        internal const int MaxSchemaTreeDepth = 16;
        internal const int MaxOverflowPagesPerRecord = 256;
        internal const int MaxCellsPerSchemaPage = 256;
        internal const int MaxExtensionColumns = 64;
        internal const int MaxIdentifierCharacters = 128;
        internal const int MaxDdlBytes = 64 * 1024;
        internal const int MaxSqlLength = 64 * 1024;
        internal const int MaxColumns = 81;
        internal const int MaxExpressionDepth = 16;

        private SqLtSchemaProfile(
            IReadOnlyCollection<string> requiredTables,
            IReadOnlyCollection<string> materializedTables)
        {
            RequiredTables = new HashSet<string>(
                requiredTables,
                StringComparer.Ordinal);
            MaterializedTables = new HashSet<string>(
                materializedTables,
                StringComparer.Ordinal);
        }

        internal IReadOnlySet<string> RequiredTables { get; }

        internal IReadOnlySet<string> MaterializedTables { get; }

        internal static IReadOnlyDictionary<string, SqLtExpectedTable> KnownTables { get; } =
            new Dictionary<string, SqLtExpectedTable>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["CaptureDevices"] = Table(
                    "CaptureDevices",
                    Integer("DeviceType"),
                    Text("DeviceName"),
                    Integer("CaptureType")),
                ["WebProxyConnections"] = Table(
                    "WebProxyConnections",
                    Text("DeviceName"),
                    Integer("RequestNumber"),
                    Integer("ConnectionId"),
                    Text("RequestTimestamp"),
                    Text("ResponseTimestamp"),
                    Text("RequestScheme"),
                    Text("RequestHost"),
                    Text("RequestPort"),
                    Text("RequestMethod"),
                    Text("RequestPath"),
                    Text("RequestStatus"),
                    Text("RequestLineAndHeaders"),
                    Text("RequestBody"),
                    Text("ResponseLineAndHeaders"),
                    Text("ResponseBody"),
                    Text("ClientIP")),
                ["NetworkTracePackets"] = Table(
                    "NetworkTracePackets",
                    Text("DeviceName"),
                    Integer("ProcessId"),
                    Integer("ThreadId"),
                    Text("Timestamp"),
                    Text("MediaType"),
                    Integer("StartPacket"),
                    Integer("EndPacket"),
                    Integer("Fragment"),
                    Integer("Send"),
                    Integer("Receive"),
                    Text("Payload")),
            };

        // The profile of a capture opened in XMAT: it needs only CaptureDevices and reads every known table.
        internal static SqLtSchemaProfile CreateGui() =>
            new(
                new[] { "CaptureDevices" },
                KnownTables.Keys.ToArray());

        internal static SqLtExpectedTable GetKnownTable(string name)
        {
            return name != null &&
                KnownTables.TryGetValue(name, out SqLtExpectedTable table)
                    ? table
                    : null;
        }

        private static SqLtExpectedTable Table(
            string name,
            params SqLtExpectedColumn[] columns) =>
            new(name, columns);

        private static SqLtExpectedColumn Text(string name) =>
            new(name, SqLtDeclaredType.Text);

        private static SqLtExpectedColumn Integer(string name) =>
            new(name, SqLtDeclaredType.Integer);
    }
}
