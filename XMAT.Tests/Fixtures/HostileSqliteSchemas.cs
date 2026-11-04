// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace XMAT.Tests.Fixtures
{
    internal static class HostileSqliteSchemas
    {
        internal const string CaptureDevicesDdl =
            "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
            "DeviceType INTEGER,DeviceName TEXT,CaptureType INTEGER)";

        internal const string WebProxyConnectionsDdl =
            "CREATE TABLE WebProxyConnections(id INTEGER PRIMARY KEY," +
            "DeviceName TEXT,RequestNumber INTEGER,ConnectionId INTEGER," +
            "RequestTimestamp TEXT,ResponseTimestamp TEXT,RequestScheme TEXT," +
            "RequestHost TEXT,RequestPort TEXT,RequestMethod TEXT,RequestPath TEXT," +
            "RequestStatus TEXT,RequestLineAndHeaders TEXT,RequestBody TEXT," +
            "ResponseLineAndHeaders TEXT,ResponseBody TEXT,ClientIP TEXT)";

        internal const string NetworkTracePacketsDdl =
            "CREATE TABLE NetworkTracePackets(id INTEGER PRIMARY KEY," +
            "DeviceName TEXT,ProcessId INTEGER,ThreadId INTEGER,Timestamp TEXT," +
            "MediaType TEXT,StartPacket INTEGER,EndPacket INTEGER,Fragment INTEGER," +
            "Send INTEGER,Receive INTEGER,Payload TEXT)";

        internal static string CreateDatabase(
            string directory,
            string fileName,
            params string[] statements)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                fileName);
            var connectionString =
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString();
            using var connection =
                new SqliteConnection(connectionString);
            connection.Open();
            foreach (string statement in statements)
            {
                using var command =
                    connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            return path;
        }

        internal static string CreateOrdinaryCapture(
            string directory,
            string fileName,
            string webDdl = null,
            bool includeNetworkTrace = false)
        {
            var statements = new List<string>
            {
                CaptureDevicesDdl,
                webDdl ?? WebProxyConnectionsDdl,
            };
            if (includeNetworkTrace)
            {
                statements.Add(
                    NetworkTracePacketsDdl);
            }
            return CreateDatabase(
                directory,
                fileName,
                statements.ToArray());
        }

        internal static string CreateGeneratedColumnCapture(
            string directory,
            string fileName)
        {
            string path = SyntheticCaptures.WriteXmatcap(
                directory,
                fileName,
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3));
            string generated =
                WebProxyConnectionsDdl.Replace(
                    "ResponseBody TEXT",
                    "ResponseBody TEXT GENERATED ALWAYS AS " +
                    "(printf('%.*c',300000000,'x')) VIRTUAL",
                    StringComparison.Ordinal);
            ReplaceSchemaSql(
                path,
                "WebProxyConnections",
                generated);
            return path;
        }

        internal static string CreateOversizedValueCapture(
            string directory,
            string fileName,
            int valueBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                valueBytes);
            string path =
                CreateOrdinaryCapture(
                    directory,
                    fileName);
            var connectionString =
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString();
            using var connection =
                new SqliteConnection(
                    connectionString);
            connection.Open();
            using (var device =
                connection.CreateCommand())
            {
                device.CommandText =
                    "INSERT INTO CaptureDevices" +
                    "(DeviceType,DeviceName,CaptureType) " +
                    "VALUES(1,'Large device',0)";
                device.ExecuteNonQuery();
            }
            using (var capture =
                connection.CreateCommand())
            {
                capture.CommandText =
                    "INSERT INTO WebProxyConnections(" +
                    "DeviceName,RequestNumber,ConnectionId," +
                    "RequestTimestamp,ResponseTimestamp,RequestScheme," +
                    "RequestHost,RequestPort,RequestMethod,RequestPath," +
                    "RequestStatus,RequestLineAndHeaders,RequestBody," +
                    "ResponseLineAndHeaders,ResponseBody,ClientIP) " +
                    "VALUES('Large device',1,1," +
                    "'2026-01-01T00:00:00.0000000Z'," +
                    "'2026-01-01T00:00:00.1000000Z','https'," +
                    "'example.test','443','GET','/','200'," +
                    "'GET / HTTP/1.1',''," +
                    "$large,'','10.0.0.1')";
                capture.Parameters.AddWithValue(
                    "$large",
                    new string(
                        'x',
                        valueBytes));
                capture.ExecuteNonQuery();
            }
            return path;
        }

        // Returns the write-ahead log that SQLite writes when the statements run against a copy of the capture in WAL
        // mode. The capture itself is not changed.
        internal static byte[] CreateWriteAheadLog(
            string path,
            params string[] statements)
        {
            string work = Path.Combine(
                Path.GetDirectoryName(path),
                $"wal-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            try
            {
                string copy = Path.Combine(
                    work,
                    Path.GetFileName(path));
                File.Copy(
                    path,
                    copy);
                using var connection =
                    new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = copy,
                            Pooling = false,
                        }.ToString());
                connection.Open();
                Assert.Equal(
                    "wal",
                    ExecuteScalar(
                        connection,
                        "PRAGMA journal_mode=WAL") as string,
                    ignoreCase: true);
                foreach (string statement in statements)
                {
                    _ = ExecuteScalar(
                        connection,
                        statement);
                }
                using var log = new FileStream(
                    copy + "-wal",
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                        FileShare.Delete);
                using var bytes = new MemoryStream();
                log.CopyTo(bytes);
                Assert.True(bytes.Length > 0);
                return bytes.ToArray();
            }
            finally
            {
                Directory.Delete(
                    work,
                    true);
            }
        }

        // Reads a table's schema SQL as SQLite reads it with the capture's -wal file, from copies of both files.
        internal static string ReadSchemaSqlWithWriteAheadLog(
            string path,
            string table)
        {
            string probe = Path.Combine(
                Path.GetDirectoryName(path),
                $"wal-probe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(probe);
            try
            {
                string copy = Path.Combine(
                    probe,
                    Path.GetFileName(path));
                File.Copy(
                    path,
                    copy);
                File.Copy(
                    path + "-wal",
                    copy + "-wal");
                using var connection =
                    new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = copy,
                            Mode = SqliteOpenMode.ReadOnly,
                            Pooling = false,
                        }.ToString());
                connection.Open();
                using var command =
                    connection.CreateCommand();
                command.CommandText =
                    "SELECT sql FROM sqlite_schema " +
                    "WHERE type='table' AND name=$name";
                command.Parameters.AddWithValue(
                    "$name",
                    table);
                return command.ExecuteScalar() as string;
            }
            finally
            {
                Directory.Delete(
                    probe,
                    true);
            }
        }

        internal static void SwitchToWriteAheadLogging(
            string path)
        {
            using var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Pooling = false,
                    }.ToString());
            connection.Open();
            Assert.Equal(
                "wal",
                ExecuteScalar(
                    connection,
                    "PRAGMA journal_mode=WAL") as string,
                ignoreCase: true);
        }

        internal static void ReplaceSchemaSql(
            string path,
            string table,
            string sql)
        {
            var connectionString =
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString();
            using var connection =
                new SqliteConnection(connectionString);
            connection.Open();
            using (var writable =
                connection.CreateCommand())
            {
                writable.CommandText =
                    "PRAGMA writable_schema=ON";
                writable.ExecuteNonQuery();
            }
            using (var update =
                connection.CreateCommand())
            {
                update.CommandText =
                    "UPDATE sqlite_schema SET sql=$sql " +
                    "WHERE type='table' AND name=$name";
                update.Parameters.AddWithValue(
                    "$sql",
                    sql);
                update.Parameters.AddWithValue(
                    "$name",
                    table);
                Assert.Equal(
                    1,
                    update.ExecuteNonQuery());
            }
        }

        internal static string LongExtensionDdl(
            int extensionColumns)
        {
            var builder = new System.Text.StringBuilder(
                WebProxyConnectionsDdl.TrimEnd(')'));
            for (var index = 0;
                index < extensionColumns;
                index++)
            {
                string name =
                    "Extension" +
                    index.ToString(
                        "D2",
                        CultureInfo.InvariantCulture) +
                    new string(
                        'x',
                        112);
                builder.Append(',')
                    .Append(name)
                    .Append(" TEXT");
            }
            return builder.Append(')')
                .ToString();
        }

        internal static void CorruptFirstOverflowPage(
            string path,
            bool cycle)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int pageSize =
                BinaryPrimitives.ReadUInt16BigEndian(
                    bytes.AsSpan(
                        16,
                        2));
            if (pageSize == 1)
            {
                pageSize = 65_536;
            }
            int usable = pageSize - bytes[20];
            int cells =
                BinaryPrimitives.ReadUInt16BigEndian(
                    bytes.AsSpan(
                        103,
                        2));
            for (var index = 0;
                index < cells;
                index++)
            {
                int cell =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        bytes.AsSpan(
                            108 + index * 2,
                            2));
                int offset = cell;
                ulong payload = ReadVarint(
                    bytes,
                    ref offset);
                _ = ReadVarint(
                    bytes,
                    ref offset);
                int maxLocal = usable - 35;
                if (payload <=
                    (ulong)maxLocal)
                {
                    continue;
                }
                int minLocal =
                    ((usable - 12) *
                        32 /
                        255) -
                    23;
                int local = minLocal +
                    ((int)payload - minLocal) %
                    (usable - 4);
                if (local > maxLocal)
                {
                    local = minLocal;
                }
                uint overflow =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        bytes.AsSpan(
                            offset + local,
                            4));
                if (overflow == 0)
                {
                    continue;
                }
                uint pageCount =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        bytes.AsSpan(
                            28,
                            4));
                BinaryPrimitives.WriteUInt32BigEndian(
                    bytes.AsSpan(
                        checked(
                            ((int)overflow - 1) *
                            pageSize),
                        4),
                    cycle
                        ? overflow
                        : pageCount + 1);
                File.WriteAllBytes(
                    path,
                    bytes);
                return;
            }
            Assert.Fail(
                "The fixture did not contain an overflow schema record.");
        }

        internal static void SetHeaderPageCount(
            string path,
            uint pageCount,
            bool countersMatch)
        {
            byte[] bytes = File.ReadAllBytes(path);
            uint changeCounter =
                BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(
                        24,
                        4));
            BinaryPrimitives.WriteUInt32BigEndian(
                bytes.AsSpan(
                    28,
                    4),
                pageCount);
            BinaryPrimitives.WriteUInt32BigEndian(
                bytes.AsSpan(
                    92,
                    4),
                countersMatch
                    ? changeCounter
                    : changeCounter + 1);
            File.WriteAllBytes(
                path,
                bytes);
        }

        internal static void AppendUnusedPage(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int pageSize =
                BinaryPrimitives.ReadUInt16BigEndian(
                    bytes.AsSpan(
                        16,
                        2));
            if (pageSize == 1)
            {
                pageSize = 65_536;
            }
            Array.Resize(
                ref bytes,
                checked(bytes.Length + pageSize));
            File.WriteAllBytes(
                path,
                bytes);
        }

        private static object ExecuteScalar(
            SqliteConnection connection,
            string sql)
        {
            using var command =
                connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private static ulong ReadVarint(
            byte[] bytes,
            ref int offset)
        {
            ulong value = 0;
            for (var index = 0;
                index < 9;
                index++)
            {
                byte current = bytes[offset++];
                if (index == 8)
                {
                    return (value << 8) |
                        current;
                }
                value = (value << 7) |
                    (uint)(current & 0x7f);
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException();
        }
    }
}
