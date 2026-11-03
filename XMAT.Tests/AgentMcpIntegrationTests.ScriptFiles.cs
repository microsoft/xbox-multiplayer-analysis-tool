// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
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
        public void ScriptFileExportRequiresReadExportAndFolderGrantsAndNeverExecutesCode()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                var human = new ScriptCaptureContext("human selected capture");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var text = "// PRIVATE_UNCOMPILED_SOURCE \u03bb\U0001F600\r\nINVALID_SOURCE(???);\n";
                script.Script = text;
                script.Filename = Path.Combine(directory.Path, "remembered.txt");
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single(item => item.DeviceName == target.DeviceName);
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                    var export = new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id));
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    var arguments = ScriptFileArguments(capture.Id, descriptor, folder.Id, "source.txt", "save-once");
                    foreach (var permissions in new AgentPermission[][] { [read, write], [export, write], [read, export] })
                    {
                        Finish(host.Runtime.SetPermissionsAsync(session, permissions));
                        var denied = Complete(client.CallToolAsync("xmat_save_script", arguments).AsTask());
                        Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    }
                    Finish(host.Runtime.SetPermissionsAsync(session, [read, export, write]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_script", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Contains(descriptor.SourceHash, host.Runtime.GetApprovalDetails(operation));
                    Assert.Contains("does NOT", host.Runtime.GetApprovalDetails(operation),
                        StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("PRIVATE_UNCOMPILED_SOURCE", JsonSerializer.Serialize(host.Runtime.GetOperation(session, operation)));
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.Equal(descriptor.Revision, receipt.GetProperty("revision").GetInt64());
                    Assert.Equal(descriptor.SourceHash, receipt.GetProperty("sourceHash").GetString());
                    Assert.Equal(Encoding.UTF8.GetBytes(text), File.ReadAllBytes(Path.Combine(directory.Path, "source.txt")));
                    Assert.Equal(descriptor, Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script);
                    Assert.False(script.IsEnabled);
                    Assert.False(script.IsCompiling);
                    Assert.Empty(script.CompilationStatus);
                    Assert.False(File.Exists(script.Filename));
                    Assert.Same(human, model.SelectedDeviceContext);
                    script.Script = "// newer human draft";
                    var replay = Payload(Complete(client.CallToolAsync("xmat_save_script", arguments).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Equal(text, File.ReadAllText(Path.Combine(directory.Path, "source.txt")));
                    Assert.Equal("// newer human draft", script.Script);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ScriptFileApprovalCannotFollowAnAbaEditOrAReplacedScript(bool replace)
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id)),
                         new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id)),
                         new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_script",
                        ScriptFileArguments(capture.Id, descriptor, folder.Id, "stale.txt", "stale")).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    if (replace)
                        target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest] =
                            new XMAT.Scripting.ScriptModel(WebServiceCaptureScriptableEventType.WebRequest);
                    else
                    {
                        var original = script.Script;
                        script.Script = "// different draft";
                        script.Script = original;
                    }
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.False(script.IsEnabled);
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
        public void ScriptFileApprovalIsInvalidatedWhenItsExportGrantIsRevoked()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    var folder = Complete(host.RegisterExportFolderAsync(directory.Path));
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                    var export = new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id));
                    var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [read, export, write]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_save_script",
                        ScriptFileArguments(capture.Id, descriptor, folder.Id, "denied.txt", "revoke")).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Finish(host.Runtime.SetPermissionsAsync(session, [read, write]));
                    Assert.False(host.Runtime.Approve(operation));
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.Equal(descriptor, Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script);
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
        public void DesktopScriptExportGrantIsIndependentFromEditAndExecution()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                var model = new CaptureAppModel();
                model.CaptureDeviceContexts.Add(new ScriptCaptureContext());
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                AgentAccessWindow manager = null;
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)manager.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    var capture = (CaptureDescriptor)((ComboBox)manager.FindName("CaptureTarget")).SelectedItem;
                    ((CheckBox)manager.FindName("ScriptReadGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("ScriptExportGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    Assert.Equal(2, session.Permissions.Count);
                    Assert.Contains(new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id)), session.Permissions);
                    Assert.Contains(new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id)), session.Permissions);
                    Assert.DoesNotContain(session.Permissions, permission => permission.Capability is
                        AgentCapability.ScriptEdit or AgentCapability.ScriptExecute or AgentCapability.FileWrite);
                }
                finally
                {
                    manager?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        private static Dictionary<string, object> ScriptFileArguments(
            Guid captureId, CaptureScriptDescriptor script, Guid folderId, string fileName, string key) => new()
            {
                ["captureId"] = captureId,
                ["scriptId"] = script.Id,
                ["expectedRevision"] = script.Revision,
                ["expectedSourceHash"] = script.SourceHash,
                ["folderId"] = folderId,
                ["fileName"] = fileName,
                ["idempotencyKey"] = key
            };
    }
}
