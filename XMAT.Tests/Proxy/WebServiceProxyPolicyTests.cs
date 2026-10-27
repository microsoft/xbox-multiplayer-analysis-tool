// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    public class WebServiceProxyPolicyTests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public async Task RaiseReceivedWebRequestAsync_AndsPolicyAndObserverVetoes(
            bool policyAccepted,
            bool observerAccepted)
        {
            var proxy = new WebServiceProxy
            {
                WebRequestPolicyAsync = (_, _) => Task.FromResult(policyAccepted)
            };
            proxy.ReceivedWebRequest += (_, args) =>
                args.AcceptRequest = observerAccepted;
            proxy.ReceivedWebRequest += (_, args) =>
                args.AcceptRequest = true;

            bool accepted = await proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: new ClientRequest(),
                cancellationToken: CancellationToken.None);

            Assert.Equal(policyAccepted && observerAccepted, accepted);
        }
    }
}
