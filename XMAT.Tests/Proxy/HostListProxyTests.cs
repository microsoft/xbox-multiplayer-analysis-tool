// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    // Live Block/Bypass edits through the app-owned policy, observed at the real origin and
    // in the capture table. Each capture's proxy is wired by the controller's production
    // Initialize, not by a test-only BypassList handoff.
    [Collection(IsolatedProxyTests.Name)]
    public class HostListProxyTests
    {
        [Fact]
        public async Task BlockAddedThroughOneCapture_IsEnforcedByAnotherCapturesProxy()
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var editor = await PolicyCapture.StartAsync(policy, "editor");
            await using var enforcer = await PolicyCapture.StartAsync(policy, "enforcer");

            Assert.True(editor.Controller.BlockList.Add("127.0.0.1"));
            using HttpClient client = enforcer.CreateClient();
            Exception failure = await Record.ExceptionAsync(() => client.GetStringAsync(origin.Url("/blocked")));
            await enforcer.DrainCallbacksAsync();

            Assert.IsAssignableFrom<HttpRequestException>(failure);
            Assert.Empty(origin.Requests);
            AssertBlockedAttempts(enforcer.Records("GET"), "127.0.0.1");
        }

        [Fact]
        public async Task BypassAddedAfterInitialization_MakesOnlyTheNextTunnelOpaque()
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await PolicyCapture.StartAsync(policy, "bypass-live");

            using (HttpClient intercepted = capture.CreateClient())
            {
                Assert.Equal("origin-ok", await intercepted.GetStringAsync(origin.Url("/intercepted")));
            }
            Assert.True(policy.BypassList.Add("127.0.0.1"));
            using (HttpClient opaque = capture.CreateClient())
            {
                Assert.Equal("origin-ok", await opaque.GetStringAsync(origin.Url("/opaque")));
            }
            await capture.DrainCallbacksAsync();

            Assert.Equal(new[] { "/intercepted", "/opaque" }, origin.Requests.Select(request => request.Path));
            Assert.Equal(2, capture.Records("CONNECT").Count());
            IDataRecord decrypted = Assert.Single(capture.Records("GET"));
            Assert.Equal("/intercepted", decrypted.Str(WebServiceCaptureMethod.FieldKey_RequestPath));
        }

        [Fact]
        public async Task BlockingAHost_LeavesAnEstablishedOpaqueTunnelButDeniesTheNextConnect()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BypassList.Add("127.0.0.1"));
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await PolicyCapture.StartAsync(policy, "opaque-established");
            using HttpClient established = capture.CreateClient();
            Assert.Equal("origin-ok", await established.GetStringAsync(origin.Url("/before")));
            // The awaited CONNECT policy records the tunnel before the proxy answers it.
            IDataRecord tunnel = Assert.Single(capture.Records("CONNECT"));

            Assert.True(policy.BlockList.Add("127.0.0.1"));

            Assert.Equal("origin-ok", await established.GetStringAsync(origin.Url("/same-tunnel")));
            // No CONNECT was attempted: the request reused the tunnel opened before the block.
            Assert.Equal(tunnel.RowId, Assert.Single(capture.Records("CONNECT")).RowId);
            using HttpClient next = capture.CreateClient();
            Exception failure = await Record.ExceptionAsync(() => next.GetStringAsync(origin.Url("/new-tunnel")));
            await capture.DrainCallbacksAsync();

            Assert.IsAssignableFrom<HttpRequestException>(failure);
            Assert.Equal(new[] { "/before", "/same-tunnel" }, origin.Requests.Select(request => request.Path));
            AssertBlockedAttempts(
                capture.Records("CONNECT").Where(row => row.RowId != tunnel.RowId).ToList(),
                "127.0.0.1");
            Assert.Empty(capture.Records("GET"));
        }

        // Path patterns need a decrypted request: they apply to intercepted HTTPS requests but
        // cannot see inside an opaque bypassed tunnel. Plain HTTP is never bypassed.
        [Fact]
        public async Task PathOnlyBlock_AppliesToInterceptedRequestsButCannotInspectAnOpaqueTunnel()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BypassList.Add("127.0.0.1"));
            Assert.True(policy.BlockList.Add("127.0.0.1/private/*"));
            Assert.True(policy.BlockList.Add("localhost/private/*"));
            await using var plainOrigin = await LoopbackOrigin.StartAsync();
            await using var tlsOrigin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await PolicyCapture.StartAsync(policy, "path-block");

            using (HttpClient intercepted = capture.CreateClient())
            {
                Exception blocked = await Record.ExceptionAsync(
                    () => intercepted.GetStringAsync($"https://localhost:{tlsOrigin.Port}/private/item"));
                Assert.IsAssignableFrom<HttpRequestException>(blocked);
            }
            using (HttpClient opaque = capture.CreateClient())
            {
                Assert.Equal("origin-ok", await opaque.GetStringAsync(tlsOrigin.Url("/private/item")));
            }
            using (HttpClient plain = capture.CreateClient())
            {
                Assert.Equal("origin-ok", await plain.GetStringAsync(plainOrigin.Url("/public")));
            }
            await capture.DrainCallbacksAsync();

            Assert.Equal(new[] { "/private/item" }, tlsOrigin.Requests.Select(request => request.Path));
            Assert.Equal(new[] { "/public" }, plainOrigin.Requests.Select(request => request.Path));
            // The client retries the aborted intercepted request on new tunnels, while the opaque
            // client opens exactly one tunnel and nothing inside it is decrypted.
            IReadOnlyList<IDataRecord> tunnels = capture.Records("CONNECT");
            Assert.Single(tunnels, row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost) == "127.0.0.1");
            Assert.Contains(tunnels, row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost) == "localhost");
            Assert.All(tunnels, row => Assert.Contains(
                row.Str(WebServiceCaptureMethod.FieldKey_RequestHost), new[] { "127.0.0.1", "localhost" }));
            IReadOnlyList<IDataRecord> requests = capture.Records("GET");
            List<IDataRecord> blockedAttempts = requests
                .Where(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost) == "localhost")
                .ToList();
            AssertBlockedAttempts(blockedAttempts, "localhost");
            Assert.All(blockedAttempts, row =>
                Assert.Equal("/private/item", row.Str(WebServiceCaptureMethod.FieldKey_RequestPath)));
            IDataRecord plainRequest = Assert.Single(
                requests, row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost) == "127.0.0.1");
            Assert.Equal("http", plainRequest.Str(WebServiceCaptureMethod.FieldKey_RequestScheme));
            Assert.Equal(blockedAttempts.Count + 1, requests.Count);
        }

        [Fact]
        public async Task RequestAwaitingAScript_IsBlockedByAnEditMadeDuringTheAwait()
        {
            var policy = new HostListPolicy();
            await using var capture = await PolicyCapture.StartAsync(policy, "awaiting-script");
            using var gate = new ScriptGate();
            await capture.EnableScriptAsync(WebServiceCaptureScriptableEventType.WebRequest, gate.Script);
            var request = new ClientRequest
            {
                RequestNumber = 1,
                Version = "HTTP/1.1",
                Scheme = "http",
                Host = "late.example",
                Port = 80,
                Method = "GET",
                Path = "/request"
            };

            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: CancellationToken.None);
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty(capture.Records("GET"));
            Assert.True(policy.BlockList.Add("late.example"));
            gate.Release();

            Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(10)));
            await capture.DrainCallbacksAsync();
            IDataRecord row = Assert.Single(capture.Records("GET"));
            Assert.Equal("late.example", row.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EditWhileConnectPolicyAwaits_DecidesThatTunnelsMode(bool block)
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await PolicyCapture.StartAsync(policy, block ? "pending-block" : "pending-bypass");
            using var gate = new ScriptGate();
            await capture.EnableScriptAsync(WebServiceCaptureScriptableEventType.SslConnectionRequest, gate.Script);
            using HttpClient client = capture.CreateClient();

            Task<string> response = client.GetStringAsync(origin.Url("/pending"));
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(30));
            // R1: the CONNECT row and the block/mode decisions all follow the awaited script.
            Assert.Empty(capture.Records("CONNECT"));
            Assert.True(block ? policy.BlockList.Add("127.0.0.1") : policy.BypassList.Add("127.0.0.1"));
            gate.Release();

            if (block)
            {
                Exception failure = await Record.ExceptionAsync(() => response.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.IsAssignableFrom<HttpRequestException>(failure);
                Assert.Empty(origin.Requests);
            }
            else
            {
                Assert.Equal("origin-ok", await response.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal("/pending", Assert.Single(origin.Requests).Path);
            }
            await capture.DrainCallbacksAsync();
            IReadOnlyList<IDataRecord> connects = capture.Records("CONNECT");
            if (block)
            {
                // The held CONNECT and any client retry are all decided by the edited list.
                AssertBlockedAttempts(connects, "127.0.0.1");
            }
            else
            {
                IDataRecord connect = Assert.Single(connects);
                Assert.Equal("127.0.0.1", connect.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            }
            Assert.Empty(capture.Records("GET"));
        }

        [Fact]
        public async Task PlainHttpToABracketedIPv6Origin_IsForwardedAndCapturedWithCanonicalHost()
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync(listenAddress: IPAddress.IPv6Loopback);
            await using var capture = await PolicyCapture.StartAsync(policy, "ipv6-http");
            using HttpClient client = capture.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/ipv6")));
            await capture.DrainCallbacksAsync();

            Assert.Equal("/ipv6", Assert.Single(origin.Requests).Path);
            IDataRecord row = Assert.Single(capture.Records("GET"));
            Assert.Equal("::1", row.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Equal(origin.Port.ToString(CultureInfo.InvariantCulture), row.Str(WebServiceCaptureMethod.FieldKey_RequestPort));
        }

        [Fact]
        public async Task ConnectToABracketedIPv6Origin_IsInterceptedAndCapturedWithCanonicalHost()
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true, listenAddress: IPAddress.IPv6Loopback);
            await using var capture = await PolicyCapture.StartAsync(policy, "ipv6-connect");
            using HttpClient client = capture.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/secure-ipv6")));
            await capture.DrainCallbacksAsync();

            Assert.Equal("/secure-ipv6", Assert.Single(origin.Requests).Path);
            IDataRecord connect = Assert.Single(capture.Records("CONNECT"));
            Assert.Equal("::1", connect.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Equal(origin.Port.ToString(CultureInfo.InvariantCulture), connect.Str(WebServiceCaptureMethod.FieldKey_RequestPort));
            IDataRecord request = Assert.Single(capture.Records("GET"));
            Assert.Equal("::1", request.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
        }

        [Fact]
        public async Task BracketedIPv6BlockEntry_BlocksCanonicalIPv6Requests()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add("[::1]"));
            await using var origin = await LoopbackOrigin.StartAsync(listenAddress: IPAddress.IPv6Loopback);
            await using var capture = await PolicyCapture.StartAsync(policy, "ipv6-block");
            using HttpClient client = capture.CreateClient();

            Exception failure = await Record.ExceptionAsync(() => client.GetStringAsync(origin.Url("/blocked")));
            await capture.DrainCallbacksAsync();

            Assert.IsAssignableFrom<HttpRequestException>(failure);
            Assert.Empty(origin.Requests);
            AssertBlockedAttempts(capture.Records("GET"), "::1");
        }

        [Fact]
        public async Task ExpandedIPv6BypassEntry_MakesTheIPv6TunnelOpaque()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BypassList.Add("0:0:0:0:0:0:0:1"));
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true, listenAddress: IPAddress.IPv6Loopback);
            await using var capture = await PolicyCapture.StartAsync(policy, "ipv6-bypass");
            using HttpClient client = capture.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/opaque-ipv6")));
            await capture.DrainCallbacksAsync();

            Assert.Equal("/opaque-ipv6", Assert.Single(origin.Requests).Path);
            IDataRecord connect = Assert.Single(capture.Records("CONNECT"));
            Assert.Equal("::1", connect.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Empty(capture.Records("GET"));
        }

        [Fact]
        public async Task DnsAuthorityWithAnExplicitPort_IsCapturedAsHostAndPort()
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await PolicyCapture.StartAsync(policy, "dns-port");
            using HttpClient client = capture.CreateClient();

            Assert.Equal("origin-ok", await client.GetStringAsync($"http://localhost:{origin.Port}/dns"));
            await capture.DrainCallbacksAsync();

            Assert.Equal("/dns", Assert.Single(origin.Requests).Path);
            IDataRecord row = Assert.Single(capture.Records("GET"));
            Assert.Equal("localhost", row.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
            Assert.Equal(origin.Port.ToString(CultureInfo.InvariantCulture), row.Str(WebServiceCaptureMethod.FieldKey_RequestPort));
        }

        // A CONNECT authority keeps the client's DNS spelling and final dot: the dotted host is
        // not the dotless entry, while exact entries still match regardless of case.
        [Fact]
        public async Task ConnectAuthority_KeepsDnsSpellingAndFinalDotForPolicyAndCapture()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add("api.example.test"));
            await using var capture = await PolicyCapture.StartAsync(policy, "connect-spelling");

            string dotted = await SendRawAsync(
                capture.ProxyPort,
                "CONNECT Api.Example.Test.:443 HTTP/1.1\r\nHost: Api.Example.Test.:443\r\n\r\n");
            string blocked = await SendRawAsync(
                capture.ProxyPort,
                "CONNECT API.EXAMPLE.TEST:443 HTTP/1.1\r\nHost: API.EXAMPLE.TEST:443\r\n\r\n");
            await capture.DrainCallbacksAsync();

            Assert.Contains("200 Connection Established", dotted);
            Assert.DoesNotContain("200 Connection Established", blocked);
            IReadOnlyList<IDataRecord> connects = capture.Records("CONNECT");
            Assert.Equal(
                new[] { "Api.Example.Test.", "API.EXAMPLE.TEST" },
                connects.OrderBy(row => row.RowId).Select(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost)));
            Assert.All(connects, row => Assert.Equal("443", row.Str(WebServiceCaptureMethod.FieldKey_RequestPort)));
        }

        // Plain HTTP keeps the client's DNS spelling for scripts, capture rows and forwarding.
        [Fact]
        public async Task PlainHttpHostSpelling_ReachesScriptsCaptureRowsAndForwarding()
        {
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add("blocked.example.test"));
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await PolicyCapture.StartAsync(policy, "http-spelling");
            using var observed = new ScriptHosts();
            await capture.EnableScriptAsync(WebServiceCaptureScriptableEventType.WebRequest, observed.Script);

            string authority = $"LocalHost:{origin.Port}";
            string forwarded = await SendRawAsync(
                capture.ProxyPort,
                $"GET http://{authority}/spelling HTTP/1.1\r\nHost: {authority}\r\nConnection: close\r\n\r\n");
            string blocked = await SendRawAsync(
                capture.ProxyPort,
                "GET http://Blocked.Example.Test/item HTTP/1.1\r\nHost: Blocked.Example.Test\r\nConnection: close\r\n\r\n");
            await capture.DrainCallbacksAsync();

            Assert.Matches(@"^HTTP/\d(\.\d)? 200\b", forwarded);
            Assert.Equal(string.Empty, blocked);
            Assert.Equal("/spelling", Assert.Single(origin.Requests).Path);
            Assert.Equal(new[] { "LocalHost", "Blocked.Example.Test" }, observed.Hosts);
            Assert.Equal(
                new[] { "LocalHost", "Blocked.Example.Test" },
                capture.Records("GET").OrderBy(row => row.RowId).Select(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost)));
        }

        // HTTP/1 Host and CONNECT authorities keep a non-IP host as the client sent it, without
        // host-name validation, case folding, percent-decoding or final-dot removal, as intercepted
        // HTTP/2 requests do; only bracket and port structure is checked at ingress. Such text is
        // not a host identity, so captured rows offer no host action, but a literal entry the
        // user types still matches it after the scripts run.
        [Fact]
        public async Task LegacyHostSpellings_ReachScriptsAndCaptureRowsUnchanged()
        {
            string[] hosts =
            {
                "Reg~Name.Example.Test",
                "Sub!Delims$&'()+;=.Example.Test",
                "Pct%2DEncoded.Example.Test",
                "Legacy..Example.Test.",
                new string('l', 64) + ".Example.Test",
                "B\u00FCcher.Example.Test"
            };
            const string blockedHost = "Pct%2DEncoded.Example.Test";
            var policy = new HostListPolicy();
            Assert.True(policy.BlockList.Add(blockedHost));
            await using var capture = await PolicyCapture.StartAsync(policy, "legacy-spelling");
            using var tunnelHosts = new ScriptHosts();
            using var requestHosts = new ScriptHosts(allow: false);
            await capture.EnableScriptAsync(WebServiceCaptureScriptableEventType.SslConnectionRequest, tunnelHosts.Script);
            await capture.EnableScriptAsync(WebServiceCaptureScriptableEventType.WebRequest, requestHosts.Script);

            var tunnelResponses = new List<string>();
            foreach (string host in hosts)
            {
                // The CONNECT target carries the authority; the plain request uses its Host header.
                tunnelResponses.Add(await SendRawAsync(capture.ProxyPort, $"CONNECT {host}:443 HTTP/1.1\r\n\r\n"));
                await SendRawAsync(
                    capture.ProxyPort,
                    $"GET http://{host}:8080/legacy HTTP/1.1\r\nHost: {host}:8080\r\nConnection: close\r\n\r\n");
            }
            await capture.DrainCallbacksAsync();

            Assert.Equal(hosts, tunnelHosts.Hosts);
            Assert.Equal(hosts, requestHosts.Hosts);
            IReadOnlyList<IDataRecord> tunnels = capture.Records("CONNECT").OrderBy(row => row.RowId).ToList();
            IReadOnlyList<IDataRecord> requests = capture.Records("GET").OrderBy(row => row.RowId).ToList();
            Assert.Equal(hosts, tunnels.Select(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost)));
            Assert.Equal(hosts, requests.Select(row => row.Str(WebServiceCaptureMethod.FieldKey_RequestHost)));
            Assert.All(tunnels, row => Assert.Equal("443", row.Str(WebServiceCaptureMethod.FieldKey_RequestPort)));
            Assert.All(requests, row => Assert.Equal("8080", row.Str(WebServiceCaptureMethod.FieldKey_RequestPort)));
            Assert.All(tunnels.Concat(requests), row => Assert.Null(new ProxyConnectionModel(row).ActionableHost));
            for (int index = 0; index < hosts.Length; index++)
            {
                if (hosts[index] == blockedHost)
                {
                    Assert.DoesNotContain("200 Connection Established", tunnelResponses[index]);
                }
                else
                {
                    Assert.Contains("200 Connection Established", tunnelResponses[index]);
                }
            }
        }

        // Intercepted HTTP/1.1 and HTTP/2 requests take their host from the same adapter, which
        // keeps a non-IP host as sent, including spellings that are not valid DNS names.
        [Theory]
        [InlineData("Api.Example.Test.:8443", "Api.Example.Test.", 8443)]
        [InlineData("[2001:DB8::1]:8443", "2001:db8::1", 8443)]
        [InlineData("Reg~Name.Example.Test:8443", "Reg~Name.Example.Test", 8443)]
        [InlineData("Pct%2DEncoded.Example.Test.:8443", "Pct%2DEncoded.Example.Test.", 8443)]
        [InlineData("B\u00FCcher.Example.Test:8443", "B\u00FCcher.Example.Test", 8443)]
        public async Task InterceptedRequestAuthority_KeepsDnsSpellingAndCanonicalIPv6(string authority, string host, int port)
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Protocol = "HTTP/2";
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            // Kestrel exposes the Host header (or HTTP/2 :authority) as the client sent it.
            context.Request.Headers["Host"] = authority;
            context.Request.Path = "/spelling";
            context.Request.Body = Stream.Null;

            ClientRequest request = await AspNetCoreRequestAdapter.CreateAsync(
                context,
                requestNumber: 1,
                CancellationToken.None);

            Assert.Equal(host, request.Host);
            Assert.Equal(port, request.Port);
        }

        [Theory]
        [InlineData("GET http://[::1/ HTTP/1.1\r\nHost: [::1\r\n\r\n")]
        [InlineData("GET http://example.test/ HTTP/1.1\r\nHost: example.test:notaport\r\n\r\n")]
        [InlineData("CONNECT ::1:443 HTTP/1.1\r\nHost: ::1:443\r\n\r\n")]
        [InlineData("CONNECT [::1]:99999 HTTP/1.1\r\nHost: [::1]:99999\r\n\r\n")]
        [InlineData("CONNECT [example.test]:443 HTTP/1.1\r\nHost: [example.test]:443\r\n\r\n")]
        [InlineData("GET http://example.test/ HTTP/1.1\r\nHost: exa]mple.test\r\n\r\n")]
        [InlineData("CONNECT :443 HTTP/1.1\r\n\r\n")]
        public async Task MalformedAuthorities_AreRejectedWithoutCaptureOrForwarding(string malformed)
        {
            var policy = new HostListPolicy();
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await PolicyCapture.StartAsync(policy, "malformed");

            string response = await SendRawAsync(capture.ProxyPort, malformed);
            using HttpClient client = capture.CreateClient();
            Assert.Equal("origin-ok", await client.GetStringAsync(origin.Url("/after")));
            await capture.DrainCallbacksAsync();

            Assert.DoesNotContain("200 Connection Established", response);
            Assert.Equal(new[] { "/after" }, origin.Requests.Select(request => request.Path));
            Assert.Equal(1, capture.RecordCount);
        }

        // A blocked request is closed without a response, which HttpClient may retry on a new
        // connection; every captured attempt must be a blank-status row for the blocked host.
        private static void AssertBlockedAttempts(IReadOnlyList<IDataRecord> attempts, string host)
        {
            Assert.NotEmpty(attempts);
            Assert.All(attempts, attempt =>
            {
                Assert.Equal(host, attempt.Str(WebServiceCaptureMethod.FieldKey_RequestHost));
                Assert.True(string.IsNullOrEmpty(attempt.Str(WebServiceCaptureMethod.FieldKey_RequestStatus)));
            });
        }

        private static async Task<string> SendRawAsync(int proxyPort, string request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            using NetworkStream stream = client.GetStream();
            // UTF-8 is ASCII for ASCII requests and carries legacy non-ASCII host spellings as sent.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            using var response = new MemoryStream();
            var buffer = new byte[1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    response.Write(buffer, 0, read);
                    // An accepted tunnel stays open, so stop once a response head has arrived.
                    if (Encoding.ASCII.GetString(response.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                // The rejected connection may be reset or held open; only what was sent matters.
            }
            return Encoding.ASCII.GetString(response.ToArray());
        }

        // Records the host that each capture script run receives, through an in-process delegate,
        // and then lets the request continue or stops it.
        private sealed class ScriptHosts : IDisposable
        {
            private readonly string _key = "xmat-host-list-hosts-" + Guid.NewGuid().ToString("N");
            private readonly ConcurrentQueue<string> _hosts = new();
            private readonly bool _allow;

            public ScriptHosts(bool allow = true)
            {
                _allow = allow;
                AppDomain.CurrentDomain.SetData(_key, (Action<string>)(host => _hosts.Enqueue(host)));
            }

            public IReadOnlyList<string> Hosts => _hosts.ToArray();

            public string Script =>
                $"var observe = (System.Action<string>)System.AppDomain.CurrentDomain.GetData(\"{_key}\"); " +
                "observe.Invoke(Params.Request.Host); " +
                $"Params.Continue = {(_allow ? "true" : "false")};";

            public void Dispose() => AppDomain.CurrentDomain.SetData(_key, null);
        }

        // An in-process gate that a capture script signals when it starts and then awaits,
        // so a test can change policy at a known point instead of guessing with delays.
        private sealed class ScriptGate : IDisposable
        {
            private readonly string _key = "xmat-host-list-gate-" + Guid.NewGuid().ToString("N");
            private readonly TaskCompletionSource<bool> _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ScriptGate()
            {
                AppDomain.CurrentDomain.SetData(_key + "-started", _started);
                AppDomain.CurrentDomain.SetData(_key + "-release", _release.Task);
            }

            public Task Started => _started.Task;

            // Script top-level code is parsed outside an async context, so "await (" would be
            // read as a call to a method named await; the script awaits a named variable instead.
            public string Script =>
                "var gateStarted = (System.Threading.Tasks.TaskCompletionSource<bool>)System.AppDomain.CurrentDomain" +
                $".GetData(\"{_key}-started\"); " +
                "var gateRelease = (System.Threading.Tasks.Task)System.AppDomain.CurrentDomain" +
                $".GetData(\"{_key}-release\"); " +
                "gateStarted.TrySetResult(true); " +
                "await gateRelease; " +
                "Params.Continue = true;";

            public void Release() => _release.TrySetResult(true);

            public void Dispose()
            {
                _release.TrySetResult(true);
                AppDomain.CurrentDomain.SetData(_key + "-started", null);
                AppDomain.CurrentDomain.SetData(_key + "-release", null);
            }
        }

        // A capture controller that receives the shared policy through its constructor and is
        // initialized by production code, hosted on the in-process proxy with an in-memory table.
        private sealed class PolicyCapture : IAsyncDisposable
        {
            private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

            private readonly ProxyTestHarness _harness;
            private readonly SqLtDatabase _database;
            private readonly IDataTable _table;
            private readonly AsyncOperationTracker _callbacks;
            private readonly CancellationTokenSource _captureCancellation;

            public WebServiceDeviceCaptureController Controller { get; }
            public WebServiceProxy Proxy { get; }
            public int ProxyPort => _harness.Port;
            public int RecordCount => _table.Dataset.DataRecords.Count();

            private PolicyCapture(
                ProxyTestHarness harness,
                WebServiceDeviceCaptureController controller,
                WebServiceProxy proxy,
                SqLtDatabase database,
                IDataTable table,
                AsyncOperationTracker callbacks,
                CancellationTokenSource captureCancellation)
            {
                _harness = harness;
                Controller = controller;
                Proxy = proxy;
                _database = database;
                _table = table;
                _callbacks = callbacks;
                _captureCancellation = captureCancellation;
            }

            public static async Task<PolicyCapture> StartAsync(HostListPolicy policy, string name)
            {
                var proxy = new WebServiceProxy();
                var controller = new WebServiceDeviceCaptureController(
                    name,
                    DeviceType.GenericProxyDevice,
                    false,
                    new ProxyPortPool(),
                    hostListPolicy: policy,
                    proxyFactory: () => proxy);
                controller.Initialize(deferPortAllocation: true);
                Assert.Same(policy, controller.HostListPolicy);
                Assert.Same(policy.BypassList, proxy.BypassList);

                var database = new SqLtDatabase();
                database.Initialize();
                IDataTable table = CreateConnectionsTable(database);
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_dataTable", PrivateInstance)
                    .SetValue(controller, table);
                var callbacks = (AsyncOperationTracker)typeof(WebServiceDeviceCaptureController)
                    .GetField("_captureCallbacks", PrivateInstance)
                    .GetValue(controller);
                callbacks.StartAccepting();
                var captureCancellation = new CancellationTokenSource();
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_captureCallbackCancellationTokenSource", PrivateInstance)
                    .SetValue(controller, captureCancellation);

                ProxyTestHarness harness = await ProxyTestHarness.StartAsync(proxy);
                return new PolicyCapture(
                    harness,
                    controller,
                    proxy,
                    database,
                    table,
                    callbacks,
                    captureCancellation);
            }

            public HttpClient CreateClient() => _harness.CreateClient();

            public IReadOnlyList<IDataRecord> Records(string method) =>
                _table.Dataset.DataRecords
                    .Where(record => record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) == method)
                    .ToList();

            public async Task EnableScriptAsync(
                WebServiceCaptureScriptableEventType eventType,
                string script)
            {
                ScriptModel model = Controller.Scripts[eventType];
                model.Script = script;
                ScriptActivationResult result = await model.EnableScriptAsync<WebServiceCaptureScriptParams>();
                Assert.True(
                    result == ScriptActivationResult.Enabled,
                    $"Script activation returned {result}:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, model.CompilationStatus.Select(diagnostic => diagnostic.ToString())));
            }

            public Task DrainCallbacksAsync() =>
                _callbacks.StopAcceptingAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _captureCancellation.CancelAsync();
                    await DrainCallbacksAsync();
                }
                finally
                {
                    await _harness.DisposeAsync();
                    _captureCancellation.Dispose();
                    _database.Dispose();
                }
            }

            // Same layout as WebServiceCaptureMethod.InitializeDataTables().
            private static IDataTable CreateConnectionsTable(SqLtDatabase database) =>
                database.CreateTable(
                    WebServiceCaptureMethod.TableName,
                    new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
        }
    }
}
