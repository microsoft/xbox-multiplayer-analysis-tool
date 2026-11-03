// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureEtlImportTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void CompleteOfflineReadsReplaceOnlyTheirScopeAndPreserveUnrelatedPacketsAndSelection()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, DateTime.UtcNow);
                var model = new CaptureAppModel { ActiveDatabase = database };
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.etl");
                File.WriteAllText(path, "reviewed offline input");
                var count = 2;
                var readerCalls = 0;
                Task Read(string frozenPath, Action<string> append, CancellationToken token)
                {
                    Assert.NotEqual(path, frozenPath);
                    Assert.Equal("reviewed offline input", File.ReadAllText(frozenPath));
                    readerCalls++;
                    for (var index = 1; index <= count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        append(NetworkTraceTestData.Event(index));
                    }
                    return Task.CompletedTask;
                }
                var imports = new CaptureEtlImports(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, model.CaptureFileInputs, Read);
                try
                {
                    var receipt = Complete(imports.ImportDesktopAsync(path));
                    Assert.True(receipt.Published);
                    Assert.Equal(2, receipt.RecordCount);
                    var target = Assert.Single(model.CaptureDeviceContexts);
                    Assert.True(target.IsReadOnly);
                    var trace = (NetworkTraceCaptureController)target.CaptureController;
                    Assert.Equal(2, trace.NetworkTracePackets.Count);
                    Assert.All(trace.NetworkTracePackets, packet => Assert.True(packet.RecordRowId > 0));
                    Assert.Null(model.SelectedDeviceContext);
                    Assert.False(trace.IsRunning);
                    Assert.Equal(3, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Count());
                    count = 1;
                    var next = Complete(imports.ImportDesktopAsync(path));
                    Assert.Equal(receipt.CaptureId, next.CaptureId);
                    Assert.False(next.AddedContext);
                    Assert.Single(trace.NetworkTracePackets);
                    count = 0;
                    Complete(imports.ImportDesktopAsync(path));
                    Assert.Empty(trace.NetworkTracePackets);
                    Assert.Single(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                    Assert.Equal(3, readerCalls);
                }
                finally
                {
                    Finish(imports.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("quota")]
        [InlineData("cancel")]
        public void IncompleteOrLimitedOfflineReadsCannotPublishPartialPackets(string failure)
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.etl");
                File.WriteAllText(path, "reviewed offline input");
                var failing = false;
                using var cancellation = new CancellationTokenSource();
                var imports = new CaptureEtlImports(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, model.CaptureFileInputs, (_, append, token) =>
                    {
                        append(NetworkTraceTestData.Event(1));
                        if (failing)
                        {
                            if (failure == "cancel")
                            { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                            else if (failure == "invalid")
                                append("{\"packet\":\"PRIVATE incomplete event\"}");
                            else
                                append(new string('x', 20000));
                        }
                        return Task.CompletedTask;
                    });
                try
                {
                    Complete(imports.ImportDesktopAsync(path));
                    var context = model.CaptureDeviceContexts.Single();
                    var trace = (NetworkTraceCaptureController)context.CaptureController;
                    var packets = trace.NetworkTracePackets;
                    var row = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single();
                    failing = true;
                    var input = Complete(model.CaptureFileInputs.RegisterAsync(path, 16384));
                    var state = Complete(imports.GetTargetAsync(input.FileName));
                    var task = imports.ImportInputAsync(input.Id, state, 16384, _ => Task.CompletedTask, cancellation.Token);
                    if (failure == "cancel")
                        Assert.ThrowsAny<OperationCanceledException>(() => Complete(task));
                    else if (failure == "quota")
                        Assert.Equal(QueryError.ResultLimit, Assert.Throws<QueryException>(() => Complete(task)).Error);
                    else
                        Assert.Equal(CaptureFileFailure.InvalidCapture, Assert.Throws<CaptureFileException>(() => Complete(task)).Failure);
                    Assert.Same(context, model.CaptureDeviceContexts.Single());
                    Assert.Same(packets, trace.NetworkTracePackets);
                    Assert.Equal(row.RowId, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                    Assert.False(model.IsCaptureDataOperationRunning);
                    Assert.False(model.IsCaptureFileLoadRunning);
                }
                finally
                {
                    Finish(imports.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public async Task OfflineReaderCancellationDoesNotInvokeAnyTraceOrElevationCommand()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "invalid.etl");
            await File.WriteAllTextAsync(path, "not an ETL file");
            var commands = 0;
            var reader = new LocalNetworkTraceEngineImpl(_ => { commands++; return Task.FromResult(0); },
                () => throw new InvalidOperationException("Offline reads must not check or request elevation."));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadEventsFromFileAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadEventsFromFileAsync(path));
            Assert.Equal(0, commands);
        }
    }
}
