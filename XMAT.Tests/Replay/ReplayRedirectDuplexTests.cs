// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // A body that a 307 or 308 keeps goes to the next hop as the first hop's went: duplex over HTTP/2, so an origin that
    // answers while it reads the upload is read at the same time, as the proxy reads it (#150). With content that is not
    // duplex, HttpClient returns the response only once the whole body is sent (Http2Connection.SendAsync), so an origin
    // that echoes the body as it reads it fills the response window, stops reading, and the send stalls until the timeout.
    // The origin is an in-process HTTPS Kestrel on a loopback address of its own, speaking HTTP/2 only.
    public sealed class ReplayRedirectDuplexTests
    {
        private const int BodyBytes = 2 * 1024 * 1024 + 1;

        // Status 0 is the control: the first hop alone, which #150 already sends duplex.
        [Theory]
        [InlineData(0, "127.0.15.12")]
        [InlineData(307, "127.0.15.10")]
        [InlineData(308, "127.0.15.11")]
        public async Task ABodyThatARedirectKeeps_IsSentDuplex_ToAnOriginThatAnswersWhileItReads(int status, string listen)
        {
            using var authority = new TestCertificateAuthority();
            IPAddress address = IPAddress.Parse(listen);
            using X509Certificate2 certificate = authority.Issue(address: address);
            using IHost host = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseKestrel(options => options.Listen(address, 0, endpoint =>
                {
                    endpoint.Protocols = HttpProtocols.Http2;
                    endpoint.UseHttps(certificate);
                }));
                web.Configure(app => app.Run(async context =>
                {
                    if (context.Request.Path == "/start")
                    {
                        context.Response.StatusCode = status;
                        context.Response.Headers.Location = "/next";
                        return;
                    }

                    context.Response.ContentType = "application/octet-stream";
                    await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
                }));
            }).Build();
            await host.StartAsync();
            try
            {
                int port = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First()).Port;
                using var sender = new ReplaySender(new ReplaySenderSetup(ChainPolicyFactory: authority.Policy));
                string body = new string('x', BodyBytes);
                var draft = new RequestReplayDraft
                {
                    Method = "POST",
                    Url = $"https://{listen}:{port}" + (status == 0 ? "/next" : "/start"),
                    HeadersText = "Content-Type: application/octet-stream",
                    BodyText = body,
                };

                ReplayExchange exchange = await sender.SendAsync(
                        draft, ReplaySendOptions.Default with { MaxRedirects = 1, Timeout = TimeSpan.FromSeconds(20) }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(60));

                Assert.True(exchange.Outcome == ReplayOutcome.Completed, $"{exchange.Outcome} {exchange.Code}");
                Assert.Equal((200, status == 0 ? 0 : 1), (exchange.StatusCode, exchange.Redirects));
                Assert.Equal(new Version(2, 0), exchange.Version);
                Assert.Equal(Encoding.ASCII.GetBytes(body), exchange.Response.Body);
            }
            finally
            {
                await host.StopAsync();
            }
        }
    }
}
