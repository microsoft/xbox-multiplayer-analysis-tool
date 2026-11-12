// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // A writable capture tab of an application model whose traffic goes through the controller's own proxy handlers: each
    // request is stored with its key and client process on a connection of its own, and its response completes it.
    internal sealed class LiveCaptureTab : ICaptureDeviceContext, IDisposable
    {
        private readonly SqLtDatabase _database = new();
        private readonly CaptureAppModel _app;
        private readonly Guid _run = Guid.NewGuid();
        private readonly Dictionary<int, (ClientProcessContext Client, ClientRequest Request)> _sessions = new();
        private readonly AsyncOperationTracker _callbacks;
        private readonly BatchedDispatcherQueue _uiUpdates;

        internal LiveCaptureTab(CaptureAppModel app = null, string name = "Delete test", bool readOnly = false)
        {
            _database.Initialize();
            _app = app ?? CaptureAppModel.AppModel;
            DeviceName = name;
            IsReadOnly = readOnly;
            Controller = new WebServiceDeviceCaptureController(name, DeviceType.LocalPC, readOnly);
            Field("_dataTable").SetValue(
                Controller, _database.CreateTable(WebServiceCaptureMethod.TableName, WebProxyCaptureSchema.CreateFields()));
            _callbacks = (AsyncOperationTracker)Field("_captureCallbacks").GetValue(Controller);
            _uiUpdates = (BatchedDispatcherQueue)Field("_uiUpdateQueue").GetValue(Controller);
            _callbacks.StartAccepting();
            _uiUpdates.StartAccepting();
            _app.CaptureDeviceContexts.Add(this);
        }

        public DeviceType DeviceType => DeviceType.LocalPC;
        public CaptureType CaptureType => CaptureType.WebProxy;
        public string DeviceName { get; }
        public bool IsSelected { get; set; }
        public bool IsReadOnly { get; set; }
        public System.Drawing.Color DeviceColor { get; set; }
        public IDeviceCaptureController CaptureController => Controller;
        internal WebServiceDeviceCaptureController Controller { get; }

        // Records request NUMBER from PROCESS and, unless STATUS is null, its response; returns its row.
        internal ProxyConnectionModel Add(int number, string process = "Game.exe", string host = "a.test", string status = "200")
        {
            var client = new ClientProcessContext(_run, number, new IPEndPoint(IPAddress.Loopback, 50000 + number),
                new IPEndPoint(IPAddress.Loopback, 8080), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
            client.Publish(Local(process, 4000 + number));
            var request = new ClientRequest
                { RequestNumber = number, Version = "HTTP/1.1", Scheme = "https", Host = host, Port = 443, Method = "GET", Path = "/" };
            _sessions[number] = (client, request);
            Assert.True(Raise("WebProxy_ReceivedWebRequestAsync",
                new HttpRequestEventArgs { ConnectionID = number, Timestamp = DateTime.Now, Request = request, ProcessContext = client }));
            Assert.True(status == null || Respond(number, status));
            return Controller.ProxyConnections.Last(row => row.RequestNumber == number);
        }

        // The response to request NUMBER, as the proxy reports it; returns whether the proxy forwards it. FORWARDED reports
        // a response that was streamed to the client before this event.
        internal bool Respond(
            int number, string status = "200", Action<ServerResponse> change = null, bool forwarded = false)
        {
            (ClientProcessContext client, ClientRequest request) = _sessions[number];
            var response = new ServerResponse
                { RequestNumber = number, Version = "HTTP/1.1", Status = status, BodyBytes = Encoding.UTF8.GetBytes("body " + number) };
            change?.Invoke(response);
            return Raise("WebProxy_ReceivedWebResponseAsync", new HttpResponseEventArgs
            {
                ConnectionID = number,
                Timestamp = DateTime.Now,
                Request = request,
                Response = response,
                ProcessContext = client,
                ResponseForwarded = forwarded,
            });
        }

        // A WebSocket frame on request NUMBER's connection.
        internal void Frame(int number, string text)
        {
            Handler<Action<object, WebSocketMessageEventArgs>>("WebProxy_WebSocketMessage")(null, new WebSocketMessageEventArgs
                { ConnectionID = number, RequestNumber = number, ProcessContext = _sessions[number].Client, Message = Encoding.UTF8.GetBytes(text) });
            WpfTestHost.Pump();
        }

        // One of the controller's proxy event handlers, by name.
        internal TDelegate Handler<TDelegate>(string name) where TDelegate : Delegate =>
            typeof(WebServiceDeviceCaptureController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate<TDelegate>(Controller);

        // Stops the capture callbacks and drains the UI updates they queued, then closes the database. Each step is attempted
        // even if another fails, and the first failure is reported. The shared dispatcher is pumped for a bounded time and
        // never blocked on work that needs it.
        public void Dispose()
        {
            ExceptionDispatchInfo failure = null;
            foreach (Action step in new Action[]
            {
                () => WpfTestHost.Complete(_callbacks.StopAcceptingAndWaitAsync(), TimeSpan.FromSeconds(10)),
                () => WpfTestHost.Complete(_uiUpdates.CompleteAndDrainAsync(), TimeSpan.FromSeconds(10)),
                () => _app.CaptureDeviceContexts.Remove(this),
                _database.Dispose,
            })
            {
                try
                {
                    step();
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            failure?.Throw();
        }

        // Runs a capture policy handler to completion, then the UI updates it queued.
        private bool Raise<T>(string policy, T args)
        {
            Task<bool> task = Handler<Func<T, CancellationToken, Task<bool>>>(policy)(args, CancellationToken.None);
            bool result = WpfTestHost.Complete(task, TimeSpan.FromSeconds(5));
            WpfTestHost.Pump();
            return result;
        }

        private static FieldInfo Field(string name) =>
            typeof(WebServiceDeviceCaptureController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
