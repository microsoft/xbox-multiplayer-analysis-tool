// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.Proxy.ResponseStreamingTests;

namespace XMAT.Tests.Proxy
{
    // The Preferences > Web Proxy streaming settings, the capture limit (Fiddler's stream
    // and forget, with a truncated capture) and what scripts and captures see of a
    // streamed response (XMAT-012).
    [Collection(IsolatedProxyTests.Name)]
    public class ResponseStreamingCaptureTests
    {
        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task StreamAllResponses_StreamsAnyType(string route)
        {
            (Version version, bool tls) = Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin("application/json", "{\"part\":", "1}", release.Task), useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.ResponseStreaming = new ResponseStreamingOptions { StreamAllResponses = true };
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/poll"), version);
                (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "{\"part\":");
                using (response)
                {
                    Assert.True(first.Contains("{\"part\":", StringComparison.Ordinal),
                        $"{route}: with Stream all responses on, the first bytes did not reach the client");
                    release.TrySetResult();
                    Assert.Equal("{\"part\":1}", first + await ReadToEndAsync(response));
                }
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Fact]
        public async Task AutoStreamOff_KeepsEventStreamsBuffered()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin("text/event-stream", "data: first\n\n", "data: second\n\n", release.Task));
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.ResponseStreaming = new ResponseStreamingOptions { AutoStreamMediaAndEvents = false };
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/events"), Route("http11").Version);
                Task<HttpResponseMessage> send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.NotSame(send, await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(750))));

                release.TrySetResult();
                using HttpResponseMessage response = await send.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("data: first\n\ndata: second\n\n", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Theory]
        [InlineData("http11", false)]
        [InlineData("https11", false)]
        [InlineData("https2", false)]
        [InlineData("http11", true)]
        [InlineData("https11", true)]
        [InlineData("https2", true)]
        public async Task BodyOverCaptureLimit_StreamsAndCapturesOnlyTheLimit(string route, bool knownLength)
        {
            const int limit = 64 * 1024;
            (Version version, bool tls) = Route(route);
            byte[] payload = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i * 31 + 7)).ToArray();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                context.Response.ContentType = "application/octet-stream";
                if (knownLength)
                    context.Response.ContentLength = payload.Length;
                for (int offset = 0; offset < payload.Length; offset += 32 * 1024)
                {
                    if (offset == payload.Length / 2)
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted);
                    await context.Response.Body.WriteAsync(payload.AsMemory(offset, 32 * 1024));
                    await context.Response.Body.FlushAsync();
                }
            }, useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.ResponseStreaming = new ResponseStreamingOptions { CaptureLimitBytes = limit };
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/download"), version);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                HttpResponseMessage response = null;
                int firstRead = 0;
                var received = new MemoryStream();
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var buffer = new byte[16 * 1024];
                    firstRead = await body.ReadAsync(buffer, timeout.Token);
                    received.Write(buffer, 0, firstRead);
                }
                catch (OperationCanceledException)
                {
                }
                using (response)
                {
                    Assert.True(firstRead > 0,
                        $"{route} knownLength={knownLength}: a body over the capture limit was not streamed while the origin held it");
                    release.TrySetResult();
                    Stream body = await response.Content.ReadAsStreamAsync();
                    await body.CopyToAsync(received);
                    Assert.Equal(payload, received.ToArray());
                }

                ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
                Assert.Equal(limit, captured.CaptureTruncatedAt);
                Assert.Equal(payload.AsSpan(0, limit).ToArray(), captured.BodyBytes);
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Fact]
        public async Task BodyWithinCaptureLimit_IsCompleteAndNotMarked()
        {
            byte[] payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)i).ToArray();
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                context.Response.ContentType = "application/octet-stream";
                await context.Response.Body.WriteAsync(payload);
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.ResponseStreaming = new ResponseStreamingOptions { CaptureLimitBytes = payload.Length };
            using HttpClient client = proxy.CreateClient();

            Assert.Equal(payload, await client.GetByteArrayAsync(origin.Url("/exact")));

            ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
            Assert.Equal(0, captured.CaptureTruncatedAt);
            Assert.Equal(payload, captured.BodyBytes);
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task StreamedResponse_ScriptSeesCopyAndCannotChangeOrBlockIt(string route)
        {
            (Version version, bool tls) = Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin("text/event-stream", "data: first\n\n", "data: second\n\n", release.Task), useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            var policyCalls = new ConcurrentQueue<(bool Forwarded, string Body)>();
            proxy.Proxy.WebResponsePolicyAsync = (args, _) =>
            {
                if (args.Response.StatusDescription == "Connection Established")
                    return Task.FromResult(true);
                policyCalls.Enqueue((args.ResponseForwarded, Encoding.ASCII.GetString(args.Response.BodyBytes)));
                args.Response.BodyBytes = "changed"u8.ToArray();
                args.Response.Headers["X-Changed"] = "yes";
                return Task.FromResult(false);
            };
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/events"), version);
                (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "data: first");
                using (response)
                {
                    Assert.True(first.Contains("data: first", StringComparison.Ordinal), $"{route}: the event stream was not streamed");
                    release.TrySetResult();
                    Assert.Equal("data: first\n\ndata: second\n\n", first + await ReadToEndAsync(response));
                    Assert.False(response.Headers.Contains("X-Changed"));
                }

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (policyCalls.IsEmpty && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                (bool forwarded, string seen) = Assert.Single(policyCalls);
                Assert.True(forwarded);
                Assert.Equal("data: first\n\ndata: second\n\n", seen);
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https2")]
        public async Task StreamedResponse_ReportsProgressAtHeadersAndAtCaptureLimit(string route)
        {
            const int limit = 16;
            (Version version, bool tls) = Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin("text/event-stream", "data: a first event\n\n", "data: second\n\n", release.Task), useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.ResponseStreaming = new ResponseStreamingOptions { CaptureLimitBytes = limit };
            var progress = new ConcurrentQueue<(string Status, bool Forwarded, int BodyLength, int TruncatedAt, string ContentType)>();
            proxy.Proxy.WebResponseProgressAsync = (args, _) =>
            {
                progress.Enqueue((
                    args.Response.Status,
                    args.ResponseForwarded,
                    args.Response.BodyBytes.Length,
                    args.Response.CaptureTruncatedAt,
                    args.Response.ContentHeaders["Content-Type"]));
                return Task.CompletedTask;
            };
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/events"), version);
                (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "data: a first event");
                using (response)
                {
                    Assert.True(first.Contains("data: a first event", StringComparison.Ordinal), $"{route}: the event stream was not streamed");
                    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                    while (progress.Count < 2 && DateTime.UtcNow < deadline)
                        await Task.Delay(20);

                    // Both notices arrive while the origin still holds the response open.
                    var notices = progress.ToArray();
                    Assert.Equal(2, notices.Length);
                    Assert.Equal(("200", true, 0, 0), (notices[0].Status, notices[0].Forwarded, notices[0].BodyLength, notices[0].TruncatedAt));
                    Assert.StartsWith("text/event-stream", notices[0].ContentType, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(("200", true, limit, limit), (notices[1].Status, notices[1].Forwarded, notices[1].BodyLength, notices[1].TruncatedAt));
                    release.TrySetResult();
                    await ReadToEndAsync(response);
                }

                ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
                Assert.Equal(limit, captured.CaptureTruncatedAt);
                Assert.Equal("data: a first ev", Encoding.ASCII.GetString(captured.BodyBytes));
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task Trailers_AreCaptured(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var rawOrigin = RawOrigin.Start(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\nTrailer: x-checksum\r\n\r\n" +
                "7\r\npayload\r\n0\r\nx-checksum: abc123\r\n\r\n");
            await using var tlsOrigin = await LoopbackOrigin.StartAsync(TrailerOrigin, useTls: true);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            string url = tls ? tlsOrigin.Url("/trailer") : $"http://127.0.0.1:{rawOrigin.Port}/trailer";
            using var request = NewRequest(HttpMethod.Get, url, version);

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal("payload", await response.Content.ReadAsStringAsync());
            ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
            Assert.Equal("abc123", captured.Trailers["x-checksum"]);
            Assert.Equal("payload", Encoding.ASCII.GetString(captured.BodyBytes));
        }

        [Fact]
        public void Preferences_ClampTheCaptureLimit()
        {
            Assert.Equal(1024 * 1024, ResponseStreamingOptions.FromPreferences(true, false, 0).CaptureLimitBytes);
            Assert.Equal(32 * 1024 * 1024, ResponseStreamingOptions.FromPreferences(true, false, 32).CaptureLimitBytes);
            Assert.Equal(768 * 1024 * 1024, ResponseStreamingOptions.FromPreferences(true, false, 5000).CaptureLimitBytes);
            Assert.True(ResponseStreamingOptions.IsAutoStreamedType(MediaTypeHeaderValue.Parse("text/event-stream; charset=utf-8")));
            Assert.True(ResponseStreamingOptions.IsAutoStreamedType(MediaTypeHeaderValue.Parse("AUDIO/mpeg")));
            Assert.False(ResponseStreamingOptions.IsAutoStreamedType(MediaTypeHeaderValue.Parse("application/json")));
        }
    }
}
