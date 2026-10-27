// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Scripting;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    // Every capture starts with these samples and Enable runs the editor text as is,
    // so an unedited sample must leave all traffic alone.
    public class ScriptDefaultsTests
    {
        private const string PlaceholderHost = "your-service.example.com";
        private const string StoreHost = "displaycatalog.mp.microsoft.com";

        public ScriptDefaultsTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Theory]
        [InlineData(WebServiceCaptureScriptableEventType.SslConnectionRequest)]
        [InlineData(WebServiceCaptureScriptableEventType.WebRequest)]
        [InlineData(WebServiceCaptureScriptableEventType.WebResponse)]
        public async Task StockSample_CompilesCleanlyAndChangesNothing(WebServiceCaptureScriptableEventType eventType)
        {
            string sample = Sample(eventType);
            Assert.DoesNotContain("NO_IMPL_", sample);

            var host = new ScriptHost();
            Assert.Empty(await host.CompileScriptAsync<WebServiceCaptureScriptParams>(sample));

            foreach (string requestHost in new[] { "samplehost.com", PlaceholderHost, StoreHost })
            {
                foreach (string method in new[] { "GET", "CONNECT" })
                {
                    var parameters = Parameters(method, requestHost);
                    Assert.Same(parameters, await host.RunScriptAsync(parameters));
                    AssertUnchanged(parameters, method, requestHost);
                }
            }
        }

        [Theory]
        [InlineData(WebServiceCaptureScriptableEventType.SslConnectionRequest, "CONNECT", false, PlaceholderHost + ":443", "200")]
        [InlineData(WebServiceCaptureScriptableEventType.WebRequest, "GET", true, "/newpath", "200")]
        [InlineData(WebServiceCaptureScriptableEventType.WebResponse, "GET", true, "/", "500")]
        [InlineData(WebServiceCaptureScriptableEventType.WebResponse, "CONNECT", true, PlaceholderHost + ":443", "200")]
        public async Task UncommentedSample_ChangesOnlyThePlaceholderHost(
            WebServiceCaptureScriptableEventType eventType,
            string method,
            bool expectedContinue,
            string expectedPath,
            string expectedStatus)
        {
            // The samples tell users to remove the leading // from the example code.
            string example = string.Join("\n", Sample(eventType).Split('\n').Select(
                line => line.StartsWith("//", StringComparison.Ordinal) ? line[2..] : line));
            var host = new ScriptHost();
            Assert.Empty(await host.CompileScriptAsync<WebServiceCaptureScriptParams>(example));

            var target = Parameters(method, PlaceholderHost);
            Assert.Same(target, await host.RunScriptAsync(target));
            Assert.Equal(expectedContinue, target.Continue);
            Assert.Equal(expectedPath, target.Request.Path);
            Assert.Equal(expectedStatus, target.Response.Status);

            var other = Parameters(method, StoreHost);
            Assert.Same(other, await host.RunScriptAsync(other));
            AssertUnchanged(other, method, StoreHost);
        }

        private static string Sample(WebServiceCaptureScriptableEventType eventType) =>
            new ScriptCollection(typeof(WebServiceCaptureScriptableEventType))[eventType].Script;

        private static WebServiceCaptureScriptParams Parameters(string method, string host) => new()
        {
            Request = new ClientRequest
            {
                Method = method,
                Scheme = "https",
                Host = host,
                Port = 443,
                Path = method == "CONNECT" ? $"{host}:443" : "/",
                Version = "HTTP/1.1"
            },
            Response = new ServerResponse
            {
                Version = "HTTP/1.1",
                Status = "200",
                StatusDescription = method == "CONNECT" ? "Connection Established" : "OK"
            }
        };

        private static void AssertUnchanged(WebServiceCaptureScriptParams parameters, string method, string host)
        {
            Assert.True(parameters.Continue);
            Assert.Equal(method, parameters.Request.Method);
            Assert.Equal(host, parameters.Request.Host);
            Assert.Equal(method == "CONNECT" ? $"{host}:443" : "/", parameters.Request.Path);
            Assert.Equal("200", parameters.Response.Status);
        }
    }
}
