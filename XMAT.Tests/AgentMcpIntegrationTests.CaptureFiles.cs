// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Theory]
        [InlineData("CaptureDatabase", "capture.xmatcap")]
        [InlineData("FiddlerSaz", "capture.saz")]
        public void CaptureFileExportRequiresItsOwnGrantAndPublishesOnlyAfterApproval(string format, string fileName)
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, device: "first");
                CaptureInvestigationTestData.AddWeb(database, 2, device: "second");
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
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [raw, write]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["format"] = format,
                        ["folderId"] = folder.Id,
                        ["fileName"] = fileName,
                        ["idempotencyKey"] = "capture-file"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [raw, write, new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Contains("explicit source/device filter", host.Runtime.GetApprovalDetails(operation));
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(session, operation).State);
                    var result = host.Runtime.GetResult(session, operation);
                    Assert.True(result.GetProperty("published").GetBoolean());
                    Assert.False(result.GetProperty("sanitized").GetBoolean());
                    var path = Path.Combine(directory.Path, fileName);
                    Assert.True(File.Exists(path));
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.True(File.Exists(path));
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
        public void CaptureFileApprovalCannotPublishAfterSourceReplacement()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = QueryDatabase();
                using var replacement = QueryDatabase();
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
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_capture", new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["format"] = "CaptureDatabase",
                        ["folderId"] = folder.Id,
                        ["fileName"] = "capture.xmatcap",
                        ["idempotencyKey"] = "stale-capture"
                    }).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    model.ActiveDatabase = replacement;
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Empty(Directory.GetFiles(directory.Path));
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
        public void LoadedNativeSaveRequiresTheLoadedSourcesOwnRawAndExportGrants()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, device: "loaded-only");
                var sourcePath = Path.Combine(directory.Path, "source.xmatcap");
                database.SaveToFile(sourcePath);
                using var loaded = new XMAT.DataAbstractionLayer.SqLtReadonlyDatabase(sourcePath);
                CaptureInvestigationTestData.AddWeb(database, 2, device: "active-only");
                var model = new CaptureAppModel { ActiveDatabase = database, LoadedDatabase = loaded };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var sources = Complete(host.GetSourcesAsync());
                    var active = sources.Single(item => item.Kind == QuerySourceKind.Active);
                    var source = sources.Single(item => item.Kind == QuerySourceKind.Loaded);
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [write, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(active.Id)),
                         new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(active.Id))]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["format"] = "CaptureDatabase",
                        ["folderId"] = folder.Id,
                        ["fileName"] = "loaded.xmatcap",
                        ["idempotencyKey"] = "loaded-native"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [write, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Contains("Loaded", host.Runtime.GetApprovalDetails(operation));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.Equal(source.Id, receipt.GetProperty("sourceId").GetGuid());
                    using var saved = new XMAT.DataAbstractionLayer.SqLtReadonlyDatabase(
                        Path.Combine(directory.Path, "loaded.xmatcap"));
                    Assert.Equal("loaded-only", Assert.Single(saved.TableByName(
                        XMAT.WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords).Str("DeviceName"));

                    arguments["idempotencyKey"] = "loaded-replaced";
                    arguments["fileName"] = "stale.xmatcap";
                    requested = Payload(Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    using var replaced = new XMAT.DataAbstractionLayer.SqLtReadonlyDatabase(sourcePath);
                    model.LoadedDatabase = replaced;
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.False(File.Exists(Path.Combine(directory.Path, "stale.xmatcap")));
                    Assert.Same(replaced, model.LoadedDatabase);
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

        [Fact]
        public void SanitizedLoadedSourceRequiresItsOwnRawExportGrantAndReturnsRedactionCounts()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, device: "first");
                CaptureInvestigationTestData.AddWeb(database, 2, device: "second");
                var sourcePath = Path.Combine(directory.Path, "source.xmatcap");
                database.SaveToFile(sourcePath);
                using var loaded = new XMAT.DataAbstractionLayer.SqLtReadonlyDatabase(sourcePath);
                var model = new CaptureAppModel { ActiveDatabase = database, LoadedDatabase = loaded };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var sources = Complete(host.GetSourcesAsync());
                    var source = sources.Single(item => item.Kind == QuerySourceKind.Loaded);
                    var active = sources.Single(item => item.Kind == QuerySourceKind.Active);
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [write, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(active.Id)),
                         new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(active.Id))]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["format"] = "SanitizedFiddlerSaz",
                        ["folderId"] = folder.Id,
                        ["fileName"] = "sanitized.saz",
                        ["deviceName"] = "first",
                        ["idempotencyKey"] = "sanitized"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [write, new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)),
                         new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Contains("NOT guaranteed anonymous", host.Runtime.GetApprovalDetails(operation));
                    Assert.Contains("first", host.Runtime.GetApprovalDetails(operation));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.True(receipt.GetProperty("sanitized").GetBoolean());
                    Assert.Equal("first", receipt.GetProperty("deviceName").GetString());
                    Assert.Equal(1, receipt.GetProperty("sanitization").GetProperty("sessions").GetInt32());
                    Assert.True(receipt.GetProperty("sanitization").GetProperty("redactedValues").GetInt32() > 0);
                    Assert.Equal("exact-device-in-source", receipt.GetProperty("scope").GetString());
                    Assert.True(File.Exists(Path.Combine(directory.Path, "sanitized.saz")));
                    arguments["format"] = "CaptureDatabase";
                    arguments["fileName"] = "native.xmatcap";
                    arguments["idempotencyKey"] = "native-device-filter";
                    var invalid = Complete(client.CallToolAsync("xmat_save_capture", arguments).AsTask());
                    Assert.Equal("InvalidRequest", Payload(invalid).GetProperty("error").GetString());
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
