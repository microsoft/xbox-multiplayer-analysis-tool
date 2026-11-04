// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Drawing;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    // Clearing a capture is a capture data operation: it holds the data-operation lease for exactly the duration of the
    // clear, so it never overlaps an analysis, a save or an export, and it releases the lease even when clearing fails.
    public sealed class CaptureClearLeaseTests
    {
        [Fact]
        public void Clear_HoldsTheDataOperationLeaseWhileItClears()
        {
            var model = new CaptureAppModel();
            bool? held = null;
            bool? overlappingOperation = null;
            bool? overlappingAnalysis = null;
            var controller = new ClearProbeController(() =>
            {
                held = model.IsCaptureDataOperationRunning;
                overlappingOperation = model.TryBeginCaptureDataOperation(out IDisposable operation);
                operation?.Dispose();
                overlappingAnalysis = model.TryBeginAnalysis(out IDisposable analysis);
                analysis?.Dispose();
            });
            Select(model, controller);

            Assert.True(model.TryClearAllCaptures(controller));

            Assert.Equal(1, controller.ClearCount);
            Assert.True(held, "Clear must hold the data-operation lease while it clears.");
            Assert.False(overlappingOperation, "A save or export must not start while a clear runs.");
            Assert.False(overlappingAnalysis, "An analysis must not start while a clear runs.");
            Assert.False(model.IsCaptureDataOperationRunning);
            Assert.True(model.CanModifyCaptureData);
        }

        [Fact]
        public void Clear_IsRefusedWhileAnAnalysisHoldsItsLease()
        {
            var model = new CaptureAppModel();
            var controller = new ClearProbeController();
            Select(model, controller);
            Assert.True(model.TryBeginAnalysis(out IDisposable analysis));

            using (analysis)
            {
                Assert.False(model.TryClearAllCaptures(controller));
            }

            Assert.Equal(0, controller.ClearCount);
        }

        // Save and both exports hold the data-operation lease while they write.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Clear_IsRefusedWhileASaveOrExportHoldsTheDataOperationLease(bool requireAllCapturesStopped)
        {
            var model = new CaptureAppModel();
            var controller = new ClearProbeController();
            Select(model, controller);
            Assert.True(model.TryBeginCaptureDataOperation(out IDisposable operation, requireAllCapturesStopped));

            using (operation)
            {
                Assert.False(model.TryClearAllCaptures(controller));
            }

            Assert.Equal(0, controller.ClearCount);
            Assert.True(model.TryClearAllCaptures(controller));
            Assert.Equal(1, controller.ClearCount);
        }

        [Fact]
        public void Clear_ReleasesTheLeaseWhenClearingFails()
        {
            var model = new CaptureAppModel();
            bool? held = null;
            var controller = new ClearProbeController(() =>
            {
                held = model.IsCaptureDataOperationRunning;
                throw new InvalidOperationException("The clear failed.");
            });
            Select(model, controller);

            Assert.Throws<InvalidOperationException>(() => model.TryClearAllCaptures(controller));

            Assert.True(held, "Clear must hold the data-operation lease while it clears.");
            Assert.False(model.IsCaptureDataOperationRunning);
            Assert.True(model.TryBeginCaptureDataOperation(out IDisposable next));
            next.Dispose();
        }

        private static void Select(CaptureAppModel model, IDeviceCaptureController controller)
        {
            var context = new ProbeContext(controller);
            model.CaptureDeviceContexts.Add(context);
            model.SelectedDeviceContext = context;
        }

        private sealed class ProbeContext(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Clear probe";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; } = controller;
        }

        // A stopped capture whose clear runs the given probe.
        private sealed class ClearProbeController(Action onClear = null) : IDeviceCaptureController
        {
            public event PropertyChangedEventHandler PropertyChanged
            {
                add { }
                remove { }
            }

            public bool IsRunning => false;
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public int ClearCount { get; private set; }

            public void Initialize()
            {
            }

            public void LoadCaptures(IEnumerable<IDataset> tables)
            {
            }

            public void ClearAllCaptures()
            {
                ClearCount++;
                onClear?.Invoke();
            }

            public Task<bool> CanCloseAsync() => Task.FromResult(true);

            public Task CloseAsync() => Task.CompletedTask;
        }
    }
}
