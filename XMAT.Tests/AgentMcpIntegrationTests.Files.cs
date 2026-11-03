// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        public void QueryFileExport_RequiresFolderGrantAndApprovalAndPreservesPublishedFileOnDisconnect()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    var export = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [raw, export]));
                    var result = Query(client, host, session, source.Id,
                        "SELECT RequestNumber,ResponseBody FROM WebProxyConnections ORDER BY id", "file-source");
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var arguments = new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["folderId"] = folder.Id,
                        ["fileName"] = "result.json",
                        ["format"] = "Json",
                        ["idempotencyKey"] = "save-once"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Assert.Empty(Payload(Complete(client.CallToolAsync("xmat_list_export_folders").AsTask()))
                        .GetProperty("folders").EnumerateArray());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [raw, export, new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))]));
                    var listed = Payload(Complete(client.CallToolAsync("xmat_list_export_folders").AsTask()));
                    Assert.Equal(folder.Id, listed.GetProperty("folders")[0].GetProperty("id").GetGuid());
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.Contains("unsanitized", host.Runtime.GetApprovalDetails(operation));
                    AddQueryRow(database.TableByName("WebProxyConnections"), 206);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(session, operation).State);
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.Equal(205, receipt.GetProperty("rowCount").GetInt64());
                    var path = Path.Combine(directory.Path, "result.json");
                    using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                    Assert.Equal(205, json.RootElement.GetProperty("rows").GetArrayLength());
                    Complete(client.CallToolAsync("xmat_release_query_result",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    var replay = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.True(File.Exists(path));
                    Assert.Single(Directory.GetFiles(directory.Path));
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
        public void QueryFileApproval_RevocationAndDestinationReplacementPreventPublication()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    var export = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id));
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [raw, export, write]));
                    var result = Query(client, host, session, source.Id, "SELECT 1 AS value", "one");
                    var arguments = new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["folderId"] = folder.Id,
                        ["fileName"] = "one.csv",
                        ["format"] = "Csv",
                        ["idempotencyKey"] = "revoked"
                    };
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Finish(host.Runtime.SetPermissionsAsync(session, [raw, export]));
                    Assert.False(host.Runtime.Approve(operation));
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Finish(host.Runtime.SetPermissionsAsync(session, [raw, export, write]));
                    arguments["idempotencyKey"] = "human-created";
                    requested = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    var path = Path.Combine(directory.Path, "one.csv");
                    File.WriteAllText(path, "human file");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Equal("human file", File.ReadAllText(path));
                    arguments["idempotencyKey"] = "existing";
                    var exists = Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask());
                    Assert.Equal("DestinationExists", Payload(exists).GetProperty("error").GetString());
                    arguments["fileName"] = "..\\escape.csv";
                    arguments["idempotencyKey"] = "escape";
                    var invalid = Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask());
                    Assert.Equal("InvalidPath", Payload(invalid).GetProperty("error").GetString());
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
        public void QueryFileExportsShareTheManagedExportAdmissionLimit()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = QueryDatabase();
                var host = new AgentAccessHost(new CaptureAppModel { ActiveDatabase = database }, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))],
                        new AgentLimits(maximumQueryResults: 1)));
                    var result = Query(client, host, session, source.Id, "SELECT 1 AS value", "quota");
                    var download = RequestExport(client, result, "Csv", "retained");
                    Assert.True(host.Runtime.Approve(download));
                    Finish(host.Runtime.WaitForCompletionAsync(session, download));
                    var artifact = host.Runtime.GetResult(session, download).GetProperty("artifactId").GetGuid();
                    var arguments = new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["folderId"] = folder.Id,
                        ["fileName"] = "one.csv",
                        ["format"] = "Csv",
                        ["idempotencyKey"] = "file-quota"
                    };
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentAccessError.LimitExceeded, host.Runtime.GetOperation(session, operation).Error);
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Complete(client.CallToolAsync("xmat_release_query_export",
                        new Dictionary<string, object> { ["artifactId"] = artifact }).AsTask());
                    arguments["idempotencyKey"] = "file-after-release";
                    requested = Payload(Complete(client.CallToolAsync("xmat_save_query_result", arguments).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.True(host.Runtime.GetResult(session, operation).GetProperty("published").GetBoolean());
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
        public void DesktopExportFolderGrant_IsSeparateFromSourceAndDoesNotGrantGeneralFileAccess()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var host = new AgentAccessHost(new CaptureAppModel(), Dispatcher.CurrentDispatcher);
                McpClient client = null;
                AgentAccessWindow window = null;
                try
                {
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    host.MarkReady();
                    host.Enable();
                    window = AgentAccessWindow.ShowOrActivate(owner, host);
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)window.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    var target = (ComboBox)window.FindName("ExportFolderTarget");
                    target.SelectedItem = target.Items.Cast<ExportFolderDescriptor>().Single(item => item.Id == folder.Id);
                    ((CheckBox)window.FindName("FileWriteGrant")).IsChecked = true;
                    ((Button)window.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    Assert.Equal(new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id)),
                        Assert.Single(session.Permissions));
                    Assert.DoesNotContain(session.Permissions, permission => permission.Capability is
                        AgentCapability.RawDataRead or AgentCapability.QueryResultExport or AgentCapability.ScriptExecute);
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_save_query_result");
                    Assert.DoesNotContain(tools, tool => tool.Name is "xmat_read_file" or "xmat_write_file");
                }
                finally
                {
                    window?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }
    }
}
