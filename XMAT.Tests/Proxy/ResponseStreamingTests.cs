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
    // Fiddler-style response streaming and chunked framing (XMAT-012). Responses are
    // buffered by default; event streams, audio and video stream as they arrive, and
    // the wire keeps the origin's framing (chunked stays chunked, trailers included
    // where the client's protocol can carry them).
    [Collection(IsolatedProxyTests.Name)]
    public class ResponseStreamingTests
    {
        private static readonly TimeSpan FirstBytesTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OriginHoldLimit = TimeSpan.FromSeconds(30);

        internal static (Version Version, bool Tls) Route(string route) => route switch
        {
            "http11" => (HttpVersion.Version11, false),
            "https11" => (HttpVersion.Version11, true),
            "https2" => (HttpVersion.Version20, true),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        [Theory]
        [InlineData("http11", "text/event-stream")]
        [InlineData("https11", "text/event-stream")]
        [InlineData("https2", "text/event-stream")]
        [InlineData("http11", "audio/mpeg")]
        [InlineData("https11", "audio/mpeg")]
        [InlineData("https2", "video/mp4")]
        public async Task AutoStreamedTypes_ReachClientWhileOriginHoldsResponse(string route, string contentType)
        {
            (Version version, bool tls) = Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin(contentType, "data: first\n\n", "data: second\n\n", release.Task), useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/stream"), version);
                (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "data: first");
                using (response)
                {
                    Assert.True(first.Contains("data: first", StringComparison.Ordinal),
                        $"{route} {contentType}: the first event did not reach the client while the origin held the response open");
                    release.TrySetResult();
                    string rest = await ReadToEndAsync(response);
                    Assert.Equal("data: first\n\ndata: second\n\n", first + rest);
                }

                ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
                Assert.Equal("data: first\n\ndata: second\n\n", Encoding.ASCII.GetString(captured.BodyBytes));
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
        public async Task OtherTypes_StayBufferedByDefault(string route)
        {
            (Version version, bool tls) = Route(route);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                HoldingOrigin("application/json", "{\"part\":", "1}", release.Task), useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            try
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/buffered"), version);
                Task<HttpResponseMessage> send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Task first = await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(750)));
                Assert.NotSame(send, first);

                release.TrySetResult();
                using HttpResponseMessage response = await send.WaitAsync(FirstBytesTimeout);
                Assert.Equal("{\"part\":1}", await response.Content.ReadAsStringAsync());
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
        public async Task ClientDisconnectMidStream_CancelsUpstreamAndCompletesTheCapture(string route)
        {
            (Version version, bool tls) = Route(route);
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                using CancellationTokenRegistration registration =
                    context.RequestAborted.Register(() => aborted.TrySetResult());
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: first\n\n");
                await context.Response.Body.FlushAsync();
                try
                {
                    // A never-ending stream: only the proxy cancelling the request ends it.
                    while (true)
                    {
                        await Task.Delay(100, context.RequestAborted);
                        await context.Response.WriteAsync(": keep-alive\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            }, useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            var policyRan = new TaskCompletionSource<(bool Forwarded, string Body)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.Proxy.WebResponsePolicyAsync = (args, _) =>
            {
                if (args.Response.StatusDescription != "Connection Established")
                    policyRan.TrySetResult((args.ResponseForwarded, Encoding.ASCII.GetString(args.Response.BodyBytes)));
                return Task.FromResult(true);
            };
            using (HttpClient client = proxy.CreateClient())
            {
                using var request = NewRequest(HttpMethod.Get, origin.Url("/never-ends"), version);
                (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "data: first");
                Assert.True(first.Contains("data: first", StringComparison.Ordinal),
                    $"{route}: the first event of a never-ending stream did not reach the client");
                response.Dispose();
            }

            Task done = await Task.WhenAny(aborted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(done, aborted.Task),
                $"{route}: the upstream request was not cancelled after the client disconnected");

            // The stream ended when the device closed it: its capture completes with what arrived.
            Task<(bool Forwarded, string Body)> policy = policyRan.Task;
            Assert.Same(policy, await Task.WhenAny(policy, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.True(policy.Result.Forwarded);
            Assert.StartsWith("data: first\n\n", policy.Result.Body, StringComparison.Ordinal);
        }

        // Only waiting on the origin counts towards the idle timeout, not a device that reads slowly.
        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task SlowClient_DoesNotTripTheOriginIdleTimeout(string route)
        {
            const int blocks = 64;
            (Version version, bool tls) = Route(route);
            byte[] block = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray();
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                context.Response.ContentType = "audio/mpeg";
                for (int i = 0; i < blocks; i++)
                    await context.Response.Body.WriteAsync(block, context.RequestAborted);
            }, useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.UpstreamTimeout = TimeSpan.FromSeconds(1);
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Get, origin.Url("/slow-device"), version);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Stream body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[64 * 1024];
            long received = await body.ReadAsync(buffer);
            // The device stops reading for longer than the idle timeout while the origin has more to send.
            await Task.Delay(TimeSpan.FromSeconds(3));
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
                received += read;

            Assert.Equal((long)blocks * block.Length, received);
        }

        [Fact]
        public async Task PlainHttp11Client_ChunkedOriginStaysChunked()
        {
            await using var origin = RawOrigin.Start(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n" +
                "6\r\npart1-\r\n5\r\npart2\r\n0\r\n\r\n");
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.1");

            Assert.Equal("chunked", response.Header("Transfer-Encoding"));
            Assert.Null(response.Header("Content-Length"));
            Assert.Equal("part1-part2", response.DecodedBody);
            Assert.Empty(response.Trailers);
        }

        [Fact]
        public async Task PlainHttp11Client_ReceivesOriginTrailers()
        {
            await using var origin = RawOrigin.Start(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\nTrailer: x-checksum\r\n\r\n" +
                "6;ext=one\r\npart1-\r\n5\r\npart2\r\n0\r\nx-checksum: abc123\r\n\r\n");
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.1");

            Assert.Equal("chunked", response.Header("Transfer-Encoding"));
            Assert.Equal("x-checksum", response.Header("Trailer"));
            Assert.Equal("part1-part2", response.DecodedBody);
            Assert.Equal("abc123", response.Trailers.GetValueOrDefault("x-checksum"));
        }

        [Fact]
        public async Task PlainHttp10Client_NeverReceivesChunkedFramingOrTrailerHeader()
        {
            await using var origin = RawOrigin.Start(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\nTrailer: x-checksum\r\n\r\n" +
                "6\r\npart1-\r\n5\r\npart2\r\n0\r\nx-checksum: abc123\r\n\r\n");
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.0");

            Assert.Null(response.Header("Transfer-Encoding"));
            Assert.Null(response.Header("Trailer"));
            Assert.Equal("part1-part2", response.Body);
        }

        // The status line carries the origin's version, and HTTP/1.0 has no chunked framing.
        [Fact]
        public async Task PlainHttp11Client_StreamFromHttp10OriginIsNotChunked()
        {
            await using var origin = RawOrigin.Start(
                "HTTP/1.0 200 OK\r\nContent-Type: text/event-stream\r\n\r\ndata: first\n\ndata: second\n\n");
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.1");

            Assert.StartsWith("HTTP/1.0 200", response.StatusLine, StringComparison.Ordinal);
            Assert.Null(response.Header("Transfer-Encoding"));
            Assert.Equal("data: first\n\ndata: second\n\n", response.Body);
        }

        [Fact]
        public async Task PlainHttp11Client_ContentLengthOriginKeepsContentLength()
        {
            await using var origin = RawOrigin.Start(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 11\r\n\r\npart1-part2");
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.1");

            Assert.Equal("11", response.Header("Content-Length"));
            Assert.Null(response.Header("Transfer-Encoding"));
            Assert.Equal("part1-part2", response.Body);
        }

        [Theory]
        [InlineData("HEAD", "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 100\r\n\r\n", "100")]
        [InlineData("GET", "HTTP/1.1 304 Not Modified\r\nETag: \"v1\"\r\n\r\n", null)]
        [InlineData("GET", "HTTP/1.1 204 No Content\r\nX-Origin: yes\r\n\r\n", null)]
        public async Task PlainHttp11Client_BodylessResponsesGetNoBodyOrChunkedFraming(
            string method, string originResponse, string expectedContentLength)
        {
            await using var origin = RawOrigin.Start(originResponse);
            await using var proxy = await ProxyTestHarness.StartAsync();

            RawResponse response = await SendRawThroughProxyAsync(proxy.Port, origin.Port, "HTTP/1.1", method);

            Assert.Null(response.Header("Transfer-Encoding"));
            Assert.Equal(expectedContentLength, response.Header("Content-Length"));
            Assert.Equal(string.Empty, response.Body);
        }

        [Fact]
        public async Task Https11Client_ChunkedOriginStaysChunked()
        {
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                await context.Response.WriteAsync("part1-");
                await context.Response.Body.FlushAsync();
                await context.Response.WriteAsync("part2");
            }, useTls: true);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Get, origin.Url("/chunked"), HttpVersion.Version11);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Assert.True(response.Headers.TransferEncodingChunked == true, "the client did not receive chunked framing");
            Assert.False(response.Content.Headers.Contains("Content-Length"));
            Assert.Equal("part1-part2", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Http2Client_ReceivesOriginTrailers()
        {
            await using var origin = await LoopbackOrigin.StartAsync(TrailerOrigin, useTls: true);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Get, origin.Url("/trailer"), HttpVersion.Version20);

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal("payload", await response.Content.ReadAsStringAsync());
            Assert.True(response.TrailingHeaders.TryGetValues("x-checksum", out IEnumerable<string> values),
                "the trailer did not reach the HTTP/2 client");
            Assert.Equal("abc123", Assert.Single(values));
        }

        [Fact]
        public async Task Https11Client_GetsNoDanglingTrailerHeader()
        {
            await using var origin = await LoopbackOrigin.StartAsync(TrailerOrigin, useTls: true);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Get, origin.Url("/trailer"), HttpVersion.Version11);

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal("payload", await response.Content.ReadAsStringAsync());
            // Kestrel cannot send HTTP/1.1 trailers, so the announcement must go too.
            Assert.False(response.Headers.Contains("Trailer"), "the client was promised a trailer it cannot receive");
            Assert.Empty(response.TrailingHeaders);
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        public async Task ChunkedUpload_IsForwardedChunked(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var origin = await LoopbackOrigin.StartAsync(useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            byte[] body = Enumerable.Range(0, 30000).Select(i => (byte)(i * 7)).ToArray();
            using var request = NewRequest(HttpMethod.Post, origin.Url("/upload"), version);
            request.Content = new UnknownLengthContent(body);

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ObservedRequest atOrigin = Assert.Single(origin.Requests);
            Assert.Equal(body, atOrigin.Body);
            Assert.False(atOrigin.Headers.ContainsKey("Content-Length"), "a chunked upload was re-framed with Content-Length");
            if (!tls)
            {
                // HTTPS origins negotiate HTTP/2, which frames bodies without Transfer-Encoding.
                Assert.Contains("chunked", atOrigin.Headers.GetValueOrDefault("Transfer-Encoding") ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(body, Assert.Single(proxy.CapturedRequests).BodyBytes);
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task ContentLengthUpload_KeepsContentLength(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var origin = await LoopbackOrigin.StartAsync(useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();
            byte[] body = Enumerable.Range(0, 30000).Select(i => (byte)(i * 7)).ToArray();
            using var request = NewRequest(HttpMethod.Post, origin.Url("/upload"), version);
            request.Content = new ByteArrayContent(body);

            using HttpResponseMessage response = await client.SendAsync(request);

            ObservedRequest atOrigin = Assert.Single(origin.Requests);
            Assert.Equal(body, atOrigin.Body);
            Assert.Equal("30000", atOrigin.Headers.GetValueOrDefault("Content-Length"));
            Assert.False(atOrigin.Headers.ContainsKey("Transfer-Encoding"));
        }

        internal static Func<HttpContext, Task> HoldingOrigin(
            string contentType,
            string first,
            string rest,
            Task release,
            long? contentLength = null)
        {
            return async context =>
            {
                context.Response.ContentType = contentType;
                context.Response.ContentLength = contentLength;
                await context.Response.WriteAsync(first);
                await context.Response.Body.FlushAsync();
                await release.WaitAsync(OriginHoldLimit, context.RequestAborted);
                await context.Response.WriteAsync(rest);
            };
        }

        internal static async Task TrailerOrigin(HttpContext context)
        {
            context.Response.DeclareTrailer("x-checksum");
            await context.Response.WriteAsync("payload");
            context.Response.AppendTrailer("x-checksum", "abc123");
        }

        internal static HttpRequestMessage NewRequest(HttpMethod method, string url, Version version) =>
            new(method, url)
            {
                Version = version,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

        // Sends the request and reads until the expected text arrives, the body ends or the
        // first-bytes timeout expires; buffering proxies hold the headers until the body ends.
        internal static async Task<(HttpResponseMessage Response, string First)> ReadFirstBytesAsync(
            HttpClient client,
            HttpRequestMessage request,
            string expected)
        {
            using var timeout = new CancellationTokenSource(FirstBytesTimeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return (null, string.Empty);
            }

            Stream body = await response.Content.ReadAsStreamAsync(CancellationToken.None);
            var text = new StringBuilder();
            var buffer = new byte[4096];
            try
            {
                while (!text.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    int read = await body.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                        break;
                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
            }
            return (response, text.ToString());
        }

        internal static async Task<string> ReadToEndAsync(HttpResponseMessage response)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var rest = new MemoryStream();
            await body.CopyToAsync(rest, timeout.Token);
            return Encoding.ASCII.GetString(rest.ToArray());
        }

        // Streamed responses raise the capture event after the body has been forwarded.
        internal static async Task<ServerResponse> WaitForCapturedResponseAsync(ProxyTestHarness proxy)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                ServerResponse response = proxy.CapturedResponses.LastOrDefault(
                    r => r.StatusDescription != "Connection Established");
                if (response != null)
                    return response;
                await Task.Delay(20);
            }
            throw new TimeoutException("No response was captured.");
        }

        internal sealed class UnknownLengthContent(byte[] body) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                int size = (body.Length + 2) / 3;
                for (int offset = 0; offset < body.Length; offset += size)
                {
                    await stream.WriteAsync(body.AsMemory(offset, Math.Min(size, body.Length - offset)));
                    await stream.FlushAsync();
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        internal static async Task<RawResponse> SendRawThroughProxyAsync(
            int proxyPort,
            int originPort,
            string clientVersion,
            string method = "GET")
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort);
            NetworkStream stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"{method} http://127.0.0.1:{originPort}/raw {clientVersion}\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
            using var all = new MemoryStream();
            var buffer = new byte[8192];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                    all.Write(buffer, 0, read);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The proxy did not finish the response.");
            }
            return RawResponse.Parse(Encoding.ASCII.GetString(all.ToArray()));
        }

        internal sealed class RawResponse
        {
            private readonly Dictionary<string, string> _headers;

            private RawResponse(string statusLine, Dictionary<string, string> headers, string body)
            {
                StatusLine = statusLine;
                _headers = headers;
                Body = body;
            }

            public string StatusLine { get; }
            public string Body { get; }
            public string DecodedBody { get; private set; }
            public Dictionary<string, string> Trailers { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string Header(string name) => _headers.TryGetValue(name, out string value) ? value : null;

            public static RawResponse Parse(string raw)
            {
                int end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                Assert.True(end >= 0, $"incomplete response head: {raw}");
                string[] lines = raw[..end].Split("\r\n");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                var response = new RawResponse(lines[0], headers, raw[(end + 4)..]);
                if (string.Equals(response.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
                    response.Dechunk();
                return response;
            }

            private void Dechunk()
            {
                var decoded = new StringBuilder();
                int position = 0;
                while (true)
                {
                    int lineEnd = Body.IndexOf("\r\n", position, StringComparison.Ordinal);
                    Assert.True(lineEnd >= 0, $"truncated chunked body: {Body}");
                    string sizeLine = Body[position..lineEnd];
                    int extension = sizeLine.IndexOf(';');
                    int size = Convert.ToInt32(extension >= 0 ? sizeLine[..extension] : sizeLine, 16);
                    position = lineEnd + 2;
                    if (size == 0)
                        break;
                    decoded.Append(Body, position, size);
                    position += size + 2;
                }
                while (true)
                {
                    int lineEnd = Body.IndexOf("\r\n", position, StringComparison.Ordinal);
                    Assert.True(lineEnd >= 0, $"missing end of chunked body: {Body}");
                    if (lineEnd == position)
                        break;
                    string line = Body[position..lineEnd];
                    int colon = line.IndexOf(':');
                    Trailers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    position = lineEnd + 2;
                }
                Assert.Equal(Body.Length, position + 2);
                DecodedBody = decoded.ToString();
            }
        }

        // Raw HTTP/1.1 origin: Kestrel cannot send HTTP/1.1 trailers or chunk extensions.
        internal sealed class RawOrigin : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _accepting;

            private RawOrigin(TcpListener listener, Func<NetworkStream, string, CancellationToken, Task> respond)
            {
                _listener = listener;
                _accepting = AcceptAsync(respond);
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public static RawOrigin Start(string response) =>
                Start((stream, _, token) => stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).AsTask());

            public static RawOrigin Start(Func<NetworkStream, string, CancellationToken, Task> respond)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new RawOrigin(listener, respond);
            }

            private async Task AcceptAsync(Func<NetworkStream, string, CancellationToken, Task> respond)
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _ = Task.Run(async () =>
                        {
                            using (connection)
                            {
                                NetworkStream stream = connection.GetStream();
                                var head = new StringBuilder();
                                var buffer = new byte[8192];
                                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    int read = await stream.ReadAsync(buffer, _stop.Token);
                                    if (read == 0)
                                        return;
                                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                                }
                                await respond(stream, head.ToString(), _stop.Token);
                                await stream.FlushAsync(_stop.Token);
                                connection.Client.Shutdown(SocketShutdown.Send);
                                await Task.Delay(200, _stop.Token);
                            }
                        });
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                _listener.Stop();
                await _accepting;
                _stop.Dispose();
            }
        }
    }
}
