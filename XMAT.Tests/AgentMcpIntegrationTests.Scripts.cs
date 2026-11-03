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
using XMAT.Scripting;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void ScriptToolsRequireSeparateReadEditAndExecutionGrantsAndExactDesktopApprovals()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                var human = new ScriptCaptureContext("human selected capture");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var captureId = Complete(host.GetCapturesAsync()).Single(capture => capture.DeviceName == target.DeviceName).Id;
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(captureId));
                    var edit = new AgentPermission(AgentCapability.ScriptEdit, AgentScope.Capture(captureId));
                    var execute = new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(captureId));
                    var denied = Payload(Complete(client.CallToolAsync("xmat_list_scripts",
                        new Dictionary<string, object> { ["captureId"] = captureId }).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [read]));
                    var listed = Payload(Complete(client.CallToolAsync("xmat_list_scripts",
                        new Dictionary<string, object> { ["captureId"] = captureId }).AsTask()));
                    Assert.Equal(3, listed.GetProperty("rows").GetArrayLength());
                    var script = listed.GetProperty("rows").EnumerateArray().Select(row => row.Deserialize<CaptureScriptDescriptor>(AgentJson.Options))
                        .Single(item => item.Event == CaptureScriptEvent.WebRequest);
                    var symbols = Payload(Complete(client.CallToolAsync("xmat_describe_scripting",
                        new Dictionary<string, object> { ["captureId"] = captureId }).AsTask()));
                    Assert.Contains(symbols.GetProperty("rows").EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "Params.Continue");
                    const string source = "// PRIVATE_SOURCE_REVISION\nParams.Continue = false;";
                    var update = ScriptArguments(captureId, script, "edit-once");
                    update["source"] = source;
                    denied = Payload(Complete(client.CallToolAsync("xmat_update_script", update).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [read, edit]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_update_script", update).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Contains("+++ proposed", host.Runtime.GetApprovalDetails(operation));
                    Assert.Contains("+Params.Continue = false;", host.Runtime.GetApprovalDetails(operation));
                    Assert.DoesNotContain("PRIVATE_SOURCE_REVISION", JsonSerializer.Serialize(host.Runtime.GetOperation(owner, operation)));
                    Assert.NotEqual(source, target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest].Script);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    var edited = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    script = edited.GetProperty("script").Deserialize<CaptureScriptDescriptor>(AgentJson.Options);
                    Assert.False(script.IsEnabled);
                    Assert.Equal(source, target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest].Script);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_update_script", update).AsTask())).GetProperty("id").GetGuid());
                    var validationId = RequestScript(client, "xmat_validate_script", ScriptArguments(captureId, script, "validate"));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, validationId));
                    var validation = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = validationId }).AsTask()));
                    Assert.False(validation.GetProperty("hasErrors").GetBoolean());
                    Assert.True(validation.GetProperty("isCurrent").GetBoolean());
                    Assert.False(target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest].IsEnabled);
                    var enable = ScriptArguments(captureId, script, "enable-once");
                    enable["enabled"] = true;
                    denied = Payload(Complete(client.CallToolAsync("xmat_set_script_enabled", enable).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [read, edit, execute]));
                    var activation = RequestScript(client, "xmat_set_script_enabled", enable);
                    Assert.Contains(source, host.Runtime.GetApprovalDetails(activation));
                    Assert.Contains("NOT sandboxed", host.Runtime.GetApprovalDetails(activation));
                    Assert.False(target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest].IsEnabled);
                    Assert.True(host.Runtime.Approve(activation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, activation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, activation).State);
                    var active = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                    Assert.True(active.IsEnabled);
                    Assert.False(Complete(active.RunScriptAsync(new WebServiceCaptureScriptParams())).Continue);
                    Assert.Same(human, model.SelectedDeviceContext);
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.False(active.IsEnabled);
                    Assert.Equal(source, active.Script);
                    Assert.True(Complete(active.RunScriptAsync(new WebServiceCaptureScriptParams())).Continue);
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
        public void FailedActivationKeepsPagedDiagnosticsProtectedAndNeverEnablesCode()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var source = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                source.Script = "PRIVATE_COMPILER_IDENTIFIER();";
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
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture));
                    var execute = new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(capture));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [read, execute]));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, source.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "compile-failure");
                    args["enabled"] = true;
                    var operation = RequestScript(client, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    var status = host.Runtime.GetOperation(owner, operation);
                    Assert.Equal(AgentOperationState.Failed, status.State);
                    Assert.Equal(AgentAccessError.ScriptCompilationFailed, status.Error);
                    Assert.DoesNotContain("PRIVATE_COMPILER_IDENTIFIER", JsonSerializer.Serialize(status));
                    Assert.False(source.IsEnabled);
                    var result = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    var validation = result.GetProperty("validationId").GetGuid();
                    var diagnostics = Payload(Complete(client.CallToolAsync("xmat_get_script_diagnostics",
                        new Dictionary<string, object> { ["validationId"] = validation }).AsTask()));
                    Assert.Contains("PRIVATE_COMPILER_IDENTIFIER", diagnostics.GetRawText());
                    Assert.Equal(1, diagnostics.GetProperty("rows")[0].GetProperty("startLine").GetInt32());
                    other = Complete(Connect(host, "diagnostic borrower"));
                    Complete(other.CallToolAsync("xmat_get_session").AsTask());
                    var borrower = host.FindSession(host.Runtime.GetSessions().Single(item => item.DeclaredClientName == "diagnostic borrower").Id);
                    Finish(host.Runtime.SetPermissionsAsync(borrower, [read]));
                    var denied = Payload(Complete(other.CallToolAsync("xmat_get_script_diagnostics",
                        new Dictionary<string, object> { ["validationId"] = validation }).AsTask()));
                    Assert.Equal("OperationNotFound", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [execute]));
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    denied = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [read, execute]));
                    denied = Payload(Complete(client.CallToolAsync("xmat_get_script_diagnostics",
                        new Dictionary<string, object> { ["validationId"] = validation }).AsTask()));
                    Assert.Equal("OperationNotFound", denied.GetProperty("error").GetString());
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
        public void ScriptChunksAreExactAndCompileOnlyValidationPreservesHumanEnabledCode()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                Complete(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture))],
                        new AgentLimits(responseRows: 2, responseBytes: 4096)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var operation = RequestScript(client, "xmat_validate_script", ScriptArguments(capture, descriptor, "human-validation"));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    var validation = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask())).GetProperty("validationId").GetGuid();
                    var released = Payload(Complete(client.CallToolAsync("xmat_release_script_validation",
                        new Dictionary<string, object> { ["validationId"] = validation }).AsTask()));
                    Assert.True(released.GetProperty("released").GetBoolean());
                    Assert.True(script.IsEnabled);
                    Assert.False(Complete(script.RunScriptAsync(new WebServiceCaptureScriptParams())).Continue);
                    Assert.DoesNotContain(host.Runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Script);
                    var text = "// " + string.Concat(Enumerable.Repeat("\u03bb\U0001F600", 2000)) + "\nParams.Continue = true;";
                    script.Script = text;
                    descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    using var bytes = new MemoryStream();
                    bool more;
                    do
                    {
                        var response = Complete(client.CallToolAsync("xmat_read_script", new Dictionary<string, object>
                        {
                            ["captureId"] = capture,
                            ["scriptId"] = script.Id,
                            ["expectedRevision"] = descriptor.Revision,
                            ["byteOffset"] = bytes.Length,
                            ["maximumBytes"] = int.MaxValue
                        }).AsTask());
                        Assert.False(response.IsError ?? false, Payload(response).GetRawText());
                        var page = Payload(response);
                        Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 4096);
                        Assert.False(page.TryGetProperty("filename", out var filename));
                        bytes.Write(page.GetProperty("bytes").GetBytesFromBase64());
                        more = page.GetProperty("hasMore").GetBoolean();
                    } while (more);
                    Assert.Equal(text, Encoding.UTF8.GetString(bytes.ToArray()));
                    script.Script = "Params.Continue = false;";
                    var stale = Payload(Complete(client.CallToolAsync("xmat_read_script", new Dictionary<string, object>
                    {
                        ["captureId"] = capture,
                        ["scriptId"] = script.Id,
                        ["expectedRevision"] = descriptor.Revision
                    }).AsTask()));
                    Assert.Equal("SourceChanged", stale.GetProperty("error").GetString());
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
        public void DelayedScriptOwnershipDoesNotAdoptAHumanReactivation()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                Task<ScriptActivationResult> human = null;
                var changed = false;
                script.PropertyChanged += (_, args) =>
                {
                    if (!changed && args.PropertyName == nameof(ScriptModel.IsEnabled) && script.IsEnabled)
                    {
                        changed = true;
                        script.DisableScript();
                        human = script.EnableScriptAsync<WebServiceCaptureScriptParams>();
                    }
                };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner, ScriptReadExecute(capture)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "delayed-owner");
                    args["enabled"] = true;
                    var operation = RequestScript(client, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.NotNull(human);
                    Assert.Equal(ScriptActivationResult.Enabled, Complete(human));
                    var owned = host.Runtime.GetOwnedResources().Single(resource => resource.Resource.Kind == AgentResourceKind.Script);
                    Assert.NotEqual(script.CaptureState().Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), owned.Revision);
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.True(script.IsEnabled);
                    Assert.False(Complete(script.RunScriptAsync(new WebServiceCaptureScriptParams())).Continue);
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
        public void HumanEditsInvalidatePendingScriptApprovalsEvenWhenTheHashReturnsToItsOldValue()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner, ScriptReadExecute(capture)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "stale-source");
                    args["enabled"] = true;
                    var operation = RequestScript(client, "xmat_set_script_enabled", args);
                    script.Script = "Params.Continue = true;";
                    script.Script = "Params.Continue = false;";
                    Assert.Equal(descriptor.SourceHash, script.CaptureState().SourceHash);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(owner, operation).State);
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
        public void DesktopGrantsSourceEditingAndApprovesTheExactDiffWithoutExecutionPermission()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
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
                    ((CheckBox)manager.FindName("ScriptReadGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("ScriptEditGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    Assert.Equal(2, session.Permissions.Count);
                    Assert.DoesNotContain(session.Permissions, permission => permission.Capability == AgentCapability.ScriptExecute);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    var script = Complete(model.CaptureScripts.GetScriptsAsync(capture)).First();
                    var args = ScriptArguments(capture, script, "desktop-diff");
                    args["source"] = "Params.Continue = false;";
                    var operation = RequestScript(client, "xmat_update_script", args);
                    WpfTestHost.Pump();
                    var operations = (DataGrid)manager.FindName("Operations");
                    operations.SelectedItem = operations.Items.Cast<AgentOperationSnapshot>().Single(item => item.Id == operation);
                    WpfTestHost.Pump();
                    Assert.Contains("+Params.Continue = false;", ((TextBox)manager.FindName("ApprovalDetails")).Text);
                    ((Button)manager.FindName("Approve")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Finish(host.Runtime.WaitForCompletionAsync(host.FindSession(session.Id), operation));
                    Assert.Equal("Params.Continue = false;", target.Controller.Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest].Script);
                    Assert.False(target.Controller.Scripts.HasActiveScripts);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AlreadyEnabledHumanScriptsAndExplicitTakeoverSurviveDisconnect(bool takeOverAgentActivation)
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                if (!takeOverAgentActivation)
                    Complete(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner, ScriptReadExecute(capture)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "human-or-takeover");
                    args["enabled"] = true;
                    var operation = RequestScript(client, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    if (takeOverAgentActivation)
                    {
                        Assert.True(host.Runtime.TakeOver(new AgentResourceKey(AgentResourceKind.Script, script.Id)));
                    }
                    else
                    {
                        Assert.DoesNotContain(host.Runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Script);
                    }
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.True(script.IsEnabled);
                    Assert.False(Complete(script.RunScriptAsync(new WebServiceCaptureScriptParams())).Continue);
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
        public void RevokeDuringCompilationDrainsAndCannotEnableTheCancelledRevision()
        {
            _application.Run(_ =>
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = new ScriptModel(WebServiceCaptureScriptableEventType.WebRequest, new ScriptHost(async source =>
                {
                    started.TrySetResult();
                    await release.Task;
                }))
                { Script = "Params.Continue = false;" };
                target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest] = script;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner, ScriptReadExecute(capture)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "cancel-compilation");
                    args["enabled"] = true;
                    var operation = RequestScript(client, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(started.Task);
                    var revoking = host.RevokeAsync(owner.Id);
                    Assert.False(revoking.IsCompleted);
                    Assert.False(script.IsEnabled);
                    release.TrySetResult();
                    Finish(revoking);
                    Assert.False(script.IsCompiling);
                    Assert.False(script.IsEnabled);
                    Assert.Equal(AgentOperationState.Cancelled, host.Runtime.GetOperations().Single(item => item.Id == operation).State);
                    Assert.Equal("Params.Continue = false;", script.Script);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    release.TrySetResult();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void AnotherAgentCannotStealActiveCodeButAHumanEditReleasesTheOldEpoch()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient first = null;
                McpClient second = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    first = Complete(Connect(host, "first script owner"));
                    Complete(first.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner, ScriptReadExecute(capture)));
                    var descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    var args = ScriptArguments(capture, descriptor, "first-owner");
                    args["enabled"] = true;
                    var operation = RequestScript(first, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    second = Complete(Connect(host, "second script owner"));
                    Complete(second.CallToolAsync("xmat_get_session").AsTask());
                    var other = host.FindSession(host.Runtime.GetSessions().Single(item => item.DeclaredClientName == "second script owner").Id);
                    Finish(host.Runtime.SetPermissionsAsync(other, ScriptReadExecute(capture)));
                    descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    args = ScriptArguments(capture, descriptor, "cannot-steal");
                    args["enabled"] = false;
                    var denied = Payload(Complete(second.CallToolAsync("xmat_set_script_enabled", args).AsTask()));
                    Assert.Equal("OwnershipConflict", denied.GetProperty("error").GetString());
                    Assert.True(script.IsEnabled);
                    script.Script = "Params.Continue = true;";
                    descriptor = Complete(model.CaptureScripts.ReadAsync(capture, script.Id)).Script;
                    args = ScriptArguments(capture, descriptor, "human-edited");
                    args["enabled"] = true;
                    operation = RequestScript(second, "xmat_set_script_enabled", args);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(other, operation));
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.True(script.IsEnabled);
                    Assert.True(Complete(script.RunScriptAsync(new WebServiceCaptureScriptParams { Continue = false })).Continue);
                    Finish(host.RevokeAsync(other.Id));
                    Assert.False(script.IsEnabled);
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

        private static AgentPermission[] ScriptReadExecute(Guid captureId) =>
        [
            new(AgentCapability.ScriptRead, AgentScope.Capture(captureId)),
            new(AgentCapability.ScriptExecute, AgentScope.Capture(captureId))
        ];

        private static Dictionary<string, object> ScriptArguments(Guid captureId, CaptureScriptDescriptor script, string key) =>
            new()
            {
                ["captureId"] = captureId,
                ["scriptId"] = script.Id,
                ["expectedRevision"] = script.Revision,
                ["expectedSourceHash"] = script.SourceHash,
                ["idempotencyKey"] = key
            };

        private static Guid RequestScript(McpClient client, string tool, Dictionary<string, object> args)
        {
            var response = Complete(client.CallToolAsync(tool, args).AsTask());
            Assert.False(response.IsError ?? false, Payload(response).GetRawText());
            return Payload(response).GetProperty("id").GetGuid();
        }
    }
}
