// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureDeleteControllerTests
    {
        [Fact]
        public void DeleteSessions_DatabaseFailureLeavesBindingsAndModelsActive() =>
            WpfTestHost.Run(_ =>
            {
                using var capture = new RaceHarness();
                Captured row = capture.Add(1, 1001);
                capture.FailDeletes();

                Exception failure = Assert.ThrowsAny<Exception>(
                    () => capture.Controller.DeleteSessions(
                        [Target(row)],
                        new HashSet<CaptureRowKey> { row.Key }));

                Assert.IsNotType<NotSupportedException>(failure);
                Assert.Contains("injected delete failure", failure.ToString(), StringComparison.Ordinal);
                Assert.Equal(CaptureRowLookup.Current, capture.Registry.Lookup(row.Key));
                Assert.Same(row.Model, capture.Controller.ProxyConnections.ByKey(row.Key));
                Assert.NotNull(capture.Record(row));
            });

        [Theory]
        [InlineData(DeleteRace.HttpResponse)]
        [InlineData(DeleteRace.ConnectCompletion)]
        [InlineData(DeleteRace.Http2Sibling)]
        [InlineData(DeleteRace.WebSocket)]
        [InlineData(DeleteRace.Resolver)]
        public void DeleteSessions_StableRequestIdentityAndInFlightRacesNeverReviveTheDeletedIdentity(DeleteRace race) =>
            WpfTestHost.Run(_ =>
            {
                using var capture = new RaceHarness();
                switch (race)
                {
                    case DeleteRace.HttpResponse:
                        AssertHttpResponseRace(capture);
                        break;
                    case DeleteRace.ConnectCompletion:
                        AssertConnectRace(capture);
                        break;
                    case DeleteRace.Http2Sibling:
                        AssertHttp2Race(capture);
                        break;
                    case DeleteRace.WebSocket:
                        AssertWebSocketRace(capture);
                        break;
                    case DeleteRace.Resolver:
                        AssertResolverRace(capture);
                        break;
                }
            });

        [Fact]
        public void Clear_DoesNotReviveAnExplicitlyRetiredWebSocket() =>
            WpfTestHost.Run(_ =>
            {
                using var capture = new RaceHarness();
                ClientProcessContext client = capture.Client(1015);
                Captured sibling = capture.Add(1, 1015, client: client);
                Captured deleted = capture.Add(2, 1015, client: client);
                capture.Frame(sibling, "sibling before delete");
                capture.Frame(deleted, "deleted before delete");
                capture.Delete(deleted, sibling);

                capture.Controller.ClearAllCaptures();
                capture.Frame(deleted, "deleted after clear");
                capture.Frame(sibling, "sibling after clear");

                WebSocketMessageModel message = Assert.Single(capture.Controller.WebSocketMessages);
                Assert.Equal("sibling after clear", message.PayloadText);
                Assert.Equal(sibling.Key.SourceId, message.RowKey.SourceId);
                Assert.True(message.RowKey.Generation > sibling.Key.Generation);
                Assert.NotEqual(sibling.Key, message.RowKey);
            });

        private static void AssertHttpResponseRace(RaceHarness capture)
        {
            ClientProcessContext client = capture.Client(1010);
            Captured deleted = capture.Add(1, 1010, client: client);
            capture.Delete(deleted);
            Captured twin = capture.Add(1, 1010, client: capture.Client(1010));
            Assert.Equal(deleted.RowId, twin.RowId);

            capture.Response(deleted, "598");
            capture.Response(deleted, "599", keyless: true);
            Assert.Equal(string.Empty, capture.Status(twin));

            Captured next = capture.Add(3, 1010, client: client);
            capture.Response(next, "202");
            Assert.Equal("202", capture.Status(next));
        }

        private static void AssertConnectRace(RaceHarness capture)
        {
            ClientProcessContext client = capture.Client(1011);
            Captured deleted = capture.Add(1, 1011, connect: true, client: client);
            capture.Delete(deleted);
            Captured twin = capture.Add(1, 1011, connect: true, client: capture.Client(1011));
            Assert.Equal(deleted.RowId, twin.RowId);

            capture.CompleteConnect(deleted);
            deleted.Request.RowKey = default;
            capture.CompleteConnect(deleted);
            Assert.Equal(string.Empty, capture.Status(twin));
        }

        private static void AssertHttp2Race(RaceHarness capture)
        {
            ClientProcessContext client = capture.Client(1012);
            Captured sibling = capture.Add(2, 1012, streamId: 3, client: client);
            Captured deleted = capture.Add(1, 1012, streamId: 1, client: client);
            capture.Delete(deleted, sibling);

            capture.Response(deleted, "598");
            capture.Response(sibling, "201");
            Assert.Equal("201", capture.Status(sibling));
            Assert.Same(sibling.Model, capture.Controller.ProxyConnections.ByKey(sibling.Key));

            Captured next = capture.Add(3, 1012, streamId: 5, client: client);
            Assert.NotNull(capture.Record(next));
        }

        private static void AssertWebSocketRace(RaceHarness capture)
        {
            ClientProcessContext client = capture.Client(1013);
            Captured sibling = capture.Add(2, 1013, client: client);
            Captured deleted = capture.Add(1, 1013, client: client);
            capture.Delete(deleted, sibling);

            capture.Frame(deleted, "deleted");
            capture.Frame(sibling, "sibling");

            WebSocketMessageModel message = Assert.Single(capture.Controller.WebSocketMessages);
            Assert.Equal(sibling.Key, message.RowKey);
            Assert.Equal("sibling", message.PayloadText);
        }

        private static void AssertResolverRace(RaceHarness capture)
        {
            ClientProcessContext client = capture.Client(1014, resolved: false);
            Captured deleted = capture.Add(1, 1014, client: client);
            capture.Delete(deleted);
            Captured twin = capture.Add(1, 1014, client: capture.Client(1014));

            client.Publish(Local("late.exe", 9001));
            capture.Pump();

            Assert.Equal(deleted.RowId, twin.RowId);
            Assert.Equal("survivor.exe", twin.Model.ClientProcess.Name);
            Assert.Equal(CaptureRowLookup.Retired, capture.Registry.Lookup(deleted.Key));
        }

        private static CaptureDeleteTarget Target(Captured row) =>
            new(row.Key, new WeakReference<ProxyConnectionModel>(row.Model));

        private static ClientProcessInfo Local(string name, int processId) =>
            new(name, processId, DateTimeOffset.UtcNow.AddMinutes(-1), ClientProcessStatus.Local);

        public enum DeleteRace
        {
            HttpResponse,
            ConnectCompletion,
            Http2Sibling,
            WebSocket,
            Resolver,
        }

        private sealed record Captured(
            ClientRequest Request,
            ClientProcessContext Client,
            ProxyConnectionModel Model,
            int ConnectionId)
        {
            internal CaptureRowKey Key => Model.RowKey;
            internal long RowId => Model.RecordRowId;
        }

        private sealed class RaceHarness : IDisposable
        {
            private readonly SqLtDatabase _database = new();
            private readonly IDataTable _table;
            private readonly AsyncOperationTracker _callbacks;
            private readonly BatchedDispatcherQueue _queue;
            private readonly Guid _run = Guid.NewGuid();

            internal RaceHarness()
            {
                _database.Initialize();
                Controller = new WebServiceDeviceCaptureController("test-console", DeviceType.LocalPC, false);
                _table = _database.CreateTable(WebServiceCaptureMethod.TableName, WebProxyCaptureSchema.CreateFields());
                Field("_dataTable").SetValue(Controller, _table);
                Registry = (CaptureRowRegistry)Field("_captureRows").GetValue(Controller);
                _callbacks = (AsyncOperationTracker)Field("_captureCallbacks").GetValue(Controller);
                _queue = (BatchedDispatcherQueue)Field("_uiUpdateQueue").GetValue(Controller);
                _callbacks.StartAccepting();
                _queue.StartAccepting();
            }

            internal WebServiceDeviceCaptureController Controller { get; }
            internal CaptureRowRegistry Registry { get; }

            internal ClientProcessContext Client(int connectionId, bool resolved = true)
            {
                var client = new ClientProcessContext(
                    _run,
                    connectionId,
                    new IPEndPoint(IPAddress.Loopback, 50000 + connectionId % 1000),
                    new IPEndPoint(IPAddress.Loopback, 8080),
                    DateTimeOffset.UtcNow,
                    Stopwatch.GetTimestamp());
                if (resolved)
                    client.Publish(Local("survivor.exe", 8000 + connectionId));
                return client;
            }

            internal Captured Add(
                int number,
                int connectionId,
                bool connect = false,
                int? streamId = null,
                ClientProcessContext client = null)
            {
                client ??= Client(connectionId);
                var request = new ClientRequest
                {
                    RequestNumber = number,
                    StreamId = streamId,
                    Version = streamId.HasValue ? "HTTP/2" : "HTTP/1.1",
                    Scheme = "https",
                    Host = "example.test",
                    Port = 443,
                    Method = connect ? "CONNECT" : "GET",
                    Path = connect ? "example.test:443" : "/" + number,
                };
                EventArgs args = connect
                    ? new SslConnectionRequestEventArgs
                    {
                        ConnectionID = connectionId, Timestamp = DateTime.Now, Request = request,
                        ProcessContext = client,
                    }
                    : new HttpRequestEventArgs
                    {
                        ConnectionID = connectionId, Timestamp = DateTime.Now, Request = request,
                        ProcessContext = client,
                    };
                Assert.True(Run(
                    connect ? "WebProxy_ReceivedSslConnectionRequestAsync" : "WebProxy_ReceivedWebRequestAsync",
                    args));
                Pump();
                IDataRecord record = Record(number, connectionId);
                ProxyConnectionModel model = Controller.ProxyConnections.Single(row => row.RecordRowId == record.RowId);
                Assert.NotEqual(default, request.RowKey);
                Assert.Equal(model.RowKey, request.RowKey);
                Assert.Equal(CaptureRowLookup.Current, Registry.Lookup(model.RowKey));
                return new Captured(request, client, model, connectionId);
            }

            internal CaptureDeleteResult Delete(Captured deleted, params Captured[] visible)
            {
                CaptureSourceStamp before = Controller.SourceStamp;
                var keys = visible.Select(row => row.Key).Append(deleted.Key).ToHashSet();
                CaptureDeleteResult result = Controller.DeleteSessions([Target(deleted)], keys);
                Assert.Equal(new[] { deleted.Key }, result.Deleted);
                Assert.Equal(before.SourceId, result.SourceStamp.SourceId);
                Assert.True(result.SourceStamp.RetirementEpoch > before.RetirementEpoch);
                Assert.Null(Controller.ProxyConnections.ByKey(deleted.Key));
                return result;
            }

            internal void Response(Captured row, string status, bool keyless = false)
            {
                if (keyless)
                    row.Request.RowKey = default;
                var response = new ServerResponse
                {
                    RequestNumber = row.Request.RequestNumber,
                    Version = row.Request.Version,
                    Status = status,
                    StatusDescription = "Test",
                };
                Assert.True(Run(
                    "WebProxy_ReceivedWebResponseAsync",
                    new HttpResponseEventArgs
                    {
                        ConnectionID = row.ConnectionId, Timestamp = DateTime.Now, Request = row.Request,
                        Response = response, ProcessContext = row.Client,
                    }));
                Pump();
            }

            internal void CompleteConnect(Captured row)
            {
                Invoke(
                    "WebProxy_CompletedSslConnectionRequest",
                    new SslConnectionCompletionEventArgs
                    {
                        ConnectionID = row.ConnectionId, Timestamp = DateTime.Now, Request = row.Request,
                        ProcessContext = row.Client,
                    });
                Pump();
            }

            internal void Frame(Captured row, string text)
            {
                Invoke(
                    "WebProxy_WebSocketMessage",
                    new WebSocketMessageEventArgs
                    {
                        ConnectionID = row.ConnectionId, RequestNumber = row.Request.RequestNumber,
                        ProcessContext = row.Client, RowKey = row.Key, MessageType = WebSocketMessageType.Text,
                        Message = Encoding.UTF8.GetBytes(text),
                    });
                Pump();
            }

            internal string Status(Captured row) =>
                Record(row).Str(WebServiceCaptureMethod.FieldKey_RequestStatus);

            internal IDataRecord Record(Captured row) =>
                Record(row.Request.RequestNumber, row.ConnectionId);

            internal void FailDeletes()
            {
                using SqliteCommand command = Connection().CreateCommand();
                command.CommandText = "CREATE TEMP TRIGGER FailCaptureDelete BEFORE DELETE ON " +
                    "WebProxyConnections BEGIN SELECT RAISE(ABORT, 'injected delete failure'); END";
                command.ExecuteNonQuery();
            }

            internal void Pump() => WpfTestHost.Pump();

            public void Dispose()
            {
                _callbacks.StopAcceptingAndWaitAsync().GetAwaiter().GetResult();
                Task drain = _queue.CompleteAndDrainAsync();
                Pump();
                drain.GetAwaiter().GetResult();
                _database.Dispose();
            }

            private IDataRecord Record(long number, long connectionId) =>
                Assert.Single(_table.Dataset.DataRecordsWhen(
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, "test-console"),
                    new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, number),
                    new FieldValue<long>(WebServiceCaptureMethod.FieldKey_ConnectionId, connectionId)));

            private bool Run(string method, EventArgs args)
            {
                Task<bool> task = (Task<bool>)typeof(WebServiceDeviceCaptureController)
                    .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Controller, [args, CancellationToken.None]);
                return task.GetAwaiter().GetResult();
            }

            private void Invoke(string method, EventArgs args) =>
                typeof(WebServiceDeviceCaptureController)
                    .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Controller, [null, args]);

            private SqliteConnection Connection() =>
                (SqliteConnection)typeof(SqLtDatabase)
                    .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(_database);

            private static FieldInfo Field(string name) =>
                typeof(WebServiceDeviceCaptureController)
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
