// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class CaptureExportSnapshotTests
    {
        [Fact]
        public void CreateExportSnapshot_FreezesSessionsAcrossAppendsAndProcessResolution()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());
            var controller = Controller(table);
            ProxyConnectionModel original = Add(controller, table, 1, complete: true);

            using ICaptureExportSnapshot snapshot =
                controller.CreateExportSnapshot("d1", AnalysisLimits.Default);
            CaptureExportSession frozen = Assert.Single(snapshot.Sessions);

            _ = Add(controller, table, 2, complete: true);
            table.UpdateRow(
                1,
                ClientProcessRecordCodec.ToFields(Process("Resolved.exe", 5000)).ToArray());
            original.SetClientProcess(Process("Resolved.exe", 5000));
            RawExportRow row = Assert.Single(snapshot.ReadChunk([Key(1)], CancellationToken.None));

            Assert.True(snapshot.IsLive);
            Assert.Equal("records", snapshot.InputKind);
            Assert.Single(snapshot.Sessions);
            Assert.Equal(Key(1), frozen.Key);
            Assert.Equal("Game.exe", frozen.Process.Name);
            Assert.Equal("Game.exe", row.Values[WebProxyCaptureSchema.ClientProcessName]);
            Assert.Equal("/api/1", frozen.Path);
            Assert.True(frozen.IsComplete);
        }

        [Fact]
        public void ReadChunk_RejectsPayloadMutationAndOmitsMissingRows()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());
            var controller = Controller(table);
            _ = Add(controller, table, 1, complete: true);
            _ = Add(controller, table, 2, complete: true);
            using ICaptureExportSnapshot snapshot =
                controller.CreateExportSnapshot("d1", AnalysisLimits.Default);

            table.UpdateRow(
                1,
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_RequestPath, "/mutated"));
            table.RemoveRowsWhere(
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, 2));

            Assert.Throws<InvalidOperationException>(
                () => snapshot.ReadChunk([Key(1)], CancellationToken.None));
            Assert.Empty(snapshot.ReadChunk([Key(2)], CancellationToken.None));
        }

        [Fact]
        public void CreateExportSnapshot_FreezesCompletionFactsForLiveRows()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());
            var controller = Controller(table);
            _ = Add(controller, table, 1, complete: true);
            _ = Add(controller, table, 2, complete: false);

            using ICaptureExportSnapshot snapshot =
                controller.CreateExportSnapshot("d1", AnalysisLimits.Default);

            Assert.Collection(
                snapshot.Sessions.OrderBy(session => session.Key.RowId),
                complete =>
                {
                    Assert.True(complete.IsComplete);
                    Assert.False(complete.NoRecordedResponse);
                },
                incomplete =>
                {
                    Assert.False(incomplete.IsComplete);
                    Assert.True(incomplete.NoRecordedResponse);
                });
        }

        [Fact]
        public async Task ReadChunk_UsesBoundedBatchesAndAllowsOneOversizedGuiRow()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());
            var controller = Controller(table);
            var batches = new List<(int Rows, long Bytes)>();
            controller.ExportReadBatchObserver = (rows, bytes) => batches.Add((rows, bytes));
            _ = Add(controller, table, 1, complete: true, responseBodyBytes: 24 * 1024 * 1024 + 1);
            for (var index = 2; index <= 65; index++)
            {
                _ = Add(controller, table, index, complete: true, responseBodyBytes: 256 * 1024);
            }
            using ICaptureExportSnapshot snapshot = new RelabeledSnapshot(
                controller.CreateExportSnapshot("d1", AnalysisLimits.Default),
                "xmatcap");
            var writer = new RecordingExportWriter();
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = new RecordingExportPublisher(),
                WriterFactory = (_, _, _, _) => writer,
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                new ExportCaptureRequest(
                    null,
                    snapshot,
                    null,
                    Selection(),
                    CaptureExportFormat.Xmatcap,
                    "oversized.xmatcap",
                    ReplaceExisting: false,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal("xmatcap", snapshot.InputKind);
            Assert.Equal(65, writer.Rows.Count);
            Assert.NotEmpty(batches);
            Assert.Contains(batches, batch =>
                batch.Rows == 1 && batch.Bytes > AnalysisLimits.Default.MaxEntryBytes);
            Assert.All(batches.Where(batch => batch.Rows > 1), batch =>
            {
                Assert.InRange(batch.Rows, 1, 64);
                Assert.InRange(batch.Bytes, 1, 8 * 1024 * 1024);
            });
        }

        private static WebServiceDeviceCaptureController Controller(IDataTable table)
        {
            var controller = new WebServiceDeviceCaptureController("Export PC", DeviceType.LocalPC, false);
            typeof(WebServiceDeviceCaptureController)
                .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, table);
            controller.IsRunning = true;
            return controller;
        }

        private static ProxyConnectionModel Add(
            WebServiceDeviceCaptureController controller,
            IDataTable table,
            int number,
            bool complete,
            int responseBodyBytes = 0)
        {
            DateTime requestTime = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc).AddSeconds(number);
            var values = new Dictionary<string, object>(Values(number), StringComparer.Ordinal)
            {
                [WebServiceCaptureMethod.FieldKey_RequestPath] = $"/api/{number}",
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] =
                    requestTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] =
                    complete
                        ? requestTime.AddMilliseconds(10).ToString(
                            "o",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty,
                [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] =
                    complete ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n" : string.Empty,
            };
            if (responseBodyBytes != 0)
            {
                values[WebServiceCaptureMethod.FieldKey_ResponseBody] =
                    Convert.ToBase64String(new byte[responseBodyBytes]);
            }
            long rowId = table.AddRow(
                WebProxyCaptureSchema.Fields.Select(field => values[field.Name]).ToArray());
            IDataRecord record = Assert.Single(
                table.Dataset.DataRecords,
                candidate => candidate.RowId == rowId);
            ProxyConnectionModel model = controller.ProxyConnections.AddFromDataRecord(record, false, Key(rowId));
            model.SetClientProcess(Process());
            return model;
        }

        private sealed class RelabeledSnapshot(
            ICaptureExportSnapshot inner,
            string inputKind) : ICaptureExportSnapshot
        {
            public IReadOnlyList<SqLtSchemaColumn> CaptureFields => inner.CaptureFields;
            public IReadOnlyList<SqLtSchemaColumn> DeviceFields => inner.DeviceFields;
            public IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors => inner.DeviceDescriptors;
            public IReadOnlyList<CaptureExportSession> Sessions => inner.Sessions;
            public bool IsLive => inner.IsLive;
            public string InputKind => inputKind;
            public SqLtFileIdentity? InputIdentity => inner.InputIdentity;
            public string InputSha256 => inner.InputSha256;
            public IReadOnlyList<RawExportRow> ReadChunk(
                IReadOnlyList<CaptureRowKey> keys,
                CancellationToken cancellationToken) =>
                inner.ReadChunk(keys, cancellationToken);
            public void ValidateDestination(string path) => inner.ValidateDestination(path);
            public void Dispose() => inner.Dispose();
        }
    }
}
