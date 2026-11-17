// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public sealed class ClientProcessResolverTests
    {
        [Fact]
        public void Constructor_RejectsUnboundedOrNonpositiveLimits()
        {
            var clock = new FakeClock();
            ClientProcessResolverOptions invalid =
                ClientProcessResolverOptions.Default with
                {
                    QueueCapacity = 0,
                };

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ClientProcessResolver(
                    new FakeTcpOwnerTableProvider(),
                    new FakeProcessIdentityProvider(),
                    new FakeLocalAddressProvider(_ => true),
                    clock,
                    invalid));
        }

        [Fact]
        public async Task Resolve_UsesTheReversedClientToProxyTuple()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50123, IPAddress.Loopback, 8080);
            var tcp = new FakeTcpOwnerTableProvider
            {
                Rows =
                [
                    new TcpOwnerRow(
                        context.ClientEndPoint,
                        context.ProxyEndPoint,
                        1234),
                    new TcpOwnerRow(
                        context.ProxyEndPoint,
                        context.ClientEndPoint,
                        Environment.ProcessId),
                ],
            };
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(
                1234,
                clock.UtcNow.AddMinutes(-2),
                "game.exe");
            await using var resolver =
                Resolver(
                    tcp,
                    processes,
                    clock);

            Assert.True(
                resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(
                new ClientProcessInfo(
                    "game.exe",
                    1234,
                    clock.UtcNow.AddMinutes(-2),
                    ClientProcessStatus.Local),
                context.Identity);
        }

        [Fact]
        public async Task Resolve_NormalizesIpv4MappedAddresses()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(
                clock,
                IPAddress.Parse("::ffff:127.0.0.1"),
                50124,
                IPAddress.Parse("::ffff:127.0.0.1"),
                8080);
            var tcp = new FakeTcpOwnerTableProvider
            {
                Rows =
                [
                    new TcpOwnerRow(
                        new IPEndPoint(
                            IPAddress.Loopback,
                            50124),
                        new IPEndPoint(
                            IPAddress.Loopback,
                            8080),
                        1235),
                ],
            };
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(
                1235,
                clock.UtcNow.AddMinutes(-2),
                "mapped.exe");
            await using var resolver =
                Resolver(
                    tcp,
                    processes,
                    clock);

            Assert.True(
                resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(
                "mapped.exe",
                context.Identity.Name);
        }

        [Fact]
        public async Task Resolve_UnsupportedIpv6DegradesWithoutNativeLookup()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(clock, IPAddress.IPv6Loopback, 50125, IPAddress.IPv6Loopback, 8080);
            var tcp = new FakeTcpOwnerTableProvider();
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock);

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(0, tcp.Calls);
            Assert.Equal(ClientProcessStatus.Unresolved, context.Identity.Status);
        }

        [Fact]
        public async Task Resolve_RejectsAProcessCreatedAfterAcceptTime()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50126, IPAddress.Loopback, 8080);
            var tcp = Rows(context, 2222);
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(2222, context.AcceptedAtUtc.AddSeconds(1), "reused.exe");
            await using var resolver = Resolver(tcp, processes, clock);

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(string.Empty, context.Identity.Name);
            Assert.Equal(2222, context.Identity.ProcessId);
            Assert.Null(context.Identity.StartTimeUtc);
            Assert.Equal(ClientProcessStatus.Unresolved, context.Identity.Status);
        }

        [Fact]
        public async Task Resolve_AccessDeniedUsesRateLimitedSnapshotName()
        {
            var clock = new FakeClock();
            ClientProcessContext first = Context(clock, IPAddress.Loopback, 50127, IPAddress.Loopback, 8080);
            ClientProcessContext second = Context(clock, IPAddress.Loopback, 50128, IPAddress.Loopback, 8080);
            var tcp = new FakeTcpOwnerTableProvider
            {
                Rows =
                [
                    new TcpOwnerRow(
                        first.ClientEndPoint,
                        first.ProxyEndPoint,
                        3333),
                    new TcpOwnerRow(
                        second.ClientEndPoint,
                        second.ProxyEndPoint,
                        3333),
                ],
            };
            var processes = new FakeProcessIdentityProvider();
            processes.AddDenied(
                3333);
            processes.SnapshotValues[3333] =
                new ProcessIdentityData(
                    3333,
                    clock.UtcNow.AddHours(-1),
                    "protected.exe");
            await using var resolver =
                Resolver(
                    tcp,
                    processes,
                    clock);

            Assert.True(resolver.TryEnqueue(first));
            Assert.True(resolver.TryEnqueue(second));
            await Task.WhenAll(resolver.DrainAsync(first.RunId), resolver.DrainAsync(second.RunId));

            Assert.Equal("protected.exe", first.Identity.Name);
            Assert.Equal("protected.exe", second.Identity.Name);
            Assert.Equal(1, processes.SnapshotCalls);
        }

        [Fact]
        public async Task Resolve_RemoteSourceBypassesEveryNativeProvider()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(
                clock,
                IPAddress.Parse("192.0.2.10"),
                50129,
                IPAddress.Loopback,
                8080);
            var tcp = new FakeTcpOwnerTableProvider();
            var processes = new FakeProcessIdentityProvider();
            await using var resolver = Resolver(tcp, processes, clock, new FakeLocalAddressProvider(_ => false));

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(0, tcp.Calls);
            Assert.Equal(0, processes.QueryCalls);
            Assert.Equal(ClientProcessStatus.Remote, context.Identity.Status);
        }

        [Fact]
        public async Task QueueSaturationIsBoundedAndFailsOpenAsUnresolved()
        {
            var clock = new FakeClock();
            var tcp = new BlockingTcpOwnerTableProvider();
            var options =
                ClientProcessResolverOptions.Default with
                {
                    QueueCapacity = 1,
                };
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock, options: options);
            ClientProcessContext first = Context(clock, IPAddress.Loopback, 50130, IPAddress.Loopback, 8080);
            ClientProcessContext overflow = Context(clock, IPAddress.Loopback, 50131, IPAddress.Loopback, 8080);

            Assert.True(resolver.TryEnqueue(first));
            await tcp.Started.Task;
            Assert.False(resolver.TryEnqueue(overflow));
            Assert.InRange(resolver.PendingCount, 0, options.QueueCapacity);
            Assert.Equal(ClientProcessStatus.Unresolved, overflow.Identity.Status);

            tcp.Release();
            await resolver.DrainAsync(first.RunId);
        }

        [Fact]
        public async Task PendingConnectionsShareOneBatchedTableSnapshot()
        {
            var clock = new FakeClock();
            var tcp = new BlockingTcpOwnerTableProvider();
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock);
            ClientProcessContext[] contexts = Enumerable.Range(0, 3).Select(index => Context(clock, IPAddress.Loopback,
                50140 + index, IPAddress.Loopback, 8080)).ToArray();
            tcp.Rows = contexts.Select((context, index) => new TcpOwnerRow(context.ClientEndPoint,
                context.ProxyEndPoint, 6000 + index)).ToArray();

            Assert.True(resolver.TryEnqueue(contexts[0]));
            await tcp.Started.Task;
            Assert.True(resolver.TryEnqueue(contexts[1]));
            Assert.True(resolver.TryEnqueue(contexts[2]));
            tcp.Release();
            await Task.WhenAll(contexts.Select(context => resolver.DrainAsync(context.RunId)));

            Assert.InRange(tcp.Calls, 1, 2);
        }

        [Fact]
        public async Task ResolutionDeadlineUsesFakeClockAndEndsUnresolved()
        {
            var clock = new FakeClock();
            var options =
                ClientProcessResolverOptions.Default with
                {
                    ResolutionBudget = TimeSpan.FromMilliseconds(20),
                    RetryDelay = TimeSpan.FromMilliseconds(10),
                };
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50150, IPAddress.Loopback, 8080);
            await using var resolver = Resolver(
                new FakeTcpOwnerTableProvider(),
                new FakeProcessIdentityProvider(),
                clock,
                options: options);

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(ClientProcessStatus.Unresolved, context.Identity.Status);
            Assert.InRange(clock.DelayCalls, 1, 3);
        }

        [Fact]
        public async Task NativeCallsRunOnlyOnWorkerAndNeverBlockEnqueue()
        {
            var clock = new FakeClock();
            var tcp = new BlockingTcpOwnerTableProvider();
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock);
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50160, IPAddress.Loopback, 8080);
            int enqueueThread = Environment.CurrentManagedThreadId;

            Assert.True(resolver.TryEnqueue(context));
            await tcp.Started.Task;

            Assert.NotEqual(enqueueThread, tcp.CallingThreadId);
            tcp.Release();
            await resolver.DrainAsync(context.RunId);
        }

        [Fact]
        public async Task DutyCycleCeilingDelaysWorkerWithoutWallClockTiming()
        {
            var clock = new FakeClock();
            var tcp = new AdvancingTcpOwnerTableProvider(clock, TimeSpan.FromMilliseconds(20));
            var options =
                ClientProcessResolverOptions.Default with
                {
                    DutyCycleWindow = TimeSpan.FromMilliseconds(50),
                    MaximumWorkPerWindow = TimeSpan.FromMilliseconds(10),
                };
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock, options: options);

            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50170, IPAddress.Loopback, 8080);
            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.True(clock.DelayCalls > 0);
        }

        [Fact]
        public async Task DrainTimeout_RetiresQueuedAndActiveExactlyOnceAndRestoresCapacity()
        {
            var clock =
                new FakeClock
                {
                    DrainTimeoutsImmediately = true,
                };
            var tcp = new BlockingTcpOwnerTableProvider();
            Guid run = Guid.NewGuid();
            var options =
                ClientProcessResolverOptions.Default with
                {
                    QueueCapacity = 2,
                    DrainBudget = TimeSpan.FromMilliseconds(25),
                };
            await using var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock, options: options);
            ClientProcessContext active = Context(clock, run, IPAddress.Loopback, 50180, IPAddress.Loopback, 8080);
            ClientProcessContext queued = Context(clock, run, IPAddress.Loopback, 50181, IPAddress.Loopback, 8080);
            ClientProcessContext replacement = Context(
                clock,
                Guid.NewGuid(),
                IPAddress.Loopback,
                50182,
                IPAddress.Loopback,
                8080);

            try
            {
                Assert.True(resolver.TryEnqueue(active));
                await tcp.Started.Task;
                Assert.True(resolver.TryEnqueue(queued));

                await Assert.ThrowsAsync<TimeoutException>(() => resolver.DrainAsync(run));

                int pending = resolver.PendingCount;
                bool replacementAccepted = resolver.TryEnqueue(replacement);
                Assert.True(
                    pending == 0 && replacementAccepted,
                    $"Pending={pending}; replacement accepted={replacementAccepted}.");
                Assert.Equal(options.DrainBudget, clock.LastWaitTimeout);
            }
            finally
            {
                tcp.Release();
            }
        }

        [Fact]
        public async Task Dispose_WithQueuedWorkClearsAccountingAndCompletesRunWaiters()
        {
            var clock = new FakeClock();
            var tcp = new BlockingTcpOwnerTableProvider();
            Guid run = Guid.NewGuid();
            var resolver = Resolver(tcp, new FakeProcessIdentityProvider(), clock);
            ClientProcessContext active = Context(clock, run, IPAddress.Loopback, 50183, IPAddress.Loopback, 8080);
            ClientProcessContext queued = Context(clock, run, IPAddress.Loopback, 50184, IPAddress.Loopback, 8080);

            try
            {
                Assert.True(resolver.TryEnqueue(active));
                await tcp.Started.Task;
                Assert.True(resolver.TryEnqueue(queued));

                Task runDrained = resolver.DrainAsync(run);
                Assert.False(runDrained.IsCompleted);
                Task disposing = resolver.DisposeAsync().AsTask();
                tcp.Release();
                await disposing.WaitAsync(TimeSpan.FromSeconds(5));
                await runDrained.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, resolver.PendingCount);
            }
            finally
            {
                tcp.Release();
                await resolver.DisposeAsync();
            }
        }

        [Fact]
        public async Task RunScopedDrain_SealsOnlyThatRunAndNeverRetiresAnotherTabsResolution()
        {
            var clock =
                new FakeClock
                {
                    DrainTimeoutsImmediately = true,
                };
            var tcp = new BlockingTcpOwnerTableProvider();
            Guid firstRun = Guid.NewGuid();
            Guid secondRun = Guid.NewGuid();
            ClientProcessContext first = Context(clock, firstRun, IPAddress.Loopback, 50190, IPAddress.Loopback, 8080);
            ClientProcessContext second = Context(
                clock,
                secondRun,
                IPAddress.Loopback,
                50191,
                IPAddress.Loopback,
                8080);
            ClientProcessContext lateFirst = Context(
                clock,
                firstRun,
                IPAddress.Loopback,
                50192,
                IPAddress.Loopback,
                8080);
            ClientProcessContext lateSecond = Context(
                clock,
                secondRun,
                IPAddress.Loopback,
                50193,
                IPAddress.Loopback,
                8080);
            tcp.Rows =
            [
                new TcpOwnerRow(
                    first.ClientEndPoint,
                    first.ProxyEndPoint,
                    7100),
                new TcpOwnerRow(
                    second.ClientEndPoint,
                    second.ProxyEndPoint,
                    7200),
                new TcpOwnerRow(
                    lateSecond.ClientEndPoint,
                    lateSecond.ProxyEndPoint,
                    7300),
            ];
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(7100, clock.UtcNow.AddMinutes(-2), "first.exe");
            processes.AddFound(7200, clock.UtcNow.AddMinutes(-2), "second.exe");
            processes.AddFound(7300, clock.UtcNow.AddMinutes(-2), "late-second.exe");
            await using var resolver = Resolver(tcp, processes, clock);

            try
            {
                Assert.True(resolver.TryEnqueue(first));
                await tcp.Started.Task;
                Assert.True(resolver.TryEnqueue(second));

                resolver.SealRun(firstRun);
                bool lateAccepted = resolver.TryEnqueue(lateFirst);
                await Assert.ThrowsAsync<TimeoutException>(() => resolver.DrainAsync(firstRun));

                Assert.True(
                    !lateAccepted && resolver.PendingCount == 1,
                    $"Late first accepted={lateAccepted}; pending={resolver.PendingCount}.");
                Assert.True(resolver.TryEnqueue(lateSecond));
                clock.DrainTimeoutsImmediately = false;
                tcp.Release();
                await resolver.DrainAsync(secondRun);
                Assert.Equal("second.exe", second.Identity.Name);
                Assert.Equal("late-second.exe", lateSecond.Identity.Name);
            }
            finally
            {
                tcp.Release();
            }
        }

        [Fact]
        public async Task BlockedApplicationDoesNotBlockAnotherNativeBatch()
        {
            var clock = new FakeClock();
            var tcp = new BlockingTcpOwnerTableProvider();
            ClientProcessContext first = Context(clock, IPAddress.Loopback, 50200, IPAddress.Loopback, 8080);
            ClientProcessContext second = Context(clock, IPAddress.Loopback, 50201, IPAddress.Loopback, 8080);
            tcp.Rows =
            [
                new TcpOwnerRow(
                    first.ClientEndPoint,
                    first.ProxyEndPoint,
                    7300),
                new TcpOwnerRow(
                    second.ClientEndPoint,
                    second.ProxyEndPoint,
                    7400),
            ];
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(7300, clock.UtcNow.AddMinutes(-2), "blocked.exe");
            processes.AddFound(7400, clock.UtcNow.AddMinutes(-2), "next.exe");
            using var applicationRelease = new ManualResetEventSlim(false);
            var applicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.IdentityChanged +=
                (_, _) =>
                {
                    applicationStarted.TrySetResult();
                    applicationRelease.Wait();
                };
            await using var resolver = Resolver(tcp, processes, clock);

            try
            {
                Assert.True(resolver.TryEnqueue(first));
                await tcp.Started.Task;
                Assert.True(resolver.TryEnqueue(second));
                tcp.Release();
                await applicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await tcp.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                applicationRelease.Set();
                tcp.Release();
            }
        }

        [Fact]
        public async Task ApplicationWorkIsExcludedFromNativeDutyAccounting()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50210, IPAddress.Loopback, 8080);
            var tcp = Rows(context, 7500);
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(7500, clock.UtcNow.AddMinutes(-2), "writer.exe");
            context.IdentityChanged += (_, _) => clock.Advance(TimeSpan.FromMilliseconds(20));
            var options =
                ClientProcessResolverOptions.Default with
                {
                    DutyCycleWindow = TimeSpan.FromMilliseconds(50),
                    MaximumWorkPerWindow = TimeSpan.FromMilliseconds(10),
                };
            await using var resolver = Resolver(tcp, processes, clock, options: options);

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal(0, clock.DelayCalls);
        }

        [Fact]
        public async Task AccessDeniedAfterCachedSnapshotRetriesUntilAnEligibleSnapshot()
        {
            var clock = new FakeClock();
            Guid run = Guid.NewGuid();
            ClientProcessContext first = Context(clock, run, IPAddress.Loopback, 50220, IPAddress.Loopback, 8080);
            var tcp =
                new FakeTcpOwnerTableProvider
                {
                    Rows =
                    [
                        new TcpOwnerRow(
                            first.ClientEndPoint,
                            first.ProxyEndPoint,
                            7600),
                    ],
                };
            var processes =
                new FakeProcessIdentityProvider();
            processes.AddDenied(
                7600);
            processes.SnapshotValues[7600] =
                new ProcessIdentityData(
                    7600,
                    clock.UtcNow.AddHours(-1),
                    "protected.exe");
            var options =
                ClientProcessResolverOptions.Default with
                {
                    ResolutionBudget =
                        TimeSpan.FromSeconds(2),
                    RetryDelay =
                        TimeSpan.FromMilliseconds(100),
                    SnapshotFallbackInterval =
                        TimeSpan.FromSeconds(1),
                };
            await using var resolver =
                Resolver(
                    tcp,
                    processes,
                    clock,
                    options: options);
            Assert.True(
                resolver.TryEnqueue(first));
            await resolver.DrainAsync(run);
            Assert.Equal(
                "protected.exe",
                first.Identity.Name);

            clock.Advance(
                TimeSpan.FromMilliseconds(100));
            ClientProcessContext second =
                Context(
                    clock,
                    run,
                    IPAddress.Loopback,
                    50221,
                    IPAddress.Loopback,
                    8080);
            tcp.Rows =
            [
                new TcpOwnerRow(
                    second.ClientEndPoint,
                    second.ProxyEndPoint,
                    7600),
            ];

            Assert.True(resolver.TryEnqueue(second));
            await resolver.DrainAsync(run);

            Assert.Equal("protected.exe", second.Identity.Name);
            Assert.True(processes.SnapshotCalls >= 2);
        }

        [Fact]
        public async Task ResolutionDeadlineUsesMonotonicAcceptTimeAcrossWallClockJumps()
        {
            var clock = new FakeClock();
            ClientProcessContext context = Context(clock, IPAddress.Loopback, 50230, IPAddress.Loopback, 8080);
            var tcp = new WallJumpTcpOwnerTableProvider(
                clock,
                new TcpOwnerRow(context.ClientEndPoint, context.ProxyEndPoint, 7700));
            var processes = new FakeProcessIdentityProvider();
            processes.AddFound(7700, clock.UtcNow.AddMinutes(-2), "clock.exe");
            var options =
                ClientProcessResolverOptions.Default with
                {
                    ResolutionBudget = TimeSpan.FromMilliseconds(20),
                    RetryDelay = TimeSpan.FromMilliseconds(10),
                };
            await using var resolver = Resolver(tcp, processes, clock, options: options);

            Assert.True(resolver.TryEnqueue(context));
            await resolver.DrainAsync(context.RunId);

            Assert.Equal("clock.exe", context.Identity.Name);
            Assert.Equal(2, tcp.Calls);
        }

        [Fact]
        public async Task LocalAddressChangeRefreshesOffCallerAndUsesCachedMembership()
        {
            int caller = Environment.CurrentManagedThreadId;
            var source = new FakeLocalAddressSnapshotSource(IPAddress.Parse("192.0.2.10"));
            using var provider = new WindowsLocalAddressProvider(source);
            await provider.RefreshIdle;
            Assert.True(provider.IsLocal(IPAddress.Parse("192.0.2.10")));
            int callsBeforeMembership = source.SnapshotCalls;
            _ = provider.IsLocal(IPAddress.Parse("192.0.2.11"));
            Assert.Equal(callsBeforeMembership, source.SnapshotCalls);

            source.SetAddresses(IPAddress.Parse("192.0.2.11"));
            source.RaiseChanged();
            await provider.RefreshIdle;

            Assert.False(provider.IsLocal(IPAddress.Parse("192.0.2.10")));
            Assert.True(provider.IsLocal(IPAddress.Parse("192.0.2.11")));
            Assert.NotEqual(caller, source.LastSnapshotThread);
        }

        [Fact]
        public async Task LocalAddressSnapshotRetriesWithoutNetworkChange()
        {
            var source =
                new FakeLocalAddressSnapshotSource(
                    IPAddress.Parse(
                        "192.0.2.30"))
                {
                    FailuresRemaining = 1,
                };
            var clock = new RetryClock();
            using var provider = new WindowsLocalAddressProvider(source, clock);

            await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.ReleaseDelay();
            await provider.RefreshIdle.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(provider.IsLocal(IPAddress.Parse("192.0.2.30")));
            Assert.Equal(2, source.SnapshotCalls);
        }

        [Fact]
        public async Task LocalAddressRetryIsCancelledWhenProviderIsDisposed()
        {
            var source =
                new FakeLocalAddressSnapshotSource(
                    IPAddress.Parse(
                        "192.0.2.31"))
                {
                    FailuresRemaining = int.MaxValue,
                };
            var clock = new RetryClock();
            using var provider = new WindowsLocalAddressProvider(source, clock);

            await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Dispose();

            await clock.DelayCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await provider.RefreshIdle.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static ClientProcessResolver Resolver(ITcpOwnerTableProvider tcp, IProcessIdentityProvider processes,
            FakeClock clock, ILocalAddressProvider localAddresses = null, ClientProcessResolverOptions options = null)
            => new(tcp, processes, localAddresses ?? new FakeLocalAddressProvider(address =>
            IPAddress.IsLoopback(address)), clock, options);

        private static FakeTcpOwnerTableProvider Rows(
            ClientProcessContext context,
            int processId) =>
            new()
            {
                Rows =
                [
                    new TcpOwnerRow(
                        context.ClientEndPoint,
                        context.ProxyEndPoint,
                        processId),
                ],
            };

        private static ClientProcessContext Context(
            FakeClock clock,
            IPAddress clientAddress,
            int clientPort,
            IPAddress proxyAddress,
            int proxyPort) =>
            Context(
                clock,
                Guid.NewGuid(),
                clientAddress,
                clientPort,
                proxyAddress,
                proxyPort);

        private static ClientProcessContext Context(
            FakeClock clock,
            Guid runId,
            IPAddress clientAddress,
            int clientPort,
            IPAddress proxyAddress,
            int proxyPort) =>
            new(
                runId,
                clientPort,
                new IPEndPoint(
                    clientAddress,
                    clientPort),
                new IPEndPoint(
                    proxyAddress,
                    proxyPort),
                clock.UtcNow,
                clock.Timestamp);

        private sealed class FakeTcpOwnerTableProvider :
            ITcpOwnerTableProvider
        {
            internal IReadOnlyList<TcpOwnerRow> Rows { get; set; } =
                Array.Empty<TcpOwnerRow>();
            internal int Calls { get; private set; }

            public IReadOnlyList<TcpOwnerRow> Snapshot(
                AddressFamily addressFamily)
            {
                _ = addressFamily;
                Calls++;
                return Rows;
            }
        }

        private sealed class BlockingTcpOwnerTableProvider :
            ITcpOwnerTableProvider
        {
            private readonly ManualResetEventSlim _release =
                new(false);
            internal TaskCompletionSource Started { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            internal TaskCompletionSource SecondStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            internal int Calls { get; private set; }
            internal int CallingThreadId { get; private set; }
            internal IReadOnlyList<TcpOwnerRow> Rows { get; set; } =
                Array.Empty<TcpOwnerRow>();

            public IReadOnlyList<TcpOwnerRow> Snapshot(
                AddressFamily addressFamily)
            {
                _ = addressFamily;
                Calls++;
                if (Calls == 2)
                {
                    SecondStarted.TrySetResult();
                }
                CallingThreadId =
                    Environment.CurrentManagedThreadId;
                Started.TrySetResult();
                _release.Wait();
                return Rows;
            }

            internal void Release() =>
                _release.Set();
        }

        private sealed class AdvancingTcpOwnerTableProvider(
            FakeClock clock,
            TimeSpan work) :
            ITcpOwnerTableProvider
        {
            public IReadOnlyList<TcpOwnerRow> Snapshot(
                AddressFamily addressFamily)
            {
                _ = addressFamily;
                clock.Advance(work);
                return Array.Empty<TcpOwnerRow>();
            }
        }

        private sealed class WallJumpTcpOwnerTableProvider(
            FakeClock clock,
            TcpOwnerRow row) :
            ITcpOwnerTableProvider
        {
            internal int Calls { get; private set; }

            public IReadOnlyList<TcpOwnerRow> Snapshot(
                AddressFamily addressFamily)
            {
                _ = addressFamily;
                Calls++;
                if (Calls == 1)
                {
                    clock.JumpUtc(
                        TimeSpan.FromDays(1));
                    return Array.Empty<TcpOwnerRow>();
                }
                return [row];
            }
        }

        private sealed class FakeProcessIdentityProvider : IProcessIdentityProvider
        {
            private readonly Dictionary<int, (ProcessIdentityQueryStatus Status, ProcessIdentityData Identity)> _queries
                = new();

            internal Dictionary<int, ProcessIdentityData> SnapshotValues { get; } = new();

            internal int QueryCalls { get; private set; }
            internal int SnapshotCalls { get; private set; }

            internal void AddFound(int processId, DateTimeOffset started, string name)
            {
                _queries[processId] = (
                    ProcessIdentityQueryStatus.Found,
                    new ProcessIdentityData(processId, started, name));
            }

            internal void AddDenied(int processId)
            {
                _queries[processId] = (ProcessIdentityQueryStatus.AccessDenied, null);
            }

            public ProcessIdentityQueryStatus Query(int processId, out ProcessIdentityData identity)
            {
                QueryCalls++;
                if (_queries.TryGetValue(processId, out var query))
                {
                    identity = query.Identity;
                    return query.Status;
                }
                identity = null;
                return ProcessIdentityQueryStatus.Missing;
            }

            public IReadOnlyDictionary<int, ProcessIdentityData> Snapshot()
            {
                SnapshotCalls++;
                return SnapshotValues;
            }
        }

        private sealed class FakeLocalAddressProvider(Func<IPAddress, bool> isLocal) : ILocalAddressProvider
        {
            public bool IsLocal(IPAddress address) => isLocal(address);
        }

        private sealed class FakeLocalAddressSnapshotSource : ILocalAddressSnapshotSource
        {
            private IReadOnlyCollection<IPAddress> _addresses;

            internal FakeLocalAddressSnapshotSource(params IPAddress[] addresses)
            {
                _addresses = addresses;
            }

            public event EventHandler Changed;

            internal int FailuresRemaining { get; set; }
            internal int SnapshotCalls { get; private set; }
            internal int LastSnapshotThread { get; private set; }

            public IReadOnlyCollection<IPAddress> Snapshot()
            {
                SnapshotCalls++;
                LastSnapshotThread = Environment.CurrentManagedThreadId;
                if (Interlocked.CompareExchange(ref _failuresObserved, 0, 0) <FailuresRemaining)
                {
                    Interlocked.Increment(ref _failuresObserved);
                    throw new InvalidOperationException("Injected address snapshot failure.");
                }
                return _addresses;
            }

            internal void SetAddresses(params IPAddress[] addresses)
            {
                _addresses = addresses;
            }

            internal void RaiseChanged()
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            private int _failuresObserved;
        }

        private sealed class FakeClock : IClientProcessResolverClock
        {
            private DateTimeOffset _utcNow = DateTimeOffset.Parse(
                "2026-11-03T20:00:00.0000000+00:00",
                System.Globalization.CultureInfo.InvariantCulture);
            private long _timestamp;

            public DateTimeOffset UtcNow => _utcNow;
            public long Timestamp => _timestamp;
            internal int DelayCalls { get; private set; }
            internal bool DrainTimeoutsImmediately { get; set; }
            internal TimeSpan LastWaitTimeout { get; private set; }

            public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(_timestamp - timestamp);

            public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DelayCalls++;
                Advance(delay);
                return ValueTask.CompletedTask;
            }

            public async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
            {
                LastWaitTimeout = timeout;
                if (task.IsCompleted)
                {
                    await task;
                    return;
                }
                if (DrainTimeoutsImmediately)
                {
                    Advance(timeout);
                    throw new TimeoutException();
                }
                await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            internal void Advance(TimeSpan value)
            {
                _utcNow += value;
                _timestamp += value.Ticks;
            }

            internal void JumpUtc(TimeSpan value)
            {
                _utcNow += value;
            }
        }

        private sealed class RetryClock : IClientProcessResolverClock
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private DateTimeOffset _utcNow = DateTimeOffset.Parse(
                "2026-11-04T01:00:00.0000000+00:00",
                System.Globalization.CultureInfo.InvariantCulture);
            private long _timestamp;

            internal TaskCompletionSource DelayStarted { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource DelayCancelled { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public DateTimeOffset UtcNow => _utcNow;
            public long Timestamp => _timestamp;

            public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(_timestamp - timestamp);

            public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                DelayStarted.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    DelayCancelled.TrySetResult();
                    throw;
                }
                _utcNow += delay;
                _timestamp += delay.Ticks;
            }

            public Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken) => task.WaitAsync(
                timeout,
                cancellationToken);

            internal void ReleaseDelay()
            {
                _release.TrySetResult();
            }
        }
    }
}
