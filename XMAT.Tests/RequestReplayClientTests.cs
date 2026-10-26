// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class RequestReplayClientTests
    {
        [Fact]
        public void DefaultHandler_IsIsolatedAndKeepsCertificateValidation()
        {
            using var handler = RequestReplayClient.CreateHandler();
            Assert.False(handler.UseProxy);
            Assert.False(handler.UseCookies);
            Assert.False(handler.AllowAutoRedirect);
            Assert.Null(handler.Credentials);
            Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        }

        [Fact]
        public async Task SendAsync_ReturnsIndependentResponseIncludingHttpErrors()
        {
            var capture = RequestReplayDraftTests.Capture("POST", "/", "original"u8.ToArray());
            var draft = RequestReplayDraft.FromCapture(capture);
            draft.BodyText = "edited";
            using var handler = new StubHandler(async (request, token) =>
            {
                Assert.Equal("edited", await request.Content.ReadAsStringAsync(token));
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Version = HttpVersion.Version20,
                    Content = new ByteArrayContent([0, 255, 1])
                };
                response.Headers.TryAddWithoutValidation("Set-Cookie", ["a=1", "b=2"]);
                response.Content.Headers.ContentType = new("application/octet-stream");
                return response;
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);

            var result = await client.SendAsync(draft, confirmed: true, CancellationToken.None);

            Assert.Equal("400", result.Response.Status);
            Assert.Equal("HTTP/2", result.Response.Version);
            Assert.Equal(new byte[] { 0, 255, 1 }, result.Response.BodyBytes);
            Assert.Equal(["a=1", "b=2"], result.Response.Headers.GetHeaderValuesAsList("Set-Cookie"));
            Assert.True(result.Duration >= TimeSpan.Zero);
            Assert.Equal("original"u8.ToArray(), capture.RequestBody);
            Assert.Equal("old response"u8.ToArray(), capture.ResponseBody);
            Assert.Equal("200", capture.Status);
        }

        [Fact]
        public async Task SendAsync_RefusesUnconfirmedUnsafeRequestBeforeNetwork()
        {
            using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var draft = new RequestReplayDraft { Url = "https://example.com/", Method = "DELETE" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendAsync(draft, confirmed: false, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_PropagatesCancellationAndCanSendAgain()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            using var cancellation = new CancellationTokenSource();
            var draft = new RequestReplayDraft { Url = "https://example.com/" };
            Task sending = client.SendAsync(draft, confirmed: false, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

            handler.Callback = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var result = await client.SendAsync(draft, confirmed: false, CancellationToken.None);
            Assert.Equal("204", result.Response.Status);
        }

        [Fact]
        public async Task SendAsync_PropagatesTransportErrorsWithoutSuccessFallback()
        {
            using var handler = new StubHandler((_, _) => throw new HttpRequestException("test failure"));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
                new RequestReplayDraft { Url = "https://example.com/" }, false, CancellationToken.None));
        }

        [Fact]
        public async Task DefaultClient_DoesNotFollowRedirectsOrReuseResponseCookies()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = new List<string>();
            Task server = Task.Run(async () =>
            {
                for (int i = 0; i < 2; ++i)
                {
                    using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var headers = new StringBuilder();
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
                        headers.AppendLine(line);
                    received.Add(headers.ToString());
                    string response = i == 0
                        ? "HTTP/1.1 302 Found\r\nLocation: /redirected\r\nSet-Cookie: private=1\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        response + "Content-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
                }
            }, timeout.Token);
            using var client = new RequestReplayClient();
            var draft = new RequestReplayDraft { Url = $"http://127.0.0.1:{port}/original" };

            var first = await client.SendAsync(draft, false, timeout.Token);
            Assert.Equal("302", first.Response.Status);
            var second = await client.SendAsync(draft, false, timeout.Token);
            await server.WaitAsync(timeout.Token);

            Assert.Equal("200", second.Response.Status);
            Assert.All(received, headers =>
            {
                Assert.StartsWith("GET /original HTTP/1.1", headers);
                Assert.DoesNotContain("Cookie:", headers, StringComparison.OrdinalIgnoreCase);
            });
        }

        private sealed class StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Callback = callback;
            internal int CallCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                ++CallCount;
                return Callback(request, cancellationToken);
            }
        }
    }
}
