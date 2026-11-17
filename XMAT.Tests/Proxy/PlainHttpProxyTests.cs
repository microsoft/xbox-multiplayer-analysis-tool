// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public class PlainHttpProxyTests
    {
        [Fact]
        public async Task PlainHttp_Get_RoundTripCaptured()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();

            string body = await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/hello?x=1");

            Assert.Equal("origin-ok", body);
            ObservedRequest seen = Assert.Single(origin.Requests);
            Assert.Equal("GET", seen.Method);
            Assert.Equal("/hello?x=1", seen.Path);
            Assert.Single(proxy.CapturedRequests);
            Assert.Single(proxy.CapturedResponses);
        }

        [Fact]
        public async Task PlainHttp_RequestAndResponseShareAcceptedProcessContext()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync(
                    $"http://127.0.0.1:{origin.Port}/identity"));

            ClientProcessContext requestContext = Assert.Single(proxy.CapturedRequestProcessContexts);
            ClientProcessContext responseContext = Assert.Single(proxy.CapturedResponseProcessContexts);
            Assert.NotNull(requestContext);
            Assert.Same(requestContext, responseContext);
        }

        [Fact]
        public async Task BlockedNativeLookupNeverBlocksTrafficLifecycle()
        {
            var native = new BlockingOwnerProvider();
            var clock = new ResolverClock();
            await using var resolver = new ClientProcessResolver(
                native,
                new MissingProcesses(),
                new LoopbackAddresses(),
                clock);
            var webProxy =
                new WebServiceProxy
                {
                    ProcessResolver = resolver,
                };
            var originArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOrigin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin =
                await LoopbackOrigin.StartAsync(
                    async context =>
                    {
                        originArrived.TrySetResult();
                        await releaseOrigin.Task.WaitAsync(context.RequestAborted);
                        await context.Response.WriteAsync("origin-ok");
                    });
            await using var proxy = await ProxyTestHarness.StartAsync(webProxy);
            using HttpClient client = proxy.CreateClient();
            Task<string> request = client.GetStringAsync(
                    $"http://127.0.0.1:{origin.Port}/nonblocking");
            try
            {
                await originArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

                await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(request.IsCompleted);
                releaseOrigin.TrySetResult();

                Assert.Equal("origin-ok", await request.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(native.Released, "Traffic waited for the blocked native provider.");
            }
            finally
            {
                releaseOrigin.TrySetResult();
                native.Release();
            }
        }

        [Fact]
        public async Task AcceptedContextUsesTheInjectedResolverClock()
        {
            var clock = new FixedResolverClock();
            await using var resolver =
                new ClientProcessResolver(
                    new EmptyOwnerProvider(),
                    new MissingProcesses(),
                    new LoopbackAddresses(),
                    clock,
                    ClientProcessResolverOptions
                        .Default with
                        {
                            ResolutionBudget = TimeSpan.FromMilliseconds(20),
                            RetryDelay = TimeSpan.FromMilliseconds(10),
                        });
            var webProxy =
                new WebServiceProxy
                {
                    ProcessResolver = resolver,
                };
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync(webProxy);
            using HttpClient client = proxy.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync(
                    $"http://127.0.0.1:{origin.Port}/accepted-clock"));
            ClientProcessContext context = Assert.Single(proxy.CapturedRequestProcessContexts);

            Assert.Equal(clock.InitialUtc, context.AcceptedAtUtc);
            Assert.Equal(clock.InitialTimestamp, context.AcceptedAtTimestamp);
        }

        [Fact]
        public async Task PlainHttp_PostWithBodyInSamePacket_Forwarded()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();

            using var content = new StringContent("payload");
            using var response = await client.PostAsync(
                $"http://127.0.0.1:{origin.Port}/post",
                content);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("payload", Encoding.UTF8.GetString(Assert.Single(origin.Requests).Body));
        }

        // The origin sets a cookie for one client; a second client must never
        // have it injected by the proxy's upstream handler.
        [Fact]
        public async Task PlainHttp_UpstreamCookies_NotSharedAcrossClients()
        {
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                context.Response.Headers.SetCookie = "session=client-a; Path=/";
                return Task.CompletedTask;
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            string url = $"http://127.0.0.1:{origin.Port}/cookie";

            using HttpClient clientA = proxy.CreateClient();
            using HttpClient clientB = proxy.CreateClient();
            (await clientA.GetAsync(url)).Dispose();
            (await clientB.GetAsync(url)).Dispose();

            Assert.All(origin.Requests, r => Assert.False(r.Headers.ContainsKey("Cookie")));
        }

        // Set-Cookie reaches each client, each client's own Cookie is forwarded back to the origin,
        // and nothing leaks between clients.
        [Fact]
        public async Task PlainHttp_CookieRoundTrip_IsPerClient()
        {
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                if (context.Request.Path == "/login")
                {
                    context.Response.Headers.SetCookie = $"session={context.Request.Headers["X-Client"]}; Path=/";
                }

                return Task.CompletedTask;
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            string baseUrl = $"http://127.0.0.1:{origin.Port}";

            async Task LoginThenCallAsync(string name)
            {
                using HttpClient client = proxy.CreateClient();
                client.DefaultRequestHeaders.Add("X-Client", name);
                (await client.GetAsync($"{baseUrl}/login")).Dispose();
                (await client.GetAsync($"{baseUrl}/next")).Dispose();
            }

            await LoginThenCallAsync("A");
            await LoginThenCallAsync("B");

            var requests = origin.Requests.ToList();
            Assert.All(
                requests.Where(r => r.Path == "/login"),
                r => Assert.False(r.Headers.ContainsKey("Cookie")));
            var next = requests.Where(r => r.Path == "/next").ToList();
            Assert.Equal(2, next.Count);
            Assert.Equal("session=A", next[0].Headers["Cookie"]);
            Assert.Equal("session=B", next[1].Headers["Cookie"]);
        }

        private sealed class BlockingOwnerProvider : ITcpOwnerTableProvider
        {
            private readonly ManualResetEventSlim _release = new(false);
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Released { get; private set; }

            public IReadOnlyList<TcpOwnerRow> Snapshot(AddressFamily addressFamily)
            {
                _ = addressFamily;
                Started.TrySetResult();
                _release.Wait();
                Released = true;
                return Array.Empty<TcpOwnerRow>();
            }

            internal void Release() => _release.Set();
        }

        private sealed class EmptyOwnerProvider : ITcpOwnerTableProvider
        {
            public IReadOnlyList<TcpOwnerRow> Snapshot(AddressFamily addressFamily)
            {
                _ = addressFamily;
                return Array.Empty<TcpOwnerRow>();
            }
        }

        private sealed class MissingProcesses : IProcessIdentityProvider
        {
            public ProcessIdentityQueryStatus Query(int processId, out ProcessIdentityData identity)
            {
                _ = processId;
                identity = null;
                return ProcessIdentityQueryStatus.Missing;
            }

            public IReadOnlyDictionary<int, ProcessIdentityData> Snapshot() => new Dictionary<int,
                    ProcessIdentityData>();
        }

        private sealed class LoopbackAddresses : ILocalAddressProvider
        {
            public bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address);
        }

        private sealed class ResolverClock : IClientProcessResolverClock
        {
            private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
            public long Timestamp => (DateTimeOffset.UtcNow - _started).Ticks;

            public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(Timestamp - timestamp);

            public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                await Task.Delay(delay, cancellationToken);
            }

            public Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken) => task.WaitAsync(
                timeout,
                cancellationToken);
        }

        private sealed class FixedResolverClock : IClientProcessResolverClock
        {
            private DateTimeOffset _utcNow = DateTimeOffset.Parse(
                "2040-01-02T03:04:05.0000000+00:00",
                System.Globalization.CultureInfo.InvariantCulture);
            private long _timestamp = 123456;

            internal DateTimeOffset InitialUtc => DateTimeOffset.Parse(
                "2040-01-02T03:04:05.0000000+00:00",
                System.Globalization.CultureInfo.InvariantCulture);
            internal long InitialTimestamp => 123456;

            public DateTimeOffset UtcNow => _utcNow;
            public long Timestamp => _timestamp;

            public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(_timestamp - timestamp);

            public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _utcNow += delay;
                _timestamp += delay.Ticks;
                return ValueTask.CompletedTask;
            }

            public Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken) => task.WaitAsync(
                timeout,
                cancellationToken);
        }
    }
}
