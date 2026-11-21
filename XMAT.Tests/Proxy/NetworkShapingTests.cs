// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Tests.Cli;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using Xunit.Abstractions;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public class NetworkShapingTests(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("http11", false)]
        [InlineData("https11", false)]
        [InlineData("https2", false)]
        [InlineData("bypass", false)]
        [InlineData("http11", true)]
        [InlineData("https11", true)]
        [InlineData("https2", true)]
        [InlineData("bypass", true)]
        public async Task RatesApplyAcrossProxyRoutesWithoutChangingBytes(string route, bool upload)
        {
            byte[] body = Payload(128 * 1024);
            bool tls = route != "http11";
            await using var origin = await LoopbackOrigin.StartAsync(
                context => upload
                    ? context.Response.WriteAsync("ok")
                    : context.Response.Body.WriteAsync(body).AsTask(),
                useTls: tls);
            var shaping = new NetworkShapingController();
            shaping.Apply(new NetworkShapingProfile(
                NetworkShapingMode.Shape, upload ? 0 : 1000, upload ? 1000 : 0));
            await using var proxy = await StartAsync(shaping);
            if (route == "bypass")
            {
                proxy.Proxy.BypassList = new BypassListModel();
                Assert.True(proxy.Proxy.BypassList.Add("127.0.0.1"));
            }
            using HttpClient client = proxy.CreateClient();
            using var request = new HttpRequestMessage(upload ? HttpMethod.Post : HttpMethod.Get, origin.Url("/rate"))
            {
                Version = route == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = upload ? new ByteArrayContent(body) : null,
            };
            var clock = Stopwatch.StartNew();
            using HttpResponseMessage response = await client.SendAsync(request);
            byte[] received = await response.Content.ReadAsByteArrayAsync();
            double elapsed = clock.Elapsed.TotalMilliseconds;
            if (upload)
            {
                Assert.Equal(body, Assert.Single(origin.Requests).Body);
            }
            else
            {
                Assert.Equal(body, received);
            }
            if (route == "bypass")
            {
                Assert.Empty(proxy.CapturedRequests);
            }
            output.WriteLine($"{route} {(upload ? "up" : "down")}: {elapsed:F0} ms, {body.Length * 8d / elapsed:F0} kbps");
            Assert.InRange(elapsed, 850, 3500);
        }

        [Fact]
        public async Task StreamingCaptureIgnoresGuiDefaultsAndCompletesOnExpiry()
        {
            using var directory = new CliTestDirectory();
            const string first = "data: first\n\n";
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(first);
                await context.Response.Body.FlushAsync();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // Normal capture expiry aborts the event stream.
                }
            });
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            ResponseStreamingOptions previous = ResponseStreamingOptions.Current;
            Task<OperationResult<CaptureProxyResult>> capture = null;
            try
            {
                ResponseStreamingOptions.Current = new ResponseStreamingOptions { AutoStreamMediaAndEvents = false };
                string path = Path.Join(directory.Root, "stream.xmatcap");
                capture = new CaptureProxyOperation().ExecuteAsync(
                    new(port, 4, path, new(1, NetworkShapingProfile.Preset("min"))),
                    new OperationContext(progress: new Progress<OperationProgress>(progress =>
                    {
                        if (progress.Phase == "capture.listening")
                            ready.TrySetResult();
                    })),
                    cancellation.Token);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var client = new HttpClient(new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                });
                using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url("/stream"));
                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(2));
                Stream body = await response.Content.ReadAsStreamAsync();
                byte[] received = new byte[Encoding.UTF8.GetByteCount(first)];
                await body.ReadExactlyAsync(received, cancellation.Token);
                Assert.Equal(first, Encoding.UTF8.GetString(received));
                Assert.True((await capture).Succeeded);

                using var saved = new SqLtReadonlyDatabase(path);
                var row = Assert.Single(saved.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                Assert.Equal(received, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
                Assert.False(string.IsNullOrEmpty(row.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp)));
            }
            finally
            {
                try
                {
                    await cancellation.CancelAsync();
                    if (capture != null)
                        await capture;
                }
                finally
                {
                    ResponseStreamingOptions.Current = previous;
                }
            }
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task ShapedEventStreamsArriveBeforeTheOriginCompletes(string route)
        {
            (Version version, bool tls) = ResponseStreamingTests.Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const string first = "data: first\n\n";
            string last = new('x', 64 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                ResponseStreamingTests.HoldingOrigin("text/event-stream", first, last, release.Task), useTls: tls);
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 500));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = ResponseStreamingTests.NewRequest(HttpMethod.Get, origin.Url("/events"), version);
                (HttpResponseMessage response, string prefix) = await ResponseStreamingTests.ReadFirstBytesAsync(
                    client, request, "data: first");
                using (response)
                {
                    Assert.Contains("data: first", prefix, StringComparison.Ordinal);
                    var clock = Stopwatch.StartNew();
                    release.TrySetResult();
                    Assert.Equal(first + last, prefix + await ResponseStreamingTests.ReadToEndAsync(response));
                    Assert.InRange(clock.Elapsed.TotalMilliseconds, 800, 4000);
                }
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Fact]
        public async Task ConcurrentConnectionsShareOneBandwidthBudget()
        {
            byte[] body = Payload(128 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 1000));
            await using var proxy = await StartAsync(shaping);
            using HttpClient first = proxy.CreateClient();
            using HttpClient second = proxy.CreateClient();
            var clock = Stopwatch.StartNew();
            byte[][] results = await Task.WhenAll(
                first.GetByteArrayAsync(origin.Url("/a")),
                second.GetByteArrayAsync(origin.Url("/b")));
            double elapsed = clock.Elapsed.TotalMilliseconds;
            Assert.All(results, result => Assert.Equal(body, result));
            output.WriteLine($"shared 256 KiB down: {elapsed:F0} ms");
            Assert.InRange(elapsed, 1750, 5000);
        }

        [Fact]
        public async Task DelayedConnectionsDoNotPrepayTheSharedRate()
        {
            // Each response fits one pacer slice, so drawing it before the latency wait would release them together.
            byte[] body = Payload(1800);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 1000, LatencyMs: 2000));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            var clock = Stopwatch.StartNew();
            double[] completed = await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
            {
                Assert.Equal(body, await client.GetByteArrayAsync(origin.Url($"/burst/{index}")));
                return clock.Elapsed.TotalMilliseconds;
            }));
            double spread = completed.Max() - completed.Min();
            output.WriteLine($"32 x 1800 B at 1 Mbps + 2000 ms RTT: first {completed.Min():F0} ms, spread {spread:F0} ms");
            // About 64 KB at 1 Mbps needs ~500 ms of the shared rate once the latency has elapsed.
            Assert.True(spread >= 300, $"Responses were released within {spread:F0} ms of each other.");
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https2")]
        public async Task LatencyIsPipelinedRatherThanAddedPerSlice(string route)
        {
            byte[] body = Payload(256 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask(), useTls: route != "http11");
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 2000, LatencyMs: 200));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            async Task<double> GetAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url("/latency"))
                {
                    Version = route == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                var clock = Stopwatch.StartNew();
                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
                return clock.Elapsed.TotalMilliseconds;
            }
            await GetAsync();
            double elapsed = await GetAsync();
            output.WriteLine($"{route}, warmed connection, 256 KiB at 2 Mbps + 200 ms RTT: {elapsed:F0} ms");
            Assert.InRange(elapsed, 1080, 2200);
        }

        [Fact]
        public async Task LiveChangesAffectAnAlreadyOpenConnection()
        {
            byte[] body = Payload(128 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask(), useTls: true);
            var shaping = new NetworkShapingController();
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/warm")));
            Assert.Single(proxy.CapturedSslProcessContexts);

            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 1000));
            var clock = Stopwatch.StartNew();
            Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/enabled")));
            Assert.InRange(clock.Elapsed.TotalMilliseconds, 850, 3500);

            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 2000));
            clock.Restart();
            Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/changed")));
            Assert.InRange(clock.Elapsed.TotalMilliseconds, 400, 1800);

            shaping.Apply(NetworkShapingProfile.Off);
            clock.Restart();
            Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/disabled")));
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(800));
            Assert.Single(proxy.CapturedSslProcessContexts);
        }

        [Fact]
        public async Task DisablingShapingReleasesAnInFlightDownload()
        {
            byte[] body = Payload(128 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 128));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            Task<byte[]> download = client.GetByteArrayAsync(origin.Url("/in-flight"));
            await Task.Delay(250);
            Assert.False(download.IsCompleted);
            shaping.Apply(NetworkShapingProfile.Off);
            Assert.Equal(body, await download.WaitAsync(TimeSpan.FromSeconds(3)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        public async Task FixedLatencyAndBoundedJitterApplyWithoutARateLimit(int jitter)
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, LatencyMs: 200, JitterMs: jitter));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            await client.GetStringAsync(origin.Url("/warm"));
            var clock = Stopwatch.StartNew();
            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/delayed")));
            Assert.InRange(clock.Elapsed.TotalMilliseconds, 180 - jitter, 800);
        }

        [Fact]
        public async Task DisabledPathThroughputBenchmark()
        {
            byte[] body = Payload(1024 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(
                context => context.Response.Body.WriteAsync(body).AsTask());
            var baseline = new List<double>();
            var disabled = new List<double>();
            for (int round = 0; round < 6; round++)
            {
                bool wrapped = round % 2 != 0;
                await using var proxy = wrapped
                    ? await StartAsync(new NetworkShapingController())
                    : await ProxyTestHarness.StartAsync();
                using HttpClient client = proxy.CreateClient();
                await client.GetByteArrayAsync(origin.Url("/warm"));
                var clock = Stopwatch.StartNew();
                for (int request = 0; request < 16; request++)
                {
                    Assert.Equal(body, await client.GetByteArrayAsync(origin.Url("/benchmark")));
                }
                (wrapped ? disabled : baseline).Add(clock.Elapsed.TotalSeconds);
            }
            double before = baseline.Order().ElementAt(1);
            double after = disabled.Order().ElementAt(1);
            output.WriteLine($"True no-middleware baseline median: {16 / before:F1} MiB/s; " +
                $"disabled middleware: {16 / after:F1} MiB/s; elapsed ratio: {after / before:F3}");
        }

        [Theory]
        [InlineData(NetworkShapingMode.Broken)]
        [InlineData(NetworkShapingMode.Disconnect)]
        public async Task FailureProfilesRejectNewConnections(NetworkShapingMode mode)
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            var shaping = new NetworkShapingController();
            shaping.Apply(new(mode));
            await using var proxy = await StartAsync(shaping);
            using HttpClient client = proxy.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            Exception failure = await Record.ExceptionAsync(() => client.GetStringAsync(origin.Url("/blocked")));
            Assert.NotNull(failure);
            Assert.Empty(origin.Requests);
        }

        [Fact]
        public async Task WebSocketTrafficIsShapedAndDisconnectAbortsTheOpenSocket()
        {
            byte[] body = Payload(64 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(body.AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
                var buffer = new byte[1];
                try
                {
                    await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Disconnecting the client ends the synthetic origin's receive.
                    return;
                }
            }, useTls: true, useWebSockets: true);
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 500));
            await using var proxy = await StartAsync(shaping);
            using var client = new ClientWebSocket();
            client.Options.Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}");
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(new Uri(origin.Url("/ws").Replace("https:", "wss:")), cancellation.Token);
            byte[] actual = new byte[body.Length];
            int received = 0;
            var clock = Stopwatch.StartNew();
            while (received < actual.Length)
            {
                ValueWebSocketReceiveResult result = await client.ReceiveAsync(actual.AsMemory(received), cancellation.Token);
                received += result.Count;
            }
            Assert.Equal(body, actual);
            Assert.InRange(clock.Elapsed.TotalMilliseconds, 800, 3500);
            shaping.Apply(new(NetworkShapingMode.Disconnect));
            await Assert.ThrowsAsync<WebSocketException>(async () =>
                await client.ReceiveAsync(new byte[1].AsMemory(), cancellation.Token));
        }

        [Fact]
        public async Task PlainWebSocketAbsoluteHttpUpgradeIsShaped()
        {
            byte[] body = Payload(64 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(body.AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
                try
                {
                    await socket.ReceiveAsync(new byte[1].AsMemory(), context.RequestAborted);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Disconnecting the client ends the synthetic origin's receive.
                    return;
                }
            }, useWebSockets: true);
            var shaping = new NetworkShapingController();
            shaping.Apply(new(NetworkShapingMode.Shape, DownKbps: 500));
            await using var proxy = await StartAsync(shaping);
            using var connection = new TcpClient();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(IPAddress.Loopback, proxy.Port, cancellation.Token);
            NetworkStream stream = connection.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET {origin.Url("/ws")} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n" +
                "Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"), cancellation.Token);
            var header = new StringBuilder();
            var next = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.True(header.Length < 8192);
                Assert.Equal(1, await stream.ReadAsync(next, cancellation.Token));
                header.Append((char)next[0]);
            }
            Assert.StartsWith("HTTP/1.1 101", header.ToString());
            using WebSocket client = WebSocket.CreateFromStream(stream, false, null, Timeout.InfiniteTimeSpan);
            var actual = new byte[body.Length];
            int received = 0;
            var clock = Stopwatch.StartNew();
            while (received < actual.Length)
            {
                received += (await client.ReceiveAsync(actual.AsMemory(received), cancellation.Token)).Count;
            }
            Assert.Equal(body, actual);
            Assert.InRange(clock.Elapsed.TotalMilliseconds, 800, 3500);
            shaping.Apply(new(NetworkShapingMode.Disconnect));
        }

        [Fact]
        public async Task CancellingBackpressuredTransfersReturnsBufferBudget()
        {
            byte[] large = Payload(2 * 1024 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(context =>
                context.Response.Body.WriteAsync(context.Request.Path == "/large" ? large : new byte[16]).AsTask());
            var controller = new NetworkShapingController();
            controller.Apply(new(NetworkShapingMode.Shape, DownKbps: 1));
            await using var proxy = await StartAsync(controller);
            using HttpClient client = proxy.CreateClient();
            using var cancellation = new CancellationTokenSource();
            Task<byte[]> download = client.GetByteArrayAsync(origin.Url("/large"), cancellation.Token);
            await WaitAsync(() => controller.BufferSlots.CurrentCount < 240);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
            await WaitAsync(() => controller.BufferSlots.CurrentCount == 256);
            controller.Apply(NetworkShapingProfile.Off);
            Assert.Equal(new byte[16], await client.GetByteArrayAsync(origin.Url("/small")));

            static async Task WaitAsync(Func<bool> condition)
            {
                var elapsed = Stopwatch.StartNew();
                while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(20);
                }
                Assert.True(condition());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StalledClientsCannotStarveOtherConnections(bool shaped)
        {
            byte[] large = Payload(8 * 1024 * 1024);
            await using var origin = await LoopbackOrigin.StartAsync(context =>
                context.Response.Body.WriteAsync(context.Request.Path == "/large" ? large : new byte[16]).AsTask());
            var controller = new NetworkShapingController();
            if (shaped)
            {
                controller.Apply(new(NetworkShapingMode.Shape, DownKbps: 1_000_000));
            }
            await using var proxy = await StartAsync(controller);
            int heldSlots = 0;
            try
            {
                // Exhaust the shared pool without relying on OS socket buffer sizes or queue scheduling.
                if (shaped)
                {
                    while (controller.BufferSlots.Wait(0))
                    {
                        heldSlots++;
                    }
                    Assert.Equal(256, heldSlots);
                }

                using var stalled = new StalledClients();
                for (int i = 0; i < 8; i++)
                {
                    // The client never reads, so its download queue fills and stays full.
                    TcpClient client = stalled.Add();
                    await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET {origin.Url("/large")} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n"));
                }
                var elapsed = Stopwatch.StartNew();
                while (origin.Requests.Count < stalled.Count && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                }
                Assert.Equal(stalled.Count, origin.Requests.Count);
                Assert.Equal(shaped ? 0 : 256, controller.BufferSlots.CurrentCount);

                using HttpClient other = proxy.CreateClient();
                other.Timeout = TimeSpan.FromSeconds(5);
                Assert.Equal(new byte[16], await other.GetByteArrayAsync(origin.Url("/small")));
            }
            finally
            {
                if (heldSlots > 0)
                    controller.BufferSlots.Release(heldSlots);
            }
        }

        private static Task<ProxyTestHarness> StartAsync(NetworkShapingController controller) =>
            ProxyTestHarness.StartAsync(configureListen: builder => builder.UseNetworkShaping(controller));

        private static byte[] Payload(int size)
        {
            var bytes = new byte[size];
            new Random(17).NextBytes(bytes);
            return bytes;
        }

        private sealed class StalledClients : IDisposable
        {
            private readonly List<TcpClient> _clients = new();

            internal int Count => _clients.Count;

            internal TcpClient Add()
            {
                var client = new TcpClient { ReceiveBufferSize = 4096 };
                _clients.Add(client);
                return client;
            }

            public void Dispose() => _clients.ForEach(client => client.Dispose());
        }
    }
}
