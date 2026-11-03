// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Controls;
using CaptureAnalysisEngine;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;
using static XMAT.Tests.CaptureDeviceTestData;
using Web = XMAT.WebServiceCapture.WebServiceCaptureMethod;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureInvestigationParityTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void LazyRawHttpFieldsAndLargeExactChunksRetainTheOriginalSnapshotAfterMutationAndReplacement()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                var row = CaptureInvestigationTestData.AddWeb(database, 1);
                var text = CaptureInvestigationTestData.Secret + string.Concat(Enumerable.Repeat("雪😀", 12000));
                database.TableByName(Web.TableName).UpdateRow(row, new FieldValue<string>(Web.FieldKey_RequestPath, text));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var result = Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.WebProxy, null, Options()));
                try
                {
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, JsonSerializer.Serialize(result));
                    database.TableByName(Web.TableName).UpdateRow(row, new FieldValue<string>(Web.FieldKey_RequestPath, "changed"));
                    CaptureInvestigationTestData.AddWeb(replacement, 1);
                    model.ActiveDatabase = replacement;
                    database.Dispose();
                    var fields = Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Record, row), 0, 100, 65536, Options()));
                    var path = fields.Rows.Single(field => Equals(field.Cells[0].Value, Web.FieldKey_RequestPath));
                    Assert.True(path.Cells[1].IsTruncated);
                    Assert.Equal(Encoding.UTF8.GetByteCount(text), path.Cells[1].ByteLength);
                    using var bytes = new MemoryStream();
                    QueryValueChunk chunk;
                    do
                    {
                        chunk = Complete(result.Details.ReadValueAsync(new(CaptureDetailKind.Record, row), path.Ordinal, 1,
                            bytes.Length, 997, Options()));
                        bytes.Write(chunk.Bytes);
                    } while (chunk.HasMore);
                    Assert.Equal(Encoding.UTF8.GetBytes(text), bytes.ToArray());
                    Assert.Equal(source, result.SourceId);
                    Assert.NotEqual(source, Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id);
                    Assert.Equal(CaptureDataError.DetailNotFound, Assert.Throws<CaptureDataException>(() =>
                        Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Record, row + 100), 0, 10, 65536, Options()))).Error);
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally { Finish(result.DisposeAsync().AsTask()); }
                Assert.Throws<ObjectDisposedException>(() =>
                    Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Record, row), 0, 1, 65536, Options())));
            });
        }

        [Fact]
        public void RawPacketFieldsUseTheOriginalStoredBytesRatherThanLiveRowsOrLossClaims()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var row = CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                var original = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().Str("Payload");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var result = Complete(model.CaptureInvestigation.SearchAsync(source, CaptureDataKind.NetworkTrace, null, Options()));
                try
                {
                    database.TableByName(NetworkTraceCaptureMethod.TableName).UpdateRow(row, new FieldValue<string>("Payload", "different"));
                    var fields = Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Record, row), 0, 100, 65536, Options()));
                    Assert.Equal(original, fields.Rows.Single(field => Equals(field.Cells[0].Value, "Payload")).Cells[1].Value);
                    Assert.DoesNotContain(original, JsonSerializer.Serialize(result.Records));
                }
                finally { Finish(result.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void WebSocketsAreLiveControllerOnlyExactEpochFilteredAndFrozenWithoutLeakingPayloads()
        {
            application.Run(_ =>
            {
                using var directory = new InvestigationDirectory();
                using var database = CaptureInvestigationTestData.WebDatabase();
                var saved = Path.Combine(directory.Path, "capture.xmatcap");
                database.SaveToFile(saved);
                using var loaded = new SqLtReadonlyDatabase(saved);
                var model = new CaptureAppModel { ActiveDatabase = database, LoadedDatabase = loaded };
                var context = new ScriptCaptureContext("test-device");
                model.CaptureDeviceContexts.Add(context);
                var message = new WebSocketMessageModel(new WebSocketMessageEventArgs
                {
                    Timestamp = CaptureInvestigationTestData.Start,
                    ConnectionID = 7,
                    RequestNumber = 9,
                    MessageType = WebSocketMessageType.Binary,
                    FromHost = true,
                    Message = [0, 255, 7, 8],
                    PayloadTruncated = true
                });
                context.Controller.WebSocketMessages.Add(message);
                var sources = Complete(model.CaptureQueries.GetSourcesAsync());
                var active = sources.Single(source => source.Kind == QuerySourceKind.Active).Id;
                var imported = sources.Single(source => source.Kind == QuerySourceKind.Loaded).Id;
                Assert.Empty(Complete(model.CaptureInvestigation.GetWebSocketsAsync(imported)));
                var available = Assert.Single(Complete(model.CaptureInvestigation.GetWebSocketsAsync(active)));
                var filter = new CaptureDataFilter(deviceName: "test-device", captureId: available.CaptureId,
                    captureEpoch: available.CaptureEpoch, connectionId: 7, fromHost: true, messageType: "Binary");
                var result = Complete(model.CaptureInvestigation.SearchAsync(active, CaptureDataKind.WebSocket, filter, Options()));
                try
                {
                    var summary = Assert.Single(result.Records);
                    Assert.Equal("ControllerSession", result.SnapshotKind);
                    Assert.True(summary.PayloadTruncated);
                    Assert.Equal(4, summary.PayloadBytes);
                    Assert.DoesNotContain(Convert.ToBase64String(message.Payload), JsonSerializer.Serialize(result));
                    message.Payload[0] = 123;
                    context.Controller.WebSocketMessages.Clear();
                    var fields = Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Record, summary.RecordId), 0, 100, 65536, Options()));
                    var payload = fields.Rows.Single(field => Equals(field.Cells[0].Value, "payload"));
                    var chunk = Complete(result.Details.ReadValueAsync(new(CaptureDetailKind.Record, summary.RecordId),
                        payload.Ordinal, 1, 0, 65536, Options()));
                    Assert.Equal(new byte[] { 0, 255, 7, 8 }, chunk.Bytes);
                    Assert.Equal(QueryValueKind.Blob, chunk.Kind);
                    context.Controller.IsRunning = true;
                    Assert.Equal(CaptureDataError.SourceChanged, Assert.Throws<CaptureDataException>(() =>
                        Complete(model.CaptureInvestigation.SearchAsync(active, CaptureDataKind.WebSocket, filter, Options()))).Error);
                    context.Controller.IsRunning = false;
                    Assert.Equal(CaptureDataError.UnsupportedData, Assert.Throws<CaptureDataException>(() =>
                        Complete(model.CaptureInvestigation.SearchAsync(imported, CaptureDataKind.WebSocket, filter, Options()))).Error);
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally { Finish(result.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void FindingDetailsExposeKnownRawFieldsButNeverSerializeAnArbitraryRuleResultGraph()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer(new CaptureInvestigationTestData.SensitiveRule()));
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var result = Complete(model.CaptureInvestigation.AnalyzeAsync(source, CaptureDataKind.WebProxy, null, Options()));
                try
                {
                    var finding = Assert.Single(result.Findings);
                    var page = Complete(result.Details.ReadPageAsync(new(CaptureDetailKind.Finding, finding.Id), 0, 100, 65536, Options()));
                    Assert.Contains(page.Rows, row => Equals(row.Cells[2].Value, "summary") &&
                        Equals(row.Cells[3].Value, CaptureInvestigationTestData.Secret));
                    Assert.Contains(page.Rows, row => Equals(row.Cells[2].Value, "resultAdapterAvailable") && Equals(row.Cells[3].Value, 0L));
                    Assert.DoesNotContain("private-value", JsonSerializer.Serialize(page));
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, JsonSerializer.Serialize(result));
                }
                finally { Finish(result.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void CompletedDesktopReportsKeepOriginalSourceAndAllConsoleManifestAfterSourceAndViewReplacement()
        {
            application.Run(_ =>
            {
                using var directory = new InvestigationDirectory();
                using var database = CaptureInvestigationTestData.WebDatabase();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                CaptureInvestigationTestData.AddWeb(database, 2, console: "10.0.0.2");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var context = new ScriptCaptureContext("test-device");
                model.CaptureDeviceContexts.Add(context);
                var trace = new TraceAnalyzer();
                trace.AddRule(new ThrottledCallsRule { Endpoint = "*" });
                trace.AddReports([new PerEndpointReport(), new StatsReport(), new CallReport()]);
                var analyzer = new XboxLiveCaptureAnalyzer(trace, model, directory.Path);
                model.CaptureAnalyzers.Add(analyzer);
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                var run = model.AddAnalysisRun(analyzer);
                Assert.True(model.TryBeginAnalysis(context, out var lease));
                string error = null;
                using (lease)
                    Assert.True(Complete(model.RunAnalysisAsync(run, context.Controller, message => error = message)) ==
                        ECaptureAnalyzerResult.Success, error);
                var resultModel = Assert.IsType<AnalysisResultsModel>(run.AnalysisData);
                Assert.True(resultModel.IsComplete);
                Assert.Equal(2, resultModel.CanonicalAnalysis.Analysis.Consoles.Count);
                var descriptor = Assert.Single(Complete(model.CaptureInvestigation.GetCompletedReportsAsync()));
                Assert.Equal(source, descriptor.SourceId);
                Assert.Equal(6, descriptor.Documents.Count);
                var navigation = new DesktopTargetProvenance(SourceId: source, SnapshotId: descriptor.SnapshotId, ReportId: descriptor.Id);
                Assert.True(Complete(model.RevealDesktopInvestigationReportAsync(navigation, candidate => ReferenceEquals(candidate, run))));
                Assert.False(Complete(model.RevealDesktopInvestigationReportAsync(navigation with { SnapshotId = Guid.NewGuid() }, candidate => true)));
                Assert.All(resultModel.ArtifactManifest, artifact => Assert.NotNull(artifact.Sha256));
                model.ActiveDatabase = replacement;
                var retained = Complete(model.CaptureInvestigation.OpenCompletedReportAsync(descriptor.Id));
                try
                {
                    model.RemoveAnalysisRun(run);
                    Assert.False(Complete(model.RevealDesktopInvestigationReportAsync(navigation, candidate => true)));
                    Assert.Empty(Complete(model.CaptureInvestigation.GetCompletedReportsAsync()));
                    Assert.Contains(Complete(model.GetInvestigationSourcesAsync()), source => source.Id == descriptor.SourceId);
                    var manifest = Complete(retained.Details.GetReportManifestAsync(4 * 1024 * 1024));
                    Assert.Equal(resultModel.ArtifactManifest.Select(artifact => artifact.RelativePath).Order(),
                        manifest.Select(artifact => artifact.RelativePath).Order());
                    var target = Path.Combine(directory.Path, "destination");
                    Directory.CreateDirectory(target);
                    var exports = new CaptureFileExports();
                    var folder = Complete(exports.RegisterFolderAsync(target));
                    var files = Complete(retained.Details.CreateReportFilesAsync(4 * 1024 * 1024));
                    try
                    {
                        var receipt = Complete(exports.SaveReportAsync(files, new(folder.Id, "report"), 4 * 1024 * 1024, _ => Task.CompletedTask));
                        Assert.True(receipt.Published);
                        var published = Path.Combine(target, "report");
                        Assert.True(File.Exists(Path.Combine(published, "console-0002-index.html")));
                        Assert.Equal(6, Directory.GetFiles(published, "console-*-*ReportDocument.json").Length);
                        foreach (var artifact in manifest)
                        {
                            using var stream = File.OpenRead(Path.Combine(published, artifact.RelativePath));
                            Assert.Equal(artifact.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
                        }
                        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "provenance.json")));
                        Assert.Equal(descriptor.SnapshotId, provenance.RootElement.GetProperty("SnapshotId").GetGuid());
                    }
                    finally { Finish(files.DisposeAsync().AsTask()); }
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally
                {
                    Finish(retained.DisposeAsync().AsTask());
                    Finish(model.DisposeInvestigationAsync());
                }
            });
        }

        [Fact]
        public void DesktopPacketReportsFreezeTheExactVisibleRowsAndOptionsWithCanonicalProvenance()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.NetworkTrace));
                var context = setup.Created.Single();
                CaptureInvestigationTestData.AddPacket(setup.Database, CaptureInvestigationTestData.Start, device: context.DeviceName);
                var selectedId = CaptureInvestigationTestData.AddPacket(setup.Database, CaptureInvestigationTestData.Start.AddMilliseconds(10),
                    device: context.DeviceName);
                var controller = Assert.IsType<NetworkTraceCaptureController>(context.CaptureController);
                var grid = new ListView();
                grid.Items.Add(new NetworkTracePacketDataModel(setup.Database.TableByName(NetworkTraceCaptureMethod.TableName)
                    .Dataset.DataRecords.Single(record => record.RowId == selectedId)));
                controller.FilteredItems = grid.Items;
                var analyzer = new NetworkTraceCaptureAnalyzer();
                analyzer.Initialize(setup.Model);
                setup.Model.CaptureAnalyzers.Add(analyzer);
                var run = setup.Model.AddAnalysisRun(analyzer);
                Assert.True(setup.Model.TryBeginAnalysis(context, out var lease));
                string error = null;
                using (lease)
                    Assert.True(Complete(setup.Model.RunAnalysisAsync(run, controller, message => error = message)) ==
                        ECaptureAnalyzerResult.Success, error);
                var model = Assert.IsType<NetworkTraceAnalyzerModel>(run.AnalysisData);
                Assert.Equal(1, model.CanonicalAnalysis.TotalPackets);
                var report = Assert.Single(Complete(setup.Model.CaptureInvestigation.GetCompletedReportsAsync()));
                Assert.Equal(CaptureDataKind.NetworkTrace, report.Kind);
                Assert.Equal(CaptureAnalysisScope.DesktopVisibleRecords, report.Scope);
                Assert.Equal(1, report.MatchedRecords);
                Assert.Equal(2, report.ScannedRecords);
                var retained = Complete(setup.Model.CaptureInvestigation.OpenCompletedReportAsync(report.Id));
                try
                {
                    Assert.Equal(selectedId, Assert.Single(retained.Records).RecordId);
                    setup.Model.RemoveAnalysisRun(run);
                    var document = Complete(retained.Details.ReadPageAsync(new(CaptureDetailKind.Report, 1), 0, 100, 65536, Options()));
                    using var json = JsonDocument.Parse((string)Assert.Single(document.Rows).Cells[2].Value);
                    Assert.Equal(1, json.RootElement.GetProperty("TotalPackets").GetInt32());
                    var manifest = Complete(retained.Details.GetReportManifestAsync(4 * 1024 * 1024));
                    Assert.Contains(manifest, artifact => artifact.RelativePath == "record-ids.json");
                    Assert.Null(setup.Model.SelectedDeviceContext);
                    Assert.Equal(0, setup.ConfigurationWrites);
                }
                finally
                {
                    Finish(retained.DisposeAsync().AsTask());
                    Finish(setup.Model.DisposeInvestigationAsync());
                }
            });
        }

        private static QueryExecutionOptions Options() => new(TimeSpan.FromSeconds(15), 4 * 1024 * 1024);
    }

    internal sealed class InvestigationDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(".test-artifacts", $"investigation-{Guid.NewGuid():N}"));
        internal InvestigationDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path))
                        Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
            }
        }
    }
}
