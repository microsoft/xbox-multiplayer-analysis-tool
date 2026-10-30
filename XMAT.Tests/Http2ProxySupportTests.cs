// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.Scripting;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class Http2ProxySupportTests
    {
        [Fact]
        public async Task CreateClientRequestAsync_MapsHttp2RequestAndStreamId()
        {
            var context = new DefaultHttpContext();
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = "POST";
            context.Request.Host = new HostString("example.com", 443);
            context.Request.Path = "/sessions";
            context.Request.QueryString = new QueryString("?include=members");
            context.Request.Headers.Append("Warning", "199 first");
            context.Request.Headers.Append("Warning", "299 second");
            context.Request.Body = new MemoryStream("payload"u8.ToArray());
            context.Features.Set<IHttp2StreamIdFeature>(new Http2StreamIdFeature(7));

            ClientRequest request = await AspNetCoreRequestAdapter.CreateAsync(
                context,
                requestNumber: 42,
                CancellationToken.None);

            Assert.Equal(42, request.RequestNumber);
            Assert.Equal("HTTP/2", request.Version);
            Assert.Equal(7, request.StreamId);
            Assert.Equal("https", request.Scheme);
            Assert.Equal("example.com", request.Host);
            Assert.Equal(443, request.Port);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/sessions?include=members", request.Path);
            Assert.Equal(["199 first", "299 second"], request.Headers.GetHeaderValuesAsList("Warning"));
            Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(request.BodyBytes));
        }

        [Theory]
        [InlineData(new[] { "Chunked" }, null)]
        [InlineData(new[] { "gzip ,\tchunked" }, new[] { "gzip" })]
        [InlineData(new[] { "gzip", "chunked" }, new[] { "gzip" })]
        public async Task CreateClientRequestAsync_DropsOnlyDecodedChunkedCoding(
            string[] transferEncodingValues,
            string[] expectedStoredValues)
        {
            DefaultHttpContext context = CreateRequestContext();
            context.Request.Protocol = "HTTP/1.1";
            foreach (string value in transferEncodingValues)
                context.Request.Headers.Append("Transfer-Encoding", value);

            ClientRequest request = await AspNetCoreRequestAdapter.CreateAsync(
                context,
                requestNumber: 1,
                CancellationToken.None);

            Assert.Equal(
                expectedStoredValues,
                request.Headers.GetHeaderValuesAsList("Transfer-Encoding"));
        }

        [Fact]
        public void CreateUpstreamRequest_PrefersHttp2WithFallback()
        {
            var request = new ClientRequest
            {
                Method = "GET",
                Path = "/sessions",
                Version = "HTTP/2",
                Host = "example.com",
                Port = 443,
                Scheme = "https"
            };
            request.Headers["Accept"] = "application/json";

            using HttpRequestMessage message = ProxyHttpRequestFactory.Create(
                request,
                new Uri("https://example.com/sessions"));

            Assert.Equal(HttpVersion.Version20, message.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, message.VersionPolicy);
            Assert.Equal("application/json", message.Headers.Accept.Single().MediaType);
        }

        [Fact]
        public void CreateUpstreamRequest_RemovesHopByHopHeaders()
        {
            var request = new ClientRequest
            {
                Method = "GET",
                BodyBytes = []
            };
            request.Headers["Connection"] = "keep-alive";
            request.Headers["Proxy-Connection"] = "keep-alive";
            request.Headers["Transfer-Encoding"] = "chunked";
            request.Headers["X-Correlation-ID"] = "123";

            using HttpRequestMessage message = ProxyHttpRequestFactory.Create(
                request,
                new Uri("https://example.com/"));

            Assert.False(message.Headers.Contains("Connection"));
            Assert.False(message.Headers.Contains("Proxy-Connection"));
            Assert.False(message.Headers.Contains("Transfer-Encoding"));
            Assert.Equal("123", message.Headers.GetValues("X-Correlation-ID").Single());
        }

        [Fact]
        public void CreateUpstreamRequest_RecalculatesContentLength()
        {
            var request = new ClientRequest
            {
                Method = "POST",
                BodyBytes = "updated"u8.ToArray()
            };
            request.ContentHeaders["Content-Length"] = "999";

            using HttpRequestMessage message = ProxyHttpRequestFactory.Create(
                request,
                new Uri("https://example.com/"));

            Assert.Equal(7, message.Content.Headers.ContentLength);
        }

        [Fact]
        public void CreateUpstreamRequest_RemovesConnectionNominatedHeaders()
        {
            var request = new ClientRequest { Method = "POST", BodyBytes = [0, 255, 42] };
            request.Headers.Add("Connection", "keep-alive, X-First");
            request.Headers.Add("Connection", " x-SECOND , Content-Type, ,");
            request.Headers["X-First"] = "connection-only";
            request.Headers["X-Second"] = "connection-only";
            request.Headers["X-End-To-End"] = "retained";
            request.ContentHeaders["Content-Type"] = "application/octet-stream";

            using var message = ProxyHttpRequestFactory.Create(
                request, new Uri("https://example.com/"));

            Assert.False(message.Headers.Contains("Connection"));
            Assert.False(message.Headers.Contains("X-First"));
            Assert.False(message.Headers.Contains("X-Second"));
            Assert.Null(message.Content.Headers.ContentType);
            Assert.Equal("retained", message.Headers.GetValues("X-End-To-End").Single());
            Assert.Equal(3, message.Content.Headers.ContentLength);
            Assert.Equal("connection-only", request.Headers["X-First"]);
            Assert.Equal("application/octet-stream", request.ContentHeaders["Content-Type"]);
        }

        [Fact]
        public async Task InvokeAsync_RemovesConnectionNominatedResponseHeaders()
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(53000, new InterceptedTunnelContext(1000, new ClientRequest()));
            var upstreamHandler = new RecordingHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("response-body")
                }
            };
            upstreamHandler.Response.Headers.TryAddWithoutValidation(
                "Connection", new[] { " X-First ", "x-second, Content-Type" });
            upstreamHandler.Response.Headers.TryAddWithoutValidation("X-First", "connection-only");
            upstreamHandler.Response.Headers.TryAddWithoutValidation("X-Second", "connection-only");
            upstreamHandler.Response.Headers.TryAddWithoutValidation("X-End-To-End", "retained");
            ServerResponse captured = null;
            proxy.ReceivedWebResponse += (_, args) => captured = args.Response;
            using var client = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(proxy, client, registry);
            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53000;
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Host = new HostString("example.com");
            context.Response.Body = new MemoryStream();

            await application.InvokeAsync(context);

            Assert.False(context.Response.Headers.ContainsKey("Connection"));
            Assert.False(context.Response.Headers.ContainsKey("X-First"));
            Assert.False(context.Response.Headers.ContainsKey("X-Second"));
            Assert.False(context.Response.Headers.ContainsKey("Content-Type"));
            Assert.Equal("retained", context.Response.Headers["X-End-To-End"].ToString());
            Assert.Equal("connection-only", captured.Headers["X-First"]);
            Assert.Equal("text/plain; charset=utf-8", captured.ContentHeaders["Content-Type"]);
            Assert.Equal("response-body", Encoding.UTF8.GetString(
                ((MemoryStream)context.Response.Body).ToArray()));
        }

        [Fact]
        public void TryRegister_DoesNotReplaceExistingTunnel()
        {
            var registry = new InterceptedTunnelRegistry();
            var first = new InterceptedTunnelContext(1000, new ClientRequest());
            var second = new InterceptedTunnelContext(1001, new ClientRequest());

            Assert.True(registry.TryRegister(53000, first));
            Assert.False(registry.TryRegister(53000, second));
            Assert.True(registry.TryGet(53000, out var registered));
            Assert.Same(first, registered);
        }

        [Fact]
        public async Task InvokeAsync_ForwardsHttp2RequestAndRaisesCaptureEvents()
        {
            var proxy = new WebServiceProxy();
            proxy.Reset();
            var registry = new InterceptedTunnelRegistry();
            var connectRequest = new ClientRequest
            {
                RequestNumber = proxy.GetNextRequestID(),
                Method = "CONNECT",
                Path = "example.com:443",
                Version = "HTTP/1.1",
                Scheme = "https",
                Host = "example.com",
                Port = 443
            };
            registry.Register(
                remotePort: 53000,
                new InterceptedTunnelContext(1000, connectRequest));

            ClientRequest capturedRequest = null;
            ServerResponse capturedResponse = null;
            proxy.ReceivedWebRequest += (_, args) => capturedRequest = args.Request;
            proxy.ReceivedWebResponse += (_, args) => capturedResponse = args.Response;

            var upstreamHandler = new RecordingHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version20,
                    Content = new StringContent("response-body")
                }
            };
            upstreamHandler.Response.Headers.TryAddWithoutValidation("Warning", "199 upstream");
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(proxy, httpClient, registry);

            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53000;
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = "POST";
            context.Request.Host = new HostString("example.com", 443);
            context.Request.Path = "/sessions";
            context.Request.QueryString = new QueryString("?include=members");
            context.Request.Body = new MemoryStream("request-body"u8.ToArray());
            context.Response.Body = new MemoryStream();
            context.Features.Set<IHttp2StreamIdFeature>(new Http2StreamIdFeature(3));

            await application.InvokeAsync(context);

            Assert.NotNull(capturedRequest);
            Assert.Equal("HTTP/2", capturedRequest.Version);
            Assert.Equal(3, capturedRequest.StreamId);
            Assert.Equal("request-body", System.Text.Encoding.UTF8.GetString(capturedRequest.BodyBytes));
            Assert.Equal(HttpVersion.Version20, upstreamHandler.Request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, upstreamHandler.Request.VersionPolicy);
            Assert.Equal(
                "https://example.com/sessions?include=members",
                upstreamHandler.Request.RequestUri.AbsoluteUri);

            Assert.NotNull(capturedResponse);
            Assert.Equal("HTTP/2", capturedResponse.Version);
            Assert.Equal(3, capturedResponse.StreamId);
            Assert.Equal(200, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            Assert.Equal("response-body", await new StreamReader(context.Response.Body).ReadToEndAsync());
        }

        [Fact]
        public async Task InvokeAsync_ReturnsBadGatewayForUnknownTunnel()
        {
            var proxy = new WebServiceProxy();
            using var httpClient = new HttpClient(new RecordingHandler());
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                new InterceptedTunnelRegistry());
            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53001;

            await application.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("GET", 100, false, null)]
        [InlineData("GET", 101, true, null)]
        [InlineData("GET", 103, true, null)]
        [InlineData("GET", 204, false, null)]
        [InlineData("GET", 204, true, null)]
        [InlineData("GET", 205, false, 0L)]
        [InlineData("GET", 205, true, 0L)]
        [InlineData("GET", 304, false, 123L)]
        [InlineData("GET", 304, true, 123L)]
        [InlineData("HEAD", 200, false, 123L)]
        [InlineData("HEAD", 200, true, 123L)]
        public async Task InvokeAsync_DoesNotWriteBodyForBodylessResponses(
            string method,
            int statusCode,
            bool scriptAddsBody,
            long? expectedContentLength)
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            proxy.ReceivedWebResponse += (_, args) =>
            {
                if (scriptAddsBody)
                {
                    args.Response.BodyBytes = "script-added body"u8.ToArray();
                }
            };
            var upstreamHandler = new RecordingHandler
            {
                Response = new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new ByteArrayContent([])
                }
            };
            upstreamHandler.Response.Content.Headers.ContentLength = 123;
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                registry);
            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53000;
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = method;
            context.Request.Host = new HostString("example.com");
            context.Request.Path = "/";
            context.Response.Body = new BodylessResponseStream();

            await application.InvokeAsync(context);

            Assert.Equal(statusCode, context.Response.StatusCode);
            Assert.Equal(expectedContentLength, context.Response.ContentLength);
        }

        [Theory]
        [InlineData("GET", 103, null)]
        [InlineData("GET", 204, null)]
        [InlineData("GET", 304, 123L)]
        [InlineData("HEAD", 200, 123L)]
        public async Task InvokeAsync_KeepsChunkedCodingOfBodylessResponses(
            string method,
            int statusCode,
            long? expectedContentLength)
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            ServerResponse capturedResponse = null;
            proxy.ReceivedWebResponse += (_, args) => capturedResponse = args.Response;
            var upstreamHandler = new RecordingHandler
            {
                Response = new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new ByteArrayContent([])
                }
            };
            // HttpClient decodes no body for these responses, so the header
            // only describes the representation and must stay captured.
            upstreamHandler.Response.Headers.TransferEncodingChunked = true;
            upstreamHandler.Response.Content.Headers.ContentLength = 123;
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                registry);
            var context = CreateRequestContext();
            context.Request.Method = method;
            context.Response.Body = new BodylessResponseStream();

            await application.InvokeAsync(context);

            Assert.Equal(statusCode, context.Response.StatusCode);
            Assert.Equal(expectedContentLength, context.Response.ContentLength);
            Assert.False(context.Response.Headers.ContainsKey("Transfer-Encoding"));
            Assert.NotNull(capturedResponse);
            Assert.Equal(
                ["chunked"],
                capturedResponse.Headers.GetHeaderValuesAsList("Transfer-Encoding"));
        }

        [Fact]
        public async Task InvokeAsync_CancelsPendingUpstreamRequestDuringShutdown()
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            using var stopping = new CancellationTokenSource();
            var upstreamHandler = new CancelableHandler();
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                registry,
                stoppingToken: stopping.Token);
            var context = CreateRequestContext();
            var lifetime = new TrackingRequestLifetimeFeature();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            var responseCaptured = false;
            proxy.ReceivedWebResponse += (_, _) => responseCaptured = true;

            var requestTask = application.InvokeAsync(context);
            try
            {
                await upstreamHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await stopping.CancelAsync();
                await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(upstreamHandler.Canceled.Task.IsCompletedSuccessfully);
            Assert.True(lifetime.Aborted);
            Assert.False(responseCaptured);
        }

        [Fact]
        public async Task InvokeAsync_CancelsPendingRequestPolicyDuringClientAbort()
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            var policyStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var policyCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.WebRequestPolicyAsync = async (_, cancellationToken) =>
            {
                policyStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return true;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    policyCanceled.TrySetResult();
                    throw;
                }
            };
            var upstreamHandler = new RecordingHandler();
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                registry);
            using var requestAbort = new CancellationTokenSource();
            var lifetime = new TrackingRequestLifetimeFeature
            {
                RequestAborted = requestAbort.Token
            };
            var context = CreateRequestContext();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

            Task requestTask = application.InvokeAsync(context);
            await policyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await requestAbort.CancelAsync();
            await requestTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(policyCanceled.Task.IsCompletedSuccessfully);
            Assert.True(lifetime.Aborted);
            Assert.Null(upstreamHandler.Request);
        }

        [Fact]
        public async Task InvokeAsync_DoesNotForwardNewRequestsAfterShutdownBegins()
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            using var stopping = new CancellationTokenSource();
            await stopping.CancelAsync();
            var upstreamHandler = new RecordingHandler();
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                httpClient,
                registry,
                stoppingToken: stopping.Token);
            var context = CreateRequestContext();
            var lifetime = new TrackingRequestLifetimeFeature();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            var requestCaptured = false;
            proxy.ReceivedWebRequest += (_, _) => requestCaptured = true;

            await application.InvokeAsync(context);

            Assert.True(lifetime.Aborted);
            Assert.False(requestCaptured);
            Assert.Null(upstreamHandler.Request);
        }

        [Fact]
        public async Task InvokeAsync_ReturnsGatewayTimeoutForUpstreamTimeout()
        {
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53000,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            using var httpClient = new HttpClient(
                new ThrowingHandler(new TaskCanceledException("Upstream timed out.")));
            var application = new InterceptedProxyApplication(proxy, httpClient, registry);
            var context = CreateRequestContext();

            await application.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        }

        [Fact]
        public async Task InvokeAsync_ReturnsBadGatewayWhenUpstreamFails()
        {
            var proxy = new WebServiceProxy();
            proxy.Reset();
            var registry = new InterceptedTunnelRegistry();
            registry.Register(
                53002,
                new InterceptedTunnelContext(1000, new ClientRequest()));
            using var httpClient = new HttpClient(
                new ThrowingHandler(new HttpRequestException("upstream unavailable")));
            var application = new InterceptedProxyApplication(proxy, httpClient, registry);
            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53002;
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Host = new HostString("example.com");
            context.Request.Path = "/";

            await application.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        }

        [Fact]
        public async Task InterceptedHttpHost_NegotiatesHttp2OverTls()
        {
            using var certificate = CreateCertificate();
            string observedProtocol = null;
            await using var host = new InterceptedHttpHost(
                (_, _) => certificate,
                async context =>
                {
                    observedProtocol = context.Request.Protocol;
                    await context.Response.WriteAsync("ok");
                });
            await host.StartAsync(CancellationToken.None);

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://localhost:{host.Port}/")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("HTTP/2", observedProtocol);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task InterceptedHttpHost_FallsBackToHttp11OverTls()
        {
            using var certificate = CreateCertificate();
            string observedProtocol = null;
            await using var host = new InterceptedHttpHost(
                (_, _) => certificate,
                async context =>
                {
                    observedProtocol = context.Request.Protocol;
                    await context.Response.WriteAsync("ok");
                });
            await host.StartAsync(CancellationToken.None);

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://localhost:{host.Port}/")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpVersion.Version11, response.Version);
            Assert.Equal("HTTP/1.1", observedProtocol);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task InvokeAsync_ForwardsNotModifiedOverTlsWithoutWritingBody(
            int protocolMajorVersion)
        {
            using var certificate = CreateCertificate();
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            var upstreamHandler = new RecordingHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    Content = new ByteArrayContent([])
                }
            };
            upstreamHandler.Response.Content.Headers.ContentLength = 123;
            upstreamHandler.Response.Headers.TryAddWithoutValidation("ETag", "\"version\"");
            using var httpClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(proxy, httpClient, registry);
            var requestCompleted = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new InterceptedHttpHost(
                (_, _) => certificate,
                async context =>
                {
                    registry.TryRegister(
                        context.Connection.RemotePort,
                        new InterceptedTunnelContext(1000, new ClientRequest()));
                    try
                    {
                        await application.InvokeAsync(context);
                        requestCompleted.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        requestCompleted.TrySetResult(ex);
                        throw;
                    }
                });
            await host.StartAsync(CancellationToken.None);
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://localhost:{host.Port}/cached")
            {
                Version = protocolMajorVersion == 2
                    ? HttpVersion.Version20
                    : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using var response = await client.SendAsync(request);

            Assert.Null(await requestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(request.Version, response.Version);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Equal(123, response.Content.Headers.ContentLength);
            Assert.Equal("\"version\"", response.Headers.ETag.Tag);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        [Theory]
        [InlineData("chunked", null)]
        [InlineData("gzip, chunked", "gzip")]
        public async Task InterceptedHttpHost_DropsDecodedChunkedCodingFromCapturedRequest(
            string transferEncoding,
            string expectedStoredCoding)
        {
            using var certificate = CreateCertificate();
            var proxy = new WebServiceProxy();
            var registry = new InterceptedTunnelRegistry();
            var capturedRequest = new TaskCompletionSource<ClientRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.ReceivedWebRequest += (_, args) => capturedRequest.TrySetResult(args.Request);
            using var httpClient = new HttpClient(new RecordingHandler());
            var application = new InterceptedProxyApplication(proxy, httpClient, registry);
            string receivedTransferEncoding = null;
            await using var host = new InterceptedHttpHost(
                (_, _) => certificate,
                context =>
                {
                    receivedTransferEncoding = context.Request.Headers.TransferEncoding.ToString();
                    registry.TryRegister(
                        context.Connection.RemotePort,
                        new InterceptedTunnelContext(1000, new ClientRequest()));
                    return application.InvokeAsync(context);
                });
            await host.StartAsync(CancellationToken.None);
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://127.0.0.1:{host.Port}/upload")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent("request-body"u8.ToArray())
            };
            request.Headers.TryAddWithoutValidation("Transfer-Encoding", transferEncoding);

            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            ClientRequest captured = await capturedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Kestrel passes the client's framing header through but de-chunks the body.
            Assert.Contains("chunked", receivedTransferEncoding);
            Assert.Equal("request-body", System.Text.Encoding.UTF8.GetString(captured.BodyBytes));
            Assert.Equal(
                expectedStoredCoding,
                captured.Headers.GetHeaderValuesAsList("Transfer-Encoding")?.Single());
        }

        [Fact]
        public void InterceptedTunnelContext_MarksTlsCompletionOnce()
        {
            var tunnel = new InterceptedTunnelContext(
                1000,
                new ClientRequest());

            Assert.True(tunnel.TryMarkTlsCompleted());
            Assert.False(tunnel.TryMarkTlsCompleted());
        }

        [Fact]
        public async Task ForwardProxy_InterceptsHttp2EndToEnd()
        {
            using var certificate = CreateCertificate();
            await using var origin = new InterceptedHttpHost(
                (_, _) => certificate,
                context => context.Response.WriteAsync("origin-response"));
            await origin.StartAsync(CancellationToken.None);

            var proxy = new WebServiceProxy();
            proxy.Reset();
            ClientRequest capturedRequest = null;
            proxy.ReceivedWebRequest += (_, args) => capturedRequest = args.Request;

            var tunnels = new InterceptedTunnelRegistry();
            using var upstreamHandler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var upstreamClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                upstreamClient,
                tunnels);
            await using var intercepted = new InterceptedHttpHost(
                (_, _) => certificate,
                application.InvokeAsync);
            await intercepted.StartAsync(CancellationToken.None);

            var logger = new Logger();
            IHost forwardHost = CreateForwardProxyHost(
                proxy,
                upstreamClient,
                intercepted,
                tunnels,
                logger);

            try
            {
                await forwardHost.StartAsync();
                int forwardPort = GetListeningPort(forwardHost);

                using var clientHandler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{forwardPort}"),
                    UseProxy = true,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                using var client = new HttpClient(clientHandler);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://localhost:{origin.Port}/through-proxy")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                using HttpResponseMessage response = await client.SendAsync(request);

                Assert.Equal(HttpVersion.Version20, response.Version);
                Assert.Equal("origin-response", await response.Content.ReadAsStringAsync());
                Assert.NotNull(capturedRequest);
                Assert.Equal("HTTP/2", capturedRequest.Version);
                Assert.NotNull(capturedRequest.StreamId);
            }
            finally
            {
                await forwardHost.StopAsync();
                forwardHost.Dispose();
            }
        }

        [Theory]
        [InlineData(1, 1, true, true)]
        [InlineData(1, 1, false, true)]
        [InlineData(1, 2, true, true)]
        [InlineData(1, 2, false, true)]
        [InlineData(2, 1, true, true)]
        [InlineData(2, 1, false, true)]
        [InlineData(2, 2, true, true)]
        [InlineData(2, 2, false, true)]
        [InlineData(1, 1, true, false)]
        [InlineData(1, 1, false, false)]
        [InlineData(1, 2, true, false)]
        [InlineData(1, 2, false, false)]
        [InlineData(2, 1, true, false)]
        [InlineData(2, 1, false, false)]
        [InlineData(2, 2, true, false)]
        [InlineData(2, 2, false, false)]
        public async Task ForwardProxy_PreservesBodiesAndScriptEditsAcrossProtocols(
            int clientVersion, int originVersion, bool knownLength, bool editBodies)
        {
            byte[] originalBody = Enumerable.Range(0, 131073).Select(i => (byte)i).ToArray();
            var proxy = new WebServiceProxy();
            ClientRequest capturedRequest = null;
            ServerResponse capturedResponse = null;
            byte[] capturedRequestBody = null;
            byte[] capturedResponseBody = null;
            proxy.ReceivedWebRequest += (_, args) =>
            {
                capturedRequest = args.Request;
                capturedRequestBody = args.Request.BodyBytes;
            };
            proxy.ReceivedWebResponse += (_, args) =>
            {
                capturedResponse = args.Response;
                capturedResponseBody = args.Response.BodyBytes;
            };
            if (editBodies)
            {
                var script = new ScriptHost();
                Assert.Empty(await script.CompileScriptAsync<WebServiceCaptureScriptParams>(
                    """
                    if (Params.Request.Method != "CONNECT")
                    {
                        if (Params.Response == null)
                        {
                            Params.Request.BodyBytes = System.Text.Encoding.UTF8.GetBytes("edited-request");
                            Params.Request.Headers["Connection"] = "X-Request-Hop, Content-Type";
                            Params.Request.Headers["X-Request-Hop"] = "remove";
                        }
                        else
                        {
                            Params.Response.BodyBytes = System.Text.Encoding.UTF8.GetBytes("edited-response");
                            Params.Response.Headers["Connection"] = "X-Response-Hop, Content-Type";
                            Params.Response.Headers["X-Response-Hop"] = "remove";
                        }
                    }
                    """));
                proxy.WebRequestPolicyAsync = async (args, token) =>
                    (await script.RunScriptAsync(
                        new WebServiceCaptureScriptParams { Request = args.Request }, token)).Continue;
                proxy.WebResponsePolicyAsync = async (args, token) =>
                    (await script.RunScriptAsync(
                        new WebServiceCaptureScriptParams
                        {
                            Request = args.Request, Response = args.Response
                        }, token)).Continue;
            }
            string observedProtocol = null;
            byte[] observedBody = null;
            long? observedLength = null;
            string observedContentType = null;
            string observedHop = null;
            string observedEndToEnd = null;

            await WithForwardProxyAsync(originVersion, proxy, async context =>
            {
                observedProtocol = context.Request.Protocol;
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                observedBody = body.ToArray();
                observedLength = context.Request.ContentLength;
                observedContentType = context.Request.ContentType;
                observedHop = context.Request.Headers["X-Request-Hop"].ToString();
                observedEndToEnd = context.Request.Headers["X-End-To-End"].ToString();
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers["X-End-To-End"] = "response-retained";
                if (knownLength)
                    context.Response.ContentLength = originalBody.Length;
                await context.Response.Body.WriteAsync(originalBody, context.RequestAborted);
            }, async (client, origin) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/edit"))
                {
                    Version = new Version(clientVersion, clientVersion == 1 ? 1 : 0),
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = knownLength
                        ? new ByteArrayContent(originalBody)
                        : new UnknownLengthContent(originalBody)
                };
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Add("X-End-To-End", "request-retained");
                using var response = await client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(request.Version, response.Version);
                Assert.Equal($"HTTP/{(originVersion == 1 ? "1.1" : "2")}", observedProtocol);
                Assert.Equal(editBodies ? "edited-request"u8.ToArray() : originalBody, observedBody);
                Assert.Equal(observedBody.Length, observedLength);
                Assert.Equal(editBodies ? null : "application/octet-stream", observedContentType);
                Assert.Equal("", observedHop);
                Assert.Equal("request-retained", observedEndToEnd);
                Assert.Equal(originalBody, capturedRequestBody);
                Assert.Equal(originalBody, capturedResponseBody);
                Assert.Equal($"HTTP/{(clientVersion == 1 ? "1.1" : "2")}", capturedRequest.Version);
                Assert.Equal(observedProtocol, capturedResponse.Version);
                Assert.Equal(capturedRequest.RequestNumber, capturedResponse.RequestNumber);
                Assert.Equal(capturedRequest.StreamId, capturedResponse.StreamId);
                Assert.Equal(clientVersion == 2, capturedRequest.StreamId.HasValue);
                Assert.False(response.Headers.Contains("X-Response-Hop"));
                Assert.False(response.Headers.Contains("Connection"));
                Assert.Equal(editBodies ? null : "application/octet-stream",
                    response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("response-retained", response.Headers.GetValues("X-End-To-End").Single());
                byte[] expectedResponse = editBodies ? "edited-response"u8.ToArray() : originalBody;
                Assert.Equal(expectedResponse, await response.Content.ReadAsByteArrayAsync());
                Assert.Equal(expectedResponse.Length, response.Content.Headers.ContentLength);
            });
        }

        [Fact]
        public async Task ForwardProxy_MultiplexesStreamsWithoutMixingCaptureData()
        {
            var proxy = new WebServiceProxy();
            var requests = new ConcurrentDictionary<string, HttpRequestEventArgs>();
            var responses = new ConcurrentDictionary<string, HttpResponseEventArgs>();
            var originConnections = new ConcurrentDictionary<string, string>();
            var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.ReceivedWebRequest += (_, args) => requests[args.Request.Path] = args;
            proxy.ReceivedWebResponse += (_, args) => responses[args.Request.Path] = args;
            await WithForwardProxyAsync(2, proxy, async context =>
            {
                if (context.Request.Path == "/warmup")
                    return;
                originConnections[context.Request.Path] = context.Connection.Id;
                if (originConnections.Count == 2)
                    bothArrived.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
            }, async (client, origin) =>
            {
                using var warmup = await client.GetAsync(new Uri(origin, "/warmup"));
                using var firstContent = new StringContent("first-body");
                using var secondContent = new StringContent("second-body");
                Task<HttpResponseMessage> first = client.PostAsync(new Uri(origin, "/first"), firstContent);
                Task<HttpResponseMessage> second = client.PostAsync(new Uri(origin, "/second"), secondContent);
                try
                {
                    await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(first.IsCompleted);
                    Assert.False(second.IsCompleted);
                    release.TrySetResult();
                    using var firstResponse = await first;
                    using var secondResponse = await second;
                    Assert.Equal("first-body", await firstResponse.Content.ReadAsStringAsync());
                    Assert.Equal("second-body", await secondResponse.Content.ReadAsStringAsync());
                    Assert.Equal(HttpVersion.Version20, firstResponse.Version);
                    Assert.Equal(HttpVersion.Version20, secondResponse.Version);
                    Assert.Equal(requests["/first"].ConnectionID, requests["/second"].ConnectionID);
                    Assert.NotEqual(requests["/first"].Request.StreamId, requests["/second"].Request.StreamId);
                    Assert.NotEqual(requests["/first"].Request.RequestNumber, requests["/second"].Request.RequestNumber);
                    Assert.Single(originConnections.Values.Distinct());
                    foreach (string path in new[] { "/first", "/second" })
                    {
                        Assert.Equal("HTTP/2", requests[path].Request.Version);
                        Assert.Equal("HTTP/2", responses[path].Response.Version);
                        Assert.Equal(requests[path].Request.RequestNumber, responses[path].Response.RequestNumber);
                        Assert.Equal(requests[path].Request.StreamId, responses[path].Response.StreamId);
                        Assert.Equal(requests[path].Request.BodyBytes, responses[path].Response.BodyBytes);
                    }
                }
                finally
                {
                    release.TrySetResult();
                }
            });
        }

        [Fact]
        public async Task ForwardProxy_CancelingOneStreamLeavesOtherStreamsUsable()
        {
            var proxy = new WebServiceProxy();
            var connections = new ConcurrentDictionary<string, int>();
            var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var originCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.ReceivedWebRequest += (_, args) => connections[args.Request.Path] = args.ConnectionID;
            await WithForwardProxyAsync(2, proxy, async context =>
            {
                if (context.Request.Path == "/cancel")
                {
                    cancelStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        originCanceled.TrySetResult();
                    }
                    return;
                }
                if (context.Request.Path == "/healthy")
                {
                    healthyStarted.TrySetResult();
                    await releaseHealthy.Task.WaitAsync(context.RequestAborted);
                }
                await context.Response.WriteAsync("still-usable", context.RequestAborted);
            }, async (client, origin) =>
            {
                using var warmup = await client.GetAsync(new Uri(origin, "/warmup"));
                using var cancellation = new CancellationTokenSource();
                Task<HttpResponseMessage> canceled = client.GetAsync(new Uri(origin, "/cancel"), cancellation.Token);
                Task<HttpResponseMessage> healthy = client.GetAsync(new Uri(origin, "/healthy"));
                try
                {
                    await Task.WhenAll(cancelStarted.Task, healthyStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
                    await originCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    releaseHealthy.TrySetResult();
                    using var healthyResponse = await healthy;
                    Assert.Equal("still-usable", await healthyResponse.Content.ReadAsStringAsync());
                    using var later = await client.GetAsync(new Uri(origin, "/after"));
                    Assert.Equal("still-usable", await later.Content.ReadAsStringAsync());
                    Assert.Equal(HttpVersion.Version20, healthyResponse.Version);
                    Assert.Equal(HttpVersion.Version20, later.Version);
                    Assert.Single(connections.Values.Distinct());
                }
                finally
                {
                    cancellation.Cancel();
                    releaseHealthy.TrySetResult();
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ForwardProxy_DropsDecodedChunkedCodingFromCapturedResponse(
            bool gzipEncoded)
        {
            string contentEncoding = gzipEncoded ? "gzip" : null;
            byte[] body = "chunked-response-body"u8.ToArray();
            if (gzipEncoded)
                body = Gzip(body);
            using var certificate = CreateCertificate();

            var proxy = new WebServiceProxy();
            proxy.Reset();
            var capturedResponse = new TaskCompletionSource<ServerResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.ReceivedWebResponse += (_, args) =>
            {
                if (args.Request.Path == "/chunked")
                    capturedResponse.TrySetResult(args.Response);
            };

            var tunnels = new InterceptedTunnelRegistry();
            var upstreamHandler = new ChunkedResponseRecorder(
                WebServiceProxy.CreateUpstreamHandler());
            using var upstreamClient = new HttpClient(upstreamHandler);
            var application = new InterceptedProxyApplication(
                proxy,
                upstreamClient,
                tunnels);
            await using var intercepted = new InterceptedHttpHost(
                (_, _) => certificate,
                application.InvokeAsync);
            await intercepted.StartAsync(CancellationToken.None);

            using IHost origin = CreateHttp11OriginHost(
                certificate,
                async context =>
                {
                    if (contentEncoding != null)
                        context.Response.Headers.ContentEncoding = contentEncoding;

                    // Without a Content-Length, Kestrel sends this HTTP/1.1
                    // response as chunks.
                    await context.Response.Body.WriteAsync(body.AsMemory(0, body.Length / 2));
                    await context.Response.Body.FlushAsync();
                    await context.Response.Body.WriteAsync(body.AsMemory(body.Length / 2));
                });
            using IHost forwardHost = CreateForwardProxyHost(
                proxy,
                upstreamClient,
                intercepted,
                tunnels,
                new Logger());

            try
            {
                await origin.StartAsync();
                await forwardHost.StartAsync();

                using var clientHandler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{GetListeningPort(forwardHost)}"),
                    UseProxy = true,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                using var client = new HttpClient(clientHandler);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://127.0.0.1:{GetListeningPort(origin)}/chunked")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                using HttpResponseMessage response = await client.SendAsync(request);

                Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
                ServerResponse captured = await capturedResponse.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                Assert.True(upstreamHandler.ReceivedChunkedResponse);
                Assert.Equal("HTTP/1.1", captured.Version);
                Assert.Null(captured.Headers.GetHeaderValuesAsList("Transfer-Encoding"));
                Assert.Equal(
                    contentEncoding,
                    captured.ContentHeaders.GetHeaderValuesAsList("Content-Encoding")?.Single());
                Assert.Equal(body, captured.BodyBytes);
            }
            finally
            {
                await forwardHost.StopAsync();
                await origin.StopAsync();
            }
        }

        // A server that refuses the WebSocket upgrade (for example 401 for an expired token)
        // must have its answer relayed to the client instead of escaping as an exception.
        [Fact]
        public async Task ForwardProxy_RelaysUpstreamWebSocketRefusal()
        {
            using var certificate = CreateCertificate();
            await using var origin = new InterceptedHttpHost(
                (_, _) => certificate,
                context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer realm=\"xmat-test\"";
                    return Task.CompletedTask;
                });
            await origin.StartAsync(CancellationToken.None);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using ClientWebSocket client = CreateProxiedWebSocket(proxy.Port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(
                new Uri($"wss://127.0.0.1:{origin.Port}/socket"), timeout.Token));

            Assert.Equal(HttpStatusCode.Unauthorized, client.HttpStatusCode);
            Assert.Contains(client.HttpResponseHeaders, header =>
                header.Key.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) &&
                header.Value.Contains("Bearer realm=\"xmat-test\""));
        }

        [Fact]
        public async Task ForwardProxy_ReturnsBadGatewayWhenWebSocketUpstreamIsUnreachable()
        {
            using Socket reservedEndpoint = ReserveNonListeningSocket(out int closedPort);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using ClientWebSocket client = CreateProxiedWebSocket(proxy.Port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(
                new Uri($"wss://127.0.0.1:{closedPort}/socket"), timeout.Token));

            Assert.Equal(HttpStatusCode.BadGateway, client.HttpStatusCode);
        }

        [Fact]
        public async Task ForwardProxy_RelaysAcceptedWebSocketUpgrade()
        {
            using var certificate = CreateCertificate();
            await using var origin = new InterceptedHttpHost(
                (_, _) => certificate,
                async context =>
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        context.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var received = new byte[64];
                    WebSocketReceiveResult message = await socket.ReceiveAsync(received, timeout.Token);
                    await socket.SendAsync(
                        new ArraySegment<byte>(received, 0, message.Count),
                        WebSocketMessageType.Text,
                        true,
                        timeout.Token);
                    WebSocketReceiveResult close = await socket.ReceiveAsync(
                        received,
                        timeout.Token);
                    if (close.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(
                            close.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            close.CloseStatusDescription,
                            timeout.Token);
                    }
                });
            await origin.StartAsync(CancellationToken.None);
            await using var proxy = await ProxyTestHarness.StartAsync();
            using ClientWebSocket client = CreateProxiedWebSocket(proxy.Port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(new Uri($"wss://127.0.0.1:{origin.Port}/socket"), timeout.Token);
                await client.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[64];
                WebSocketReceiveResult echo = await client.ReceiveAsync(buffer, timeout.Token);

                Assert.Equal(HttpStatusCode.SwitchingProtocols, client.HttpStatusCode);
                Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, echo.Count));
            }
            finally
            {
                await CloseWebSocketAsync(client);
            }
        }

        private static ClientWebSocket CreateProxiedWebSocket(int proxyPort)
        {
            var client = new ClientWebSocket();
            client.Options.Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}");
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            client.Options.CollectHttpResponseDetails = true;
            return client;
        }

        private static Socket ReserveNonListeningSocket(out int port)
        {
            var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)socket.LocalEndPoint).Port;
            return socket;
        }

        private static async Task CloseWebSocketAsync(ClientWebSocket client)
        {
            if (client.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await client.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test complete",
                    timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
            finally
            {
                if (client.State != WebSocketState.Closed)
                {
                    client.Abort();
                }
            }
        }

        [Fact]
        public async Task ForwardProxy_StopCancelsActiveTunnelsAcrossRepeatedCaptures()
        {
            using var certificate = CreateCertificate();
            var proxy = new WebServiceProxy();
            proxy.Reset();

            for (var capture = 0; capture < 2; capture++)
            {
                var registry = new InterceptedTunnelRegistry();
                var upstreamHandler = new CancelableHandler();
                using var upstreamClient = new HttpClient(upstreamHandler);
                var application = new InterceptedProxyApplication(
                    proxy,
                    upstreamClient,
                    registry);
                var interceptedPort = 0;
                await using var intercepted = new InterceptedHttpHost(
                    (_, _) => certificate,
                    context =>
                    {
                        interceptedPort = context.Connection.RemotePort;
                        return application.InvokeAsync(context);
                    });
                await intercepted.StartAsync(CancellationToken.None);
                var forwardHost = CreateForwardProxyHost(
                    proxy,
                    upstreamClient,
                    intercepted,
                    registry,
                    new Logger());
                Task stopTask = null;
                try
                {
                    await forwardHost.StartAsync();
                    using var handler = new HttpClientHandler
                    {
                        Proxy = new WebProxy($"http://127.0.0.1:{GetListeningPort(forwardHost)}"),
                        UseProxy = true,
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    };
                    using var client = new HttpClient(handler);
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        "https://example.test/pending")
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact
                    };
                    var requestTask = client.SendAsync(request);
                    await upstreamHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    stopTask = ProxyHostShutdown.StopAndDisposeAsync(
                        forwardHost,
                        TimeSpan.FromSeconds(10));
                    await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

                    await upstreamHandler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                    {
                        using var response = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
                    });
                    Assert.False(registry.TryGet(interceptedPort, out _));
                }
                finally
                {
                    stopTask ??= ProxyHostShutdown.StopAndDisposeAsync(
                        forwardHost,
                        TimeSpan.FromSeconds(5));
                    await stopTask;
                }
            }
        }

        private static DefaultHttpContext CreateRequestContext()
        {
            var context = new DefaultHttpContext();
            context.Connection.RemotePort = 53000;
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Host = new HostString("example.com");
            context.Request.Path = "/";
            return context;
        }

        private static X509Certificate2 CreateCertificate()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5));
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx),
                null,
                X509KeyStorageFlags.Exportable);
        }

        private static byte[] Gzip(byte[] content)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(content);
            }

            return compressed.ToArray();
        }

        private sealed class Http2StreamIdFeature(int streamId) : IHttp2StreamIdFeature
        {
            public int StreamId { get; } = streamId;
        }

        private sealed class UnknownLengthContent(byte[] body) : HttpContent
        {
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
                stream.WriteAsync(body).AsTask();
        }

        private static async Task WithForwardProxyAsync(
            int originVersion,
            WebServiceProxy proxy,
            RequestDelegate originApplication,
            Func<HttpClient, Uri, Task> test)
        {
            using var certificate = CreateCertificate();
            using var origin = new HostBuilder().ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = originVersion == 2 ? HttpProtocols.Http2 : HttpProtocols.Http1;
                    listen.UseHttps(certificate);
                }));
                webBuilder.Configure(app => app.Run(originApplication));
            }).Build();
            var tunnels = new InterceptedTunnelRegistry();
            using var upstreamHandler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var upstream = new HttpClient(upstreamHandler) { Timeout = TimeSpan.FromSeconds(20) };
            var application = new InterceptedProxyApplication(proxy, upstream, tunnels);
            await using var intercepted = new InterceptedHttpHost((_, _) => certificate, application.InvokeAsync);
            using var forward = CreateForwardProxyHost(proxy, upstream, intercepted, tunnels, new Logger());
            try
            {
                await origin.StartAsync();
                await intercepted.StartAsync(CancellationToken.None);
                await forward.StartAsync();
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{GetListeningPort(forward)}"),
                    UseProxy = true,
                    MaxConnectionsPerServer = 1,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                using var client = new HttpClient(handler)
                {
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = TimeSpan.FromSeconds(20)
                };
                await test(client, new Uri($"https://localhost:{GetListeningPort(origin)}"));
            }
            finally
            {
                await forward.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await origin.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public HttpRequestMessage Request { get; private set; }
            public HttpResponseMessage Response { get; set; } =
                new(HttpStatusCode.NoContent);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Request = request;
                return Task.FromResult(Response);
            }
        }

        // Records the origin framing before the proxy captures the response.
        private sealed class ChunkedResponseRecorder(HttpMessageHandler innerHandler)
            : DelegatingHandler(innerHandler)
        {
            public bool ReceivedChunkedResponse { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                ReceivedChunkedResponse = response.Headers.TransferEncodingChunked == true;
                return response;
            }
        }

        private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }
        }

        private sealed class CancelableHandler : HttpMessageHandler
        {
            internal TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Canceled { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Canceled.TrySetResult();
                    throw;
                }

                throw new InvalidOperationException("The pending request completed without cancellation.");
            }
        }

        private sealed class TrackingRequestLifetimeFeature : IHttpRequestLifetimeFeature
        {
            public CancellationToken RequestAborted { get; set; }
            public bool Aborted { get; private set; }

            public void Abort()
            {
                Aborted = true;
            }
        }

        private sealed class BodylessResponseStream : MemoryStream
        {
            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "A bodyless response must not write to the response stream, even for an empty body.");
            }
        }

        // HTTP/1.1 only: HTTP/2 has no chunked framing, and the proxy's
        // upstream HttpClient would otherwise negotiate HTTP/2.
        private static IHost CreateHttp11OriginHost(
            X509Certificate2 certificate,
            RequestDelegate application)
        {
            var builder = new HostBuilder();
            builder.ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1;
                        listenOptions.UseHttps(certificate);
                    });
                });
                webBuilder.Configure(app => app.Run(application));
            });
            return builder.Build();
        }

        private static IHost CreateForwardProxyHost(
            WebServiceProxy proxy,
            HttpClient httpClient,
            InterceptedHttpHost interceptedHost,
            InterceptedTunnelRegistry tunnels,
            Logger logger)
        {
            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    {
                        listenOptions.UseConnectionHandler<ForwardProxyConnectionHandler>();
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(httpClient);
                    services.AddSingleton(interceptedHost);
                    services.AddSingleton(tunnels);
                    services.AddSingleton(logger);
                    services.AddSingleton(proxy);
                    services.AddSingleton<ForwardProxyConnectionHandler>();
                });
                webBuilder.Configure(_ => { });
            });
            return builder.Build();
        }

        private static int GetListeningPort(IHost host)
        {
            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            return new Uri(addresses.Addresses.Single()).Port;
        }
    }
}
