// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class XmatcapExportWriterTests
    {
        [Fact]
        public void Complete_WritesRawValuesExtensionsAndOriginalIds()
        {
            using var directory = new ExportTestDirectory();
            string output = directory.PathOf("subset.xmatcap");
            IReadOnlyList<SqLtSchemaColumn> fields = CaptureFields(
                new SqLtSchemaColumn("QuoteText", SqLtDeclaredType.Text),
                new SqLtSchemaColumn("ExtraInteger", SqLtDeclaredType.Integer),
                new SqLtSchemaColumn("ExtraBlob", SqLtDeclaredType.None),
                new SqLtSchemaColumn("ExtraReal", SqLtDeclaredType.None),
                new SqLtSchemaColumn("ExtraNull", SqLtDeclaredType.None),
                new SqLtSchemaColumn("MissingExtension", SqLtDeclaredType.Text));
            var values = new Dictionary<string, object>(Values(42), StringComparer.Ordinal)
            {
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Imported PC",
                [WebServiceCaptureMethod.FieldKey_RequestPath] = "/users/O'Reilly?json=\"quoted\"",
                [WebProxyCaptureSchema.ClientProcessName] = "Frozen.exe",
                [WebProxyCaptureSchema.ClientProcessId] = 9001L,
                ["QuoteText"] = "single ' and double \" quotes",
                ["ExtraInteger"] = 17L,
                ["ExtraBlob"] = new byte[] { 0, 1, 2, 255 },
                ["ExtraReal"] = 3.25d,
                ["ExtraNull"] = null,
            };
            IReadOnlyDictionary<string, RawExportRow> devices =
                new Dictionary<string, RawExportRow>
                {
                    ["d2"] = DeviceRow(88, "Second PC"),
                    ["d1"] = DeviceRow(77, "Imported PC"),
                };

            using (var writer = new XmatcapExportWriter(output, fields, DeviceFields, devices))
            {
                writer.Write(new RawExportRow(Key(42), 42, 3, values), CancellationToken.None);
                ExportWriteSummary summary = writer.Complete(CancellationToken.None);
                Assert.Equal(1, summary.RowsWritten);
            }

            using var connection = Open(output);
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT rowid, DeviceName FROM CaptureDevices ORDER BY rowid";
                using SqliteDataReader device = command.ExecuteReader();
                Assert.True(device.Read());
                Assert.Equal(77L, device.GetInt64(0));
                Assert.Equal("Imported PC", device.GetString(1));
                Assert.False(device.Read());
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT rowid, RequestNumber, ConnectionId, RequestPath, ClientProcessName, ClientProcessId, " +
                    "QuoteText, ExtraInteger, " +
                    "ExtraBlob, ExtraReal, ExtraNull, MissingExtension FROM WebProxyConnections";
                using SqliteDataReader capture = command.ExecuteReader();
                Assert.True(capture.Read());
                Assert.Equal(42L, capture.GetInt64(0));
                Assert.Equal(42L, capture.GetInt64(1));
                Assert.Equal(1042L, capture.GetInt64(2));
                Assert.Equal("/users/O'Reilly?json=\"quoted\"", capture.GetString(3));
                Assert.Equal("Frozen.exe", capture.GetString(4));
                Assert.Equal(9001L, capture.GetInt64(5));
                Assert.Equal("single ' and double \" quotes", capture.GetString(6));
                Assert.Equal(17L, capture.GetInt64(7));
                Assert.Equal(new byte[] { 0, 1, 2, 255 }, (byte[])capture.GetValue(8));
                Assert.Equal(3.25d, capture.GetDouble(9));
                Assert.True(capture.IsDBNull(10));
                Assert.True(capture.IsDBNull(11));
                Assert.False(capture.Read());
            }
        }

        [Theory]
        [InlineData(true, CaptureTextFormat.LiteralUserVersion)]
        [InlineData(false, CaptureTextFormat.AmbiguousUserVersion)]
        public void Complete_WritesTheLiteralMarkerOnlyWhenAsked(bool markLiteral, int expected)
        {
            using var directory = new ExportTestDirectory();
            string output = directory.PathOf("marked.xmatcap");

            using (var writer = new XmatcapExportWriter(
                output,
                CaptureFields(),
                DeviceFields,
                new Dictionary<string, RawExportRow>(),
                markLiteral))
            {
                writer.Write(Row(1), CancellationToken.None);
                writer.Complete(CancellationToken.None);
            }

            using var connection = Open(output);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal((long)expected, Convert.ToInt64(command.ExecuteScalar()));
        }

        [Fact]
        public void Constructor_UnexpectedFailureReleasesTheStagedFileAndRethrows()
        {
            using var directory = new ExportTestDirectory();
            string output = directory.PathOf("failed.xmatcap");

            Exception failure = Record.Exception(
                () => new XmatcapExportWriter(
                    output,
                    CaptureFields(),
                    DeviceFields,
                    new Dictionary<string, RawExportRow>
                    {
                        ["d1"] = new RawExportRow(default, 1, 0, null),
                    }));

            Assert.NotNull(failure);
            Assert.IsNotType<CaptureExportException>(failure);
            File.Delete(output);
            Assert.False(File.Exists(output));
        }

        [Theory]
        [InlineData(13, CaptureExportErrorCodes.WriteFailed, nameof(OperationOutcome.Failed))]
        [InlineData(18, CaptureExportErrorCodes.InputLimit, nameof(OperationOutcome.InvalidInput))]
        [InlineData(19, null, null)]
        public void Write_ClassifiesSqliteFailures(int sqliteCode, string expectedCode, string outcomeName)
        {
            using var directory = new ExportTestDirectory();
            using var writer = new XmatcapExportWriter(
                directory.PathOf(sqliteCode + ".xmatcap"),
                CaptureFields(),
                DeviceFields,
                new Dictionary<string, RawExportRow>());
            var row = new RawExportRow(Key(1), 1, 1, new ThrowingValues(SqliteFailure(sqliteCode)));

            Exception failure = Record.Exception(() => writer.Write(row, CancellationToken.None));

            if (expectedCode == null)
            {
                Assert.IsType<SqliteException>(failure);
                return;
            }
            CaptureExportException expected = Assert.IsType<CaptureExportException>(failure);
            Assert.Equal(expectedCode, expected.Code);
            Assert.Equal(Enum.Parse<OperationOutcome>(outcomeName), expected.Outcome);
        }

        [Fact]
        public void Complete_SynthesizesADeviceDescriptorForAnImportedTab()
        {
            using var directory = new ExportTestDirectory();
            string output = directory.PathOf("imported.xmatcap");
            var values = new Dictionary<string, object>(Values(5), StringComparer.Ordinal)
            {
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Imported SAZ",
            };

            using (var writer = new XmatcapExportWriter(
                output,
                CaptureFields(),
                DeviceFields,
                new Dictionary<string, RawExportRow>()))
            {
                writer.Write(new RawExportRow(Key(5), 5, 1, values), CancellationToken.None);
                _ = writer.Complete(CancellationToken.None);
            }

            using var connection = Open(output);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT DeviceType, DeviceName, CaptureType FROM CaptureDevices";
            using SqliteDataReader device = command.ExecuteReader();
            Assert.True(device.Read());
            Assert.Equal((long)DeviceType.Unknown, device.GetInt64(0));
            Assert.Equal("Imported SAZ", device.GetString(1));
            Assert.Equal((long)CaptureType.WebProxy, device.GetInt64(2));
            Assert.False(device.Read());
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private sealed class ThrowingValues(SqliteException failure) : IReadOnlyDictionary<string, object>
        {
            public object this[string key] => throw failure;
            public IEnumerable<string> Keys => Array.Empty<string>();
            public IEnumerable<object> Values => Array.Empty<object>();
            public int Count => 1;
            public bool ContainsKey(string key) => true;
            public bool TryGetValue(string key, out object value) => throw failure;
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<string, object>>().GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

    }
}
