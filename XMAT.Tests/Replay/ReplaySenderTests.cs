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
        [InlineData(303, "HEAD", "HEAD", false)]
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

        // A server never decides where a request's headers and body go next: no redirect to another origin is followed,
        // whatever it carries and however many redirects may be followed.
        [Theory]
        [InlineData(302, "GET")]
        [InlineData(307, "POST")]
        [InlineData(308, "PUT")]
        public async Task SendAsync_NeverFollowsARedirectToAnotherOrigin(int status, string method)
        {
            await using var target = new LoopbackHttpServer(_ => LoopbackResponse.Ok("other origin"));
            await using var origin = new LoopbackHttpServer(_ => LoopbackResponse.Redirect(status, target.Origin + "/landing"));
            using var sender = new ReplaySender();
            var draft = new RequestReplayDraft
            {
                Method = method,
                Url = origin.Origin + "/start",
                HeadersText = "Authorization: Bearer secret\r\nCookie: session=secret\r\nX-Authorization: playfab-secret\r\n" +
                    "X-EntityToken: entity-secret\r\nContent-Type: application/json",
                BodyText = "{\"secret\":\"body\"}",
            };

            ReplayExchange exchange = await sender.SendAsync(
                draft, ReplaySendOptions.Default with { MaxRedirects = 10 }, CancellationToken.None);

            Assert.Equal((status, 0, ReplaySender.RedirectOriginChangeCode), (exchange.StatusCode, exchange.Redirects, exchange.Code));
            Assert.Equal(0, target.Connections);
            LoopbackRequest sent = Assert.Single(origin.Requests);
            Assert.Equal("playfab-secret", sent.Header("X-Authorization"));
        }

        [Theory]
        [InlineData("http://example.test/next", ReplaySender.RedirectInsecureCode)]
        [InlineData("ftp://example.test/next", ReplaySender.RedirectUnsupportedCode)]
        [InlineData("https://user:pass@example.test/next", ReplaySender.RedirectUnsupportedCode)]
        [InlineData("https://[bad", ReplaySender.RedirectUnsupportedCode)]
        [InlineData("https://other.test/next", ReplaySender.RedirectOriginChangeCode)]
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
                ReplaySendOptions.Default with { MaxRedirects = 5 },
                CancellationToken.None);

            Assert.Equal((302, code), (exchange.StatusCode, exchange.Code));
            Assert.Equal(1, handler.CallCount);
        }

        // Which Location to follow must be unambiguous: none, or more than one, is not followed.
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public async Task SendAsync_NeedsExactlyOneLocationToFollowARedirect(int locations)
        {
            using var handler = new StubHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                for (int index = 0; index < locations; index++)
                {
                    response.Headers.TryAddWithoutValidation("Location", $"/next{index}");
                }

                return Task.FromResult(response);
            });
            using var invoker = new HttpMessageInvoker(handler);
            using var sender = new ReplaySender(invoker);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = "https://example.test/start" },
                ReplaySendOptions.Default with { MaxRedirects = 5 },
                CancellationToken.None);

            Assert.Equal((307, ReplaySender.RedirectUnsupportedCode), (exchange.StatusCode, exchange.Code));
            Assert.Equal(1, handler.CallCount);
        }

        // A fragment in Location is never sent; the request goes to the path and query.
        [Fact]
        public async Task SendAsync_FollowsALocationWithAFragmentWithoutSendingIt()
        {
            await using var server = new LoopbackHttpServer(request => request.Target == "/start"
                ? LoopbackResponse.Redirect(302, "/next?x=1#fragment")
                : LoopbackResponse.Ok());
            using var sender = new ReplaySender();

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/start" },
                ReplaySendOptions.Default with { MaxRedirects = 1 },
                CancellationToken.None);

            Assert.Equal((200, 1), (exchange.StatusCode, exchange.Redirects));
            Assert.Equal("/next?x=1", server.Requests[1].Target);
            Assert.Equal("GET /next?x=1 HTTP/1.1", exchange.Request.StartLine);
        }

        // Anything other than the caller's cancellation that a send provokes ends that send as failed, with its error.
        [Fact]
        public async Task SendAsync_AnyOtherFailureOfTheSendIsAResult()
        {
            using var handler = new StubHandler((_, _) => throw new InvalidOperationException("stub failure"));
            using var invoker = new HttpMessageInvoker(handler);
            using var sender = new ReplaySender(invoker);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = "https://example.test/" }, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.IsType<InvalidOperationException>(exchange.Error);
        }

        // A response without a body is never read, so a server cannot make one look too large or fail it.
        [Fact]
        public async Task SendAsync_NeverReadsTheBodyOfAHeadResponse()
        {
            using var handler = new StubHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnreadableContent(33_554_432) }));
            using var invoker = new HttpMessageInvoker(handler);
            using var sender = new ReplaySender(invoker);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Method = "HEAD", Url = "https://example.test/" },
                ReplaySendOptions.Default with { MaxResponseBytes = 1024 },
                CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
            Assert.Empty(exchange.Response.Body);
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

        // HEAD, 204 and 304 responses have no body, so a Content-Length over the limit is no reason to refuse them.
        [Theory]
        [InlineData("HEAD", 200, "OK")]
        [InlineData("GET", 304, "Not Modified")]
        public async Task SendAsync_ResponsesWithoutABodyAreNeverTooLarge(string method, int status, string reason)
        {
            await using var server = new LoopbackHttpServer(
                _ => new LoopbackResponse(status, reason, "Content-Length: 33554432\r\n", NoLength: true));
            using var sender = new ReplaySender();

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Method = method, Url = server.Origin + "/large" },
                ReplaySendOptions.Default with { MaxResponseBytes = 1024 },
                CancellationToken.None);

            Assert.Equal((ReplayOutcome.Completed, status, null), (exchange.Outcome, exchange.StatusCode, exchange.Code));
            Assert.Empty(exchange.Response.Body);
        }

        // A Location that the URL parser cannot take back in its escaped form ends that send, never the whole run.
        [Fact]
        public async Task SendAsync_AnUnusableRedirectTargetIsAResult()
        {
            await using var server = new LoopbackHttpServer(request => request.Target == "/start"
                ? LoopbackResponse.Redirect(302, "/a" + new string(' ', 22000) + "b")
                : LoopbackResponse.Ok());
            using var sender = new ReplaySender();

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = server.Origin + "/start" },
                ReplaySendOptions.Default with { MaxRedirects = 1 },
                CancellationToken.None);

            Assert.True(
                exchange.Outcome is ReplayOutcome.Completed or ReplayOutcome.Failed,
                $"{exchange.Outcome} {exchange.Code}");
            Assert.Equal("/start", server.Requests[0].Target);
        }

        // The body that SetBodyBytes gave goes out unchanged, and the draft keeps the same array instead of a copy.
        [Fact]
        public async Task SendAsync_SendsASharedBodyWithoutCopyingItIntoTheDraft()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var sender = new ReplaySender();
            byte[] body = Enumerable.Range(0, 70_000).Select(index => (byte)index).ToArray();
            var draft = new RequestReplayDraft { Method = "PUT", Url = server.Origin + "/upload" };

            draft.SetBodyBytes(body);
            ReplayExchange exchange = await sender.SendAsync(draft, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Same(body, draft.GetBodyBytes());
            Assert.True(draft.BodyIsBase64);
            Assert.Equal(Convert.ToBase64String(body), draft.BodyText);
            Assert.Equal(ReplayOutcome.Completed, exchange.Outcome);
            Assert.Equal(body, Assert.Single(server.Requests).Body);
            Assert.Same(body, exchange.Request.Body);
        }

        [Fact]
        public async Task SendAsync_ConnectionFailureIsAResultWithItsError()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
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

        // Content that declares a length but fails if anything reads it.
        private sealed class UnreadableContent : HttpContent
        {
            internal UnreadableContent(long length)
            {
                Headers.ContentLength = length;
            }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) =>
                throw new Xunit.Sdk.XunitException("A body without content was read.");

            protected override bool TryComputeLength(out long length)
            {
                length = Headers.ContentLength ?? 0;
                return true;
            }

            protected override Task<Stream> CreateContentReadStreamAsync() =>
                throw new Xunit.Sdk.XunitException("A body without content was read.");
        }
    }
}
