// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Export;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.Proxy.ResponseStreamingTests;

namespace XMAT.Tests.Proxy
{
    // Stopping a capture while a streamed response is still open completes that response's capture with what arrived
    // (XMAT-012): its row keeps the forwarded prefix and the time the stream ended, its WebResponse script still runs on
    // a copy, and the session exports as complete.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class StreamedResponseShutdownCaptureTests
    {
        private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(60);

        [Theory]
        [InlineData("http11")]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task StoppingTheCapture_CompletesAnOpenStreamedResponse(string route)
        {
            (Version version, bool tls) = Route(route);
            await using var origin = await LoopbackOrigin.StartAsync(EventsUntilAborted, useTls: tls);
            using X509Certificate2 certificate = ProxyTestHarness.CreateCertificate();
            await using LiveCapture capture = await LiveCapture.StartAsync(certificate);
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
                await capture.Controller.StopProxyAsync().WaitAsync(StopBudget);
            }
            DateTime stopped = DateTime.Now;

            IDataRecord record = capture.RowFor("/events");
            var row = new ProxyConnectionModel(record);
            Assert.Equal("data: first\n\n", Encoding.ASCII.GetString(row.ResponseBody));
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

        // A capture tab's controller with its own proxy, started as XMAT starts it, recording into a database of its own.
        // Its UI updates run on the shared WPF application's dispatcher, as in XMAT, and HTTPS uses CERTIFICATE.
        private sealed class LiveCapture : IAsyncDisposable
        {
            private readonly SqLtDatabase _database;
            private readonly IDataTable _table;
            private readonly ConcurrentQueue<string> _warnings = new();

            private LiveCapture(
                SqLtDatabase database,
                IDataTable table,
                WebServiceDeviceCaptureController controller,
                ushort port)
            {
                _database = database;
                _table = table;
                Controller = controller;
                Port = port;
                Controller.LogObserver = (level, message) =>
                {
                    if (level == LogLevel.WARN)
                    {
                        _warnings.Enqueue(message);
                    }
                };
            }

            internal WebServiceDeviceCaptureController Controller { get; }
            internal ushort Port { get; }
            internal IReadOnlyCollection<string> Warnings => _warnings;

            internal static async Task<LiveCapture> StartAsync(X509Certificate2 certificate)
            {
                CaptureUiApplicationFixture.RunOnApplication(_ => { });
                var database = new SqLtDatabase();
                database.Initialize();
                var proxy = new WebServiceProxy { InterceptedCertificateOverride = _ => certificate };
                var controller = new WebServiceDeviceCaptureController(
                    "streaming-shutdown",
                    DeviceType.GenericProxyDevice,
                    false,
                    new ProxyPortPool(),
                    proxyFactory: () => proxy)
                {
                    Port = FreePort(),
                };
                controller.Initialize(deferPortAllocation: true);
                IDataTable table = database.CreateTable(
                    WebServiceCaptureMethod.TableName,
                    WebProxyCaptureSchema.CreateFields());
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, table);
                var capture = new LiveCapture(database, table, controller, controller.Port);
                await controller.StartProxyAsync(false);
                return capture;
            }

            internal async Task EnableResponseScriptAsync(string script)
            {
                ScriptModel model = Controller.Scripts[WebServiceCaptureScriptableEventType.WebResponse];
                model.Script = script;
                Assert.Equal(
                    ScriptActivationResult.Enabled,
                    await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
            }

            // A device that sends everything through this capture's proxy and trusts any certificate.
            internal HttpClient CreateClient() => new(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{Port}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });

            internal IDataRecord RowFor(string path) => Assert.Single(
                _table.Dataset.DataRecords,
                record => record.Str(WebServiceCaptureMethod.FieldKey_RequestPath)
                    .EndsWith(path, StringComparison.Ordinal));

            // Best effort: a failed test still stops its proxy, and the cleanup never hides the test's own failure.
            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (Controller.IsRunning)
                    {
                        await Controller.StopProxyAsync().WaitAsync(StopBudget);
                    }
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                }
                finally
                {
                    _database.Dispose();
                }
            }

            private static ushort FreePort()
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
    }
}
