// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Diagnostics;
using System.Text;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.Proxy.ResponseStreamingTests;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public class AutoResponderTests
    {
        [Fact]
        public void Rules_MatchMethodAndAbsoluteUrlInOrder()
        {
            var rules = new AutoResponderRules();
            rules.Add(new AutoResponderRule("https://example.com/api/*", "POST", 429, "Retry-After: 3", "slow down", 0));
            rules.Add(new AutoResponderRule("https://example.com/*", "*", 503, "", "offline", 0));

            Assert.Equal(429, rules.Match("POST", new Uri("https://example.com/api/item?x=1")).Status);
            Assert.Equal(503, rules.Match("GET", new Uri("https://example.com/api/item")).Status);
            Assert.Null(rules.Match("POST", new Uri("https://another.example/api/item")));
            rules.Remove(rules.Items[0]);
            Assert.Equal(503, rules.Match("POST", new Uri("https://example.com/api/item")).Status);
        }

        [Fact]
        public void Rules_CanBeDisabledWithoutLosingRules()
        {
            var rules = new AutoResponderRules();
            rules.Add(new AutoResponderRule("https://EXAMPLE.com/Case/*", "*", 200, "", "", 0));
            Assert.NotNull(rules.Match("GET", new Uri("https://example.com/Case/x")));
            Assert.Null(rules.Match("GET", new Uri("https://example.com/case/x")));
            rules.Enabled = false;
            Assert.Null(rules.Match("GET", new Uri("https://example.com/Case/x")));
            rules.Enabled = true;
            Assert.Single(rules.Items);
        }

        [Theory]
        [InlineData("https://example.com:443/path", "https://example.com/path")]
        [InlineData("http://example.com:80/path", "http://example.com/path")]
        [InlineData("https://example.com", "https://example.com/")]
        [InlineData("https://example.com?x=1", "https://example.com/?x=1")]
        [InlineData("https://*.example.com?x=*", "https://api.example.com/?x=1")]
        public void Rules_NormalizeLiteralAuthority(string pattern, string requestUrl)
        {
            var rules = new AutoResponderRules();
            rules.Add(new AutoResponderRule(pattern, "GET", 200, "", "", 0));
            Assert.NotNull(rules.Match("GET", new Uri(requestUrl)));
        }

        [Fact]
        public void Rule_PreservesBinaryResponseBody()
        {
            var rule = new AutoResponderRule("https://example.com/*", "*", 200,
                "Content-Type: application/octet-stream", "AP8B", 0, bodyIsBase64: true);
            ServerResponse response = rule.CreateResponse(new ClientRequest { Version = "HTTP/1.1" });
            Assert.Equal(new byte[] { 0, 255, 1 }, response.BodyBytes);
            Assert.Equal("3", response.ContentHeaders["Content-Length"]);
        }

        [Theory]
        [InlineData(204)]
        [InlineData(304)]
        public void BodylessStatus_OmitsContentLength(int status)
        {
            var rule = new AutoResponderRule("https://example.com/*", "*", status, "", "", 0);
            ServerResponse response = rule.CreateResponse(new ClientRequest { Version = "HTTP/1.1" });
            Assert.False(response.ContentHeaders.Contains("Content-Length"));
        }

        [Fact]
        public void RuleSets_RoundTripAndRejectInvalidInputWithoutReplacingRules()
        {
            var rules = new AutoResponderRules();
            rules.Add(new AutoResponderRule("https://example.com/*", "POST", 429, "Retry-After: 3", "AP8B", 15, true));
            string json = rules.Export();
            var restored = new AutoResponderRules();
            restored.Import(json);
            AutoResponderRule rule = Assert.Single(restored.Items);
            Assert.True(rule.BodyIsBase64);
            Assert.Equal("AP8B", rule.BodyText);
            Assert.Equal(15, rule.DelayMilliseconds);
            Assert.Throws<ArgumentException>(() => restored.Import("{\"rules\":[{\"pattern\":\"invalid\"}]}"));
            Assert.Same(rule, Assert.Single(restored.Items));
        }

        [Fact]
        public void RuleSets_RejectExportsTooLargeToLoad()
        {
            var rules = new AutoResponderRules();
            string body = Convert.ToBase64String(new byte[1024 * 1024]);
            for (int i = 0; i < 4; i++)
                rules.Add(new AutoResponderRule($"https://example.com/{i}", "GET", 200, "", body, 0, true));
            Assert.Throws<ArgumentException>(() => rules.Export());
        }

        [Theory]
        [InlineData("", "GET", 200, "", 0)]
        [InlineData("https://example.com/*", "BAD METHOD", 200, "", 0)]
        [InlineData("https://example.com/*", "GET", 101, "", 0)]
        [InlineData("https://example.com/*", "GET", 200, "Broken header", 0)]
        [InlineData("https://example.com/*", "GET", 200, "Content-Length: 42", 0)]
        [InlineData("https://example.com/*", "GET", 200, "X-Test: value\u0001", 0)]
        [InlineData("https://example.com/*", "GET", 200, "X-Test: \u00e9", 0)]
        [InlineData("https://example.com/*", "GET", 200, "", -1)]
        public void Rule_RejectsInvalidInput(string pattern, string method, int status, string headers, int delay)
        {
            Assert.Throws<ArgumentException>(() => new AutoResponderRule(pattern, method, status, headers, "body", delay));
        }

        [Theory]
        [InlineData(AutoResponderScenario.Throttled, 429)]
        [InlineData(AutoResponderScenario.Unauthorized, 401)]
        [InlineData(AutoResponderScenario.Unavailable, 503)]
        [InlineData(AutoResponderScenario.Delayed, 200)]
        public void Scenarios_CreateEditableRules(AutoResponderScenario scenario, int status)
        {
            AutoResponderRule rule = AutoResponderRule.FromScenario(scenario, "https://example.com/*");
            Assert.Equal(status, rule.Status);
            Assert.Equal(scenario == AutoResponderScenario.Delayed ? 5000 : 0, rule.DelayMilliseconds);
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task MatchingRequest_ReturnsMockWithoutContactingOrigin(string route)
        {
            (Version version, bool tls) = Route(route);
            int calls = 0;
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = 204;
                return Task.CompletedTask;
            }, useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.AutoResponder.Add(new AutoResponderRule(origin.Url("/mock*"), "POST", 429,
                "Retry-After: 4\r\nContent-Type: text/plain", "try later", 0));
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Post, origin.Url("/mock"), version);
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("try later", await response.Content.ReadAsStringAsync());
            Assert.Equal("4", response.Headers.GetValues("Retry-After").Single());
            Assert.Equal(0, calls);
            ServerResponse captured = await WaitForCapturedResponseAsync(proxy);
            Assert.Equal("429", captured.Status);
            Assert.Equal("try later", Encoding.UTF8.GetString(captured.BodyBytes));
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task UnmatchedAndDisabledRules_ForwardRequestsNormally(string route)
        {
            (Version version, bool tls) = Route(route);
            int calls = 0;
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = 204;
                return Task.CompletedTask;
            }, useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.AutoResponder.Add(new AutoResponderRule(origin.Url("/mock"), "*", 503, "", "offline", 0));
            using HttpClient client = proxy.CreateClient();
            using (var request = NewRequest(HttpMethod.Get, origin.Url("/other"), version))
            using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            proxy.Proxy.AutoResponder.Enabled = false;
            using (var request = NewRequest(HttpMethod.Get, origin.Url("/mock"), version))
            using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(2, calls);
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task HeadMock_SendsNoBodyButPreservesRepresentationLength(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var origin = await LoopbackOrigin.StartAsync(useTls: tls);
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.AutoResponder.Add(new AutoResponderRule(origin.Url("/head"), "HEAD", 200, "", "hello", 0));
            using HttpClient client = proxy.CreateClient();
            using var request = NewRequest(HttpMethod.Head, origin.Url("/head"), version);
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(5, response.Content.Headers.ContentLength);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task MockResponse_IsRecordedInLiveCapture(string route)
        {
            (Version version, bool tls) = Route(route);
            using var certificate = ProxyTestHarness.CreateCertificate();
            await using LiveProxyCapture capture = await LiveProxyCapture.StartAsync(certificate);
            string url = (tls ? "https" : "http") + "://mock.example/path";
            capture.Controller.AutoResponder.Add(new AutoResponderRule(url, "GET", 503, "Content-Type: text/plain", "offline", 0));
            using HttpClient client = capture.CreateClient();
            using var request = NewRequest(HttpMethod.Get, url, version);
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await capture.Controller.StopProxyAsync().WaitAsync(LiveProxyCapture.StopBudget);
            IDataRecord record = capture.RowFor("/path");
            var row = new ProxyConnectionModel(record);
            Assert.Equal("503", row.Status);
            Assert.Equal("offline", Encoding.UTF8.GetString(row.ResponseBody));
            Assert.True(row.HasRecordedResponseTimestamp);
        }

        [Fact]
        public async Task DelayedRule_WaitsBeforeSendingWithoutCallingOrigin()
        {
            int calls = 0;
            await using var origin = await LoopbackOrigin.StartAsync(context =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
            await using var proxy = await ProxyTestHarness.StartAsync();
            proxy.Proxy.AutoResponder.Add(new AutoResponderRule(origin.Url("/delay"), "*", 200, "", "delayed", 250));
            using HttpClient client = proxy.CreateClient();
            Stopwatch watch = Stopwatch.StartNew();
            using HttpResponseMessage response = await client.GetAsync(origin.Url("/delay"));
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(200));
            Assert.Equal("delayed", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, calls);
        }
    }
}
