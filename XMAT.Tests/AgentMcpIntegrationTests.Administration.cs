// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void AdministrationToolsUseTheRealCompanionAndExistingExactApprovalRuntime()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel
                {
                    PreferencesModel = new AppPreferencesModel
                    {
                        WebProxyCapturePreferences = new WebServiceCapture.Models.PreferencesModel(),
                        NetworkAnalysisPreferences = new NetworkTraceAnalysisOptionsModel()
                    }
                };
                var target = new Context("explicit desktop target");
                var human = new Context("human-selected");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                ICaptureDeviceContext revealed = null;
                var closeRequests = 0;
                model.DesktopActions.Configure((context, _) =>
                {
                    revealed = context;
                    return Task.FromResult(true);
                }, () => closeRequests++, null);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    var names = Complete(client.ListToolsAsync().AsTask()).Select(tool => tool.Name).ToArray();
                    Assert.Contains("xmat_get_host_policy", names);
                    Assert.Contains("xmat_update_host_policy", names);
                    Assert.Contains("xmat_get_preferences", names);
                    Assert.Contains("xmat_update_preferences", names);
                    Assert.Contains("xmat_get_diagnostics", names);
                    Assert.Contains("xmat_export_diagnostics", names);
                    Assert.Contains("xmat_list_navigation_targets", names);
                    Assert.Contains("xmat_reveal_desktop_target", names);
                    Assert.Contains("xmat_get_desktop_lifecycle", names);
                    Assert.Contains("xmat_request_desktop_lifecycle", names);
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Assert.Equal("PermissionDenied",
                        Payload(Complete(client.CallToolAsync("xmat_get_host_policy").AsTask())).GetProperty("error").GetString());
                    Assert.Equal("PermissionDenied",
                        Payload(Complete(client.CallToolAsync("xmat_get_diagnostics").AsTask())).GetProperty("error").GetString());
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new(AgentCapability.HostPolicy, AgentScope.Application),
                            new(AgentCapability.Navigation, AgentScope.Application),
                            new(AgentCapability.Lifecycle, AgentScope.Application),
                            new(AgentCapability.Preferences, AgentScope.Application)]));
                    var policy = Payload(Complete(client.CallToolAsync("xmat_get_host_policy").AsTask()));
                    var arguments = new Dictionary<string, object>
                    {
                        ["request"] = new
                        {
                            expectedRevision = policy.GetProperty("revision").GetInt64(),
                            list = "Block",
                            action = "Add",
                            pattern = "[0:0::1]"
                        },
                        ["idempotencyKey"] = "policy-once"
                    };
                    var operation = Payload(Complete(client.CallToolAsync("xmat_update_host_policy", arguments).AsTask()));
                    var operationId = operation.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", operation.GetProperty("state").GetString());
                    Assert.Empty(model.HostListPolicy.BlockList.BlockedUrls);
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operationId));
                    Assert.Equal("::1", Assert.Single(model.HostListPolicy.BlockList.BlockedUrls));
                    Assert.Equal(operationId, Payload(Complete(client.CallToolAsync("xmat_update_host_policy", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    var preferences = Payload(Complete(client.CallToolAsync("xmat_get_preferences").AsTask()));
                    Assert.Equal(1, preferences.GetProperty("options").GetProperty("minimumPort").GetInt32());
                    Assert.True(preferences.GetProperty("changesPersistOnDisconnect").GetBoolean());
                    var descriptor = Complete(model.CaptureCatalog.GetCapturesAsync()).Single(capture => capture.DeviceName == target.DeviceName);
                    var navigation = Payload(Complete(client.CallToolAsync("xmat_reveal_desktop_target",
                        new Dictionary<string, object>
                        {
                            ["request"] = new DesktopNavigationRequest(descriptor.Id, descriptor.Revision, descriptor.CaptureEpoch),
                            ["idempotencyKey"] = "navigation-once"
                        }).AsTask()));
                    operationId = navigation.GetProperty("id").GetGuid();
                    Assert.Null(revealed);
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operationId));
                    Assert.Same(target, revealed);
                    Assert.Same(human, model.SelectedDeviceContext);
                    var lifecycle = Payload(Complete(client.CallToolAsync("xmat_get_desktop_lifecycle").AsTask()));
                    operation = Payload(Complete(client.CallToolAsync("xmat_request_desktop_lifecycle",
                        new Dictionary<string, object>
                        {
                            ["expectedRevision"] = lifecycle.GetProperty("revision").GetInt64(),
                            ["action"] = "Close",
                            ["idempotencyKey"] = "normal-close-request"
                        }).AsTask()));
                    operationId = operation.GetProperty("id").GetGuid();
                    Assert.Equal(0, closeRequests);
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operationId));
                    CaptureDeviceTestData.PumpUntil(() => closeRequests == 1);
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    Assert.True(receipt.GetProperty("scheduledAfterOperation").GetBoolean());
                    Finish(client.DisposeAsync().AsTask());
                    client = null;
                    Finish(host.Runtime.EndSessionAsync(session));
                    Assert.Equal("::1", Assert.Single(model.HostListPolicy.BlockList.BlockedUrls));
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
        public void AccessWindowGrantsTheFiveApplicationCapabilitiesWithoutCaptureOrRawAccess()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                var model = new CaptureAppModel();
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                AgentAccessWindow manager = null;
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)manager.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    foreach (var name in new[] { "HostPolicyGrant", "PreferencesGrant", "DiagnosticsGrant", "NavigationGrant", "LifecycleGrant" })
                        ((CheckBox)manager.FindName(name)).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    CaptureDeviceTestData.PumpUntil(() => host.Runtime.GetSession(session).Permissions.Count == 5);
                    Assert.All(host.Runtime.GetSession(session).Permissions, permission => Assert.Equal(AgentScope.Application, permission.Scope));
                    Assert.DoesNotContain(host.Runtime.GetSession(session).Permissions,
                        permission => permission.Capability is AgentCapability.RawDataRead or AgentCapability.CaptureControl or AgentCapability.FileWrite);
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
