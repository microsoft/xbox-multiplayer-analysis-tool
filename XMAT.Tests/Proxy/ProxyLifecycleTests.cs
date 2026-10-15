// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    // Lifecycle behaviour of the proxy host (start, stop, failure reporting).
    // Startup failures must surface and roll back, and Running is reported only once both
    // listeners are ready.
    public class ProxyLifecycleTests
    {
        private const string LogName = "xmat-lifecycle-tests.log";

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

        private static async Task<bool> CanConnectAsync(int port)
        {
            using var tcp = new TcpClient();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task<string> GetThroughProxyAsync(int proxyPort, string url)
        {
            using var client = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true
            });
            return await client.GetStringAsync(url);
        }

        [Fact]
        public async Task Stop_BeforeStart_DoesNotThrow()
        {
            var proxy = new WebServiceProxy();

            await proxy.StopProxyAsync();

            Assert.False(proxy.IsProxyEnabled);
        }

        [Fact]
        public async Task Stop_Twice_DoesNotThrowAndStaysStopped()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));

            await proxy.StopProxyAsync();
            await proxy.StopProxyAsync();

            Assert.False(proxy.IsProxyEnabled);
        }

        [Fact]
        public async Task StartStop_Repeated_ServesTrafficEachCycleAndReleasesPort()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = GetFreePort();
            var proxy = new WebServiceProxy();

            for (int cycle = 0; cycle < 5; cycle++)
            {
                await proxy.StartProxyAsync(Options(port));
                try
                {
                    Assert.True(await WaitUntilAsync(() => CanConnectAsync(port), TimeSpan.FromSeconds(5)),
                        $"listener never became reachable in cycle {cycle}");
                    Assert.Equal("origin-ok", await GetThroughProxyAsync(port, $"http://127.0.0.1:{origin.Port}/c{cycle}"));
                }
                finally
                {
                    await proxy.StopProxyAsync();
                }

                Assert.False(proxy.IsProxyEnabled);
                Assert.False(await CanConnectAsync(port), $"port {port} still accepting after stop in cycle {cycle}");
            }
        }

        // The exclusive bind must not make an immediate same-port restart fail while the
        // previous run's connections are still lingering (TIME_WAIT / open keep-alive).
        [Fact]
        public async Task ImmediateSamePortRestart_WithTrafficAndOpenKeepAlive_Repeated10Times()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            using var client = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true
            });

            for (int cycle = 0; cycle < 10; cycle++)
            {
                await proxy.StartProxyAsync(Options(port));
                Assert.Equal("origin-ok", await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/r{cycle}"));
                await proxy.StopProxyAsync();
                Assert.Equal(ProxyState.Stopped, proxy.State);
            }
        }

        // Callers configure the device after StartProxy returns, so the listener
        // must already accept connections at that point.
        [Fact]
        public async Task Start_ListenerAcceptsConnectionsWhenCallReturns()
        {
            var proxy = new WebServiceProxy();
            for (int i = 0; i < 10; i++)
            {
                int port = GetFreePort();
                await proxy.StartProxyAsync(Options(port));
                try
                {
                    Assert.True(await CanConnectAsync(port), $"listener not ready immediately after start (iteration {i})");
                }
                finally
                {
                    await proxy.StopProxyAsync();
                }
            }
        }

        // A failed bind must never be reported as a running proxy: callers would
        // leave the PC or Xbox pointed at a dead listener.
        [Fact]
        public async Task Start_PortInUse_ThrowsAndLeavesProxyStopped()
        {
            using var squatter = new TcpListener(IPAddress.Any, 0);
            squatter.Start();
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;

            var proxy = new WebServiceProxy();

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => proxy.StartProxyAsync(Options(port)));

            // ShowProxyOperationError shows ex.Message to the user, so it must be informative.
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.False(proxy.IsProxyEnabled);
            Assert.Equal(ProxyState.Stopped, proxy.State);
        }

        // Kestrel's default bind succeeds over another application's loopback-only bind
        // of the same port; the exclusive public bind must refuse it.
        [Fact]
        public async Task Start_PortHeldOnLoopbackOnly_Throws()
        {
            using var squatter = new TcpListener(IPAddress.Loopback, 0);
            squatter.Start();
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;

            var proxy = new WebServiceProxy();

            await Assert.ThrowsAnyAsync<Exception>(() => proxy.StartProxyAsync(Options(port)));
            Assert.Equal(ProxyState.Stopped, proxy.State);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public async Task Start_InvalidPublicPort_IsRejected(int port)
        {
            var proxy = new WebServiceProxy();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => proxy.StartProxyAsync(Options(port)));

            Assert.Equal(ProxyState.Stopped, proxy.State);
        }

        [Fact]
        public async Task Start_WhileRunning_Throws()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => proxy.StartProxyAsync(Options(GetFreePort())));
                Assert.Equal(ProxyState.Running, proxy.State);
            }
            finally
            {
                await proxy.StopProxyAsync();
            }
        }

        // Failure after the intercepted host bound but before the public bind completes
        // must dispose the intercepted host, release the port and allow a clean restart.
        [Theory]
        [InlineData("InterceptedStarted")]
        [InlineData("PublicStarted")]
        public async Task Start_FailureAfterPhase_RollsBackAndAllowsRestart(string phase)
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == phase)
                        throw new InvalidOperationException("injected " + phase);
                }
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => proxy.StartProxyAsync(Options(port)));

            Assert.Equal("injected " + phase, ex.Message);
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(await CanConnectAsync(port));

            proxy.StartupFaultInjection = null;
            await proxy.StartProxyAsync(Options(port));
            try
            {
                Assert.True(await CanConnectAsync(port));
            }
            finally
            {
                await proxy.StopProxyAsync();
            }
        }

        // Regression: a cancellation callback that threw during a startup rollback aborted the
        // rollback, leaving the proxy Faulted with its listeners still bound.
        [Fact]
        public async Task Start_FailureWithThrowingCancellationCallback_StillRollsBack()
        {
            int port = GetFreePort();
            WebServiceProxy proxy = null;
            proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == "InterceptedStarted")
                    {
                        _ = proxy.RunCancellationToken.Register(
                            () => throw new InvalidOperationException("cancellation callback failed"));
                    }

                    if (p == "PublicStarted")
                        throw new InvalidOperationException("injected PublicStarted");
                }
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => proxy.StartProxyAsync(Options(port)));

            Assert.Equal("injected PublicStarted", ex.Message);
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);
            Assert.False(await CanConnectAsync(port));

            proxy.StartupFaultInjection = null;
            await proxy.StartProxyAsync(Options(port));
            await proxy.StopProxyAsync();
            Assert.Equal(ProxyState.Stopped, proxy.State);
        }

        [Fact]
        public async Task Start_Cancelled_RollsBackToStopped()
        {
            int port = GetFreePort();
            using var cts = new CancellationTokenSource();
            var proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == "InterceptedStarted")
                        cts.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => proxy.StartProxyAsync(Options(port), cts.Token));

            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task Stop_Concurrent_CoalescesAndEndsStopped()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => proxy.StopProxyAsync()));

            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task Stop_DuringStart_AbortsStartAndEndsStopped()
        {
            int port = GetFreePort();
            var reachedIntercepted = new TaskCompletionSource();
            WebServiceProxy proxy = null;
            proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == "InterceptedStarted")
                    {
                        // Held until the stop cancels the run, so no timing assumption is needed.
                        reachedIntercepted.TrySetResult();
                        proxy.RunCancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                    }
                }
            };

            Task start = Task.Run(() => proxy.StartProxyAsync(Options(port)));
            await reachedIntercepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task stop = proxy.StopProxyAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(await CanConnectAsync(port));
        }

        // Regression: a stop that arrives after start owns the gate, but before Starting is
        // published, used to see Stopped and return, leaving the new listener running.
        [Fact]
        public async Task Stop_WhileStartOwnsGateBeforeStartingPublished_StopsTheListener()
        {
            int port = GetFreePort();
            var atGate = new TaskCompletionSource();
            var release = new ManualResetEventSlim(false);
            var proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == "GateAcquired")
                    {
                        atGate.TrySetResult();
                        release.Wait(TimeSpan.FromSeconds(10));
                    }
                }
            };

            Task start = Task.Run(() => proxy.StartProxyAsync(Options(port)));
            await atGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task stop = proxy.StopProxyAsync();

            // The start still owns the gate, so a correct stop is queued behind it.
            Assert.False(stop.IsCompleted);

            release.Set();
            try
            {
                await start;
            }
            catch (OperationCanceledException)
            {
            }

            await stop.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);
            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task Restart_InsideProxyStopped_ThenStop_StopsTheNewListener()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            Task secondStop = null;
            proxy.ProxyStopped += (_, _) =>
            {
                if (secondStop != null)
                    return;

                // Runs after the first stop released the gate. The restart must not be joined
                // by (or stopped through) the old stop operation.
                proxy.StartProxyAsync(Options(port)).GetAwaiter().GetResult();
                secondStop = proxy.StopProxyAsync();
            };
            await proxy.StartProxyAsync(Options(port));

            await proxy.StopProxyAsync();
            Assert.NotNull(secondStop);
            await secondStop.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);
            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task Stop_DuringLoggerInitialization_AbortsStart()
        {
            int port = GetFreePort();
            var reached = new TaskCompletionSource();
            WebServiceProxy proxy = null;
            proxy = new WebServiceProxy
            {
                StartupFaultInjection = p =>
                {
                    if (p == "LoggerInitialized")
                    {
                        reached.TrySetResult();
                        proxy.RunCancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                    }
                }
            };

            Task start = Task.Run(() => proxy.StartProxyAsync(Options(port)));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task stop = proxy.StopProxyAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);
            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task Faulted_LogCleanupFailure_IsRetriedByNextStop()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            int closeAttempts = 0;
            proxy.LogCloseFaultInjection = () =>
            {
                if (closeAttempts++ == 0)
                    throw new IOException("log close failed");
            };

            await Assert.ThrowsAsync<IOException>(() => proxy.StopProxyAsync());
            Assert.Equal(ProxyState.Faulted, proxy.State);
            Assert.True(proxy.HasOutstandingResources);

            await proxy.StopProxyAsync();

            Assert.Equal(2, closeAttempts);
            Assert.Equal(ProxyState.Stopped, proxy.State);
            Assert.False(proxy.HasOutstandingResources);
            await proxy.StartProxyAsync(Options(port));
            await proxy.StopProxyAsync();
        }

        [Fact]
        public async Task Faulted_StopWithFailingCleanup_StaysFaulted_ThenRetrySucceeds()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            proxy.ShutdownFaultInjection = () => throw new InvalidOperationException("cleanup failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StopProxyAsync());
            Assert.Equal(ProxyState.Faulted, proxy.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StopProxyAsync());
            Assert.Equal(ProxyState.Faulted, proxy.State);
            Assert.NotNull(proxy.FaultException);

            proxy.ShutdownFaultInjection = null;
            await proxy.StopProxyAsync();
            Assert.Equal(ProxyState.Stopped, proxy.State);
        }

        [Fact]
        public async Task Faulted_StartIsRejectedUntilStopSucceeds()
        {
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            proxy.ShutdownFaultInjection = () => throw new InvalidOperationException("cleanup failed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StopProxyAsync());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => proxy.StartProxyAsync(Options(port)));
            Assert.Contains("StopProxyAsync", ex.Message);
            Assert.Equal(ProxyState.Faulted, proxy.State);

            proxy.ShutdownFaultInjection = null;
            await proxy.StopProxyAsync();
            await proxy.StartProxyAsync(Options(port));
            await proxy.StopProxyAsync();
        }

        [Fact]
        public async Task Stop_DuringActiveTraffic_CompletesPromptly()
        {
            using var release = new CancellationTokenSource();
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                try { await Task.Delay(Timeout.Infinite, release.Token); }
                catch (OperationCanceledException) { }
            });
            int port = GetFreePort();
            var proxy = new WebServiceProxy();
            await proxy.StartProxyAsync(Options(port));
            Assert.True(await WaitUntilAsync(() => CanConnectAsync(port), TimeSpan.FromSeconds(5)));

            Task<string> inFlight = GetThroughProxyAsync(port, $"http://127.0.0.1:{origin.Port}/slow");
            await Task.Delay(500);

            var sw = Stopwatch.StartNew();
            await proxy.StopProxyAsync().WaitAsync(TimeSpan.FromSeconds(15));
            sw.Stop();

            release.Cancel();
            Assert.False(proxy.IsProxyEnabled);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"stop took {sw.Elapsed}");
            await Task.WhenAny(inFlight, Task.Delay(2000));
        }

        private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (await condition())
                    return true;
                await Task.Delay(50);
            }
            return false;
        }
    }
}
