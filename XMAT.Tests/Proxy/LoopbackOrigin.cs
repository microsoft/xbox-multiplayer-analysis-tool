// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
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

    // Plain-HTTP origin on a loopback ephemeral port that records every request it receives.
    internal sealed class LoopbackOrigin : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly ConcurrentQueue<ObservedRequest> _requests;

        public int Port { get; }
        public IReadOnlyCollection<ObservedRequest> Requests => _requests;

        private LoopbackOrigin(IHost host, int port, ConcurrentQueue<ObservedRequest> requests)
        {
            _host = host;
            Port = port;
            _requests = requests;
        }

        public static async Task<LoopbackOrigin> StartAsync(
            Func<HttpContext, Task> respond = null)
        {
            var requests = new ConcurrentQueue<ObservedRequest>();
            IHost host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
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
            return new LoopbackOrigin(host, new Uri(addresses.Addresses.Single()).Port, requests);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
