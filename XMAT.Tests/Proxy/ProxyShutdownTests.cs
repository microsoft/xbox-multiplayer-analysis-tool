// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    // These tests deliberately hold a thread-pool thread to simulate work that ignores
    // cancellation. Running them on their own keeps a held thread from stalling unrelated
    // tests on a small CI runner, and keeps parallel load from delaying their stop deadlines.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ThreadHoldingProxyTests
    {
        public const string Name = "Thread-holding proxy tests";
    }

    // Shared stop deadline, concurrent host shutdown and cleanup-failure handling.
    [Collection(ThreadHoldingProxyTests.Name)]
    public class ProxyShutdownTests
    {
        private const string LogName = "xmat-shutdown-tests.log";

        private sealed class SyncProgress : IProgress<WebProxyStopProgress>
        {
            private readonly List<(WebProxyStopPhase Phase, TimeSpan At)> _reports = new();
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public void Report(WebProxyStopProgress value)
            {
                lock (_reports)
                {
                    _reports.Add((value.Phase, _clock.Elapsed));
                }
            }

            public TimeSpan FirstReport(WebProxyStopPhase phase)
            {
                lock (_reports)
                {
                    return _reports.First(r => r.Phase == phase).At;
                }
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static WebServiceProxyOptions Options(int port) => new()
        {
            Port = port,
            LogFilePath = LogName,
            CurrentLogLevel = LogLevel.DEBUG
        };

        private static bool CanBindExclusively(int port)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                probe.Start();
                probe.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // A request whose capture callback ignores cancellation, so its connection stays open
        // until the stop deadline aborts it. Stop cancels idle and in-flight reads at once, so
        // only work like this reaches the deadline. Returns once the callback is blocked.
        private static async Task<TcpClient> OpenBlockedRequestAsync(
            WebServiceProxy proxy,
            int port,
            ManualResetEventSlim release)
        {
            var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.ReceivedWebRequest += (_, _) =>
            {
                requestSeen.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            };

            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var request = Encoding.ASCII.GetBytes("GET http://127.0.0.1:1/ HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            await client.GetStream().WriteAsync(request);
            await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return client;
        }

        [Fact]
        public async Task Stop_WithStuckConnection_HonoursSharedDeadlineAndAbortsIt()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy { StopDeadline = TimeSpan.FromSeconds(1) };
            await proxy.StartProxyAsync(Options(port));
            var release = new ManualResetEventSlim(false);
            using var stuck = await OpenBlockedRequestAsync(proxy, port, release);

            try
            {
                var clock = Stopwatch.StartNew();
                await proxy.StopProxyAsync();
                clock.Stop();

                Assert.Equal(ProxyState.Stopped, proxy.State);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"stop took {clock.Elapsed}");
                Assert.True(proxy.AbortedConnectionCount >= 1);
                Assert.True(CanBindExclusively(port), "port still held after stop");
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task Stop_StopsBothHostsConcurrently()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy { StopDeadline = TimeSpan.FromSeconds(2) };
            await proxy.StartProxyAsync(Options(port));
            var release = new ManualResetEventSlim(false);
            using var stuck = await OpenBlockedRequestAsync(proxy, port, release);
            var progress = new SyncProgress();

            // The blocked request holds the public host until the deadline, so a sequential
            // stop would report the intercepted phase seconds later.
            try
            {
                await proxy.StopProxyAsync(progress);
            }
            finally
            {
                release.Set();
            }

            var gap = (progress.FirstReport(WebProxyStopPhase.StoppingInterceptedListener) -
                progress.FirstReport(WebProxyStopPhase.StoppingPublicListener)).Duration();
            Assert.True(proxy.AbortedConnectionCount >= 1, "the blocked request did not hold the public host");
            Assert.True(gap < TimeSpan.FromSeconds(1), $"hosts stopped sequentially, gap {gap}");
        }

        [Fact]
        public async Task Stop_WithFailingCleanup_StaysFaulted_ThenRetrySucceeds()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            proxy.ShutdownFaultInjection = () => throw new InvalidOperationException("injected cleanup failure");

            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StopProxyAsync());

            Assert.Equal(ProxyState.Faulted, proxy.State);
            Assert.Contains("injected", proxy.FaultException.Message);

            proxy.ShutdownFaultInjection = null;
            await proxy.StopProxyAsync();
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.Null(proxy.FaultException);
            await proxy.StartProxyAsync(Options(port));
            await proxy.StopProxyAsync();
        }

        // Regression: a cancellation callback that threw escaped before teardown and before the
        // shared stop task was retired, so the hosts kept running and every later stop returned
        // the same failed task.
        [Fact]
        public async Task Stop_WhenCancellationCallbackThrows_StillCleansUpAndRetries()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            using var throwing = proxy.RunCancellationToken.Register(
                () => throw new InvalidOperationException("cancellation callback failed"));
            int cleanupFailures = 1;
            proxy.ShutdownFaultInjection = () =>
            {
                if (cleanupFailures-- > 0)
                    throw new InvalidOperationException("injected cleanup failure");
            };

            // The first stop reports the cleanup failure, not the callback's, and still stops both hosts.
            var first = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StopProxyAsync());
            Assert.Equal("injected cleanup failure", first.Message);
            Assert.Equal(ProxyState.Faulted, proxy.State);
            Assert.Null(proxy.PublicHost);
            Assert.Null(proxy.InterceptedHost);
            Assert.True(CanBindExclusively(port), "port still held after the first stop");

            // The next stop is a new operation, and it finishes the cleanup.
            await proxy.StopProxyAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);

            await proxy.StartProxyAsync(Options(port));
            await proxy.StopProxyAsync();
            Assert.Equal(ProxyState.Stopped, proxy.State);
        }
    }
}
