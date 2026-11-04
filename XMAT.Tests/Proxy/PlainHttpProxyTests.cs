// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XMAT.Tests.Proxy
{
    public class PlainHttpProxyTests
    {
        [Fact]
        public async Task PlainHttp_Get_RoundTripCaptured()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();

            string body = await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/hello?x=1");

            Assert.Equal("origin-ok", body);
            ObservedRequest seen = Assert.Single(origin.Requests);
            Assert.Equal("GET", seen.Method);
            Assert.Equal("/hello?x=1", seen.Path);
            Assert.Single(proxy.CapturedRequests);
            Assert.Single(proxy.CapturedResponses);
        }

        [Fact]
        public async Task PlainHttp_PostWithBodyInSamePacket_Forwarded()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var proxy = await ProxyTestHarness.StartAsync();
            using HttpClient client = proxy.CreateClient();

            using var content = new StringContent("payload");
            using var response = await client.PostAsync(
                $"http://127.0.0.1:{origin.Port}/post",
                content);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("payload", Encoding.UTF8.GetString(Assert.Single(origin.Requests).Body));
        }

        // The origin sets a cookie for one client; a second client must never
        // have it injected by the proxy's upstream handler.
        [Fact]
        public async Task PlainHttp_UpstreamCookies_NotSharedAcrossClients()
        {
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                context.Response.Headers.SetCookie = "session=client-a; Path=/";
                return Task.CompletedTask;
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            string url = $"http://127.0.0.1:{origin.Port}/cookie";

            using HttpClient clientA = proxy.CreateClient();
            using HttpClient clientB = proxy.CreateClient();
            (await clientA.GetAsync(url)).Dispose();
            (await clientB.GetAsync(url)).Dispose();

            Assert.All(origin.Requests, r => Assert.False(r.Headers.ContainsKey("Cookie")));
        }

        // Set-Cookie reaches each client, each client's own Cookie is forwarded back to the origin,
        // and nothing leaks between clients.
        [Fact]
        public async Task PlainHttp_CookieRoundTrip_IsPerClient()
        {
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                if (context.Request.Path == "/login")
                {
                    context.Response.Headers.SetCookie = $"session={context.Request.Headers["X-Client"]}; Path=/";
                }

                return Task.CompletedTask;
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            string baseUrl = $"http://127.0.0.1:{origin.Port}";

            async Task LoginThenCallAsync(string name)
            {
                using HttpClient client = proxy.CreateClient();
                client.DefaultRequestHeaders.Add("X-Client", name);
                (await client.GetAsync($"{baseUrl}/login")).Dispose();
                (await client.GetAsync($"{baseUrl}/next")).Dispose();
            }

            await LoginThenCallAsync("A");
            await LoginThenCallAsync("B");

            var requests = origin.Requests.ToList();
            Assert.All(
                requests.Where(r => r.Path == "/login"),
                r => Assert.False(r.Headers.ContainsKey("Cookie")));
            var next = requests.Where(r => r.Path == "/next").ToList();
            Assert.Equal(2, next.Count);
            Assert.Equal("session=A", next[0].Headers["Cookie"]);
            Assert.Equal("session=B", next[1].Headers["Cookie"]);
        }
    }
}
