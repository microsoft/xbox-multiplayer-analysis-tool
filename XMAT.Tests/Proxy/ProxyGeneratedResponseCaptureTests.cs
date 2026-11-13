// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Proxy.ResponseStreamingTests;

namespace XMAT.Tests.Proxy
{
    // When XMAT answers a request itself because it cannot pass on the server's response (XMAT-012), the capture records
    // the answer that the device received, with the time XMAT sent it, and the session exports as complete. Recording it
    // runs no WebResponse script: XMAT already sent that response, so a script can neither change nor block it.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class ProxyGeneratedResponseCaptureTests
    {
        [Theory]
        [InlineData("http11", "unsupported-coding", HttpStatusCode.BadGateway)]
        [InlineData("https11", "unsupported-coding", HttpStatusCode.BadGateway)]
        [InlineData("https2", "unsupported-coding", HttpStatusCode.BadGateway)]
        [InlineData("http11", "unreachable", HttpStatusCode.BadGateway)]
        [InlineData("https11", "unreachable", HttpStatusCode.BadGateway)]
        [InlineData("https2", "unreachable", HttpStatusCode.BadGateway)]
        [InlineData("http11", "timeout", HttpStatusCode.GatewayTimeout)]
        [InlineData("https11", "timeout", HttpStatusCode.GatewayTimeout)]
        [InlineData("https2", "timeout", HttpStatusCode.GatewayTimeout)]
        public async Task AResponseThatXmatSendsItself_IsCapturedAsTheDeviceReceivedIt(
            string route,
            string failure,
            HttpStatusCode expected)
        {
            (Version version, bool tls) = Route(route);
            using X509Certificate2 certificate = ProxyTestHarness.CreateCertificate();
            await using LiveProxyCapture capture = await LiveProxyCapture.StartAsync(
                certificate,
                () => new FailingServer(failure));
            await capture.EnableResponseScriptAsync(
                "if (Params.Request.Method != \"CONNECT\") { Params.Response.Status = \"299\"; Params.Continue = false; }");
            using HttpClient client = capture.CreateClient();
            DateTime sent = DateTime.Now;
            using var request = NewRequest(
                HttpMethod.Get,
                (tls ? "https" : "http") + "://failing.test/resource",
                version);

            using HttpResponseMessage response = await client.SendAsync(request);
            // Stopping the capture waits until the proxy has recorded every response it sent.
            await capture.Controller.StopProxyAsync().WaitAsync(LiveProxyCapture.StopBudget);
            DateTime stopped = DateTime.Now;

            Assert.Equal(expected, response.StatusCode);
            IDataRecord record = capture.RowFor("/resource");
            var row = new ProxyConnectionModel(record);
            Assert.Equal(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), row.Status);
            Assert.True(row.HasRecordedResponseTimestamp, $"{route} {failure}: the response time was not recorded");
            Assert.InRange(
                DateTime.Parse(record.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp), CultureInfo.InvariantCulture),
                sent,
                stopped);
            Assert.Empty(capture.Warnings);
            using ICaptureExportSnapshot snapshot = capture.Controller.CreateExportSnapshot("d1");
            Assert.True(
                snapshot.Sessions.Single(session => session.Path.EndsWith("/resource", StringComparison.Ordinal)).IsComplete,
                $"{route} {failure}: the session is not complete for export");
        }

        // The server side of each failure: a response in a transfer coding that XMAT doesn't decode, a server that can't
        // be reached, and one that doesn't answer in time.
        private sealed class FailingServer(string failure) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _ = request;
                _ = cancellationToken;
                return failure switch
                {
                    "unsupported-coding" => Task.FromResult(GzipCoded()),
                    "unreachable" => Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("No connection could be made to the server.")),
                    _ => Task.FromException<HttpResponseMessage>(
                        new TaskCanceledException("The server did not answer in time.")),
                };
            }

            private static HttpResponseMessage GzipCoded()
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version11,
                    Content = new ByteArrayContent("gzip-coded body"u8.ToArray()),
                };
                response.Content.Headers.ContentLength = null;
                response.Headers.TransferEncoding.Add(new TransferCodingHeaderValue("gzip"));
                response.Headers.TransferEncodingChunked = true;
                return response;
            }
        }
    }
}
