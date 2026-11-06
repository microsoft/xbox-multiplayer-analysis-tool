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
                controller.CreateExportSnapshot("d1");
            CaptureExportSession frozen = Assert.Single(snapshot.Sessions);

            _ = Add(controller, table, 2, complete: true);
            table.UpdateRow(
                1,
                ClientProcessRecordCodec.ToFields(Process("Resolved.exe", 5000)).ToArray());
            original.SetClientProcess(Process("Resolved.exe", 5000));
            RawExportRow row = Assert.Single(snapshot.ReadChunk([Key(1)], CancellationToken.None).Rows);

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
                controller.CreateExportSnapshot("d1");

            table.UpdateRow(
                1,
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_RequestPath, "/mutated"));
            table.RemoveRowsWhere(
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, 2));

            Assert.Throws<InvalidOperationException>(
                () => snapshot.ReadChunk([Key(1)], CancellationToken.None));
            Assert.Empty(snapshot.ReadChunk([Key(2)], CancellationToken.None).Rows);
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
                controller.CreateExportSnapshot("d1");

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

        [Theory]
        [InlineData(true, "columns")]
        [InlineData(false, "columns")]
        [InlineData(true, "freeze")]
        public void CreateExportSnapshot_ConstructionFailureDisposesOnlyOwnedDataset(bool ownsDataset, string failurePoint)
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());
            if (!ownsDataset)
            {
                Dictionary<string, object> values = Values(1);
                _ = table.AddRow(WebProxyCaptureSchema.Fields.Select(field => values[field.Name]).ToArray());
            }
            var probe = new ExportReadProbe(table);
            var payloadDisposals = 0;
            var metadataDisposals = 0;
            probe.DatasetDisposed = metadata => _ = metadata ? metadataDisposals++ : payloadDisposals++;
            IDataset borrowed = ownsDataset ? null : probe.Dataset;
            WebServiceDeviceCaptureController controller = ownsDataset
                ? Controller(probe)
                : new WebServiceDeviceCaptureController("Export PC", DeviceType.LocalPC, true);
            if (borrowed != null)
            {
                controller.LoadCaptureDataset(borrowed);
            }
            var failure = new InvalidOperationException("Injected capture-column failure.");
            if (failurePoint == "freeze")
                probe.MetadataReadFailure = failure;
            else
                probe.ColumnsFailure = failure;
            try
            {
                InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                    () => controller.CreateExportSnapshot("d1"));

                Assert.Same(failure, actual);
                Assert.Equal(ownsDataset ? 1 : 0, payloadDisposals);
                Assert.Equal(failurePoint == "freeze" ? 1 : 0, metadataDisposals);
            }
            finally
            {
                (borrowed as IDisposable)?.Dispose();
            }

            Assert.Equal(1, payloadDisposals);
        }

        [Fact]
        public async Task ReadChunk_UsesBoundedBatchesAndAllowsOneOversizedGuiRow()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            var probe = new ExportReadProbe(database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields()));
            IDataTable table = probe;
            var controller = Controller(table);
            _ = Add(controller, table, 1, complete: true, responseBodyBytes: 24 * 1024 * 1024 + 1);
            for (var index = 2; index <= 66; index++)
            {
                _ = Add(controller, table, index, complete: true);
            }
            for (var index = 67; index <= 130; index++)
            {
                _ = Add(controller, table, index, complete: true, responseBodyBytes: 256 * 1024);
            }
            probe.Reset();
            using var snapshot = new RelabeledSnapshot(
                controller.CreateExportSnapshot("d1"),
                "xmatcap",
                probe);
            Assert.All(probe.Reads, read => Assert.True(read.IsMetadata));
            Assert.Contains(probe.Reads, read => read.Route == ExportReadRoute.DataRecordsWhen);
            CaptureExportChunk first = snapshot.ReadChunk(
                snapshot.Sessions.Select(session => session.Key).ToArray(),
                CancellationToken.None);
            Assert.Single(first.ConsumedKeys);
            Assert.Single(first.Rows);
            snapshot.Reads.Clear();
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
            Assert.Equal(130, writer.Rows.Count);
            Assert.All(snapshot.Reads, AssertBoundedPayloadRead);
            Assert.Contains(snapshot.Reads, read =>
                read.Chunk.Rows.Count == 1 &&
                ReadBytes(read.Chunk) > AnalysisLimits.Default.MaxEntryBytes);
            Assert.Contains(snapshot.Reads, read =>
                read.Chunk.Rows.Count == 64 &&
                read.Reads.Sum(request => request.Materialized) == 64);
            Assert.All(snapshot.Reads.Where(read => read.Chunk.Rows.Count > 1), read =>
            {
                Assert.InRange(read.Chunk.Rows.Count, 1, 64);
                Assert.InRange(ReadBytes(read.Chunk), 1, 8 * 1024 * 1024);
            });
        }

        private static void AssertBoundedPayloadRead(ObservedRead read)
        {
            Assert.All(read.Reads, request =>
            {
                Assert.False(request.IsMetadata);
                Assert.Equal(ExportReadRoute.DataRecordsFromLimited, request.Route);
                Assert.Equal((int?)1, request.Limit);
            });
            int materialized = read.Reads.Sum(request => request.Materialized);
            Assert.InRange(materialized, read.Chunk.Rows.Count, read.Chunk.Rows.Count + 1);
        }

        private static long ReadBytes(CaptureExportChunk chunk) =>
            chunk.Rows.Sum(row => CaptureRawValues.Measure(row.Values));

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
                table.Dataset.DataRecordsFrom(rowId, 1),
                candidate => candidate.RowId == rowId);
            ProxyConnectionModel model = controller.ProxyConnections.AddFromDataRecord(record, false, Key(rowId));
            model.SetClientProcess(Process());
            return model;
        }

        private sealed record ObservedRead(
            CaptureExportChunk Chunk,
            IReadOnlyList<ExportReadRequest> Reads);

        private sealed class RelabeledSnapshot(
            ICaptureExportSnapshot inner,
            string inputKind,
            ExportReadProbe probe) : ICaptureExportSnapshot
        {
            internal List<ObservedRead> Reads { get; } = new();

            public IReadOnlyList<SqLtSchemaColumn> CaptureFields => inner.CaptureFields;
            public IReadOnlyList<SqLtSchemaColumn> DeviceFields => inner.DeviceFields;
            public IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors => inner.DeviceDescriptors;
            public IReadOnlyList<CaptureExportSession> Sessions => inner.Sessions;
            public bool IsLive => inner.IsLive;
            public string InputKind => inputKind;
            public SqLtFileIdentity? InputIdentity => inner.InputIdentity;
            public string InputSha256 => inner.InputSha256;
            public CaptureExportChunk ReadChunk(
                IReadOnlyList<CaptureRowKey> keys,
                CancellationToken cancellationToken)
            {
                probe.Reset();
                CaptureExportChunk chunk = inner.ReadChunk(keys, cancellationToken);
                Reads.Add(new ObservedRead(chunk, probe.Reads));
                return chunk;
            }

            public void ValidateDestination(string path) => inner.ValidateDestination(path);
            public void Dispose() => inner.Dispose();
        }
    }
}
