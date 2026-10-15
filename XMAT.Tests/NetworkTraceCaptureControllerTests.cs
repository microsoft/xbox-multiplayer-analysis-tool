// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
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
    }
}
