// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Reflection;
using System.Windows.Threading;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    internal sealed class CaptureDeviceTestData : IDisposable
    {
        internal CaptureDeviceTestData(Func<Task> save = null, TimeSpan? inspectionTimeout = null)
        {
            Database = CaptureInvestigationTestData.WebDatabase();
            Database.CreateTable("CaptureDevices",
                new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"));
            Model = new CaptureAppModel
            {
                ActiveDatabase = Database,
                CaptureDevicesTable = Database.TableByName("CaptureDevices")
            };
            Devices = new CaptureDevices(Model, Dispatcher.CurrentDispatcher, Create,
                () => GdkInstalled, () => DefaultConsole(), save ?? Save, inspectionTimeout);
        }

        internal SqLtDatabase Database { get; }
        internal CaptureAppModel Model { get; }
        internal CaptureDevices Devices { get; }
        internal ProxyPortPool Pool { get; } = new();
        internal List<DeviceContext> Created { get; } = new();
        internal bool GdkInstalled { get; set; } = true;
        internal Func<Task<string>> DefaultConsole { get; set; } = () => Task.FromResult("192.0.2.1");
        internal int SaveCount { get; private set; }
        internal int ConfigurationWrites => Created.Sum(context => context.Configuration.WriteCount);

        private ICaptureDeviceContext Create(DeviceType type, string name, CaptureType captureType)
        {
            var context = new DeviceContext(type, name, captureType, Pool, Database, Model);
            Created.Add(context);
            return context;
        }

        private Task Save()
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        internal CaptureDeviceChange Add(CaptureDeviceRequest request)
        {
            var creation = Complete(Devices.PrepareAddAsync(request));
            return Complete(Devices.AddAsync(creation));
        }

        internal static T Complete<T>(Task<T> task)
        {
            PumpUntil(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }

        internal static void Finish(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        internal static void PumpUntil(Func<bool> condition)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(20), "The device test did not complete.");
                WpfTestHost.Pump();
                Thread.Sleep(2);
            }
        }

        public void Dispose()
        {
            foreach (var context in Created)
            {
                if (context.Proxy != null)
                {
                    context.Proxy.IsRunning = false;
                    Finish(context.Proxy.CloseAsync());
                }
                else
                {
                    ((NetworkTraceCaptureController)context.CaptureController).Dispose();
                }
            }
            Finish(Model.AgentAccess.DisposeAsync().AsTask());
            Database.Dispose();
        }

        internal sealed class DeviceContext : ICaptureDeviceContext
        {
            internal DeviceContext(
                DeviceType type, string name, CaptureType capture, ProxyPortPool pool,
                SqLtDatabase database, CaptureAppModel model)
            {
                DeviceType = type;
                DeviceName = name;
                CaptureType = capture;
                if (capture == CaptureType.WebProxy)
                {
                    Proxy = new WebServiceDeviceCaptureController(name, type, false, pool,
                        Configuration, hostListPolicy: model.HostListPolicy, proxyFactory: () => Listener);
                    Proxy.Initialize();
                    typeof(WebServiceDeviceCaptureController).GetField(
                        "_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(Proxy, database.TableByName(WebServiceCaptureMethod.TableName));
                    CaptureController = Proxy;
                }
                else
                {
                    if (!database.TableNames().Contains(NetworkTraceCaptureMethod.TableName))
                    {
                        database.CreateTable(NetworkTraceCaptureMethod.TableName,
                            new Field<string>("DeviceName"), new Field<int>("ProcessId"), new Field<int>("ThreadId"),
                            new Field<string>("Timestamp"), new Field<string>("MediaType"), new Field<int>("StartPacket"),
                            new Field<int>("EndPacket"), new Field<int>("Fragment"), new Field<int>("Send"),
                            new Field<int>("Receive"), new Field<string>("Payload"));
                    }
                    var trace = new NetworkTraceCaptureController(name, type, new FakeTraceEngineFactory());
                    trace.Initialize(database.TableByName(NetworkTraceCaptureMethod.TableName));
                    CaptureController = trace;
                }
            }

            internal WebServiceDeviceCaptureController Proxy { get; }
            internal DeviceConfiguration Configuration { get; } = new();
            internal DeviceListener Listener { get; } = new();
            public DeviceType DeviceType { get; }
            public CaptureType CaptureType { get; }
            public string DeviceName { get; }
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }
        }

        internal sealed class DeviceConfiguration : IProxyDeviceConfiguration
        {
            internal ProxyConfiguration Current { get; set; } = new(ProxyConfigurationState.Disabled);
            internal Func<Task<ProxyConfiguration>> Read { get; set; }
            internal bool AllowWrites { get; set; }
            internal Func<ProxyEndpoint, Task> Enable { get; set; }
            internal Func<ProxyEndpoint, Task> Disable { get; set; }
            internal int ReadCount { get; private set; }
            internal int WriteCount { get; private set; }

            public Task<ProxyConfiguration> ReadAsync()
            {
                ReadCount++;
                return Read?.Invoke() ?? Task.FromResult(Current);
            }

            public Task DisableAsync() => DisableAsync(null);

            public async Task DisableAsync(ProxyEndpoint previous)
            {
                WriteCount++;
                if (!AllowWrites)
                    throw new InvalidOperationException("Device-management tests must never configure a device.");
                if (Disable != null)
                    await Disable(previous);
                else
                    Current = new(ProxyConfigurationState.Disabled);
            }

            public async Task EnableAsync(ProxyEndpoint endpoint)
            {
                WriteCount++;
                if (!AllowWrites)
                    throw new InvalidOperationException("Device-management tests must never configure a device.");
                if (Enable != null)
                    await Enable(endpoint);
                else
                    Current = new(ProxyConfigurationState.Enabled, endpoint);
            }
        }

        internal sealed class DeviceListener : IWebServiceProxy
        {
#pragma warning disable CS0067
            public event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
            public event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
            public event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
            public event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
            public event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
            public event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
            public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
            public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
            public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
            public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
            public event EventHandler ProxyStopped;
#pragma warning restore CS0067
            internal int StartCount { get; private set; }
            public bool IsProxyEnabled { get; private set; }
            public BypassListModel BypassList { get; set; }
            public Func<SslConnectionRequestEventArgs, CancellationToken, Task<bool>> SslConnectionRequestPolicyAsync { get; set; }
            public Func<HttpRequestEventArgs, CancellationToken, Task<bool>> WebRequestPolicyAsync { get; set; }
            public Func<HttpResponseEventArgs, CancellationToken, Task<bool>> WebResponsePolicyAsync { get; set; }

            public Task StartProxyAsync(WebServiceProxyOptions options, CancellationToken cancellationToken = default)
            {
                StartCount++;
                IsProxyEnabled = true;
                return Task.CompletedTask;
            }

            public Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null)
            {
                IsProxyEnabled = false;
                return Task.CompletedTask;
            }

            public void Reset() { }
        }
    }
}
