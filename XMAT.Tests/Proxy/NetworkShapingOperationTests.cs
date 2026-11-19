// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    public class NetworkShapingOperationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MissingRequestOrControllerReturnsInvalidInput(bool missingController)
        {
            var result = await new SetNetworkShapingOperation().ExecuteAsync(
                missingController ? new(null, NetworkShapingProfile.Off) : null,
                OperationContext.None, CancellationToken.None);
            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            Assert.Equal("capture.shaping.invalid", Assert.Single(result.Diagnostics).Code);
        }

        [Fact]
        public async Task ObserverFailureDoesNotMisreportTheAppliedPolicyOrSkipOtherObservers()
        {
            var controller = new NetworkShapingController();
            bool notified = false;
            controller.Changed += (_, _) => throw new InvalidOperationException("Synthetic observer failure.");
            controller.Changed += (_, _) => notified = true;
            var result = await new SetNetworkShapingOperation().ExecuteAsync(
                new(controller, NetworkShapingProfile.Preset("avg")),
                OperationContext.None, CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(NetworkShapingMode.Shape, controller.Current.Mode);
            Assert.True(notified);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "capture.shaping.notification-failed");
        }

        [Fact]
        public async Task FullHistoryIsReportedSeparatelyFromInvalidSettings()
        {
            var controller = new NetworkShapingController();
            var shape = new NetworkShapingProfile(NetworkShapingMode.Shape, DownKbps: 192);
            for (int count = 1; count < NetworkShapingController.MaximumHistory; count++)
            {
                controller.Apply(shape);
            }
            var result = await new SetNetworkShapingOperation().ExecuteAsync(
                new(controller, shape), OperationContext.None, CancellationToken.None);
            Assert.Equal(OperationOutcome.Conflict, result.Outcome);
            Assert.Equal(SetNetworkShapingOperation.HistoryFullCode, Assert.Single(result.Diagnostics).Code);
        }
    }
}
