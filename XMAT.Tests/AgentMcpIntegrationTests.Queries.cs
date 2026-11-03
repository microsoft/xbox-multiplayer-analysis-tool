// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void RealClientDiscoversSchemaButNeedsExactRawSourceGrantForQueries()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary]));
                    var sources = Payload(Complete(client.CallToolAsync("xmat_list_sources").AsTask()));
                    var sourceId = sources.GetProperty("sources")[0].GetProperty("id").GetGuid();
                    var schema = Payload(Complete(client.CallToolAsync("xmat_describe_schema",
                        new Dictionary<string, object> { ["sourceId"] = sourceId }).AsTask()));
                    Assert.Equal("WebProxyConnections", schema.GetProperty("rows")[0].GetProperty("name").GetString());
                    Assert.Equal(3, schema.GetProperty("samples").GetArrayLength());
                    var fields = Payload(Complete(client.CallToolAsync("xmat_describe_schema",
                        new Dictionary<string, object> { ["sourceId"] = sourceId, ["tableName"] = "WebProxyConnections" }).AsTask()));
                    Assert.Equal("id", fields.GetProperty("rows")[0].GetProperty("name").GetString());
                    Assert.True(fields.GetProperty("rows")[0].GetProperty("primaryKey").GetBoolean());
                    var request = new Dictionary<string, object>
                    {
                        ["sourceId"] = sourceId,
                        ["sql"] = "SELECT RequestStatus,count(*) AS Calls FROM WebProxyConnections GROUP BY RequestStatus ORDER BY RequestStatus",
                        ["idempotencyKey"] = "counts"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_run_query", request).AsTask());
                    Assert.True(denied.IsError);
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Assert.Empty(host.Runtime.GetOperations());

                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [summary, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()))]));
                    denied = Complete(client.CallToolAsync("xmat_run_query", request).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());

                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(sourceId));
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary, raw],
                        new AgentLimits(responseRows: 2, responseBytes: 8192, queryTimeoutSeconds: 5,
                            maximumQueryResultBytes: 1024 * 1024, maximumQueryResults: 2)));
                    var operationId = Payload(Complete(client.CallToolAsync("xmat_run_query", request).AsTask()))
                        .GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(session, operationId));
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    var resultId = receipt.GetProperty("resultId").GetGuid();
                    Assert.Equal(2, receipt.GetProperty("rowCount").GetInt64());
                    var page = Payload(Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = resultId }).AsTask()));
                    Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
                    var cells = page.GetProperty("rows")[1].GetProperty("cells");
                    Assert.Equal("429", cells[0].GetProperty("value").GetString());
                    Assert.Equal("Integer", cells[1].GetProperty("kind").GetString());
                    Assert.Equal(68, cells[1].GetProperty("value").GetInt64());
                    var replay = Payload(Complete(client.CallToolAsync("xmat_run_query", request).AsTask()));
                    Assert.Equal(operationId, replay.GetProperty("id").GetGuid());

                    Finish(host.Runtime.SetPermissionsAsync(session, [summary]));
                    var revoked = Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask());
                    Assert.True(revoked.IsError);
                    Assert.Equal("PermissionDenied", Payload(revoked).GetProperty("error").GetString());
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void QueryPagesRemainFrozenAreConnectionBoundAndReleaseCacheCapacity()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient first = null;
                McpClient second = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    first = Complete(Connect(host, "query owner"));
                    Complete(first.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    var limits = new AgentLimits(responseRows: 2, responseBytes: 8192, maximumQueryResults: 1);
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw], limits));
                    var id = Query(first, host, owner, source.Id,
                        "SELECT id,RequestPath,ResponseBody FROM WebProxyConnections ORDER BY id", "frozen");
                    AddQueryRow(database.TableByName("WebProxyConnections"), 206);
                    var firstPage = Payload(Complete(first.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = id }).AsTask()));
                    Assert.Equal(205, firstPage.GetProperty("totalRows").GetInt64());
                    Assert.Equal(2, firstPage.GetProperty("rows").GetArrayLength());
                    Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
                    var cursor = firstPage.GetProperty("nextCursor").GetString();
                    var next = Payload(Complete(first.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = id, ["cursor"] = cursor }).AsTask()));
                    Assert.Equal(2, next.GetProperty("offset").GetInt64());
                    Assert.Equal(3, next.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetInt64());

                    second = Complete(Connect(host, "not owner"));
                    Complete(second.CallToolAsync("xmat_get_session").AsTask());
                    var other = host.FindSession(host.Runtime.GetSessions().Single(item => item.DeclaredClientName == "not owner").Id);
                    Finish(host.Runtime.SetPermissionsAsync(other, [raw]));
                    var borrowed = Complete(second.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = id, ["cursor"] = cursor }).AsTask());
                    Assert.True(borrowed.IsError);
                    Assert.Equal("OperationNotFound", Payload(borrowed).GetProperty("error").GetString());

                    var excess = Payload(Complete(first.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = "SELECT 1",
                        ["idempotencyKey"] = "full-cache"
                    }).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, excess));
                    Assert.Equal(AgentAccessError.LimitExceeded, host.Runtime.GetOperation(owner, excess).Error);
                    var released = Payload(Complete(first.CallToolAsync("xmat_release_query_result",
                        new Dictionary<string, object> { ["resultId"] = id }).AsTask()));
                    Assert.True(released.GetProperty("released").GetBoolean());
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    var latest = Query(first, host, owner, source.Id, "SELECT count(*) FROM WebProxyConnections", "after-release");
                    var after = Payload(Complete(first.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = latest }).AsTask()));
                    Assert.Equal(206, after.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetInt64());
                }
                finally
                {
                    if (first != null)
                        Finish(first.DisposeAsync().AsTask());
                    if (second != null)
                        Finish(second.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void QueryFailuresExposeDetailsOnlyBehindTheRawGrantAndNeverMutateSource()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_run_query",
                        new Dictionary<string, object>
                        {
                            ["sourceId"] = source.Id,
                            ["sql"] = "SELECT 'private-value' FROM MissingTable",
                            ["idempotencyKey"] = "invalid"
                        }).AsTask()));
                    var id = requested.GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, id));
                    var status = host.Runtime.GetOperation(owner, id);
                    Assert.Equal(AgentOperationState.Failed, status.State);
                    Assert.DoesNotContain("MissingTable", JsonSerializer.Serialize(status));
                    Assert.DoesNotContain("private-value", JsonSerializer.Serialize(status));
                    var detail = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = id }).AsTask()));
                    Assert.Equal("InvalidQuery", detail.GetProperty("queryError").GetString());
                    Assert.Contains("MissingTable", detail.GetProperty("message").GetString());

                    var mutation = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = "DELETE FROM WebProxyConnections",
                        ["idempotencyKey"] = "write"
                    }).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, mutation));
                    Assert.Equal(AgentOperationState.Failed, host.Runtime.GetOperation(owner, mutation).State);
                    Assert.Equal(205, database.TableByName("WebProxyConnections").Dataset.DataRecords.Count());

                    var running = new Context("running");
                    running.Controller.SetRunning(true);
                    model.CaptureDeviceContexts.Add(running);
                    var busy = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = "SELECT 1",
                        ["idempotencyKey"] = "busy"
                    }).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, busy));
                    Assert.Equal(AgentAccessError.OperationUnavailable, host.Runtime.GetOperation(owner, busy).Error);
                    Assert.True(running.Controller.IsRunning);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void LargeQueryValuesHaveMarkedPreviewsAndExactBudgetedByteChunks()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw], new AgentLimits(responseBytes: 8192)));
                    var id = Query(client, host, owner, source.Id,
                        "SELECT 'start'||char(0)||replace(hex(zeroblob(4000)),'00','\u03bb\U0001F600') AS body", "large-text");
                    var response = Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = id }).AsTask());
                    var page = Payload(response);
                    Assert.False(response.IsError ?? false, page.GetRawText());
                    var preview = page.GetProperty("rows")[0].GetProperty("cells")[0];
                    Assert.True(preview.GetProperty("isTruncated").GetBoolean());
                    using var bytes = new MemoryStream();
                    bool more;
                    do
                    {
                        var chunk = Payload(Complete(client.CallToolAsync("xmat_get_query_value", new Dictionary<string, object>
                        {
                            ["resultId"] = id,
                            ["row"] = 0,
                            ["column"] = 0,
                            ["byteOffset"] = bytes.Length,
                            ["maximumBytes"] = int.MaxValue
                        }).AsTask()));
                        Assert.True(Encoding.UTF8.GetByteCount(chunk.GetRawText()) <= 8192);
                        Assert.Equal("utf-8", chunk.GetProperty("encoding").GetString());
                        var data = chunk.GetProperty("bytes").GetBytesFromBase64();
                        Assert.NotEmpty(data);
                        bytes.Write(data);
                        more = chunk.GetProperty("hasMore").GetBoolean();
                    } while (more);
                    Assert.Equal("start\0" + string.Concat(Enumerable.Repeat("\u03bb\U0001F600", 4000)),
                        Encoding.UTF8.GetString(bytes.ToArray()));
                    Assert.Equal(bytes.Length, preview.GetProperty("byteLength").GetInt64());
                    Finish(host.Runtime.SetPermissionsAsync(owner, []));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw]));
                    var lost = Payload(Complete(client.CallToolAsync("xmat_get_query_value", new Dictionary<string, object>
                    {
                        ["resultId"] = id,
                        ["row"] = 0,
                        ["column"] = 0
                    }).AsTask()));
                    Assert.Equal("OperationNotFound", lost.GetProperty("error").GetString());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void QueryTimeoutAndCancellationLeaveEarlierCompletedResultsAvailable()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id))]));
                    var previous = Query(client, host, owner, source.Id, "SELECT 42 AS answer", "previous");
                    const string expensive = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n";
                    var timed = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = expensive,
                        ["idempotencyKey"] = "timeout",
                        ["timeoutSeconds"] = 1
                    }).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, timed));
                    Assert.Equal(AgentAccessError.QueryTimeout, host.Runtime.GetOperation(owner, timed).Error);
                    var error = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = timed }).AsTask()));
                    Assert.Equal("Timeout", error.GetProperty("queryError").GetString());
                    var cancelled = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = expensive,
                        ["idempotencyKey"] = "cancel"
                    }).AsTask())).GetProperty("id").GetGuid();
                    Complete(client.CallToolAsync("xmat_cancel_operation",
                        new Dictionary<string, object> { ["operationId"] = cancelled }).AsTask());
                    Finish(host.Runtime.WaitForCompletionAsync(owner, cancelled));
                    Assert.Equal(AgentOperationState.Cancelled, host.Runtime.GetOperation(owner, cancelled).State);
                    var page = Payload(Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = previous }).AsTask()));
                    Assert.Equal(42, page.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetInt64());
                    Assert.Single(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        private static Guid Query(McpClient client, AgentAccessHost host, AgentSession owner, Guid sourceId, string sql, string key)
        {
            var id = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
            {
                ["sourceId"] = sourceId,
                ["sql"] = sql,
                ["idempotencyKey"] = key
            }).AsTask())).GetProperty("id").GetGuid();
            Finish(host.Runtime.WaitForCompletionAsync(owner, id));
            Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, id).State);
            return Payload(Complete(client.CallToolAsync("xmat_get_result",
                new Dictionary<string, object> { ["operationId"] = id }).AsTask())).GetProperty("resultId").GetGuid();
        }

        private static SqLtDatabase QueryDatabase()
        {
            var database = new SqLtDatabase();
            database.Initialize();
            var table = database.CreateTable("WebProxyConnections", new Field<long>("RequestNumber"),
                new Field<string>("DeviceName"), new Field<string>("RequestHost"), new Field<string>("RequestStatus"),
                new Field<string>("RequestPath"), new Field<string>("ResponseBody"));
            for (var index = 1; index <= 205; index++)
                AddQueryRow(table, index);
            return database;
        }

        private static void AddQueryRow(IDataTable table, int index) => table.AddRow(
            (long)index, "test-device", "service.example", index % 3 == 0 ? "429" : "200",
            $"/calls/{index}?token=sample", Convert.ToBase64String(Encoding.UTF8.GetBytes($"sensitive-{index}")));
    }
}
