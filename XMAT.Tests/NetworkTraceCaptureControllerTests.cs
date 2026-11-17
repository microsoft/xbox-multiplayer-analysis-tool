// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NetworkTraceCaptureControls;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkTraceCaptureControllerTests(CaptureUiApplicationFixture application)
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Theory]
        [InlineData("start")]
        [InlineData("stop")]
        [InlineData("download")]
        public void FailedTransition_BlocksCloseAndResumesAtFailedPhase(string phase)
        {
            application.Run(window =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine();
                factory.Engines.Enqueue(engine);
                var target = CreateTarget(model, factory);
                using var controller = target.Controller;
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var expected = new IOException("Trace operation failed.");
                switch (phase)
                {
                    case "start": engine.Start = () => pending.Task; break;
                    case "stop": engine.Stop = () => pending.Task; break;
                    case "download": engine.Download = _ => pending.Task; break;
                }

                var starting = phase == "start";
                if (!starting)
                    Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);
                var operation = model.CaptureOperations.SetRunningAsync(target, starting);
                WaitFor(() => phase switch
                {
                    "start" => engine.StartCount == 1,
                    "stop" => engine.StopCount == 1,
                    _ => engine.DownloadCount == 1
                });
                Assert.False(operation.IsCompleted);
                Assert.True(controller.IsTransitioning);
                Assert.True(controller.IsRunning);
                Assert.False(Complete(controller.CanCloseAsync()));
                Assert.Equal(phase == "download" ? 1 : 0, engine.Subscribers);

                pending.SetException(expected);
                Assert.Same(expected, Assert.Throws<IOException>(() => Complete(operation)));
                Assert.False(controller.IsTransitioning);
                Assert.True(controller.IsCapturing);
                Assert.True(controller.IsRunning);
                Assert.Equal(DeviceStatusEnum.RecoveryRequired, controller.DeviceStatus);
                Assert.Equal(phase == "download" ? CaptureTraceRecoveryPhase.Download : CaptureTraceRecoveryPhase.Stop,
                    controller.RecoveryPhase);
                Assert.Equal(phase != "download", controller.MayBeTracing);
                Assert.False(Complete(controller.CanCloseAsync()));
                Assert.Equal(CaptureOperationFailure.RecoveryRequired,
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)).Failure);
                Assert.Equal(0, engine.Subscribers);

                engine.Stop = () => Task.CompletedTask;
                engine.Download = _ => Task.CompletedTask;
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(1, factory.ConnectCount);
                Assert.Equal(1, factory.DisconnectCount);
                Assert.Equal(1, engine.StartCount);
                Assert.Equal(phase == "stop" ? 2 : 1, engine.StopCount);
                Assert.Equal(phase == "download" ? 2 : 1, engine.DownloadCount);
                Assert.Equal(0, engine.Subscribers);
                Assert.False(controller.IsRunning);
                Assert.False(controller.RecoveryRequired);
                Assert.True(Complete(controller.CanCloseAsync()));
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConsoleDisconnect_BlocksCloseUntilCleanupCompletes(bool disconnectFails)
        {
            application.Run(window =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine();
                var nextEngine = new FakeTraceEngine();
                factory.Engines.Enqueue(engine);
                factory.Engines.Enqueue(nextEngine);
                var target = CreateTarget(model, factory, DeviceType.XboxConsole);
                using var controller = target.Controller;
                var errors = new List<Exception>();
                var view = new NetworkTraceCaptureView(model, errors.Add) { DataContext = target };
                WpfTestHost.Show(window, view);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);

                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                factory.BeforeDisconnect = () => pending.Task;
                var operation = model.CaptureOperations.SetRunningAsync(target, false);
                WaitFor(() => factory.DisconnectCount == 1);
                Assert.False(operation.IsCompleted);
                Assert.Equal(DeviceStatusEnum.Disconnecting, controller.DeviceStatus);
                Assert.True(controller.IsRunning);
                Assert.False(controller.MayBeTracing);
                Assert.False(Complete(controller.CanCloseAsync()));
                Assert.False(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)).Failure);
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(model.CaptureOperations.SetRunningAsync(target, false)).Failure);
                Assert.Equal(0, engine.Subscribers);

                factory.BeforeDisconnect = () => Task.CompletedTask;
                if (disconnectFails)
                {
                    var expected = new IOException("Disconnect failed.");
                    pending.SetException(expected);
                    Assert.Same(expected, Assert.Throws<IOException>(() => Complete(operation)));
                    Assert.Equal(CaptureTraceRecoveryPhase.Disconnect, controller.RecoveryPhase);
                    Assert.False(Complete(controller.CanCloseAsync()));
                    Assert.True(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                    ExecuteCaptureCommand(view);
                    WaitFor(() => !controller.IsTransitioning &&
                        (controller.DeviceStatus == DeviceStatusEnum.Idle || errors.Count > 0));
                }
                else
                {
                    pending.SetResult();
                    Assert.True(Complete(operation).Succeeded);
                }

                Assert.Empty(errors);
                Assert.Equal(1, engine.StopCount);
                Assert.Equal(1, engine.DownloadCount);
                Assert.Equal(disconnectFails ? 2 : 1, factory.DisconnectCount);
                Assert.True(Complete(controller.CanCloseAsync()));
                Assert.True(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(2, factory.ConnectCount);
                Assert.Equal(1, nextEngine.StartCount);
                Assert.Equal(0, nextEngine.Subscribers);
            });
        }

        [Fact]
        public void DeviceStatusChange_RefreshesCaptureCommandAvailability()
        {
            application.Run(window =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = CreateTarget(model, new FakeTraceEngineFactory());
                using var controller = target.Controller;
                var view = new NetworkTraceCaptureView(model) { DataContext = target };
                WpfTestHost.Show(window, view);
                var toolbar = ((Grid)view.Content).Children.OfType<ToolBar>().Single();
                var button = toolbar.Items.OfType<ToggleButton>().Single();
                Assert.True(button.IsEnabled);

                controller.DeviceStatus = DeviceStatusEnum.Disconnecting;
                WpfTestHost.Pump();
                Assert.False(button.IsEnabled);

                controller.DeviceStatus = DeviceStatusEnum.Idle;
                WpfTestHost.Pump();
                Assert.True(button.IsEnabled);
            });
        }

        [Fact]
        public void PartialDownload_UpdatesPacketsAndFiltersOnlyAfterRecoveryCommandSucceeds()
        {
            application.Run(window =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine();
                factory.Engines.Enqueue(engine);
                var target = CreateTarget(model, factory);
                using var controller = target.Controller;
                var table = database.TableByName(NetworkTraceCaptureMethod.TableName);
                var expected = new IOException("Download incomplete.");
                engine.Download = source =>
                {
                    source.Emit(NetworkTraceTestData.Event(1));
                    return Task.FromException(expected);
                };
                var errors = new List<Exception>();
                var view = new NetworkTraceCaptureView(model, errors.Add) { DataContext = target };
                WpfTestHost.Show(window, view);
                var grid = (DataGrid)view.FindName("PacketList");

                ExecuteCaptureCommand(view);
                WaitFor(() => !controller.IsTransitioning &&
                    (controller.DeviceStatus == DeviceStatusEnum.Tracing || errors.Count > 0));
                Assert.Empty(errors);
                ExecuteCaptureCommand(view);
                WaitFor(() => errors.Count > 0);

                Assert.Same(expected, Assert.Single(errors));
                Assert.Equal(CaptureTraceRecoveryPhase.Download, controller.RecoveryPhase);
                Assert.False(controller.MayBeTracing);
                Assert.Empty(table.Dataset.DataRecords);
                Assert.Empty(controller.NetworkTracePackets);
                Assert.Empty(grid.Items);
                Assert.Empty(controller.PidFilterList);
                Assert.Empty(controller.TidFilterList);
                Assert.Equal(0, engine.Subscribers);

                engine.Download = source =>
                {
                    source.Emit(NetworkTraceTestData.Event(2));
                    return Task.CompletedTask;
                };
                ExecuteCaptureCommand(view);
                WaitFor(() => !controller.IsTransitioning && controller.DeviceStatus == DeviceStatusEnum.Idle);

                Assert.Single(errors);
                Assert.Single(table.Dataset.DataRecords);
                var packet = Assert.Single(controller.NetworkTracePackets);
                Assert.Equal(2, packet.Timestamp.Second);
                Assert.Same(packet, Assert.Single(view.NetworkTracePackets));
                Assert.Same(grid.Items, controller.FilteredItems);
                Assert.Same(packet, Assert.Single(controller.FilteredItems.Cast<object>()));
                Assert.Equal("1", Assert.Single(controller.PidFilterList).Text);
                Assert.Equal("2", Assert.Single(controller.TidFilterList).Text);
                Assert.True(controller.HaveTraces);
                Assert.False(controller.IsRunning);
                Assert.False(controller.RecoveryRequired);
                Assert.Equal(1, engine.StopCount);
                Assert.Equal(2, engine.DownloadCount);
                Assert.Equal(0, engine.Subscribers);
            });
        }

        private static TraceCaptureContext CreateTarget(
            CaptureAppModel model, FakeTraceEngineFactory factory, DeviceType deviceType = DeviceType.LocalPC)
        {
            var target = new TraceCaptureContext(factory, deviceType: deviceType);
            target.Controller.Initialize(model.ActiveDatabase.TableByName(NetworkTraceCaptureMethod.TableName));
            model.CaptureDeviceContexts.Add(target);
            return target;
        }

        private static void ExecuteCaptureCommand(NetworkTraceCaptureView view)
        {
            Assert.True(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
            NetworkTraceCommands.StartStopCapture.Execute(null, view);
        }

        private static T Complete<T>(Task<T> task) => WpfTestHost.Complete(task, Timeout);

        private static void WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < Timeout)
                WpfTestHost.Pump();
            Assert.True(condition(), "The trace transition did not reach the expected state.");
        }
    }
}
