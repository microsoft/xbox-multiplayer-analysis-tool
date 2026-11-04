// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using XMAT.Operations.Processes;
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

        [Fact]
        public async Task VetoObserversReceiveIdentityWithoutChangingTheirDecision()
        {
            var proxy =
                new WebServiceProxy
                {
                    WebRequestPolicyAsync = (_, _) => Task.FromResult(false),
                };
            var process = new ClientProcessContext(
                Guid.NewGuid(),
                17,
                new IPEndPoint(IPAddress.Loopback, 50190),
                new IPEndPoint(IPAddress.Loopback, 8080),
                DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp());
            process.Publish(
                new ClientProcessInfo(
                    "blocked.exe",
                    9911,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    ClientProcessStatus.Local));
            ClientProcessContext observed = null;
            proxy.ReceivedWebRequest += (_, args) => observed = args.ProcessContext;

            bool accepted = await proxy.RaiseReceivedWebRequestAsync(
                1000,
                new ClientRequest(),
                CancellationToken.None,
                process);

            Assert.False(accepted);
            Assert.Same(process, observed);
        }
    }
}
