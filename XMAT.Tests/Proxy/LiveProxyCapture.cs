// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using XMAT.DataAbstractionLayer;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    // A capture tab's controller with its own proxy, started as XMAT starts it, recording into a database of its own.
    // Its UI updates run on the shared WPF application's dispatcher, as in XMAT, and HTTPS uses CERTIFICATE. The proxy
    // reaches servers through the handlers that UPSTREAM makes, when given.
    internal sealed class LiveProxyCapture : IAsyncDisposable
    {
        internal static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(60);

        private readonly SqLtDatabase _database;
        private readonly IDataTable _table;
        private readonly ConcurrentQueue<string> _warnings = new();

        private LiveProxyCapture(
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

        internal static async Task<LiveProxyCapture> StartAsync(
            X509Certificate2 certificate,
            Func<HttpMessageHandler> upstream = null)
        {
            CaptureUiApplicationFixture.RunOnApplication(_ => { });
            var database = new SqLtDatabase();
            database.Initialize();
            var proxy = new WebServiceProxy
            {
                InterceptedCertificateOverride = _ => certificate,
                UpstreamHandlerFactory = upstream,
            };
            var controller = new WebServiceDeviceCaptureController(
                "live-capture",
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
            var capture = new LiveProxyCapture(database, table, controller, controller.Port);
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

        // Best effort: a failed test still stops its proxy, and the cleanup never hides the test's own failure: a stop that
        // fails is only traced. The database is disposed last, whatever happened.
        public async ValueTask DisposeAsync()
        {
            using (_database)
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
                    System.Diagnostics.Trace.WriteLine(
                        $"Stopping a live capture failed: {exception.GetType().Name}: {exception.Message}");
                }
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
