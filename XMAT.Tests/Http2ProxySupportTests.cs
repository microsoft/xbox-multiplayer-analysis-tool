// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        private sealed class Http2StreamIdFeature(int streamId) : IHttp2StreamIdFeature
        {
            public int StreamId { get; } = streamId;
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

        private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }
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
