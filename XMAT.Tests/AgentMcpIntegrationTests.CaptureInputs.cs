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
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void NativeInputLoadsOnlyAfterIndependentGrantsAndApprovalAndDoesNotGrantSourceAccess()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var original = File.ReadAllBytes(path);
                var model = new CaptureAppModel();
                var human = new ScriptCaptureContext("selected human capture");
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    var otherPath = Path.Combine(directory.Path, "other.xmatcap");
                    CaptureFileLoadTests.WriteCapture(otherPath, []);
                    var other = Complete(host.RegisterInputFileAsync(otherPath));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var load = new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application);
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    var arguments = CaptureInputArguments(input.Id, null, "load-once");
                    foreach (var permissions in new AgentPermission[][]
                    {
                        [load], [file],
                        [load, new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(other.Id))]
                    })
                    {
                        Finish(host.Runtime.SetPermissionsAsync(session, permissions));
                        var denied = Payload(Complete(client.CallToolAsync("xmat_load_capture", arguments).AsTask()));
                        Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    }
                    Assert.Null(model.LoadedDatabase);
                    Finish(host.Runtime.SetPermissionsAsync(session, [load, file]));
                    var listed = Payload(Complete(client.CallToolAsync("xmat_list_input_files").AsTask()));
                    var metadata = Assert.Single(listed.GetProperty("inputs").EnumerateArray());
                    Assert.Equal("CaptureDatabase", metadata.GetProperty("kind").GetString());
                    Assert.False(metadata.TryGetProperty("path", out var absolutePath));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_load_capture", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Null(model.LoadedDatabase);
                    Assert.Contains(input.Sha256, host.Runtime.GetApprovalDetails(operation));
                    Assert.Contains("read-only", host.Runtime.GetApprovalDetails(operation));
                    Assert.DoesNotContain("capture.xmatcap", JsonSerializer.Serialize(host.Runtime.GetOperation(session, operation)));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    var sourceId = receipt.GetProperty("sourceId").GetGuid();
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.False(receipt.GetProperty("sourceGrantsAdded").GetBoolean());
                    Assert.False(receipt.GetProperty("activeDataReplaced").GetBoolean());
                    Assert.False(receipt.GetProperty("selectedTabChanged").GetBoolean());
                    Assert.Equal(1, receipt.GetProperty("addedContexts").GetInt32());
                    Assert.Same(human, model.SelectedDeviceContext);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    var context = model.CaptureDeviceContexts.Single(item => item.IsReadOnly);
                    Assert.Single(((WebServiceDeviceCaptureController)context.CaptureController).ProxyConnections);
                    var query = new Dictionary<string, object>
                    {
                        ["sourceId"] = sourceId,
                        ["sql"] = "SELECT count(*) FROM CaptureDevices",
                        ["idempotencyKey"] = "new-source-query"
                    };
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_run_query", query).AsTask()))
                        .GetProperty("error").GetString());
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(sourceId));
                    Finish(host.Runtime.SetPermissionsAsync(session, [load, file, raw]));
                    var result = Query(client, host, session, sourceId, "SELECT count(*) FROM CaptureDevices", "explicit-raw");
                    var page = Payload(Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask()));
                    Assert.Equal(1, page.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetInt64());
                    File.WriteAllText(path, "changed since the completed load");
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_load_capture", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Single(((WebServiceDeviceCaptureController)context.CaptureController).GetCaptureRecords());
                    Assert.Same(human, model.SelectedDeviceContext);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("content")]
        [InlineData("replacement")]
        [InlineData("source")]
        [InlineData("contexts")]
        [InlineData("revoke")]
        public void NativeInputApprovalCannotFollowChangedInputOrLoadedSourceOrViewRegistry(string change)
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var initial = Path.Combine(directory.Path, "initial.xmatcap");
                var requestedPath = Path.Combine(directory.Path, "requested.xmatcap");
                var humanPath = Path.Combine(directory.Path, "human.xmatcap");
                CaptureFileLoadTests.WriteCapture(initial, [("original", CaptureType.WebProxy)]);
                CaptureFileLoadTests.WriteCapture(requestedPath, [("requested", CaptureType.WebProxy)]);
                CaptureFileLoadTests.WriteCapture(humanPath, [("human", CaptureType.WebProxy)]);
                var model = new CaptureAppModel();
                var source = Complete(model.CaptureFileLoads.LoadDesktopAsync(initial)).SourceId;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(requestedPath));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var load = new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application);
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [load, file]));
                    var wrongSource = Payload(Complete(client.CallToolAsync("xmat_load_capture",
                        CaptureInputArguments(input.Id, null, "wrong-source")).AsTask()));
                    Assert.Equal("SourceChanged", wrongSource.GetProperty("error").GetString());
                    var operation = Payload(Complete(client.CallToolAsync("xmat_load_capture",
                        CaptureInputArguments(input.Id, source, "stale")).AsTask())).GetProperty("id").GetGuid();
                    if (change == "content")
                        File.WriteAllText(requestedPath, "modified");
                    else if (change == "replacement")
                    {
                        var temporary = Path.Combine(directory.Path, "replacement.xmatcap");
                        File.WriteAllBytes(temporary, File.ReadAllBytes(requestedPath));
                        File.Move(temporary, requestedPath, overwrite: true);
                    }
                    else if (change == "source")
                        Complete(model.CaptureFileLoads.LoadDesktopAsync(humanPath));
                    else if (change == "contexts")
                        Finish(model.RemoveDeviceContextAsync(model.CaptureDeviceContexts.Single()));
                    else
                        Finish(host.Runtime.SetPermissionsAsync(session, [load]));
                    if (change == "revoke")
                        Assert.False(host.Runtime.Approve(operation));
                    else
                    {
                        Assert.True(host.Runtime.Approve(operation));
                        Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                        Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    }
                    Assert.DoesNotContain(model.CaptureDeviceContexts, context => context.DeviceName == "requested");
                    Assert.False(model.CaptureFileLoads.IsLoading);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DesktopCaptureInputGrantsDoNotImplicitlyGrantRawDataOrDeviceControl()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                CaptureFileLoadTests.WriteCapture(path, []);
                var model = new CaptureAppModel();
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
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
                    ((CheckBox)manager.FindName("CaptureLoadGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var permissions = host.Runtime.GetSessions().Single().Permissions;
                    Assert.Equal(2, permissions.Count);
                    Assert.Contains(new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application), permissions);
                    Assert.Contains(new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id)), permissions);
                    Assert.Null(model.LoadedDatabase);
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_load_capture");
                    Assert.DoesNotContain(tools, tool => tool.Name is "xmat_open_database" or "xmat_read_file" or "xmat_register_input_file");
                }
                finally
                {
                    manager?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("revoke")]
        [InlineData("deadline")]
        [InlineData("source")]
        public void InterruptedNativeInputCopyHasNoPublicationAndReleasesAdmissionForRetry(string change)
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var model = new CaptureAppModel();
                var input = Complete(model.CaptureFileInputs.RegisterAsync(path, 1024 * 1024));
                var paused = new PausedCaptureInputs(model.CaptureFileInputs);
                var loads = new CaptureFileLoads(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, paused);
                var runtime = new AgentRuntime();
                runtime.Enable();
                var session = runtime.Connect("paused native loader");
                var load = new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application);
                var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                var limits = new AgentLimits(queryTimeoutSeconds: change == "deadline" ? 1 : 60);
                Finish(runtime.SetPermissionsAsync(session, [load, file], limits));
                var operations = new AgentCaptureFileApplication(runtime, model.CaptureQueries, model.CaptureFiles,
                    model.CaptureFileExports, () => true, paused, loads);
                SqLtReadonlyDatabase newer = null;
                try
                {
                    var operation = Complete(operations.LoadAsync(session, input.Id, null, "paused", default));
                    Assert.True(runtime.Approve(operation));
                    PumpUntil(() => paused.Copied.Task.IsCompleted);
                    Assert.Null(model.LoadedDatabase);
                    if (change == "cancel")
                        Assert.True(Complete(runtime.CancelAsync(session, operation)));
                    else if (change == "revoke")
                        Finish(runtime.SetPermissionsAsync(session, [load], limits));
                    else if (change == "source")
                    {
                        model.LoadedDatabase = newer = new SqLtReadonlyDatabase(path);
                        paused.Continue.TrySetResult();
                    }
                    Finish(runtime.WaitForCompletionAsync(session, operation));
                    var status = runtime.GetOperation(session, operation);
                    if (change == "deadline")
                        Assert.Equal(AgentAccessError.QueryTimeout, status.Error);
                    else
                        Assert.Equal(change == "source" ? AgentOperationState.SourceChanged :
                            AgentOperationState.Cancelled, status.State);
                    Assert.Empty(model.CaptureDeviceContexts);
                    Assert.Same(newer, model.LoadedDatabase);
                    Assert.False(loads.IsLoading);
                    Assert.False(model.IsCaptureDataOperationRunning);
                    paused.Continue.TrySetResult();
                    Finish(runtime.SetPermissionsAsync(session, [load, file]));
                    var state = Complete(loads.GetStateAsync());
                    var retry = Complete(operations.LoadAsync(session, input.Id, state.LoadedSourceId, "retry", default));
                    Assert.True(runtime.Approve(retry));
                    Finish(runtime.WaitForCompletionAsync(session, retry));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, retry).State);
                    Assert.True(runtime.GetResult(session, retry).GetProperty("published").GetBoolean());
                    Assert.Single(model.CaptureDeviceContexts);
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(loads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                    newer?.Dispose();
                }
            });
        }

        private static Dictionary<string, object> CaptureInputArguments(Guid inputId, Guid? expectedSource, string key) =>
            new() { ["inputId"] = inputId, ["expectedLoadedSourceId"] = expectedSource, ["idempotencyKey"] = key };

        private sealed class PausedCaptureInputs(ICaptureFileInputs inner) : ICaptureFileInputs
        {
            internal TaskCompletionSource Copied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IReadOnlyList<InputFileDescriptor> GetInputs() => inner.GetInputs();
            public Task<InputFileDescriptor> RegisterAsync(string path, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.RegisterAsync(path, maximumBytes, cancellationToken);
            public Task ValidateAsync(Guid inputId, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.ValidateAsync(inputId, maximumBytes, cancellationToken);
            public Task<ScriptFileInput> ReadScriptAsync(Guid inputId, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.ReadScriptAsync(inputId, maximumBytes, cancellationToken);
            public Task<InputFileDescriptor> CopySazAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.CopySazAsync(inputId, destination, maximumBytes, cancellationToken);
            public async Task<InputFileDescriptor> CopyCaptureAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default)
            {
                var result = await inner.CopyCaptureAsync(inputId, destination, maximumBytes, cancellationToken);
                Copied.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                return result;
            }
        }
    }
}
