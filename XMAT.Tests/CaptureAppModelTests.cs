// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.ComponentModel;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureAppModelTests
    {
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

            internal void SetIsRunning(bool isRunning)
            {
                (CaptureController as TestCaptureController).SetIsRunning(isRunning);
            }

        }

        private sealed class TestCaptureController : IDeviceCaptureController
        {
            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;
            public bool CloseCalled { get; private set; }

            internal TestCaptureController(bool isRunning)
            {
                IsRunning = isRunning;
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
                return Task.FromResult(true);
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
    }
}
