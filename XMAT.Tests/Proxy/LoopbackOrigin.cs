// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace XMAT.Tests.Proxy
{
    internal sealed record ObservedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    // HTTP (or HTTPS with a self-signed certificate) origin on a loopback ephemeral port
    // that records every request it receives.
    internal sealed class LoopbackOrigin : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly ConcurrentQueue<ObservedRequest> _requests;
        private readonly X509Certificate2 _certificate;
        private readonly string _authorityHost;

        public int Port { get; }
        public IReadOnlyCollection<ObservedRequest> Requests => _requests;

        private LoopbackOrigin(
            IHost host,
            int port,
            ConcurrentQueue<ObservedRequest> requests,
            X509Certificate2 certificate,
            IPAddress address)
        {
            _host = host;
            Port = port;
            _requests = requests;
            _certificate = certificate;
            _authorityHost = address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]"
                : address.ToString();
        }

        public string Url(string pathAndQuery) =>
            $"{(_certificate == null ? "http" : "https")}://{_authorityHost}:{Port}{pathAndQuery}";

        public static async Task<LoopbackOrigin> StartAsync(
            Func<HttpContext, Task> respond = null,
            bool useTls = false,
            IPAddress listenAddress = null)
        {
            IPAddress address = listenAddress ?? IPAddress.Loopback;
            var requests = new ConcurrentQueue<ObservedRequest>();
            X509Certificate2 certificate = useTls ? ProxyTestHarness.CreateCertificate() : null;
            IHost host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.ConfigureKestrel(o => o.Listen(address, 0, listen =>
                    {
                        if (certificate != null)
                        {
                            listen.UseHttps(certificate);
                        }
                    }));
                    web.Configure(app => app.Run(async context =>
                    {
                        using var buffer = new MemoryStream();
                        await context.Request.Body.CopyToAsync(buffer);
                        requests.Enqueue(new ObservedRequest(
                            context.Request.Method,
                            context.Request.Path + context.Request.QueryString,
                            context.Request.Headers.ToDictionary(
                                h => h.Key,
                                h => h.Value.ToString(),
                                StringComparer.OrdinalIgnoreCase),
                            buffer.ToArray()));

                        if (respond != null)
                        {
                            await respond(context);
                        }
                        else
                        {
                            await context.Response.WriteAsync("origin-ok");
                        }
                    }));
                })
                .Build();

            await host.StartAsync();
            var addresses = host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            return new LoopbackOrigin(
                host,
                new Uri(addresses.Addresses.Single()).Port,
                requests,
                certificate,
                address);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            _certificate?.Dispose();
        }
    }
}
