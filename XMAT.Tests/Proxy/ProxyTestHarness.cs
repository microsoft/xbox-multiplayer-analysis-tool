// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    // Runs the real forward-proxy pipeline in-process on a loopback ephemeral port.
    // Needs no WPF, dialogs or user certificate store, so it also serves as a
    // building block for headless-capture tests.
    internal sealed class ProxyTestHarness : IAsyncDisposable
    {
        private readonly IHost _forwardHost;
        private readonly InterceptedHttpHost _intercepted;
        private readonly HttpClient _upstream;
        private readonly X509Certificate2 _certificate;

        public WebServiceProxy Proxy { get; }
        public int Port { get; }
        public List<ClientRequest> CapturedRequests { get; } = [];
        public List<ServerResponse> CapturedResponses { get; } = [];

        private ProxyTestHarness(
            WebServiceProxy proxy,
            IHost forwardHost,
            InterceptedHttpHost intercepted,
            HttpClient upstream,
            X509Certificate2 certificate,
            int port)
        {
            Proxy = proxy;
            _forwardHost = forwardHost;
            _intercepted = intercepted;
            _upstream = upstream;
            _certificate = certificate;
            Port = port;
        }

        public static async Task<ProxyTestHarness> StartAsync()
        {
            X509Certificate2 certificate = CreateCertificate();
            var proxy = new WebServiceProxy();
            proxy.Reset();

            var tunnels = new InterceptedTunnelRegistry();
            var upstream = new HttpClient(WebServiceProxy.CreateUpstreamHandler());
            var logger = new Logger();
            var application = new InterceptedProxyApplication(proxy, upstream, tunnels);
            var intercepted = new InterceptedHttpHost((_, _) => certificate, application.InvokeAsync);
            await intercepted.StartAsync(CancellationToken.None);

            IHost forwardHost = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
                        l.UseConnectionHandler<ForwardProxyConnectionHandler>()));
                    web.ConfigureServices(s =>
                    {
                        s.AddSingleton(upstream);
                        s.AddSingleton(intercepted);
                        s.AddSingleton(tunnels);
                        s.AddSingleton(logger);
                        s.AddSingleton(proxy);
                        s.AddSingleton<ForwardProxyConnectionHandler>();
                    });
                    web.Configure(_ => { });
                })
                .Build();
            await forwardHost.StartAsync();

            var addresses = forwardHost.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            int port = new Uri(addresses.Addresses.Single()).Port;

            var harness = new ProxyTestHarness(proxy, forwardHost, intercepted, upstream, certificate, port);
            proxy.ReceivedWebRequest += (_, args) => harness.CapturedRequests.Add(args.Request);
            proxy.ReceivedWebResponse += (_, args) => harness.CapturedResponses.Add(args.Response);
            return harness;
        }

        // HttpClient that sends all traffic through the proxy under test.
        public HttpClient CreateClient() => new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        public async ValueTask DisposeAsync()
        {
            await _forwardHost.StopAsync();
            _forwardHost.Dispose();
            await _intercepted.DisposeAsync();
            _upstream.Dispose();
            _certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }
    }
}
