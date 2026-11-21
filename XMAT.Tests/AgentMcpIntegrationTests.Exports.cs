// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void QueryExportRequiresItsOwnGrantAndApprovalAndDownloadsTheEntireFrozenResult()
        {
            _application.Run(_ =>
            {
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                McpClient other = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    var export = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id));
                    var limits = new AgentLimits(responseRows: 2, responseBytes: 4096);
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw], limits));
                    var result = Query(client, host, owner, source.Id,
                        "SELECT RequestNumber,ResponseBody FROM WebProxyConnections ORDER BY id", "export-source");
                    var arguments = new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["format"] = "Json",
                        ["idempotencyKey"] = "export-once"
                    };
                    var denied = Payload(Complete(client.CallToolAsync("xmat_export_query_result", arguments).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw, export], limits));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_export_query_result", arguments).AsTask()));
                    var operationId = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.DoesNotContain(host.Runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Artifact);
                    Assert.Contains("unsanitized", host.Runtime.GetApprovalDetails(operationId));
                    Assert.Contains("spreadsheet", host.Runtime.GetApprovalDetails(operationId));
                    var replay = Payload(Complete(client.CallToolAsync("xmat_export_query_result", arguments).AsTask()));
                    Assert.Equal(operationId, replay.GetProperty("id").GetGuid());
                    AddQueryRow(database.TableByName("WebProxyConnections"), 206);
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operationId));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, operationId).State);
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    var artifactId = receipt.GetProperty("artifactId").GetGuid();
                    Assert.Equal(205, receipt.GetProperty("rowCount").GetInt64());
                    Assert.False(receipt.TryGetProperty("path", out var destination));
                    Complete(client.CallToolAsync("xmat_release_query_result",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    replay = Payload(Complete(client.CallToolAsync("xmat_export_query_result", arguments).AsTask()));
                    Assert.Equal(operationId, replay.GetProperty("id").GetGuid());

                    other = Complete(Connect(host, "not export owner"));
                    Complete(other.CallToolAsync("xmat_get_session").AsTask());
                    var otherSession = host.FindSession(host.Runtime.GetSessions()
                        .Single(item => item.DeclaredClientName == "not export owner").Id);
                    Finish(host.Runtime.SetPermissionsAsync(otherSession, [raw, export]));
                    var borrowed = Payload(Complete(other.CallToolAsync("xmat_get_query_export_chunk",
                        new Dictionary<string, object> { ["artifactId"] = artifactId }).AsTask()));
                    Assert.Equal("OperationNotFound", borrowed.GetProperty("error").GetString());
                    var output = Download(client, artifactId, limits.ResponseBytes);
                    Assert.Equal(receipt.GetProperty("length").GetInt64(), output.LongLength);
                    using var json = JsonDocument.Parse(output);
                    var rows = json.RootElement.GetProperty("rows");
                    Assert.Equal(205, rows.GetArrayLength());
                    Assert.Equal(205, rows[204].EnumerateArray().First().GetInt64());
                    Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("sensitive-205")), rows[204][1].GetString());
                    var released = Payload(Complete(client.CallToolAsync("xmat_release_query_export",
                        new Dictionary<string, object> { ["artifactId"] = artifactId }).AsTask()));
                    Assert.True(released.GetProperty("released").GetBoolean());
                    Assert.Empty(host.Runtime.GetOwnedResources());
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
        public void RevokingExportDestroysArtifactsWithoutDiscardingTheRawQueryResult()
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
                    var export = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw, export]));
                    var result = Query(client, host, owner, source.Id, "SELECT 'private-value' AS body", "csv-source");
                    var operationId = RequestExport(client, result, "Csv", "csv");
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operationId));
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    var artifact = receipt.GetProperty("artifactId").GetGuid();
                    Assert.Equal("\"body\"\r\n\"private-value\"\r\n", Encoding.UTF8.GetString(Download(client, artifact, 64 * 1024)));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw]));
                    Assert.DoesNotContain(host.Runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Artifact);
                    var protectedReceipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    Assert.Equal("PermissionDenied", protectedReceipt.GetProperty("error").GetString());
                    var page = Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    Assert.NotEqual(true, page.IsError);
                    Finish(host.Runtime.SetPermissionsAsync(owner, [raw, export]));
                    var lost = Payload(Complete(client.CallToolAsync("xmat_get_query_export_chunk",
                        new Dictionary<string, object> { ["artifactId"] = artifact }).AsTask()));
                    Assert.Equal("OperationNotFound", lost.GetProperty("error").GetString());
                    var next = RequestExport(client, result, "Json", "json-after-regrant");
                    Assert.True(host.Runtime.Approve(next));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, next));
                    Finish(host.RevokeAsync(owner.Id));
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
        public void ExportQuotaFailurePublishesNoArtifactAndReleasedPendingResultsCannotBeExported()
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
                        [new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id))],
                        new AgentLimits(maximumQueryResultBytes: 32 * 1024)));
                    var result = Query(client, host, owner, source.Id, "SELECT CAST(zeroblob(12000) AS TEXT) AS body", "large");
                    var operation = RequestExport(client, result, "Json", "too-large");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentAccessError.LimitExceeded, host.Runtime.GetOperation(owner, operation).Error);
                    var failure = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    Assert.Equal("ResultLimit", failure.GetProperty("queryError").GetString());
                    Assert.All(host.Runtime.GetOwnedResources(), item => Assert.Equal(AgentResourceKind.QueryResult, item.Resource.Kind));
                    var pending = RequestExport(client, result, "Csv", "release-before-approval");
                    Complete(client.CallToolAsync("xmat_release_query_result",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    Assert.True(host.Runtime.Approve(pending));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, pending));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(owner, pending).State);
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

        private static Guid RequestExport(McpClient client, Guid resultId, string format, string key) =>
            Payload(Complete(client.CallToolAsync("xmat_export_query_result", new Dictionary<string, object>
            {
                ["resultId"] = resultId,
                ["format"] = format,
                ["idempotencyKey"] = key
            }).AsTask())).GetProperty("id").GetGuid();

        private static byte[] Download(McpClient client, Guid artifactId, int responseBytes)
        {
            using var output = new MemoryStream();
            bool more;
            do
            {
                var page = Payload(Complete(client.CallToolAsync("xmat_get_query_export_chunk", new Dictionary<string, object>
                {
                    ["artifactId"] = artifactId,
                    ["byteOffset"] = output.Length,
                    ["maximumBytes"] = int.MaxValue
                }).AsTask()));
                Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= responseBytes);
                var bytes = page.GetProperty("bytes").GetBytesFromBase64();
                Assert.True(bytes.Length > 0);
                output.Write(bytes);
                more = page.GetProperty("hasMore").GetBoolean();
            } while (more);
            return output.ToArray();
        }
    }
}
