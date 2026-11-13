// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Proxy.ResponseStreamingTests;

namespace XMAT.Tests.Proxy
{
    // Stopping a capture while a streamed response is still open completes that response's capture with what arrived
    // (XMAT-012): its row keeps the forwarded prefix and the time the stream ended, its WebResponse script still runs on
    // a copy, and the session exports as complete.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class StreamedResponseShutdownCaptureTests
    {
        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task StoppingTheCapture_CompletesAnOpenStreamedResponse(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var origin = await LoopbackOrigin.StartAsync(EventsUntilAborted, useTls: tls);
            using X509Certificate2 certificate = ProxyTestHarness.CreateCertificate();
            await using LiveProxyCapture capture = await LiveProxyCapture.StartAsync(certificate);
            await capture.EnableResponseScriptAsync(
                "if (Params.Request.Method != \"CONNECT\") { Params.Response.Status = \"599\"; }");
            using HttpClient client = capture.CreateClient();
            DateTime sent = DateTime.Now;
            using var request = NewRequest(HttpMethod.Get, origin.Url("/events"), version);

            (HttpResponseMessage response, string first) = await ReadFirstBytesAsync(client, request, "data: first");
            using (response)
            {
                Assert.True(first.Contains("data: first", StringComparison.Ordinal),
                    $"{route}: the event stream did not reach the client");
                await capture.Controller.StopProxyAsync().WaitAsync(LiveProxyCapture.StopBudget);
            }
            DateTime stopped = DateTime.Now;

            IDataRecord record = capture.RowFor("/events");
            var row = new ProxyConnectionModel(record);
            Assert.Equal("data: first\n\n", Encoding.ASCII.GetString(row.ResponseBody ?? Array.Empty<byte>()));
            Assert.True(row.HasRecordedResponseTimestamp, $"{route}: the open stream's capture was not completed");
            Assert.InRange(
                DateTime.Parse(record.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp)),
                sent,
                stopped);
            Assert.Equal("200", row.Status);
            Assert.Contains(capture.Warnings, warning => warning.Contains("/events", StringComparison.Ordinal));
            using ICaptureExportSnapshot snapshot = capture.Controller.CreateExportSnapshot("d1");
            Assert.True(
                snapshot.Sessions.Single(session => session.Path.EndsWith("/events", StringComparison.Ordinal)).IsComplete,
                $"{route}: the stopped stream's session is not complete for export");
        }

        // An event stream that sends one event, then stays open until its request is aborted.
        private static async Task EventsUntilAborted(HttpContext context)
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
