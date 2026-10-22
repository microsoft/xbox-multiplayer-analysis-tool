// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using CaptureAnalysisEngine;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class CaptureAppModelTests
    {
        [Fact]
        public async Task CanCloseAsync_WithNoDevices_ReturnsTrue()
        {
            var model = new CaptureAppModel();

            Assert.True(await model.CanCloseAsync());
        }

        [Fact]
        public async Task CanCloseAsync_ChecksWritableDevicesSequentially()
        {
            var firstResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TestCaptureController(() => firstResult.Task);
            var second = new TestCaptureController(() => Task.FromResult(true));
            var model = new CaptureAppModel();
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(second));

            var closeTask = model.CanCloseAsync();

            Assert.False(closeTask.IsCompleted);
            Assert.Equal(1, first.CloseCheckCount);
            Assert.Equal(0, second.CloseCheckCount);

            firstResult.SetResult(true);

            Assert.True(await closeTask);
            Assert.Equal(1, second.CloseCheckCount);
            Assert.Equal(2, model.CaptureDeviceContexts.Count);
        }

        [Fact]
        public async Task CanCloseAsync_SkipsReadOnlyCaptures()
        {
            var imported = new TestCaptureController(() => throw new InvalidOperationException("Read-only capture checked."));
            var active = new TestCaptureController(() => Task.FromResult(true));
            var model = new CaptureAppModel();
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(imported) { IsReadOnly = true });
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(active));

            Assert.True(await model.CanCloseAsync());
            Assert.Equal(0, imported.CloseCheckCount);
            Assert.Equal(1, active.CloseCheckCount);
        }

        [Fact]
        public async Task CanCloseAsync_StopsWhenDeviceCannotClose()
        {
            var first = new TestCaptureController(() => Task.FromResult(true));
            var second = new TestCaptureController(() => Task.FromResult(false));
            var third = new TestCaptureController(() => Task.FromResult(true));
            var model = new CaptureAppModel();
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(second));
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(third));

            Assert.False(await model.CanCloseAsync());
            Assert.Equal(1, first.CloseCheckCount);
            Assert.Equal(1, second.CloseCheckCount);
            Assert.Equal(0, third.CloseCheckCount);
            Assert.Equal(3, model.CaptureDeviceContexts.Count);
        }

        [Fact]
        public async Task CanCloseAsync_PropagatesCloseCheckErrors()
        {
            var expected = new InvalidOperationException("Close check failed.");
            var controller = new TestCaptureController(() => Task.FromException<bool>(expected));
            var model = new CaptureAppModel();
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(controller));

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(model.CanCloseAsync);

            Assert.Same(expected, actual);
        }

        [Fact]
        public async Task CanCloseAsync_UsesSnapshotOfDeviceContexts()
        {
            var model = new CaptureAppModel();
            var first = new TestCaptureController(() =>
            {
                model.CaptureDeviceContexts.Clear();
                return Task.FromResult(true);
            });
            var second = new TestCaptureController(() => Task.FromResult(true));
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(second));

            Assert.True(await model.CanCloseAsync());
            Assert.Equal(1, first.CloseCheckCount);
            Assert.Equal(1, second.CloseCheckCount);
        }

        [Fact]
        public void CanAnalyzeCaptures_ReturnsFalseWhenAnotherCaptureIsRunning()
        {
            var model = new CaptureAppModel();
            var selectedContext = new TestCaptureDeviceContext(false);
            var runningContext = new TestCaptureDeviceContext(true);
            model.CaptureDeviceContexts.Add(selectedContext);
            model.CaptureDeviceContexts.Add(runningContext);
            model.SelectedDeviceContext = selectedContext;

            Assert.True(model.IsAnyCaptureRunning);
            Assert.False(model.CanAnalyzeCaptures);
            Assert.False(model.CanBeginCaptureDataOperation);
            Assert.False(model.TryBeginAnalysis(out var analysisLease));
            Assert.Null(analysisLease);
            Assert.False(model.TryBeginCaptureDataOperation(
                out var operationLease,
                true));
            Assert.Null(operationLease);

            runningContext.SetIsRunning(false);

            Assert.False(model.IsAnyCaptureRunning);
            Assert.True(model.CanAnalyzeCaptures);
        }

        [Fact]
        public void AnalysisLease_BlocksOverlappingAnalysisAndCaptureMutation()
        {
            var model = new CaptureAppModel();
            var selectedContext = new TestCaptureDeviceContext(false);
            model.CaptureDeviceContexts.Add(selectedContext);
            model.SelectedDeviceContext = selectedContext;

            Assert.True(model.CanAnalyzeCaptures);
            Assert.True(model.CanModifyCaptureData);
            Assert.True(model.TryBeginAnalysis(out var analysisLease));

            using (analysisLease)
            {
                Assert.True(model.IsAnalysisRunning);
                Assert.False(model.CanAnalyzeCaptures);
                Assert.False(model.CanModifyCaptureData);
                Assert.False(model.TryBeginAnalysis(out var overlappingLease));
                Assert.Null(overlappingLease);
                Assert.False(model.TryBeginCaptureDataOperation(out var operationLease));
                Assert.Null(operationLease);
            }

            Assert.False(model.IsAnalysisRunning);
            Assert.True(model.CanAnalyzeCaptures);
            Assert.True(model.CanModifyCaptureData);
        }

        [Fact]
        public void CaptureDataOperation_BlocksAnalysisUntilReleased()
        {
            var model = new CaptureAppModel();
            var selectedContext = new TestCaptureDeviceContext(false);
            model.CaptureDeviceContexts.Add(selectedContext);
            model.SelectedDeviceContext = selectedContext;

            Assert.True(model.TryBeginCaptureDataOperation(out var operationLease));

            using (operationLease)
            {
                Assert.True(model.IsCaptureDataOperationRunning);
                Assert.False(model.CanAnalyzeCaptures);
                Assert.False(model.CanModifyCaptureData);
                Assert.False(model.TryBeginAnalysis(out var analysisLease));
                Assert.Null(analysisLease);
            }

            Assert.False(model.IsCaptureDataOperationRunning);
            Assert.True(model.CanAnalyzeCaptures);
            Assert.True(model.CanModifyCaptureData);
        }

        [Fact]
        public void TryBeginAnalysis_RequiresSelectedDevice()
        {
            var model = new CaptureAppModel();

            Assert.False(model.CanAnalyzeCaptures);
            Assert.False(model.TryBeginAnalysis(out var analysisLease));
            Assert.Null(analysisLease);
        }

        [Fact]
        public void CaptureDeviceContexts_AddRemoveAndReset_UpdateRunningState()
        {
            var model = new CaptureAppModel();
            var removedContext = new TestCaptureDeviceContext(true);

            model.CaptureDeviceContexts.Add(removedContext);

            Assert.True(model.IsAnyCaptureRunning);

            model.CaptureDeviceContexts.Remove(removedContext);
            removedContext.SetIsRunning(false);
            removedContext.SetIsRunning(true);

            Assert.False(model.IsAnyCaptureRunning);

            var resetContext = new TestCaptureDeviceContext(true);
            model.CaptureDeviceContexts.Add(resetContext);

            Assert.True(model.IsAnyCaptureRunning);

            model.CaptureDeviceContexts.Clear();
            resetContext.SetIsRunning(false);
            resetContext.SetIsRunning(true);

            Assert.False(model.IsAnyCaptureRunning);
        }

        [Fact]
        public void BackgroundRunningStateChange_InvalidatesCommandsOnCapturedDispatcher()
        {
            Exception dispatcherException = null;
            using var completed = new ManualResetEventSlim();

            var dispatcherThread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherTimer timeoutTimer = null;
                EventHandler requeryHandler = null;

                try
                {
                    var model = new CaptureAppModel(dispatcher);
                    var context = new TestCaptureDeviceContext(false);
                    model.CaptureDeviceContexts.Add(context);
                    var dispatcherThreadId = Environment.CurrentManagedThreadId;

                    requeryHandler = (sender, args) =>
                    {
                        if (!model.IsAnyCaptureRunning)
                        {
                            return;
                        }

                        try
                        {
                            Assert.Equal(
                                dispatcherThreadId,
                                Environment.CurrentManagedThreadId);
                        }
                        catch (Exception ex)
                        {
                            dispatcherException = ex;
                        }
                        finally
                        {
                            completed.Set();
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    };
                    CommandManager.RequerySuggested += requeryHandler;

                    timeoutTimer = new DispatcherTimer(DispatcherPriority.Send)
                    {
                        Interval = TimeSpan.FromSeconds(5)
                    };
                    timeoutTimer.Tick += (sender, args) =>
                    {
                        dispatcherException ??= new TimeoutException(
                            "Command requery was not raised on the captured dispatcher.");
                        completed.Set();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    };
                    timeoutTimer.Start();

                    _ = Task.Run(() => context.SetIsRunning(true));
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    dispatcherException = ex;
                    completed.Set();
                }
                finally
                {
                    if (requeryHandler != null)
                    {
                        CommandManager.RequerySuggested -= requeryHandler;
                    }
                    timeoutTimer?.Stop();
                }
            });
            dispatcherThread.SetApartmentState(ApartmentState.STA);
            dispatcherThread.Start();

            Assert.True(
                completed.Wait(TimeSpan.FromSeconds(10)),
                "The dispatcher test did not complete.");
            Assert.True(
                dispatcherThread.Join(TimeSpan.FromSeconds(10)),
                "The dispatcher thread did not stop.");
            Assert.Null(dispatcherException);
        }

        [Fact]
        public async Task RemoveDeviceContextAsync_DoesNotDeleteReadOnlyDeviceRegistration()
        {
            var model = new CaptureAppModel();
            var captureDevicesTable = new TestDataTable();
            var context = new TestCaptureDeviceContext(false)
            {
                IsReadOnly = true
            };
            model.CaptureDevicesTable = captureDevicesTable;
            model.CaptureDeviceContexts.Add(context);

            await model.RemoveDeviceContextAsync(context);

            Assert.Equal(0, captureDevicesTable.RemoveCallCount);
            Assert.Empty(model.CaptureDeviceContexts);
            Assert.True(context.TestController.CloseCalled);
        }

        [Fact]
        public async Task RemoveDeviceContextAsync_DeletesOnlyMatchingCaptureRegistration()
        {
            var model = new CaptureAppModel();
            var captureDevicesTable = new TestDataTable();
            var context = new TestCaptureDeviceContext(false);
            model.CaptureDevicesTable = captureDevicesTable;
            model.CaptureDeviceContexts.Add(context);

            await model.RemoveDeviceContextAsync(context);

            var filters = Assert.Single(captureDevicesTable.RemovedFilters);
            Assert.Equal(3, filters.Length);
            Assert.Contains(filters, filter =>
                filter.Name == "DeviceType" &&
                (long)filter.Value == (long)context.DeviceType);
            Assert.Contains(filters, filter =>
                filter.Name == "DeviceName" &&
                (string)filter.Value == context.DeviceName);
            Assert.Contains(filters, filter =>
                filter.Name == "CaptureType" &&
                (long)filter.Value == (long)context.CaptureType);
            Assert.True(context.TestController.CloseCalled);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RunAnalysisAsync_ContainsAnalyzerExceptionsAndAllowsRetry(bool asynchronousFailure)
        {
            var model = new CaptureAppModel();
            var context = new TestCaptureDeviceContext(false);
            model.CaptureDeviceContexts.Add(context);
            model.SelectedDeviceContext = context;
            var expected = new AggregateException(new FormatException("Invalid capture status."));
            var analyzer = new TestAnalyzer(run =>
            {
                Assert.True(((AnalysisRunModel)run).IsProcessing);
                if (asynchronousFailure)
                {
                    return Task.FromException<ECaptureAnalyzerResult>(expected);
                }
                throw expected;
            });
            var analysisRun = model.AddAnalysisRun(analyzer);
            var errors = new List<string>();
            Assert.True(model.TryBeginAnalysis(out var lease));

            using (lease)
            {
                Assert.Equal(
                    ECaptureAnalyzerResult.UnknownError,
                    await model.RunAnalysisAsync(analysisRun, context.CaptureController, errors.Add));
                Assert.False(analysisRun.IsProcessing);
                Assert.Empty(model.AnalysisRuns);
                Assert.True(model.IsAnalysisRunning);
            }

            Assert.True(model.CanAnalyzeCaptures);
            Assert.Contains(expected.ToString(), Assert.Single(errors));
            var success = new TestAnalyzer(_ => Task.FromResult(ECaptureAnalyzerResult.Success));
            var retry = model.AddAnalysisRun(success);
            Assert.True(model.TryBeginAnalysis(out var retryLease));
            using (retryLease)
            {
                Assert.Equal(
                    ECaptureAnalyzerResult.Success,
                    await model.RunAnalysisAsync(retry, context.CaptureController));
            }
            Assert.Same(retry, Assert.Single(model.AnalysisRuns));
            Assert.False(retry.IsProcessing);
            Assert.True(model.CanAnalyzeCaptures);
        }

        [Theory]
        [InlineData(ECaptureAnalyzerResult.NoSuitableData)]
        [InlineData(ECaptureAnalyzerResult.UnknownError)]
        public async Task RunAnalysisAsync_RemovesFailedResults(ECaptureAnalyzerResult result)
        {
            var model = new CaptureAppModel();
            var analyzer = new TestAnalyzer(_ => Task.FromResult(result));
            var analysisRun = model.AddAnalysisRun(analyzer);

            Assert.Equal(result, await model.RunAnalysisAsync(analysisRun, new TestCaptureController(false)));
            Assert.False(analysisRun.IsProcessing);
            Assert.Empty(model.AnalysisRuns);
        }

        [Fact]
        public async Task RunAnalysisAsync_DoesNotSuppressFatalExceptions()
        {
            var model = new CaptureAppModel();
            var expected = new AggregateException(new OutOfMemoryException("Fatal test failure."));
            var analyzer = new TestAnalyzer(_ => Task.FromException<ECaptureAnalyzerResult>(expected));
            var analysisRun = model.AddAnalysisRun(analyzer);

            var actual = await Assert.ThrowsAsync<AggregateException>(
                () => model.RunAnalysisAsync(analysisRun, new TestCaptureController(false)));

            Assert.Same(expected, actual);
            Assert.False(analysisRun.IsProcessing);
            Assert.Empty(model.AnalysisRuns);
        }

        [Fact]
        public void TraceAnalyzer_EmptyCapture_ProducesNoReportGroups()
        {
            var analyzer = new TraceAnalyzer();
            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(new List<ServiceCallItem>());

            Assert.Empty(analyzer.Run(new RulesEngine(false), data, null));
        }

        [Fact]
        public async Task ImportDataCapturesAsync_FailedReimportPreservesExistingTabSelectionAndRows()
        {
            string path = CreateRepositoryLocalSazPath();
            string deviceName = $"Fiddler - {Path.GetFileName(path)}";
            using var database = CreateCaptureDatabase(out var table);
            AddCaptureRow(table, "Unrelated capture", "unrelated.example", "/keep", "unrelated");
            try
            {
                WriteSaz(path, "original.example", "/original", "original body");
                var model = new CaptureAppModel();
                var originalContext = new TestCaptureDeviceContext(false);
                model.CaptureDeviceContexts.Add(originalContext);
                model.SelectedDeviceContext = originalContext;
                var importer = CreateFiddlerImporter(table);

                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler,
                    path,
                    importer);

                var importedContext = model.SelectedDeviceContext;
                var importedController =
                    (WebServiceDeviceCaptureController)importedContext.CaptureController;
                var selectedConnection =
                    Assert.Single(importedController.ProxyConnections);
                importedController.SelectedConnectionModel = selectedConnection;
                Assert.Equal("original.example", selectedConnection.Host);
                AssertCaptureRow(
                    table,
                    deviceName,
                    "original.example",
                    "/original",
                    "original body");
                int originalRowId =
                    Assert.Single(RowsForDevice(table, deviceName)).RowId;

                model.SelectedDeviceContext = originalContext;
                await File.WriteAllTextAsync(path, "not a ZIP archive");

                await Assert.ThrowsAsync<InvalidDataException>(
                    () => model.ImportDataCapturesAsync(
                        ImportType.Fiddler,
                        path,
                        importer));

                Assert.Equal(2, model.CaptureDeviceContexts.Count);
                Assert.Same(originalContext, model.SelectedDeviceContext);
                Assert.Contains(importedContext, model.CaptureDeviceContexts);
                Assert.Equal("original.example", Assert.Single(importedController.ProxyConnections).Host);
                Assert.Same(
                    selectedConnection,
                    importedController.SelectedConnectionModel);
                AssertCaptureRow(
                    table,
                    deviceName,
                    "original.example",
                    "/original",
                    "original body");
                Assert.Equal(
                    originalRowId,
                    Assert.Single(RowsForDevice(table, deviceName)).RowId);
                AssertCaptureRow(
                    table,
                    "Unrelated capture",
                    "unrelated.example",
                    "/keep",
                    "unrelated");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task ImportDataCapturesAsync_SuccessfulReimportReplacesRowsWithoutAppending()
        {
            string path = CreateRepositoryLocalSazPath();
            string deviceName = $"Fiddler - {Path.GetFileName(path)}";
            using var database = CreateCaptureDatabase(out var table);
            AddCaptureRow(table, "Unrelated capture", "unrelated.example", "/keep", "unrelated");
            try
            {
                var model = new CaptureAppModel();
                var importer = CreateFiddlerImporter(table);
                WriteSaz(path, "original.example", "/original", "original body");

                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler,
                    path,
                    importer);

                var importedContext = model.SelectedDeviceContext;
                var controller =
                    (WebServiceDeviceCaptureController)importedContext.CaptureController;
                controller.SelectedConnectionModel =
                    Assert.Single(controller.ProxyConnections);
                var changedProperties = new List<string>();
                controller.PropertyChanged += (_, args) =>
                    changedProperties.Add(args.PropertyName);
                WriteSaz(path, "replacement.example", "/replacement", "replacement body");

                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler,
                    path,
                    importer);

                Assert.Same(importedContext, model.SelectedDeviceContext);
                Assert.Same(
                    importedContext,
                    model.GetDeviceContext(
                        DeviceType.Unknown,
                        deviceName,
                        CaptureType.WebProxy,
                        true));
                var visibleConnection = Assert.Single(controller.ProxyConnections);
                Assert.Equal("replacement.example", visibleConnection.Host);
                Assert.Equal("/replacement", visibleConnection.Path);
                Assert.Null(controller.SelectedConnectionModel);
                Assert.Contains(
                    nameof(WebServiceDeviceCaptureController.ProxyConnections),
                    changedProperties);
                AssertCaptureRow(
                    table,
                    deviceName,
                    "replacement.example",
                    "/replacement",
                    "replacement body");
                AssertCaptureRow(
                    table,
                    "Unrelated capture",
                    "unrelated.example",
                    "/keep",
                    "unrelated");
                Assert.Equal(2, table.Dataset.DataRecords.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task ImportDataCapturesAsync_FailedFreshImportRemovesAttemptAndRestoresSelection()
        {
            string path = CreateRepositoryLocalSazPath();
            string deviceName = $"Fiddler - {Path.GetFileName(path)}";
            using var database = CreateCaptureDatabase(out var table);
            try
            {
                WriteSaz(path, "new.example", "/new", "new body");
                var model = new CaptureAppModel();
                var originalContext = new TestCaptureDeviceContext(false);
                model.CaptureDeviceContexts.Add(originalContext);
                model.SelectedDeviceContext = originalContext;
                ExecuteSql(database,
                    "CREATE TRIGGER FailImport AFTER INSERT ON WebProxyConnections " +
                    "BEGIN SELECT RAISE(ABORT, 'Injected fresh import failure'); END;");
                model.CaptureDeviceContexts.CollectionChanged += (_, args) =>
                {
                    if (args.NewItems?.Count > 0)
                    {
                        model.SelectedDeviceContext =
                            (ICaptureDeviceContext)args.NewItems[0];
                    }
                };

                var actual = await Assert.ThrowsAsync<SqliteException>(
                    () => model.ImportDataCapturesAsync(
                        ImportType.Fiddler,
                        path,
                        CreateFiddlerImporter(table)));

                Assert.Contains("Injected fresh import failure", actual.Message);
                Assert.Same(originalContext, Assert.Single(model.CaptureDeviceContexts));
                Assert.Same(originalContext, model.SelectedDeviceContext);
                Assert.Empty(RowsForDevice(table, deviceName));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("AFTER DELETE", "OLD.RequestHost = 'original.example'")]
        [InlineData("BEFORE INSERT", "NEW.RequestHost = 'replacement.example'")]
        [InlineData("AFTER INSERT", "NEW.RequestHost = 'second-replacement.example'")]
        public async Task ImportDataCapturesAsync_PersistenceFailureRestoresExactOriginalRows(
            string triggerPoint, string condition)
        {
            string path = CreateRepositoryLocalSazPath();
            string deviceName = $"Fiddler - {Path.GetFileName(path)}";
            using var database = CreateCaptureDatabase(out var table);
            AddCaptureRow(table, "Unrelated capture", "unrelated.example", "/keep", "unrelated");
            try
            {
                var model = new CaptureAppModel();
                var importer = CreateFiddlerImporter(table);
                WriteSaz(path, "original.example", "/original", "original body");
                AppendSazFrame(path, 2, "second-original.example", "/second", "second original body");
                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler,
                    path,
                    importer);
                var importedContext = model.SelectedDeviceContext;
                var originalRows = SnapshotRows(table);
                var controller = (WebServiceDeviceCaptureController)importedContext.CaptureController;
                var originalConnections = controller.ProxyConnections;

                WriteSaz(path, "replacement.example", "/replacement", "replacement body");
                AppendSazFrame(path, 2, "second-replacement.example", "/second", "second replacement body");
                ExecuteSql(database,
                    $"CREATE TRIGGER FailImport {triggerPoint} ON WebProxyConnections " +
                    $"WHEN {condition} BEGIN SELECT RAISE(ABORT, 'Injected persistence failure'); END;");

                var actual = await Assert.ThrowsAsync<SqliteException>(
                    () => model.ImportDataCapturesAsync(
                        ImportType.Fiddler,
                        path,
                        importer));

                Assert.Contains("Injected persistence failure", actual.Message);
                Assert.Same(importedContext, model.SelectedDeviceContext);
                Assert.Same(originalConnections, controller.ProxyConnections);
                Assert.Equal(originalRows, SnapshotRows(table));
                Assert.Equal(3, table.Dataset.DataRecords.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReplaceImportedRows_InsertsFirstRowBeforeEnumeratingSecond()
        {
            const string deviceName = "Fiddler - streaming.saz";
            using var database = CreateCaptureDatabase(out var table);
            bool firstInsertObserved = false;
            database.CreateFunction("observe_import_insert", () =>
            {
                firstInsertObserved = true;
                return 0;
            });
            ExecuteSql(database,
                "CREATE TRIGGER ObserveImportInsert AFTER INSERT ON WebProxyConnections " +
                "BEGIN SELECT observe_import_insert(); END;");

            IEnumerable<object[]> Rows()
            {
                var first = CreateCaptureRowValues(
                    deviceName,
                    "first\"quoted.example",
                    "/first's",
                    "first body");
                first[15] = null;
                yield return first;

                Assert.True(
                    firstInsertObserved,
                    "The first row must be inserted before the second row is requested.");
                yield return CreateCaptureRowValues(
                    deviceName,
                    "second.example",
                    "/second",
                    "second body",
                    2);
            }

            var rowIds = ((IImportedCaptureTable)table).ReplaceImportedRows(
                new FieldValue<string>(
                    WebServiceCaptureMethod.FieldKey_DeviceName,
                    deviceName),
                Rows());

            var records = RowsForDevice(table, deviceName)
                .OrderBy(record => record.RowId)
                .ToList();
            Assert.Equal(2, records.Count);
            Assert.Equal(
                rowIds.ToArray(),
                records.Select(record => (long)record.RowId).ToArray());
            Assert.Equal(
                "first\"\"quoted.example",
                records[0].Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Equal(
                "/first's",
                records[0].Str(WebServiceCaptureMethod.FieldKey_RequestPath));
            Assert.Equal(
                string.Empty,
                records[0].Str(WebServiceCaptureMethod.FieldKey_ClientIP));
            Assert.Equal(
                2L,
                records[1].Int(WebServiceCaptureMethod.FieldKey_RequestNumber));
        }

        [Fact]
        public void ReplaceImportedRows_IteratorFailureRollsBackExactOriginalRows()
        {
            const string deviceName = "Fiddler - rollback.saz";
            using var database = CreateCaptureDatabase(out var table);
            AddCaptureRow(
                table,
                deviceName,
                "original.example",
                "/original",
                "original body");
            AddCaptureRow(
                table,
                "Unrelated capture",
                "unrelated.example",
                "/keep",
                "unrelated body");
            var originalRows = SnapshotRows(table);
            int insertsObserved = 0;
            database.CreateFunction("observe_failed_import_insert", () =>
            {
                insertsObserved++;
                return 0;
            });
            ExecuteSql(database,
                "CREATE TRIGGER ObserveFailedImportInsert AFTER INSERT ON WebProxyConnections " +
                "BEGIN SELECT observe_failed_import_insert(); END;");

            IEnumerable<object[]> Rows()
            {
                yield return CreateCaptureRowValues(
                    deviceName,
                    "replacement.example",
                    "/replacement",
                    "replacement body");
                throw new InvalidOperationException("Injected iterator failure.");
            }

            var actual = Assert.Throws<InvalidOperationException>(() =>
                ((IImportedCaptureTable)table).ReplaceImportedRows(
                    new FieldValue<string>(
                        WebServiceCaptureMethod.FieldKey_DeviceName,
                        deviceName),
                    Rows()));

            Assert.Equal("Injected iterator failure.", actual.Message);
            Assert.Equal(1, insertsObserved);
            Assert.Equal(originalRows, SnapshotRows(table));
        }

        private static Func<string, ICaptureDeviceContext, Task> CreateFiddlerImporter(
            IDataTable table)
        {
            return (path, context) =>
                WebServiceCaptureMethod.MethodInstance.ImportFromFiddlerAsync(
                    path,
                    context,
                    table);
        }

        [Fact]
        public async Task ImportDataCapturesAsync_PersistsInBackgroundAndPublishesFiltersBeforeConnections()
        {
            await OnDispatcherAsync(async () =>
            {
                string path = CreateRepositoryLocalSazPath();
                using var database = CreateCaptureDatabase(out var table);
                try
                {
                    var model = new CaptureAppModel();
                    WriteSaz(path, "original.example", "/original", "original body");
                    await model.ImportDataCapturesAsync(
                        ImportType.Fiddler, path, CreateFiddlerImporter(table));
                    var context = model.SelectedDeviceContext;
                    var controller = (WebServiceDeviceCaptureController)context.CaptureController;
                    int uiThreadId = Environment.CurrentManagedThreadId;
                    var properties = new List<string>();
                    var errors = new List<string>();
                    controller.PropertyChanged += (_, args) =>
                    {
                        Assert.Equal(uiThreadId, Environment.CurrentManagedThreadId);
                        properties.Add(args.PropertyName);
                        if (args.PropertyName == nameof(controller.HostFilterList))
                        {
                            throw new InvalidOperationException("Injected view notification failure.");
                        }
                    };
                    WriteSaz(path, "replacement.example", "/replacement", "replacement body");

                    await model.ImportDataCapturesAsync(ImportType.Fiddler, path,
                        (file, target) => WebServiceCaptureMethod.MethodInstance.ImportFromFiddlerAsync(
                            file, target, table, errors.Add));

                    Assert.NotEqual(uiThreadId, table.ImportThreadId);
                    Assert.Same(context, model.SelectedDeviceContext);
                    Assert.Equal(
                        new[] { nameof(controller.HostFilterList), nameof(controller.StatusFilterList),
                            nameof(controller.MethodFilterList), nameof(controller.SelectedConnectionModel),
                            nameof(controller.ProxyConnections) },
                        properties);
                    Assert.Contains("Injected view notification failure", Assert.Single(errors));
                    AssertCaptureRow(table, context.DeviceName,
                        "replacement.example", "/replacement", "replacement body");
                }
                finally
                {
                    File.Delete(path);
                }
            });
        }

        [Fact]
        public async Task ImportDataCapturesAsync_EmptyArchiveReplacesExistingRows()
        {
            string path = CreateRepositoryLocalSazPath();
            using var database = CreateCaptureDatabase(out var table);
            try
            {
                var model = new CaptureAppModel();
                WriteSaz(path, "original.example", "/original", "original body");
                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler, path, CreateFiddlerImporter(table));
                var context = model.SelectedDeviceContext;
                using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
                {
                }

                await model.ImportDataCapturesAsync(
                    ImportType.Fiddler, path, CreateFiddlerImporter(table));

                Assert.Same(context, model.SelectedDeviceContext);
                Assert.Empty(((WebServiceDeviceCaptureController)context.CaptureController).ProxyConnections);
                Assert.Empty(table.Dataset.DataRecords);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static async Task OnDispatcherAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await action();
                        completion.SetResult();
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    }
                }));
                Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Import test dispatcher did not stop.");
            }
        }

        private static SqliteConnection CreateCaptureDatabase(
            out ObservedImportDataTable table)
        {
            var database = new SqliteConnection("Data Source=:memory:");
            database.Open();
            table = new ObservedImportDataTable(
                new SqLtDataTable(database,
                    WebServiceCaptureMethod.TableName,
                    new IField[] {
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
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP) }));
            return database;
        }

        private static void ExecuteSql(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static object[][] SnapshotRows(IDataTable table)
        {
            var fields = table.Dataset.FieldDefs.ToList();
            return table.Dataset.DataRecords.OrderBy(record => record.RowId)
                .Select(record => new object[] { record.RowId }.Concat(fields.Select(field =>
                    field.ValueType == FieldValueType.Integer
                        ? (object)record.Int(field.Name)
                        : record.Str(field.Name))).ToArray())
                .ToArray();
        }

        private static IReadOnlyList<IDataRecord> RowsForDevice(
            IDataTable table,
            string deviceName)
        {
            return table.Dataset.DataRecordsWhen(
                new FieldValue<string>(
                    WebServiceCaptureMethod.FieldKey_DeviceName,
                    deviceName))
                .ToList();
        }

        private static string CreateRepositoryLocalSazPath()
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory,
                "CaptureAppModelFixtures");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"{Guid.NewGuid():N}.saz");
        }

        private static void WriteSaz(
            string path,
            string host,
            string requestPath,
            string responseBody)
        {
            using var archive = new ZipArchive(
                File.Create(path),
                ZipArchiveMode.Create);
            WriteSazFrame(archive, 1, host, requestPath, responseBody);
        }

        private static void AppendSazFrame(
            string path, int frame, string host, string requestPath, string responseBody)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
            WriteSazFrame(archive, frame, host, requestPath, responseBody);
        }

        private static void WriteSazFrame(
            ZipArchive archive, int frame, string host, string requestPath, string responseBody)
        {
            WriteArchiveEntry(
                archive,
                $"raw/{frame}_c.txt",
                $"GET https://{host}{requestPath} HTTP/1.1\r\n" +
                $"Host: {host}\r\n\r\n");
            WriteArchiveEntry(
                archive,
                $"raw/{frame}_s.txt",
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n\r\n" +
                responseBody);
            WriteArchiveEntry(
                archive,
                $"raw/{frame}_m.xml",
                "<Session>" +
                "<SessionTimers " +
                "ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                "ClientDoneResponse=\"2026-01-01T00:00:00.1000000Z\" />" +
                "<SessionFlags>" +
                "<SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" />" +
                "</SessionFlags>" +
                "</Session>");
        }

        private static void WriteArchiveEntry(
            ZipArchive archive,
            string entryName,
            string contents)
        {
            using var stream = archive.CreateEntry(entryName).Open();
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void AddCaptureRow(
            IDataTable table,
            string deviceName,
            string host,
            string requestPath,
            string responseBody)
        {
            table.AddRow(CreateCaptureRowValues(
                deviceName,
                host,
                requestPath,
                responseBody));
        }

        private static object[] CreateCaptureRowValues(
            string deviceName,
            string host,
            string requestPath,
            string responseBody,
            long requestNumber = 1)
        {
            return new object[]
            {
                deviceName,
                requestNumber,
                requestNumber,
                "2026-01-01T00:00:00.0000000Z",
                "2026-01-01T00:00:00.1000000Z",
                "https",
                host,
                "443",
                "GET",
                requestPath,
                "200",
                $"GET https://{host}{requestPath} HTTP/1.1\r\nHost: {host}\r\n\r\n",
                string.Empty,
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(responseBody)),
                "10.0.0.1"
            };
        }

        private static void AssertCaptureRow(
            IDataTable table,
            string deviceName,
            string expectedHost,
            string expectedPath,
            string expectedResponseBody)
        {
            var row = Assert.Single(RowsForDevice(table, deviceName));
            Assert.Equal(
                expectedHost,
                row.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Equal(
                expectedPath,
                row.Str(WebServiceCaptureMethod.FieldKey_RequestPath));
            Assert.Equal(
                expectedResponseBody,
                Encoding.UTF8.GetString(Convert.FromBase64String(
                    row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody))));
        }

        private sealed class TestAnalyzer : ICaptureAnalyzer
        {
            private readonly Func<ICaptureAnalysisRun, Task<ECaptureAnalyzerResult>> _run;

            internal TestAnalyzer(Func<ICaptureAnalysisRun, Task<ECaptureAnalyzerResult>> run)
            {
                _run = run;
            }

            public ICaptureMethod SupportedCaptureMethod => null;
            public string Description => "Test analyzer";
            public bool IsSelected { get; set; }
            public ICaptureAnalyzerPreferences PreferencesModel => null;
            public void Initialize(ICaptureAppModel appModel) { }
            public void Shutdown() { }
            public Task<ECaptureAnalyzerResult> RunAsync(
                ICaptureAnalysisRun analysisRun, IDeviceCaptureController captureController) => _run(analysisRun);
        }

        private sealed class TestCaptureDeviceContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; private set; }
            internal TestCaptureController TestController =>
                CaptureController as TestCaptureController;

            internal TestCaptureDeviceContext(bool isRunning)
            {
                CaptureController = new TestCaptureController(isRunning);
            }

            internal TestCaptureDeviceContext(TestCaptureController controller)
            {
                CaptureController = controller;
            }

            internal void SetIsRunning(bool isRunning)
            {
                (CaptureController as TestCaptureController).SetIsRunning(isRunning);
            }

        }

        private sealed class TestCaptureController : IDeviceCaptureController
        {
            private readonly Func<Task<bool>> _canCloseAsync;

            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;
            public int CloseCheckCount { get; private set; }
            public bool CloseCalled { get; private set; }

            internal TestCaptureController(Func<Task<bool>> canCloseAsync)
            {
                _canCloseAsync = canCloseAsync;
            }

            internal TestCaptureController(bool isRunning)
            {
                IsRunning = isRunning;
                _canCloseAsync = () => Task.FromResult(true);
            }

            internal void SetIsRunning(bool isRunning)
            {
                IsRunning = isRunning;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsRunning)));
            }

            public void Initialize()
            {
            }

            public void LoadCaptures(IEnumerable<IDataset> tables)
            {
            }

            public void ClearAllCaptures()
            {
            }

            public Task<bool> CanCloseAsync()
            {
                CloseCheckCount++;
                return _canCloseAsync();
            }

            public Task CloseAsync()
            {
                CloseCalled = true;
                return Task.CompletedTask;
            }
        }

        private sealed class TestDataTable : IDataTable
        {
            public event Action<IDataTable, long> RowAdded
            {
                add { }
                remove { }
            }
            public event Action<IDataTable, long> RowUpdated
            {
                add { }
                remove { }
            }
            public event Action<IDataTable, long> RowsRemoved
            {
                add { }
                remove { }
            }

            public string Name => "CaptureDevices";
            public IDataset Dataset => null;
            public int RemoveCallCount => RemovedFilters.Count;
            public List<IFieldValue[]> RemovedFilters { get; } = new();

            public IDataset Subset(params string[] fieldNames)
            {
                return null;
            }

            public long AddRow(params object[] fieldValueArgs)
            {
                return 0;
            }

            public void UpdateRow(long rowId, params IFieldValue[] fieldNamesAndValues)
            {
            }

            public void RemoveRowsWhere(params IFieldValue[] fieldNamesAndValues)
            {
                RemovedFilters.Add(fieldNamesAndValues);
            }
        }

        private sealed class ObservedImportDataTable : IDataTable, IImportedCaptureTable
        {
            private readonly IDataTable _inner;
            internal int ImportThreadId { get; private set; }

            public event Action<IDataTable, long> RowAdded
            {
                add => _inner.RowAdded += value;
                remove => _inner.RowAdded -= value;
            }
            public event Action<IDataTable, long> RowUpdated
            {
                add => _inner.RowUpdated += value;
                remove => _inner.RowUpdated -= value;
            }
            public event Action<IDataTable, long> RowsRemoved
            {
                add => _inner.RowsRemoved += value;
                remove => _inner.RowsRemoved -= value;
            }

            public string Name => _inner.Name;
            public IDataset Dataset => _inner.Dataset;

            internal ObservedImportDataTable(IDataTable inner)
            {
                _inner = inner;
            }

            public IReadOnlyList<long> ReplaceImportedRows(
                IFieldValue capture, IEnumerable<object[]> rows)
            {
                ImportThreadId = Environment.CurrentManagedThreadId;
                return ((IImportedCaptureTable)_inner).ReplaceImportedRows(capture, rows);
            }

            public IDataset Subset(params string[] fieldNames)
            {
                return _inner.Subset(fieldNames);
            }

            public long AddRow(params object[] fieldValueArgs)
            {
                return _inner.AddRow(fieldValueArgs);
            }

            public void UpdateRow(
                long rowId,
                params IFieldValue[] fieldNamesAndValues)
            {
                _inner.UpdateRow(rowId, fieldNamesAndValues);
            }

            public void RemoveRowsWhere(
                params IFieldValue[] fieldNamesAndValues)
            {
                _inner.RemoveRowsWhere(fieldNamesAndValues);
            }
        }
    }
}
