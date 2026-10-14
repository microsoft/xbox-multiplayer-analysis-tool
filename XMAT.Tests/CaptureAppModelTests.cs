// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using XMAT.Models;
using XMAT.SharedInterfaces;

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
            model.CaptureDeviceContexts.Add(new TestDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestDeviceContext(second));

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
            model.CaptureDeviceContexts.Add(new TestDeviceContext(imported) { IsReadOnly = true });
            model.CaptureDeviceContexts.Add(new TestDeviceContext(active));

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
            model.CaptureDeviceContexts.Add(new TestDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestDeviceContext(second));
            model.CaptureDeviceContexts.Add(new TestDeviceContext(third));

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
            model.CaptureDeviceContexts.Add(new TestDeviceContext(controller));

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
            model.CaptureDeviceContexts.Add(new TestDeviceContext(first));
            model.CaptureDeviceContexts.Add(new TestDeviceContext(second));

            Assert.True(await model.CanCloseAsync());
            Assert.Equal(1, first.CloseCheckCount);
            Assert.Equal(1, second.CloseCheckCount);
        }

        private sealed class TestDeviceContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.XboxConsole;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "test-console";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; set; }

            public TestDeviceContext(IDeviceCaptureController controller)
            {
                CaptureController = controller;
            }
        }

        private sealed class TestCaptureController : IDeviceCaptureController
        {
            private readonly Func<Task<bool>> _canCloseAsync;

            public bool IsRunning => false;
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public int CloseCheckCount { get; private set; }

            public TestCaptureController(Func<Task<bool>> canCloseAsync)
            {
                _canCloseAsync = canCloseAsync;
            }

            public Task<bool> CanCloseAsync()
            {
                CloseCheckCount++;
                return _canCloseAsync();
            }

            public void Initialize() => throw new NotSupportedException();
            public void LoadCaptures(IEnumerable<IDataset> tables) => throw new NotSupportedException();
            public void ClearAllCaptures() => throw new NotSupportedException();
            public void Close() => throw new NotSupportedException();
        }
    }
}
