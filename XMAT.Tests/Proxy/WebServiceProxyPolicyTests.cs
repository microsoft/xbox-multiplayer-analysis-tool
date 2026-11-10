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
        public async Task LiveSslRecheck_TracksBeforeInitialDecisionAndRemovesClosedConnections()
        {
            var proxy = new WebServiceProxy();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int aborted = 0;
            var request = new ClientRequest { Method = "CONNECT", Host = "blocked.test", Path = "blocked.test:443" };
            proxy.SslConnectionRequestPolicyAsync = async (_, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return true;
            };
            Guid registration = proxy.TrackSslConnection(
                1000, "127.0.0.1", request,
                () => Interlocked.Increment(ref aborted), CancellationToken.None);
            Task<bool> initialDecision = proxy.RaiseReceivedSslConnectionRequestAsync(
                1000, "127.0.0.1", request, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await proxy.RecheckSslConnectionsAsync((_, _) => Task.FromResult(false));
                Assert.Equal(1, aborted);
                proxy.UntrackSslConnection(registration);
                await proxy.RecheckSslConnectionsAsync((_, _) => Task.FromResult(false));
                Assert.Equal(1, aborted);
            }
            finally
            {
                release.TrySetResult(true);
                await initialDecision;
            }
        }

        [Fact]
        public async Task LiveSslRecheck_DoesNotAbortConnectionClosedWhilePolicyAwaits()
        {
            var proxy = new WebServiceProxy();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int aborted = 0;
            Guid registration = proxy.TrackSslConnection(
                1000, "127.0.0.1", new ClientRequest(),
                () => Interlocked.Increment(ref aborted), CancellationToken.None);
            Task recheck = proxy.RecheckSslConnectionsAsync(async (_, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return false;
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                proxy.UntrackSslConnection(registration);
            }
            finally
            {
                release.TrySetResult(true);
                await recheck;
            }
            Assert.Equal(0, aborted);
        }

        [Fact]
        public async Task LiveSslRecheck_ResetDisplayIds_DoesNotLoseOrConfuseActiveConnections()
        {
            var proxy = new WebServiceProxy();
            proxy.Reset();
            int blockedId = proxy.GetNextConnectionID();
            int blockedAborts = 0;
            Guid blocked = proxy.TrackSslConnection(
                blockedId, "127.0.0.1", new ClientRequest { Host = "blocked.test" },
                () => ++blockedAborts, CancellationToken.None);
            proxy.Reset();
            int allowedId = proxy.GetNextConnectionID();
            Assert.Equal(blockedId, allowedId);
            int allowedAborts = 0;
            Guid allowed = proxy.TrackSslConnection(
                allowedId, "127.0.0.1", new ClientRequest { Host = "allowed.test" },
                () => ++allowedAborts, CancellationToken.None);

            await proxy.RecheckSslConnectionsAsync(
                (args, _) => Task.FromResult(args.Request.Host != "blocked.test"));
            Assert.Equal(1, blockedAborts);
            Assert.Equal(0, allowedAborts);
            proxy.UntrackSslConnection(allowed);
            await proxy.RecheckSslConnectionsAsync((_, _) => Task.FromResult(false));
            Assert.Equal(2, blockedAborts);
            Assert.Equal(0, allowedAborts);
            proxy.UntrackSslConnection(blocked);
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
