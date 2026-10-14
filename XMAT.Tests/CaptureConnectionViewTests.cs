// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class CaptureConnectionViewTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("Invalid device context")]
        [InlineData(42)]
        public void TryGetCaptureContext_RejectsMissingOrIncorrectContext(object dataContext)
        {
            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                dataContext,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_RejectsMissingController()
        {
            var context = new TestCaptureDeviceContext(null);

            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_RejectsOtherCaptureController()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC);
            var context = new TestCaptureDeviceContext(controller);

            Assert.False(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Null(captureDeviceContext);
            Assert.Null(captureController);
        }

        [Fact]
        public void TryGetCaptureContext_ReturnsWebProxyContextAndController()
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device",
                DeviceType.Unknown,
                true);
            var context = new TestCaptureDeviceContext(controller);

            Assert.True(CaptureConnectionView.TryGetCaptureContext(
                context,
                out var captureDeviceContext,
                out var captureController));

            Assert.Same(context, captureDeviceContext);
            Assert.Same(controller, captureController);
        }

        private sealed class TestCaptureDeviceContext : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }

            internal TestCaptureDeviceContext(IDeviceCaptureController captureController)
            {
                CaptureController = captureController;
            }
        }
    }
}
