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
        public void SazImportRequiresExactInputAndActiveSourceGrantsAndPublishesOnlyAfterApproval()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var human = setup.Created.Single();
                setup.Model.SelectedDeviceContext = human;
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var import = new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id));
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    var request = SazArguments(input.Id, source.Id, "import-once");
                    foreach (var grants in new AgentPermission[][] {
                        [import], [file],
                        [new(AgentCapability.CaptureImport, AgentScope.Source(Guid.NewGuid())), file] })
                    {
                        Finish(host.Runtime.SetPermissionsAsync(session, grants));
                        Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_import_saz", request).AsTask()))
                            .GetProperty("error").GetString());
                    }
                    Finish(host.Runtime.SetPermissionsAsync(session, [import, file]));
                    var inputs = Payload(Complete(client.CallToolAsync("xmat_list_input_files").AsTask()));
                    Assert.Equal("FiddlerSaz", Assert.Single(inputs.GetProperty("inputs").EnumerateArray())
                        .GetProperty("kind").GetString());
                    var pending = Payload(Complete(client.CallToolAsync("xmat_import_saz", request).AsTask()));
                    var operation = pending.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", pending.GetProperty("state").GetString());
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.Empty(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    var approval = host.Runtime.GetApprovalDetails(operation);
                    Assert.Contains(input.Sha256, approval);
                    Assert.Contains("ALL matching", approval);
                    Assert.Contains("existing grants", approval);
                    Assert.DoesNotContain(CaptureSazImportTests.Secret, JsonSerializer.Serialize(host.Runtime.GetOperation(session, operation)));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var receipt = host.Runtime.GetResult(session, operation);
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.False(receipt.GetProperty("sourceGrantsAdded").GetBoolean());
                    Assert.True(receipt.GetProperty("existingSourceGrantsCoverImportedData").GetBoolean());
                    Assert.False(receipt.GetProperty("selectedTabChanged").GetBoolean());
                    Assert.Equal(1, receipt.GetProperty("recordCount").GetInt32());
                    Assert.Same(human, setup.Model.SelectedDeviceContext);
                    Assert.DoesNotContain(CaptureSazImportTests.Secret, receipt.GetRawText());
                    var query = new Dictionary<string, object>
                    {
                        ["sourceId"] = source.Id,
                        ["sql"] = "SELECT RequestBody FROM WebProxyConnections",
                        ["idempotencyKey"] = "no-raw"
                    };
                    Assert.Equal("PermissionDenied", Payload(Complete(client.CallToolAsync("xmat_run_query", query).AsTask()))
                        .GetProperty("error").GetString());
                    var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [import, file, raw]));
                    var result = Query(client, host, session, source.Id, "SELECT count(*) FROM WebProxyConnections", "explicit-raw");
                    var page = Payload(Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask()));
                    Assert.Equal(1, page.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetInt64());
                    File.WriteAllText(path, "changed after completed import");
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_import_saz", request).AsTask()))
                        .GetProperty("id").GetGuid());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Single(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    Assert.Equal(2, setup.Model.CaptureDeviceContexts.Count);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("input")]
        [InlineData("source")]
        [InlineData("target")]
        [InlineData("revoke")]
        public void SazApprovalCannotFollowChangedInputSourceTargetOrRevokedPermission(string change)
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    var input = Complete(host.RegisterInputFileAsync(path));
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var import = new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id));
                    var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    Finish(host.Runtime.SetPermissionsAsync(session, [import, file]));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_import_saz",
                        SazArguments(input.Id, source.Id, "stale")).AsTask())).GetProperty("id").GetGuid();
                    if (change == "input")
                        CaptureSazImportTests.WriteSaz(path, "changed.example");
                    else if (change == "source")
                        setup.Model.ActiveDatabase = replacement;
                    else if (change == "target")
                        setup.Model.AddDeviceContext(DeviceType.Unknown, "Fiddler - capture.saz", CaptureType.WebProxy, true, null);
                    else
                        Finish(host.Runtime.SetPermissionsAsync(session, [file]));
                    if (change == "revoke")
                        Assert.False(host.Runtime.Approve(operation));
                    else
                    {
                        Assert.True(host.Runtime.Approve(operation));
                        Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                        Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    }
                    Assert.Empty(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    Assert.Empty(replacement.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                }
                finally
                {
                    setup.Model.ActiveDatabase = setup.Database;
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("revoke")]
        [InlineData("deadline")]
        [InlineData("source")]
        public void InterruptedSazStagingPreservesDataAndAllowsAnApprovedRetry(string change)
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                var input = Complete(setup.Model.CaptureFileInputs.RegisterAsync(path, 65536));
                var paused = new PausedSazInputs(setup.Model.CaptureFileInputs);
                var imports = new CaptureSazImports(setup.Model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)setup.Model.CaptureQueries, paused);
                var runtime = new AgentRuntime();
                runtime.Enable();
                var session = runtime.Connect("paused SAZ import");
                var source = Complete(setup.Model.CaptureQueries.GetSourcesAsync()).Single();
                var import = new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id));
                var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                var limits = new AgentLimits(queryTimeoutSeconds: change == "deadline" ? 1 : 60);
                Finish(runtime.SetPermissionsAsync(session, [import, file], limits));
                var operations = new AgentCaptureFileApplication(runtime, setup.Model.CaptureQueries, setup.Model.CaptureFiles,
                    setup.Model.CaptureFileExports, () => true, paused, imports: imports);
                try
                {
                    var operation = Complete(operations.ImportSazAsync(session, input.Id, source.Id, "paused", default));
                    Assert.True(runtime.Approve(operation));
                    PumpUntil(() => paused.Copied.Task.IsCompleted);
                    Assert.True(setup.Model.IsCaptureFileLoadRunning);
                    Assert.False(setup.Model.IsCaptureDataOperationRunning);
                    Assert.False(Complete(setup.Model.CanCloseAsync()));
                    if (change == "cancel")
                        Assert.True(Complete(runtime.CancelAsync(session, operation)));
                    else if (change == "revoke")
                        Finish(runtime.SetPermissionsAsync(session, [file], limits));
                    else if (change == "source")
                    {
                        setup.Model.ActiveDatabase = replacement;
                        paused.Continue.TrySetResult();
                    }
                    Finish(runtime.WaitForCompletionAsync(session, operation));
                    if (change == "deadline")
                        Assert.Equal(AgentAccessError.QueryTimeout, runtime.GetOperation(session, operation).Error);
                    else
                        Assert.Equal(change == "source" ? AgentOperationState.SourceChanged : AgentOperationState.Cancelled,
                            runtime.GetOperation(session, operation).State);
                    Assert.Empty(setup.Model.CaptureDeviceContexts);
                    Assert.Empty(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    Assert.False(setup.Model.IsCaptureFileLoadRunning);
                    setup.Model.ActiveDatabase = setup.Database;
                    paused.Continue.TrySetResult();
                    Finish(runtime.SetPermissionsAsync(session, [import, file]));
                    var retry = Complete(operations.ImportSazAsync(session, input.Id, source.Id, "retry", default));
                    Assert.True(runtime.Approve(retry));
                    Finish(runtime.WaitForCompletionAsync(session, retry));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, retry).State);
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    setup.Model.ActiveDatabase = setup.Database;
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(imports.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DesktopSazGrantsAreIndependentOfRawAccessAndUnavailableForLoadedSources()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher);
                AgentAccessWindow manager = null;
                McpClient client = null;
                SqLtReadonlyDatabase loaded = null;
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
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var chooser = (ComboBox)manager.FindName("InputFileTarget");
                    chooser.SelectedItem = chooser.Items.Cast<InputFileDescriptor>().Single(item => item.Id == input.Id);
                    ((CheckBox)manager.FindName("FileReadGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("CaptureImportGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var grants = host.Runtime.GetSessions().Single().Permissions;
                    Assert.Equal(2, grants.Count);
                    Assert.Contains(new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id)), grants);
                    Assert.Contains(new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id)), grants);
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_import_saz");
                    Assert.Contains(tools, tool => tool.Name == "xmat_import_etl");
                    Assert.DoesNotContain(tools, tool => tool.Name == "xmat_read_file");
                    var nativePath = Path.Combine(directory.Path, "loaded.xmatcap");
                    setup.Database.SaveToFile(nativePath);
                    setup.Model.LoadedDatabase = loaded = new SqLtReadonlyDatabase(nativePath);
                    var sources = (ComboBox)manager.FindName("QuerySourceTarget");
                    PumpUntil(() => sources.Items.Cast<QuerySourceDescriptor>().Any(item => item.Kind == QuerySourceKind.Loaded));
                    sources.SelectedItem = sources.Items.Cast<QuerySourceDescriptor>().Single(item => item.Kind == QuerySourceKind.Loaded);
                    Assert.False(((CheckBox)manager.FindName("CaptureImportGrant")).IsEnabled);
                    Assert.False(((CheckBox)manager.FindName("CaptureImportGrant")).IsChecked);
                }
                finally
                {
                    manager?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    loaded?.Dispose();
                }
            });
        }

        private static Dictionary<string, object> SazArguments(Guid input, Guid source, string key) =>
            new() { ["inputId"] = input, ["sourceId"] = source, ["idempotencyKey"] = key };

        private sealed class PausedSazInputs(ICaptureFileInputs inner) : ICaptureFileInputs
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
            public Task<InputFileDescriptor> CopyCaptureAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.CopyCaptureAsync(inputId, destination, maximumBytes, cancellationToken);
            public async Task<InputFileDescriptor> CopySazAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default)
            {
                var result = await inner.CopySazAsync(inputId, destination, maximumBytes, cancellationToken);
                Copied.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                return result;
            }
        }
    }
}
