// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using Web = XMAT.WebServiceCapture.WebServiceCaptureMethod;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void RawInspectionThroughTheRealCompanionIsExactSourceOwnedBoundedAndOriginalSnapshotBound()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                var row = CaptureInvestigationTestData.AddWeb(database, 1);
                var path = CaptureInvestigationTestData.Secret + new string('雪', 8000);
                database.TableByName(Web.TableName).UpdateRow(row, new FieldValue<string>(Web.FieldKey_RequestPath, path));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                McpClient other = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source));
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary], new(responseRows: 2, responseBytes: 4096)));
                    var result = RunData(client, host, owner, source, "WebProxy", false, "original");
                    var args = new Dictionary<string, object> { ["resultId"] = result, ["kind"] = "Record", ["itemId"] = row };
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_get_data_details", args).AsTask()))
                        .GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary, raw], new(responseRows: 2, responseBytes: 4096)));
                    database.TableByName(Web.TableName).UpdateRow(row, new FieldValue<string>(Web.FieldKey_RequestPath, "changed"));
                    CaptureInvestigationTestData.AddWeb(replacement, 1);
                    model.ActiveDatabase = replacement;
                    database.Dispose();
                    JsonElement field = default;
                    string cursor = null;
                    do
                    {
                        if (cursor != null)
                            args["cursor"] = cursor;
                        var page = Payload(Complete(client.CallToolAsync("xmat_get_data_details", args).AsTask()));
                        Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 4096);
                        Assert.Equal(source, page.GetProperty("sourceId").GetGuid());
                        foreach (var value in page.GetProperty("rows").EnumerateArray())
                            if (value.GetProperty("cells")[0].GetProperty("value").GetString() == Web.FieldKey_RequestPath)
                                field = value.Clone();
                        cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null :
                            page.GetProperty("nextCursor").GetString();
                    } while (cursor != null && field.ValueKind == JsonValueKind.Undefined);
                    Assert.Equal(JsonValueKind.Object, field.ValueKind);
                    Assert.True(field.GetProperty("cells")[1].GetProperty("isTruncated").GetBoolean());
                    var chunkArgs = new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["kind"] = "Record",
                        ["itemId"] = row,
                        ["row"] = field.GetProperty("ordinal").GetInt64(),
                        ["column"] = 1,
                        ["byteOffset"] = Encoding.UTF8.GetByteCount(path) - 13,
                        ["maximumBytes"] = 65536
                    };
                    var chunk = Payload(Complete(client.CallToolAsync("xmat_get_data_detail_value", chunkArgs).AsTask()));
                    Assert.True(chunk.TryGetProperty("bytes", out var chunkBytes), chunk.GetRawText());
                    Assert.Equal(Encoding.UTF8.GetBytes(path)[^13..], chunkBytes.GetBytesFromBase64());
                    Assert.False(chunk.GetProperty("hasMore").GetBoolean());
                    other = Complete(Connect(host, "raw borrower"));
                    Complete(other.CallToolAsync("xmat_get_session").AsTask());
                    var borrower = host.FindSession(host.Runtime.GetSessions().Single(session => session.DeclaredClientName == "raw borrower").Id);
                    Finish(host.Runtime.SetPermissionsAsync(borrower, [summary, raw]));
                    Assert.Equal("OperationNotFound", Payload(Complete(other.CallToolAsync("xmat_get_data_detail_value", chunkArgs).AsTask()))
                        .GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary]));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary, raw]));
                    Assert.Equal("OperationNotFound", Payload(Complete(client.CallToolAsync("xmat_get_data_detail_value", chunkArgs).AsTask()))
                        .GetProperty("error").GetString());
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, DataPage(client, result).GetRawText());
                    Complete(client.CallToolAsync("xmat_release_data_result", new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    if (other != null)
                        Finish(other.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void WebSocketMetadataAndRawPayloadThroughTheCompanionUseExplicitControllerEpochs()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var context = new ScriptCaptureContext("websocket device");
                model.CaptureDeviceContexts.Add(context);
                var bytes = Encoding.UTF8.GetBytes(CaptureInvestigationTestData.Secret);
                context.Controller.WebSocketMessages.Add(new WebSocketMessageModel(new WebSocketMessageEventArgs
                {
                    Timestamp = CaptureInvestigationTestData.Start,
                    ConnectionID = 5,
                    RequestNumber = 9,
                    MessageType = WebSocketMessageType.Text,
                    Message = bytes,
                    FromHost = false
                }));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary]));
                    var availability = Payload(Complete(client.CallToolAsync("xmat_list_websocket_captures",
                        new Dictionary<string, object> { ["sourceId"] = source }).AsTask())).GetProperty("rows")[0];
                    Assert.True(availability.GetProperty("available").GetBoolean());
                    var args = new Dictionary<string, object>
                    {
                        ["sourceId"] = source,
                        ["kind"] = "WebSocket",
                        ["idempotencyKey"] = "websockets",
                        ["filter"] = new
                        {
                            deviceName = context.DeviceName,
                            captureId = availability.GetProperty("captureId").GetGuid(),
                            captureEpoch = availability.GetProperty("captureEpoch").GetInt64(),
                            connectionId = 5,
                            messageType = "Text"
                        }
                    };
                    var operation = Payload(Complete(client.CallToolAsync("xmat_search_captures", args).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    var result = host.Runtime.GetResult(owner, operation).GetProperty("resultId").GetGuid();
                    var page = DataPage(client, result);
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, page.GetRawText());
                    Assert.DoesNotContain(Convert.ToBase64String(bytes), page.GetRawText());
                    Assert.Equal(5, page.GetProperty("rows")[0].GetProperty("connectionId").GetInt32());
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [summary, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source))]));
                    var fields = Payload(Complete(client.CallToolAsync("xmat_get_data_details", new Dictionary<string, object>
                    { ["resultId"] = result, ["kind"] = "Record", ["itemId"] = 1 }).AsTask()));
                    var payload = fields.GetProperty("rows").EnumerateArray().Single(row =>
                        row.GetProperty("cells")[0].GetProperty("value").GetString() == "payload");
                    context.Controller.WebSocketMessages.Clear();
                    var chunk = Payload(Complete(client.CallToolAsync("xmat_get_data_detail_value", new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["kind"] = "Record",
                        ["itemId"] = 1,
                        ["row"] = payload.GetProperty("ordinal").GetInt64(),
                        ["column"] = 1
                    }).AsTask()));
                    Assert.True(chunk.TryGetProperty("bytes", out var chunkBytes), chunk.GetRawText());
                    Assert.Equal(bytes, chunkBytes.GetBytesFromBase64());
                    context.Controller.IsRunning = true;
                    context.Controller.IsRunning = false;
                    args["idempotencyKey"] = "stale epoch";
                    operation = Payload(Complete(client.CallToolAsync("xmat_search_captures", args).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentAccessError.SourceChanged, host.Runtime.GetOperation(owner, operation).Error);
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }
    }
}
