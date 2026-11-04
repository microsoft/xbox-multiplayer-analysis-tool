// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class XmatcapExportWriterTests
    {
        [Fact]
        public void Complete_WritesRawValuesExtensionsAndOriginalIdsWithoutChangingSource()
        {
            using var directory = new ExportTestDirectory();
            string source = directory.PathOf("source.xmatcap");
            string output = directory.PathOf("subset.xmatcap");
            File.WriteAllBytes(source, "source-bytes-must-not-change"u8.ToArray());
            string sourceHash = Sha256(source);
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

            Assert.Equal(sourceHash, Sha256(source));
            using var connection = Open(output);
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT rowid, DeviceName FROM CaptureDevices ORDER BY rowid";
                using SqliteDataReader device = command.ExecuteReader();
                Assert.True(device.Read());
                Assert.Equal(77L, device.GetInt64(0));
                Assert.Equal("Imported PC", device.GetString(1));
                Assert.True(device.Read());
                Assert.Equal(88L, device.GetInt64(0));
                Assert.Equal("Second PC", device.GetString(1));
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
            var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            return connection;
        }

    }
}
