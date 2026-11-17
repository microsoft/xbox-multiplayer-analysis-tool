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
    }
}
