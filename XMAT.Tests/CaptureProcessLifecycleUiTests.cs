// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureProcessLifecycleUiTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CaptureProcessLifecycleUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Fact]
        public void StaleRowUpdate_IsANoOpOnTheApplicationDispatcher()
        {
            _application.Run(window =>
            {
                var handled = new List<Exception>();
                Guid run = Guid.NewGuid();
                var registry = new CaptureRowRegistry(Guid.NewGuid(), (_, _) => true);
                var context = Context(run, 1, 50200);
                registry.StartRun(run);
                CaptureRowKey key = registry.RegisterInserted(
                    context,
                    requestToken: 1,
                    rowId: 1,
                    ClientProcessInfo.NotRecorded,
                    out _);
                registry.AdvanceGeneration();
                var queue = new BatchedDispatcherQueue(System.Windows.Application.Current.Dispatcher, 1, handled.Add);
                queue.StartAccepting();

                Assert.True(queue.TryEnqueue(
                    () => Assert.False(
                        registry.TryUpdate(run, key, () => throw new InvalidOperationException()))));
                WpfTestHost.Complete(queue.CompleteAndDrainAsync(), TimeSpan.FromSeconds(5));

                Assert.Empty(handled);
            });
        }

        [Fact]
        public void Clear_RejectsQueuedOldWebSocketFrameAndCapturesNewFrame()
        {
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true);
                AsyncOperationTracker callbacks = Field<AsyncOperationTracker>(controller, "_captureCallbacks");
                BatchedDispatcherQueue queue = Field<BatchedDispatcherQueue>(controller, "_uiUpdateQueue");
                CaptureRowRegistry registry = Field<CaptureRowRegistry>(controller, "_captureRows");
                Guid run = Guid.NewGuid();
                var context = Context(run, 1000, 50400);
                registry.StartRun(run);
                CaptureRowKey oldKey = registry.RegisterInserted(
                    context,
                    requestToken: 40,
                    rowId: 40,
                    ClientProcessInfo.NotRecorded,
                    out _);
                callbacks.StartAccepting();
                queue.StartAccepting();
                var handler = typeof(WebServiceDeviceCaptureController)
                    .GetMethod("WebProxy_WebSocketMessage", BindingFlags.Instance | BindingFlags.NonPublic)
                    .CreateDelegate<Action<object, WebSocketMessageEventArgs>>(controller);

                handler(null, Message("old", context));
                controller.ClearAllCaptures();
                handler(null, Message("new", context));
                WpfTestHost.Complete(callbacks.StopAcceptingAndWaitAsync(), TimeSpan.FromSeconds(5));
                WpfTestHost.Complete(queue.CompleteAndDrainAsync(), TimeSpan.FromSeconds(5));

                WebSocketMessageModel message = Assert.Single(controller.WebSocketMessages);
                Assert.Equal("new", message.PayloadText);
                Assert.NotEqual(default, message.RowKey);
                Assert.NotEqual(oldKey.Generation, message.RowKey.Generation);
            });
        }

        [Fact]
        public void Stop_TerminalizesResolvingRowsBeforeUiDrainAndRejectsLateResolution()
        {
            _application.Run(window =>
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                IDataTable table = database.CreateTable(
                    WebServiceCaptureMethod.TableName,
                    WebProxyCaptureSchema.CreateFields());
                var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true);
                SetField(controller, "_dataTable", table);
                SetField(
                    controller,
                    "_webProxy",
                    new WebServiceDeviceCaptureControllerTests.FailingProxy(
                        new InvalidOperationException("unused start failure"),
                        stopThrows: false,
                        stopFailures: 0));
                AsyncOperationTracker callbacks = Field<AsyncOperationTracker>(controller, "_captureCallbacks");
                BatchedDispatcherQueue queue = Field<BatchedDispatcherQueue>(controller, "_uiUpdateQueue");
                CaptureRowRegistry registry = Field<CaptureRowRegistry>(controller, "_captureRows");
                Guid run = Guid.NewGuid();
                var process = Context(run, 1001, 50401);
                registry.StartRun(run);
                SetField(controller, "_activeRun", run);
                controller.IsRunning = true;
                callbacks.StartAccepting();
                queue.StartAccepting();
                var request = new ClientRequest
                {
                    RequestNumber = 1,
                    Version = "HTTP/1.1",
                    Scheme = "http",
                    Host = "example.test",
                    Port = 80,
                    Method = "GET",
                    Path = "/pending",
                };

                bool accepted = WpfTestHost.Complete(
                    InvokeRequestPolicy(controller, request, process),
                    TimeSpan.FromSeconds(5));
                WpfTestHost.Pump();
                ProxyConnectionModel model = Assert.Single(controller.ProxyConnections);
                Assert.True(accepted);
                Assert.True(model.IsProcessResolving);

                WpfTestHost.Complete(controller.StopProxyAsync(), TimeSpan.FromSeconds(5));

                Assert.False(model.IsProcessResolving);
                Assert.Equal(ClientProcessStatus.Unresolved, model.ClientProcess.Status);
                process.Publish(new ClientProcessInfo(
                    "late.exe",
                    7001,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    ClientProcessStatus.Local));
                WpfTestHost.Pump();
                Assert.Equal(ClientProcessStatus.Unresolved, model.ClientProcess.Status);
                IDataset freshDataset = table.Dataset;
                using (freshDataset as IDisposable)
                {
                    Assert.Equal(
                        ClientProcessStatus.Unresolved,
                        ClientProcessRecordCodec.Read(Assert.Single(freshDataset.DataRecords)).Status);
                }
            });
        }

        private static WebSocketMessageEventArgs Message(string text, ClientProcessContext process) =>
            new()
            {
                ConnectionID = 1000,
                RequestNumber = 40,
                ProcessContext = process,
                MessageType = WebSocketMessageType.Text,
                Message = System.Text.Encoding.UTF8.GetBytes(text),
            };

        private static ClientProcessContext Context(Guid run, long connectionToken, int clientPort) =>
            new(
                run,
                connectionToken,
                new IPEndPoint(IPAddress.Loopback, clientPort),
                new IPEndPoint(IPAddress.Loopback, 8080),
                DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp());

        private static Task<bool> InvokeRequestPolicy(
            WebServiceDeviceCaptureController controller,
            ClientRequest request,
            ClientProcessContext process)
        {
            var method = typeof(WebServiceDeviceCaptureController).GetMethod(
                "WebProxy_ReceivedWebRequestAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var policy = method.CreateDelegate<
                Func<HttpRequestEventArgs, CancellationToken, Task<bool>>>(controller);
            return policy(
                new HttpRequestEventArgs
                {
                    ConnectionID = 1001,
                    Timestamp = DateTime.Now,
                    Request = request,
                    ProcessContext = process,
                },
                CancellationToken.None);
        }

        private static T Field<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    }
}
