// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.NetworkTrace;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkTraceCaptureControllerTests
    {
        [Fact]
        public void IsCapturing_UpdatesIsRunning()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC);

            controller.IsCapturing = true;

            Assert.True(controller.IsRunning);

            controller.IsCapturing = false;

            Assert.False(controller.IsRunning);
        }

        [Fact]
        public async Task CanCloseAsync_ReturnsFalseWhileCaptureIsRunning()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC)
            {
                IsCapturing = true
            };

            Assert.False(await controller.CanCloseAsync());

            controller.IsCapturing = false;

            Assert.True(await controller.CanCloseAsync());
        }

        [Theory]
        [InlineData("start")]
        [InlineData("stop")]
        [InlineData("download")]
        public async Task CaptureFailure_ResetsStateAndAllowsClosing(string phase)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new TestTraceEngine { PendingPhase = phase, PendingOperation = pending.Task };
            var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
            {
                _networkTraceEngine = engine
            };
            var expected = new InvalidOperationException("Trace operation failed.");
            if (phase != "start")
            {
                await controller.StartCapture(null);
            }

            var operation = phase == "start" ? controller.StartCapture(null) : controller.StopCapture(null);
            Assert.False(operation.IsCompleted);
            Assert.True(controller.IsRunning);
            Assert.False(await controller.CanCloseAsync());

            pending.SetException(expected);
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);

            Assert.Same(expected, actual);
            Assert.False(controller.IsCapturing);
            Assert.False(controller.IsRunning);
            Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);
            Assert.True(await controller.CanCloseAsync());
            Assert.Equal(0, engine.SubscriberCount);

            engine.PendingPhase = null;
            await controller.StartCapture(null);
            Assert.True(controller.IsRunning);
            Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
            Assert.Equal(1, engine.SubscriberCount);
            await controller.StopCapture(null);
            Assert.True(await controller.CanCloseAsync());
            Assert.Equal(0, engine.SubscriberCount);
        }

        [Fact]
        public async Task Capture_CanRestartWithOneEventSubscription()
        {
            var engine = new TestTraceEngine();
            var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
            {
                _networkTraceEngine = engine
            };

            for (int capture = 0; capture < 2; capture++)
            {
                await controller.StartCapture(null);
                Assert.True(controller.IsCapturing);
                Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
                Assert.Equal(1, engine.SubscriberCount);
                Assert.False(await controller.CanCloseAsync());

                await controller.StopCapture(null);
                Assert.False(controller.IsCapturing);
                Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);
                Assert.Equal(0, engine.SubscriberCount);
                Assert.True(await controller.CanCloseAsync());
            }
        }

        private sealed class TestTraceEngine : INetworkTraceEngine
        {
            internal string PendingPhase { get; set; }
            internal Task PendingOperation { get; set; } = Task.CompletedTask;
            internal int SubscriberCount => EventRecordAvailable?.GetInvocationList().Length ?? 0;

            public event EventHandler<string> EventRecordAvailable;

            public Task StartPacketTraceAsync() =>
                PendingPhase == "start" ? PendingOperation : Task.CompletedTask;

            public Task StopPacketTraceAsync() =>
                PendingPhase == "stop" ? PendingOperation : Task.CompletedTask;

            public Task GetAllEventsAsync() =>
                PendingPhase == "download" ? PendingOperation : Task.CompletedTask;
        }
    }
}
