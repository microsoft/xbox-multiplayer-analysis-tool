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
        public void CaptureControllerReplacement_UntracksOldAndTracksNewController()
        {
            var model = new CaptureAppModel();
            var context = new TestCaptureDeviceContext(false);
            var originalController = context.TestController;
            model.CaptureDeviceContexts.Add(context);

            originalController.SetIsRunning(true);

            Assert.True(model.IsAnyCaptureRunning);

            var replacementController = context.ReplaceController(false);

            Assert.False(model.IsAnyCaptureRunning);

            originalController.SetIsRunning(false);
            originalController.SetIsRunning(true);

            Assert.False(model.IsAnyCaptureRunning);

            replacementController.SetIsRunning(true);

            Assert.True(model.IsAnyCaptureRunning);
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

        private sealed class TestCaptureDeviceContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController
            {
                get => _captureController;
                set
                {
                    _captureController = value;
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(CaptureController)));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;

            private IDeviceCaptureController _captureController;
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

            internal TestCaptureController ReplaceController(bool isRunning)
            {
                var controller = new TestCaptureController(isRunning);
                CaptureController = controller;
                return controller;
            }
        }

        private sealed class TestCaptureController : IDeviceCaptureController
        {
            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;

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
                return Task.CompletedTask;
            }
        }
    }
}
