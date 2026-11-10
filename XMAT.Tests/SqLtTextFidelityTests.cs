// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class SqLtTextFidelityTests
    {
        private static readonly string[] TextValues =
        {
            "a'b",
            "a''b",
            "'x'",
            "a\"b",
            "a\"\"b",
            "a\"b'c\"\"d",
            "\u2018\u2019\u201c\u201d",
            "Καλημέρα café",
            string.Empty,
        };

        [Fact]
        public void DataValuesStayLiteralThroughEverySqlPath()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table =
                database.CreateTable(
                    "Fidelity",
                    new Field<string>("Capture"),
                    new Field<string>("Value"),
                    new Field<long>("Number"));
            var ids = new List<long>();
            for (int index = 0; index < TextValues.Length; index++)
            {
                ids.Add(
                    table.AddRow(
                        "capture",
                        TextValues[index],
                        (long)index));
            }

            Assert.Equal(
                TextValues,
                table.Dataset.DataRecords
                    .OrderBy(record => record.RowId)
                    .Select(record => record.Str("Value")));

            for (int index = 0; index < TextValues.Length; index++)
            {
                string updated =
                    TextValues[TextValues.Length - index - 1];
                table.UpdateRow(
                    ids[index],
                    new FieldValue<string>(
                        "Value",
                        updated));
                IDataRecord record =
                    Assert.Single(
                        table.Dataset.DataRecordsWhen(
                            new FieldValue<long>(
                                "Number",
                                index),
                            new FieldValue<string>(
                                "Value",
                                updated)));
                Assert.Equal(updated, record.Str("Value"));
            }

            table.AddRow(
                "remove",
                "a\"b",
                100L);
            table.AddRow(
                "remove",
                "a\"\"b",
                101L);
            table.RemoveRowsWhere(
                new FieldValue<string>(
                    "Capture",
                    "remove"),
                new FieldValue<string>(
                    "Value",
                    "a\"b"));
            IDataRecord remaining =
                Assert.Single(
                    table.Dataset.DataRecordsWhen(
                        new FieldValue<string>(
                            "Capture",
                            "remove")));
            Assert.Equal(
                "a\"\"b",
                remaining.Str("Value"));

            var import = (IImportedCaptureTable)table;
            string importedCapture =
                "import\"capture";
            IReadOnlyList<long> importedIds =
                import.ReplaceImportedRows(
                    CaptureTextSource.Literal,
                    new FieldValue<string>(
                        "Capture",
                        importedCapture),
                    TextValues.Select(
                        (value, index) =>
                            new object[]
                            {
                                importedCapture,
                                value,
                                (long)index,
                            }));
            Assert.Equal(
                TextValues.Length,
                importedIds.Count);
            Assert.Equal(
                TextValues,
                table.Dataset.DataRecordsWhen(
                        new FieldValue<string>(
                            "Capture",
                            importedCapture))
                    .OrderBy(record => record.RowId)
                    .Select(record => record.Str("Value")));

            import.ReplaceImportedRows(
                CaptureTextSource.Literal,
                new FieldValue<string>(
                    "Capture",
                    importedCapture),
                new[]
                {
                    new object[]
                    {
                        importedCapture,
                        "replacement\"value",
                        200L,
                    },
                });
            IDataRecord replacement =
                Assert.Single(
                    table.Dataset.DataRecordsWhen(
                        new FieldValue<string>(
                            "Capture",
                            importedCapture)));
            Assert.Equal(
                "replacement\"value",
                replacement.Str("Value"));

            Assert.Throws<InvalidOperationException>(
                () => import.ReplaceImportedRows(
                    CaptureTextSource.Unknown,
                    new FieldValue<string>(
                        "Capture",
                        importedCapture),
                    Array.Empty<object[]>()));
            Assert.Equal(
                "replacement\"value",
                Assert.Single(
                    table.Dataset.DataRecordsWhen(
                        new FieldValue<string>(
                            "Capture",
                            importedCapture)))
                    .Str("Value"));
        }

        [Theory]
        [InlineData("plain'quote")]
        [InlineData("x' OR '1'='1")]
        public void RemoveRowsWhere_DeletesOnlyTheExactQuoteBearingValue(
            string value)
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table =
                database.CreateTable(
                    "DeleteFidelity",
                    new Field<string>("Capture"),
                    new Field<string>("Value"),
                    new Field<long>("Number"));
            table.AddRow("target", value, 1L);
            table.AddRow("target", "keep", 2L);
            table.AddRow("other", value, 3L);

            table.RemoveRowsWhere(
                new FieldValue<string>(
                    "Capture",
                    "target"),
                new FieldValue<string>(
                    "Value",
                    value));

            IDataRecord[] remaining =
                table.Dataset.DataRecords
                    .OrderBy(
                        record => record.Int("Number"))
                    .ToArray();
            Assert.Equal(
                new long[] { 2, 3 },
                remaining.Select(
                    record => record.Int("Number")));
            Assert.Equal(
                new[] { "keep", value },
                remaining.Select(
                    record => record.Str("Value")));
            Assert.Equal(
                new[] { "target", "other" },
                remaining.Select(
                    record => record.Str("Capture")));
        }

        [Fact]
        public void IntegerTypesAndWriteSideNullBehaviorStayUnchanged()
        {
            using var directory = new TestDirectory();
            string path =
                Path.Join(
                    directory.Path,
                    "types.xmatcap");
            using (var database = new SqLtDatabase())
            {
                database.Initialize();
                IDataTable table =
                    database.CreateTable(
                        "Types",
                        new Field<string>("TextValue"),
                        new Field<long>("NumberValue"));
                table.AddRow("minimum", long.MinValue);
                table.AddRow("zero", 0);
                table.AddRow("maximum", long.MaxValue);
                table.AddRow("int-minimum", int.MinValue);
                table.AddRow("int-maximum", int.MaxValue);
                table.AddRow(null, null);

                Assert.Equal(
                    new[]
                    {
                        long.MinValue,
                        0,
                        long.MaxValue,
                        int.MinValue,
                        int.MaxValue,
                    },
                    table.Dataset.DataRecords
                        .OrderBy(record => record.RowId)
                        .Take(5)
                        .Select(record => record.Int("NumberValue")));
                database.SaveToFile(path);
            }

            using var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString());
            connection.Open();
            using var command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT typeof(TextValue),TextValue," +
                "typeof(NumberValue),NumberValue " +
                "FROM Types WHERE id=6";
            using SqliteDataReader reader =
                command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("text", reader.GetString(0));
            Assert.Equal(string.Empty, reader.GetString(1));
            Assert.Equal("text", reader.GetString(2));
            Assert.Equal(string.Empty, reader.GetString(3));
        }

        [Fact]
        public void GuardedDeviceFilterMatchesAQuoteBearingName()
        {
            const string Device = "dev\"ice";
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "quoted-device.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1));
            using (var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Pooling = false,
                    }.ToString()))
            {
                connection.Open();
                foreach (string table in
                    new[]
                    {
                        "CaptureDevices",
                        "WebProxyConnections",
                    })
                {
                    using var update =
                        connection.CreateCommand();
                    update.CommandText =
                        $"UPDATE {table} SET DeviceName=$device";
                    update.Parameters.AddWithValue(
                        "$device",
                        Device);
                    update.ExecuteNonQuery();
                }
            }

            using var database =
                new SqLtReadonlyDatabase(path);
            var controller =
                new WebServiceDeviceCaptureController(
                    Device,
                    DeviceType.Unknown,
                    true);
            controller.LoadCaptureDataset(
                database.TableByName(
                        WebServiceCaptureMethod.TableName)
                    .Dataset);

            IDataRecord record =
                Assert.Single(
                    controller.GetCaptureRecords());
            Assert.Equal(
                Device,
                record.Str(
                    WebServiceCaptureMethod.FieldKey_DeviceName));
            Assert.Same(
                database.CaptureTextSource,
                controller.ExportTextSource);
        }

        [Fact]
        public async Task RealCaptureSchema_SaveLoadAndModelKeepExactQuotes()
        {
            const string Device = "dev\"ice";
            // Production FirstLineAndHeaders includes the blank line that ends the header block.
            const string Headers =
                "GET / HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "If-None-Match: \"v1\"\r\n" +
                "X-Adjacent: \"\"\r\n\r\n";
            using var directory = new TestDirectory();
            string path =
                Path.Join(
                    directory.Path,
                    "fidelity.xmatcap");
            using (var database = new SqLtDatabase())
            {
                database.Initialize();
                IDataTable devices =
                    database.CreateTable(
                        "CaptureDevices",
                        new Field<long>("DeviceType"),
                        new Field<string>("DeviceName"),
                        new Field<long>("CaptureType"));
                IDataTable captures =
                    CreateWebProxyTable(database);
                devices.AddRow(
                    (long)DeviceType.XboxConsole,
                    Device,
                    (long)CaptureType.WebProxy);
                captures.AddRow(
                    Device,
                    1L,
                    1L,
                    ProxyConnectionModel.ToDateTimeString(
                        DateTime.UnixEpoch),
                    ProxyConnectionModel.ToDateTimeString(
                        DateTime.UnixEpoch.AddMilliseconds(10)),
                    "https",
                    "example.test",
                    "443",
                    "GET",
                    "/",
                    "200",
                    Headers,
                    string.Empty,
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
                    string.Empty,
                    "10.0.0.1");
                database.SaveToFile(path);
            }

            using var saved =
                new SqLtReadonlyDatabase(path);
            IDataRecord record =
                Assert.Single(
                    saved.TableByName(
                            WebServiceCaptureMethod.TableName)
                        .Dataset.DataRecordsWhen(
                            new FieldValue<string>(
                                WebServiceCaptureMethod.FieldKey_DeviceName,
                                Device)));
            var model =
                new ProxyConnectionModel(record);

            Assert.Equal(Device, record.Str("DeviceName"));
            Assert.Equal(Headers, model.RequestLineAndHeaders);
            Assert.Same(
                saved.CaptureTextSource,
                model.CaptureTextSource);
            Assert.Equal(
                CaptureTextSemantics.Literal,
                model.CaptureTextSource.Semantics);

            using var cycleDatabase =
                new SqLtDatabase();
            cycleDatabase.Initialize();
            IDataTable cycleTable =
                CreateWebProxyTable(
                    cycleDatabase);
            model.AddToDataTable(
                Device,
                cycleTable);
            var captureMethod =
                (WebServiceCaptureMethod)Activator.CreateInstance(
                    typeof(WebServiceCaptureMethod),
                    nonPublic: true);
            typeof(WebServiceCaptureMethod)
                .GetProperty(
                    nameof(WebServiceCaptureMethod.WebProxyConnectionsTable),
                    BindingFlags.Instance |
                        BindingFlags.NonPublic)
                .SetValue(
                    captureMethod,
                    cycleTable);
            var controller =
                new WebServiceDeviceCaptureController(
                    Device,
                    DeviceType.Unknown,
                    true);
            var context =
                new Context(
                    controller);
            byte[][] requestEntries =
                new byte[3][];
            string exportedHeaders =
                Headers.Replace(
                    "GET / HTTP/1.1",
                    "GET https://example.test/ HTTP/1.1",
                    StringComparison.Ordinal);
            for (int cycle = 0;
                cycle < requestEntries.Length;
                cycle++)
            {
                string archive =
                    Path.Join(
                        directory.Path,
                        $"cycle-{cycle}.saz");
                OperationResult<SazExportResult> exported =
                    await ExportAsync(
                        archive,
                        cycleTable,
                        Device);
                Assert.True(
                    exported.Succeeded,
                    exported.Diagnostics
                        .FirstOrDefault()?
                        .Code);
                requestEntries[cycle] =
                    RequestEntry(
                        archive);
                await captureMethod.ImportFromFiddlerAsync(
                    archive,
                    context,
                    cycleTable);
                ProxyConnectionModel imported =
                    Assert.Single(
                        controller.ProxyConnections);
                Assert.Equal(
                    exportedHeaders,
                    imported.RequestLineAndHeaders);
                Assert.Equal(
                    exportedHeaders,
                    Assert.Single(
                            cycleTable.Dataset.DataRecords)
                        .Str(
                            WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders));
            }
            Assert.Equal(
                requestEntries[1],
                requestEntries[2]);
        }

        // Save Captures keeps a stored head exactly, without the separator the archive export adds.
        [Fact]
        public void SaveCaptures_KeepsAnUnterminatedStoredHeadExactly()
        {
            const string Device = "Device 1";
            byte[] body =
                "X-Injected: 1\r\n\r\ntail"u8.ToArray();
            using var directory = new TestDirectory();
            string source =
                XMAT.Tests.Replay.ReplayCaptures.WriteXmatcap(
                    directory.Path,
                    "source.xmatcap",
                    (Device, new[]
                    {
                        new XMAT.Tests.Replay.CapturedExchange(
                            "POST",
                            "http://127.0.0.1/upload",
                            Body: body),
                    }));
            string saved =
                Path.Join(
                    directory.Path,
                    "saved.xmatcap");
            string storedHead;
            using (var input = new SqLtReadonlyDatabase(source))
            {
                IDataRecord record =
                    Assert.Single(
                        input.TableByName(
                                WebServiceCaptureMethod.TableName)
                            .Dataset.DataRecords);
                storedHead =
                    record.Str(
                        WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
                Assert.False(
                    storedHead.EndsWith(
                        "\r\n\r\n",
                        StringComparison.Ordinal));
                using var database = new SqLtDatabase();
                database.Initialize();
                database.CreateTable(
                        "CaptureDevices",
                        new Field<long>("DeviceType"),
                        new Field<string>("DeviceName"),
                        new Field<long>("CaptureType"))
                    .AddRow(
                        (long)DeviceType.XboxConsole,
                        Device,
                        (long)CaptureType.WebProxy);
                new ProxyConnectionModel(record).AddToDataTable(
                    Device,
                    CreateWebProxyTable(database));
                database.SaveToFile(saved);
            }

            using var reloaded = new SqLtReadonlyDatabase(saved);
            IDataRecord savedRecord =
                Assert.Single(
                    reloaded.TableByName(
                            WebServiceCaptureMethod.TableName)
                        .Dataset.DataRecords);
            Assert.Equal(
                storedHead,
                savedRecord.Str(
                    WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders));
            Assert.Equal(
                Convert.ToBase64String(body),
                savedRecord.Str(
                    WebServiceCaptureMethod.FieldKey_RequestBody));
        }

        private static async Task<OperationResult<SazExportResult>>
            ExportAsync(
                string path,
                IDataTable table,
                string device)
        {
            var operation =
                new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                    new SazExportPlanRequest(
                        SazExportSource.Active(
                            table,
                            device,
                            CaptureTextSource.Of(table)),
                        SazExportKind.Plain,
                        null,
                        SazExportBudgets.Default),
                    OperationContext.None,
                    CancellationToken.None);
            Assert.True(prepared.Succeeded);
            await using var output =
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None);
            return await operation.ExecuteAsync(
                new SazExportRequest(
                    prepared.Value,
                    new SazExportDestination.ToStream(
                        output),
                    null),
                OperationContext.None,
                CancellationToken.None);
        }

        private static byte[] RequestEntry(
            string path)
        {
            using ZipArchive archive =
                ZipFile.OpenRead(
                    path);
            ZipArchiveEntry entry =
                Assert.Single(
                    archive.Entries,
                    candidate =>
                        candidate.FullName.EndsWith(
                            "_c.txt",
                            StringComparison.Ordinal));
            using Stream stream =
                entry.Open();
            using var bytes =
                new MemoryStream();
            stream.CopyTo(
                bytes);
            return bytes.ToArray();
        }

        private sealed class Context(
            WebServiceDeviceCaptureController controller) :
            ICaptureDeviceContext
        {
            public DeviceType DeviceType =>
                DeviceType.Unknown;

            public CaptureType CaptureType =>
                CaptureType.WebProxy;

            public string DeviceName =>
                controller.DeviceName;

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; } =
                true;

            public Color DeviceColor { get; set; }

            public IDeviceCaptureController CaptureController =>
                controller;
        }

        private static IDataTable CreateWebProxyTable(
            SqLtDatabase database) =>
            database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory(
                bool shortPath = false)
            {
                Path = shortPath
                    ? System.IO.Path.Join(
                        System.IO.Path.GetTempPath(),
                        "xmat-path-" +
                        Guid.NewGuid().ToString("N"))
                    : System.IO.Path.Join(
                        AppContext.BaseDirectory,
                        "DatabaseSaveFixtures",
                        Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
