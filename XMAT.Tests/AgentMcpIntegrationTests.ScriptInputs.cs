// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
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
        public void ScriptInputRequiresExactFileAndEditGrantsAndLoadsOnlyAfterDiffApproval()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "input.txt");
                const string text = "// PRIVATE_INPUT \u03bb\U0001F600\r\nINVALID_UNCOMPILED_SOURCE(???);\n";
                File.WriteAllText(path, text, Encoding.Unicode);
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                var human = new ScriptCaptureContext("human selected capture");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Filename = Path.Combine(directory.Path, "remembered-not-loaded.txt");
                script.Script = "Params.Continue = false;";
                Finish(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    var otherPath = Path.Combine(directory.Path, "other.txt");
                    File.WriteAllText(otherPath, "// other file");
                    var other = Complete(host.RegisterInputFileAsync(otherPath));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single(item => item.DeviceName == target.DeviceName);
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                    var edit = new AgentPermission(AgentCapability.ScriptEdit, AgentScope.Capture(capture.Id));
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    var arguments = ScriptInputArguments(capture.Id, descriptor, input.Id, "load-once");
                    foreach (var permissions in new AgentPermission[][]
                    {
                        [read, edit], [edit, file], [read, file],
                        [read, edit, new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(other.Id))]
                    })
                    {
                        Finish(host.Runtime.SetPermissionsAsync(session, permissions));
                        var denied = Complete(client.CallToolAsync("xmat_load_script", arguments).AsTask());
                        Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    }
                    Finish(host.Runtime.SetPermissionsAsync(session, [read, edit, file]));
                    var inputs = Payload(Complete(client.CallToolAsync("xmat_list_input_files").AsTask()));
                    var listed = Assert.Single(inputs.GetProperty("inputs").EnumerateArray());
                    Assert.Equal(input.Id, listed.GetProperty("id").GetGuid());
                    Assert.False(listed.TryGetProperty("path", out var absolutePath));
                    Assert.DoesNotContain("PRIVATE_INPUT", inputs.GetRawText());
                    var requested = Payload(Complete(client.CallToolAsync("xmat_load_script", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Contains(input.Sha256, host.Runtime.GetApprovalDetails(operation));
                    Assert.Contains("+INVALID_UNCOMPILED_SOURCE", host.Runtime.GetApprovalDetails(operation));
                    Assert.DoesNotContain("PRIVATE_INPUT", JsonSerializer.Serialize(host.Runtime.GetOperation(session, operation)));
                    Assert.True(script.IsEnabled);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.Equal(input.Id, receipt.GetProperty("inputId").GetGuid());
                    Assert.Equal(input.Sha256, receipt.GetProperty("inputHash").GetString());
                    Assert.False(receipt.GetProperty("fileNameRebound").GetBoolean());
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                        receipt.GetProperty("script").GetProperty("sourceHash").GetString());
                    Assert.Equal(text, script.Script);
                    Assert.False(script.IsEnabled);
                    Assert.False(script.IsCompiling);
                    Assert.Empty(script.CompilationStatus);
                    Assert.Equal(Path.Combine(directory.Path, "remembered-not-loaded.txt"), script.Filename);
                    Assert.Same(human, model.SelectedDeviceContext);
                    File.WriteAllText(path, "// changed input after successful load");
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_load_script", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Equal(text, script.Script);
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
        [InlineData("content")]
        [InlineData("replacement")]
        [InlineData("script")]
        [InlineData("revoke")]
        public void ScriptInputApprovalCannotFollowChangedInputOrTargetOrRevokedFileAccess(string change)
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "input.txt");
                File.WriteAllText(path, "// original source");
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var original = script.Script;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                    var edit = new AgentPermission(AgentCapability.ScriptEdit, AgentScope.Capture(capture.Id));
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [read, edit, file]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_load_script",
                        ScriptInputArguments(capture.Id, descriptor, input.Id, "stale")).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    if (change == "content")
                        File.WriteAllText(path, "// modified source");
                    else if (change == "replacement")
                    {
                        var replacement = Path.Combine(directory.Path, "replacement.txt");
                        File.WriteAllText(replacement, "// original source");
                        File.Move(replacement, path, overwrite: true);
                    }
                    else if (change == "script")
                    {
                        script.Script = "// newer human edit";
                        script.Script = original;
                    }
                    else
                        Finish(host.Runtime.SetPermissionsAsync(session, [read, edit]));
                    if (change == "revoke")
                        Assert.False(host.Runtime.Approve(operation));
                    else
                    {
                        Assert.True(host.Runtime.Approve(operation));
                        Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                        Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    }
                    Assert.Equal(original, script.Script);
                    Assert.False(script.IsEnabled);
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
        public void DesktopInputGrantIsExactAndDoesNotImplicitlyGrantScriptEditsOrFileBrowsing()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "input.txt");
                File.WriteAllText(path, "// reviewed");
                var host = new AgentAccessHost(new CaptureAppModel(), Dispatcher.CurrentDispatcher);
                AgentAccessWindow manager = null;
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    host.MarkReady();
                    host.Enable();
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)manager.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    var inputs = (ComboBox)manager.FindName("InputFileTarget");
                    inputs.SelectedItem = inputs.Items.Cast<InputFileDescriptor>().Single(item => item.Id == input.Id);
                    ((CheckBox)manager.FindName("FileReadGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    Assert.Equal(new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id)),
                        Assert.Single(session.Permissions));
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_load_script");
                    Assert.DoesNotContain(tools, tool => tool.Name is "xmat_read_file" or "xmat_register_input_file");
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

        private static Dictionary<string, object> ScriptInputArguments(
            Guid captureId, CaptureScriptDescriptor script, Guid inputId, string key) => new()
            {
                ["captureId"] = captureId,
                ["scriptId"] = script.Id,
                ["expectedRevision"] = script.Revision,
                ["expectedSourceHash"] = script.SourceHash,
                ["inputId"] = inputId,
                ["idempotencyKey"] = key
            };
    }
}
