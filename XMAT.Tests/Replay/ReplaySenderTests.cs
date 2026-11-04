// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // ReplaySender against loopback servers, and against an in-memory handler where a real server cannot help (HTTPS).
    public class ReplaySenderTests
    {
        [Fact]
        public async Task SendAsync_ReturnsAnyStatusAndTheRequestAsBuilt()
        {
            await using var server = new LoopbackHttpServer(
                _ => new LoopbackResponse(503, "Service Unavailable", "X-Server: test\r\n", "busy"u8.ToArray(), Chunked: true));
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = server.Origin + "/path?q=1",
                HeadersText = "X-Test: one\r\nContent-Type: text/plain",
                BodyText = "hello",
            };

            ReplayExchange exchange = await sender.SendAsync(draft, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 503, null), (exchange.Outcome, exchange.StatusCode, exchange.Code));
            Assert.Equal("busy"u8.ToArray(), exchange.Response.Body);
            Assert.Contains(new KeyValuePair<string, string>("X-Server", "test"), exchange.Response.Headers);
            // The body is no longer chunked, so the saved message must not say it is.
            Assert.DoesNotContain(exchange.Response.Headers, header => header.Key == "Transfer-Encoding");
            Assert.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", Encoding.Latin1.GetString(exchange.Response.ToBytes()));
            Assert.EndsWith("\r\n\r\nbusy", Encoding.Latin1.GetString(exchange.Response.ToBytes()));
            Assert.Equal("POST /path?q=1 HTTP/1.1", exchange.Request.StartLine);
            Assert.Contains(new KeyValuePair<string, string>("Host", $"127.0.0.1:{server.Port}"), exchange.Request.Headers);
            Assert.Contains(new KeyValuePair<string, string>("X-Test", "one"), exchange.Request.Headers);
            Assert.Contains(new KeyValuePair<string, string>("Content-Length", "5"), exchange.Request.ContentHeaders);
            LoopbackRequest received = Assert.Single(server.Requests);
            Assert.Equal(("POST", "/path?q=1", "one"), (received.Method, received.Target, received.Header("X-Test")));
            Assert.Equal("hello"u8.ToArray(), received.Body);
        }

        [Fact]
        public async Task SendAsync_FollowsSameOriginRedirectsOnlyUpToTheLimit()
        {
            await using var server = new LoopbackHttpServer(request => request.Target switch
            {
                "/a" => LoopbackResponse.Redirect(302, "/b"),
                "/b" => LoopbackResponse.Redirect(307, "/c?x=1"),
                _ => LoopbackResponse.Ok("done"),
            });
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft { Url = server.Origin + "/a", HeadersText = "Authorization: Bearer same-origin" };

            ReplayExchange followed = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 2 }, CancellationToken.None);
            ReplayExchange limited = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 1 }, CancellationToken.None);
            ReplayExchange notFollowed = await sender.SendAsync(draft, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Equal((200, 2, null), (followed.StatusCode, followed.Redirects, followed.Code));
            Assert.Equal("GET /c?x=1 HTTP/1.1", followed.Request.StartLine);
            Assert.Equal((307, 1, ReplaySender.RedirectLimitCode), (limited.StatusCode, limited.Redirects, limited.Code));
            Assert.Equal((302, 0, (string)null), (notFollowed.StatusCode, notFollowed.Redirects, notFollowed.Code));
            Assert.Equal(new[] { "/a", "/b", "/c?x=1", "/a", "/b", "/a" }, server.Requests.Select(request => request.Target));
            Assert.All(server.Requests, request => Assert.Equal("Bearer same-origin", request.Header("Authorization")));
        }

        [Theory]
        [InlineData(303, "PUT", "GET", false)]
        [InlineData(302, "POST", "GET", false)]
        [InlineData(301, "POST", "GET", false)]
        [InlineData(302, "PUT", "PUT", true)]
        [InlineData(307, "POST", "POST", true)]
        [InlineData(308, "PUT", "PUT", true)]
        public async Task SendAsync_RedirectsChangeTheMethodAsBrowsersDo(
            int status, string method, string expectedMethod, bool keepsBody)
        {
            await using var server = new LoopbackHttpServer(
                request => request.Target == "/from" ? LoopbackResponse.Redirect(status, "/to") : LoopbackResponse.Ok());
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft
            {
                Method = method,
                Url = server.Origin + "/from",
                HeadersText = "Content-Type: text/plain",
                BodyText = "payload",
            };

            ReplayExchange exchange = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 1 }, CancellationToken.None);

            Assert.Equal((200, 1), (exchange.StatusCode, exchange.Redirects));
            LoopbackRequest redirected = server.Requests[1];
            Assert.Equal(("/to", expectedMethod), (redirected.Target, redirected.Method));
            Assert.Equal(keepsBody ? "payload"u8.ToArray() : Array.Empty<byte>(), redirected.Body);
            Assert.Equal(keepsBody ? "text/plain" : null, redirected.Header("Content-Type"));
        }

        [Fact]
        public async Task SendAsync_CrossOriginRedirect_StopsUnlessAllowed_AndThenDropsCredentials()
        {
            await using var target = new LoopbackHttpServer(_ => LoopbackResponse.Ok("other origin"));
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Redirect(302, target.Origin + "/landing"));
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft
            {
                Url = origin.Origin + "/start",
                HeadersText = "Authorization: Bearer secret\r\nCookie: session=secret\r\nX-Trace: kept",
            };

            ReplayExchange blocked = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 3 }, CancellationToken.None);
            Assert.Equal((302, 0, ReplaySender.RedirectOriginChangeCode), (blocked.StatusCode, blocked.Redirects, blocked.Code));
            Assert.Empty(target.Requests);

            ReplayExchange allowed = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 3, AllowOriginChange = true }, CancellationToken.None);
            Assert.Equal((200, 1, null), (allowed.StatusCode, allowed.Redirects, allowed.Code));
            LoopbackRequest landed = Assert.Single(target.Requests);
            Assert.Null(landed.Header("Authorization"));
            Assert.Null(landed.Header("Cookie"));
            Assert.Equal("kept", landed.Header("X-Trace"));
            Assert.All(origin.Requests, request => Assert.Equal("Bearer secret", request.Header("Authorization")));
        }

        [Theory]
        [InlineData("http://example.test/next", ReplaySender.RedirectInsecureCode)]
        [InlineData("ftp://example.test/next", ReplaySender.RedirectUnsupportedCode)]
        [InlineData("https://user:pass@example.test/next", ReplaySender.RedirectUnsupportedCode)]
        public async Task SendAsync_NeverFollowsDowngradesCredentialsOrOtherSchemes(string location, string code)
        {
            using var handler = new StubHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.TryAddWithoutValidation("Location", location);
                return Task.FromResult(response);
            });
            using var invoker = new HttpMessageInvoker(handler);
            using var sender = new ReplaySender(invoker);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = "https://example.test/start" },
                ReplaySendOptions.Default with { MaxRedirects = 5, AllowOriginChange = true },
                CancellationToken.None);

            Assert.Equal((302, code), (exchange.StatusCode, exchange.Code));
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_TimeoutIsAResult_ButTheCallersCancellationThrows()
        {
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(Hang: true));
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft { Url = server.Origin + "/slow" };

            ReplayExchange timedOut = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { Timeout = TimeSpan.FromMilliseconds(200) }, CancellationToken.None);
            Assert.Equal((ReplayOutcome.TimedOut, ReplaySender.TimeoutCode), (timedOut.Outcome, timedOut.Code));
            Assert.Null(timedOut.Response);
            Assert.Equal("GET /slow HTTP/1.1", timedOut.Request.StartLine);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sender.SendAsync(draft, ReplaySendOptions.Default, cancellation.Token));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendAsync_KeepsOnlyTheStatusAndHeadersOfAnOversizedResponse(bool chunked)
        {
            await using var server = new LoopbackHttpServer(
                _ => new LoopbackResponse(Headers: "X-Kept: yes\r\n", Body: new byte[2048], Chunked: chunked));
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft { Url = server.Origin + "/" };

            ReplayExchange tooLarge = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxResponseBytes = 2047 }, CancellationToken.None);
            ReplayExchange fits = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxResponseBytes = 2048 }, CancellationToken.None);

            Assert.Equal((ReplayOutcome.TooLarge, ReplaySender.TooLargeCode, 200), (tooLarge.Outcome, tooLarge.Code, tooLarge.StatusCode));
            Assert.Empty(tooLarge.Response.Body);
            Assert.Contains(new KeyValuePair<string, string>("X-Kept", "yes"), tooLarge.Response.Headers);
            Assert.Equal((ReplayOutcome.Completed, 2048), (fits.Outcome, fits.Response.Body.Length));
        }

        [Fact]
        public async Task SendAsync_ConnectionFailureIsAResultWithItsError()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var sender = new ReplaySender();

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = $"http://127.0.0.1:{port}/" }, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.IsType<HttpRequestException>(exchange.Error);
            Assert.Null(exchange.Response);
            Assert.NotNull(exchange.Request);
        }

        private sealed class StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
        {
            internal int CallCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                ++CallCount;
                return callback(request, cancellationToken);
            }
        }
    }
}
