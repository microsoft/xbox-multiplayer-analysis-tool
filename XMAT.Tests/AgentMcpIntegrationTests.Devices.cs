// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void DeviceCreation_RequiresSeparateGrantAndApprovalWithoutStartingOrGrantingAccess()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var human = setup.Created.Single();
                setup.Model.SelectedDeviceContext = human;
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_add_device");
                    Assert.Contains(tools, tool => tool.Name == "xmat_get_device");
                    Assert.Contains(tools, tool => tool.Name == "xmat_set_pending_proxy_port");
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var arguments = new Dictionary<string, object>
                    {
                        ["kind"] = "WindowsHandheldOrRemoteDevice",
                        ["captureType"] = "WebProxy",
                        ["idempotencyKey"] = "device-add-once"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_add_device", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Assert.Single(setup.Created);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary]));
                    denied = Complete(client.CallToolAsync("xmat_add_device", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [summary, new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application)]));
                    var options = Payload(Complete(client.CallToolAsync("xmat_describe_devices").AsTask()));
                    Assert.False(options.GetProperty("creationGrantsControl").GetBoolean());
                    var requested = Payload(Complete(client.CallToolAsync("xmat_add_device", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Single(setup.Created);
                    Assert.Contains("does NOT start", host.Runtime.GetApprovalDetails(operation));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var result = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    var captureId = result.GetProperty("captureId").GetGuid();
                    Assert.True(result.GetProperty("changed").GetBoolean());
                    Assert.True(result.GetProperty("persisted").GetBoolean());
                    Assert.Equal(2, setup.Created.Count);
                    Assert.Same(human, setup.Model.SelectedDeviceContext);
                    Assert.Equal(0, setup.ConfigurationWrites);
                    Assert.All(setup.Created, context => Assert.Equal(0, context.Listener.StartCount));
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    var replay = Payload(Complete(client.CallToolAsync("xmat_add_device", arguments).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    Assert.Equal(2, setup.SaveCount);
                    denied = Complete(client.CallToolAsync("xmat_set_capture_running", new Dictionary<string, object>
                    {
                        ["captureId"] = captureId,
                        ["expectedRevision"] = result.GetProperty("revision").GetInt64(),
                        ["running"] = true,
                        ["idempotencyKey"] = "no-implicit-control"
                    }).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(client.DisposeAsync().AsTask());
                    client = null;
                    Finish(host.Runtime.EndSessionAsync(session));
                    Assert.Equal(2, setup.Model.CaptureDeviceContexts.Count);
                    Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(setup.Created[1].Proxy.Port, "other"));
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
        public void DeviceDrafts_RequireExactTargetGrantAndSurviveDisconnectWithoutApplying()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [summary, new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application)]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = added.CaptureId,
                        ["expectedRevision"] = added.Revision,
                        ["port"] = 9001,
                        ["idempotencyKey"] = "port-draft"
                    };
                    var denied = Complete(client.CallToolAsync("xmat_set_pending_proxy_port", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [summary, new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_pending_proxy_port", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.False(setup.Created.Single().Proxy.HasPendingPort);
                    Assert.Contains("does NOT start", host.Runtime.GetApprovalDetails(operation),
                        StringComparison.OrdinalIgnoreCase);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(session, operation).State);
                    var replay = Payload(Complete(client.CallToolAsync("xmat_set_pending_proxy_port", arguments).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    var state = Payload(Complete(client.CallToolAsync("xmat_get_device",
                        new Dictionary<string, object> { ["captureId"] = added.CaptureId }).AsTask()));
                    Assert.Equal(9001, state.GetProperty("pendingProxyPort").GetInt32());
                    Assert.True(state.GetProperty("isProxyPortReserved").GetBoolean());
                    Assert.True(state.GetProperty("isPendingProxyPortReserved").GetBoolean());
                    Assert.Equal(0, setup.Created.Single().Listener.StartCount);
                    Assert.Equal(0, setup.ConfigurationWrites);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    arguments["port"] = 9002;
                    var conflict = Complete(client.CallToolAsync("xmat_set_pending_proxy_port", arguments).AsTask());
                    Assert.Equal("IdempotencyConflict", Payload(conflict).GetProperty("error").GetString());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.Equal(9001, setup.Created.Single().Proxy.PendingPort);
                    Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(9001, "other"));
                    Assert.Equal(0, setup.ConfigurationWrites);
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
        public void DeviceRemoval_RechecksExternalStateAfterApprovalAndReplaysCommittedRemoval()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.WebProxy));
                var target = setup.Created.Single();
                CaptureInvestigationTestData.AddWeb(setup.Database, 1, device: target.DeviceName);
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application),
                         new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId))]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = added.CaptureId,
                        ["expectedRevision"] = added.Revision,
                        ["idempotencyKey"] = "remove-unsafe"
                    };
                    target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("127.0.0.1", target.Proxy.Port));
                    var denied = Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask());
                    Assert.Equal("RecoveryRequired", Payload(denied).GetProperty("error").GetString());
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                    target.Configuration.Current = new(ProxyConfigurationState.Disabled);
                    var requested = Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    target.Configuration.Current = new(ProxyConfigurationState.Unknown, Detail: "PRIVATE_CONFIG");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.RecoveryRequired, host.Runtime.GetOperation(session, operation).State);
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.Single(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    target.Configuration.Current = new(ProxyConfigurationState.Disabled);
                    arguments["idempotencyKey"] = "remove-safe";
                    requested = Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(session, operation).State);
                    var result = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    Assert.True(result.GetProperty("removed").GetBoolean());
                    Assert.True(result.GetProperty("persisted").GetBoolean());
                    Assert.Empty(setup.Model.CaptureDeviceContexts);
                    Assert.Empty(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    var replay = Payload(Complete(client.CallToolAsync("xmat_remove_device", arguments).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    Assert.Equal(0, setup.ConfigurationWrites);
                    var detached = Complete(client.CallToolAsync("xmat_get_device",
                        new Dictionary<string, object> { ["captureId"] = added.CaptureId }).AsTask());
                    Assert.Equal("OperationNotFound", Payload(detached).GetProperty("error").GetString());
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
        public void DeviceDefaultSelection_AndPendingPortRevisionsInvalidateDesktopApprovals()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application),
                         new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_add_device", new Dictionary<string, object>
                    {
                        ["kind"] = "DefaultXboxConsole",
                        ["captureType"] = "NetworkTrace",
                        ["idempotencyKey"] = "default-console"
                    }).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    setup.DefaultConsole = () => Task.FromResult("192.0.2.2");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Single(setup.Created);
                    requested = Payload(Complete(client.CallToolAsync("xmat_set_pending_proxy_port", new Dictionary<string, object>
                    {
                        ["captureId"] = added.CaptureId,
                        ["expectedRevision"] = added.Revision,
                        ["port"] = 9001,
                        ["idempotencyKey"] = "stale-port"
                    }).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    setup.Created.Single().Proxy.QueuePortChange(9002, () => { });
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Equal(9002, setup.Created.Single().Proxy.PendingPort);
                    Assert.Equal(0, setup.ConfigurationWrites);
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
        public void DevicePersistenceFailure_ReturnsProtectedCommittedReceiptWithoutPrivateErrorText()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData(() =>
                    Task.FromException(new IOException("PRIVATE_SETTINGS_PATH")));
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application)]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_add_device", new Dictionary<string, object>
                    {
                        ["kind"] = "LocalPC",
                        ["captureType"] = "WebProxy",
                        ["idempotencyKey"] = "save-failure"
                    }).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var status = host.Runtime.GetOperation(session, operation);
                    Assert.Equal(AgentAccessError.PersistenceFailed, status.Error);
                    Assert.True(status.HasResult);
                    var result = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    Assert.True(result.GetProperty("changed").GetBoolean());
                    Assert.False(result.GetProperty("persisted").GetBoolean());
                    Assert.DoesNotContain("PRIVATE_SETTINGS_PATH", result.GetRawText());
                    Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.Equal(0, setup.ConfigurationWrites);
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
        public void DesktopDeviceGrants_AreApplicationCreationAndExactCaptureConfiguration()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var setup = new CaptureDeviceTestData();
                var chosen = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                setup.Add(new(CaptureDeviceKind.WindowsHandheldOrRemoteDevice, CaptureType.WebProxy));
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher, devices: setup.Devices);
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
                    var targets = (ComboBox)manager.FindName("CaptureTarget");
                    targets.SelectedItem = targets.Items.Cast<CaptureDescriptor>().Single(item => item.Id == chosen.CaptureId);
                    ((CheckBox)manager.FindName("SummaryGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("DeviceCreationGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("DeviceConfigurationGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    Assert.Contains(new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application), session.Permissions);
                    Assert.Contains(new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(chosen.CaptureId)),
                        session.Permissions);
                    Assert.Equal(3, session.Permissions.Count);
                    Assert.DoesNotContain(session.Permissions, permission => permission.Capability is
                        AgentCapability.CaptureControl or AgentCapability.ScriptExecute or AgentCapability.RawDataRead);
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
    }
}
