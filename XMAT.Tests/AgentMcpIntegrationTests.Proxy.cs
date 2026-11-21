// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
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
        public void ProxyEnable_RequiresSeparateConfigurationControlAndExactRestartApproval()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher,
                    proxyOperations: CaptureProxyOperationsTests.Operations(setup));
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(added.CaptureId));
                    var config = new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId));
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary, control]));
                    var options = Payload(Complete(client.CallToolAsync("xmat_get_proxy_options",
                        new Dictionary<string, object> { ["captureId"] = added.CaptureId }).AsTask()));
                    Assert.Equal("192.0.2.10", options.GetProperty("sourceAddresses")[0].GetString());
                    var arguments = ProxyArguments(added.CaptureId, added.Revision, "enable", true);
                    var denied = Complete(client.CallToolAsync("xmat_set_device_proxy", arguments).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Assert.Equal(0, target.Configuration.WriteCount);
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary, control, config]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_device_proxy", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.True(host.Runtime.GetOperation(session, operation).Effects.HasFlag(AgentOperationEffects.ConsoleRestart));
                    Assert.Contains("192.0.2.10", host.Runtime.GetApprovalDetails(operation));
                    Assert.Equal(0, target.Listener.StartCount);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    var result = host.Runtime.GetResult(session, operation);
                    Assert.True(result.GetProperty("configurationConfirmed").GetBoolean());
                    Assert.True(result.GetProperty("isRunning").GetBoolean());
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_set_device_proxy", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Assert.Equal(1, target.Configuration.WriteCount);
                    Finish(host.RevokeAsync(session.Id));
                    Assert.True(target.Proxy.IsRunning);
                    Assert.Equal(AgentResourceState.RecoveryRequired, Assert.Single(host.Runtime.GetOwnedResources()).State);
                    Assert.Equal(1, target.Configuration.WriteCount);
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
        public void ProxyApproval_RejectsChangedObservedEndpointAndRequiresActiveScriptTrust()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher,
                    proxyOperations: CaptureProxyOperationsTests.Operations(setup));
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(added.CaptureId)),
                         new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_device_proxy",
                        ProxyArguments(added.CaptureId, added.Revision, "stale", true)).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("other.example", 8080));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(session, operation).State);
                    Assert.Equal(0, target.Listener.StartCount);
                    Assert.Equal(0, target.Configuration.WriteCount);
                    target.Configuration.Current = new(ProxyConfigurationState.Disabled);
                    var script = target.Proxy.Scripts.First();
                    script.Script = "Params.Continue = true;";
                    Finish(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                    Assert.True(script.IsEnabled);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var denied = Complete(client.CallToolAsync("xmat_set_device_proxy",
                        ProxyArguments(capture.Id, capture.Revision, "script-grant", true)).AsTask());
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Assert.Equal(0, target.Configuration.WriteCount);
                    script.DisableScript();
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
        public void ProxyPortApplication_UsesExistingDraftAndReturnsExactCommittedRecoveryReceipt()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                target.Configuration.Enable = _ => throw new IOException("PRIVATE_COMMITTED_SDK_ERROR");
                var queued = CaptureDeviceTestData.Complete(setup.Devices.SetPendingProxyPortAsync(
                    added.CaptureId, added.Revision, 9001));
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher,
                    proxyOperations: CaptureProxyOperationsTests.Operations(setup));
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(added.CaptureId)),
                         new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(added.CaptureId))]));
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = queued.CaptureId,
                        ["expectedRevision"] = queued.Revision,
                        ["sourceAddress"] = "192.0.2.10",
                        ["idempotencyKey"] = "apply-once"
                    };
                    var requested = Payload(Complete(client.CallToolAsync("xmat_apply_proxy_port", arguments).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.RecoveryRequired, host.Runtime.GetOperation(session, operation).State);
                    var result = host.Runtime.GetResult(session, operation);
                    Assert.True(result.GetProperty("portCommitted").GetBoolean());
                    Assert.Equal(9001, result.GetProperty("port").GetInt32());
                    Assert.Equal(0, result.GetProperty("pendingPort").GetInt32());
                    Assert.True(result.GetProperty("isRunning").GetBoolean());
                    Assert.False(result.GetProperty("configurationConfirmed").GetBoolean());
                    Assert.DoesNotContain("PRIVATE_COMMITTED_SDK_ERROR", result.GetRawText());
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_apply_proxy_port", arguments).AsTask()))
                        .GetProperty("id").GetGuid());
                    Assert.Equal(1, target.Configuration.WriteCount);
                    Finish(host.RevokeAsync(session.Id));
                    Assert.True(target.Proxy.IsRunning);
                    Assert.Equal(1, target.Configuration.WriteCount);
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
        public void ProxyDisable_DoesNotClaimConfirmationOrStopListenerUntilReadbackSucceeds()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("foreign.example", 8080));
                Finish(target.Proxy.StartProxyAsync(false));
                var capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                target.Configuration.Disable = _ =>
                {
                    target.Configuration.Current = new(ProxyConfigurationState.Unknown);
                    return Task.CompletedTask;
                };
                var host = new AgentAccessHost(setup.Model, Dispatcher.CurrentDispatcher,
                    proxyOperations: CaptureProxyOperationsTests.Operations(setup, timeout: TimeSpan.FromMilliseconds(100)));
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id)),
                         new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(capture.Id))]));
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_device_proxy",
                        ProxyArguments(capture.Id, capture.Revision, "disable", false)).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Contains("foreign.example", host.Runtime.GetApprovalDetails(operation));
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.RecoveryRequired, host.Runtime.GetOperation(session, operation).State);
                    Assert.False(host.Runtime.GetResult(session, operation).GetProperty("configurationConfirmed").GetBoolean());
                    Assert.True(target.Proxy.IsRunning);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    target.Configuration.Disable = null;
                    capture = Complete(host.GetCapturesAsync()).Single();
                    requested = Payload(Complete(client.CallToolAsync("xmat_set_device_proxy",
                        ProxyArguments(capture.Id, capture.Revision, "retry-disable", false)).AsTask()));
                    operation = requested.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operation));
                    Assert.True(host.Runtime.GetResult(session, operation).GetProperty("configurationConfirmed").GetBoolean());
                    Assert.False(target.Proxy.IsRunning);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        private static Dictionary<string, object> ProxyArguments(Guid captureId, long revision, string key, bool enabled)
        {
            var arguments = new Dictionary<string, object>
            {
                ["captureId"] = captureId,
                ["expectedRevision"] = revision,
                ["enabled"] = enabled,
                ["idempotencyKey"] = key
            };
            if (enabled)
                arguments["sourceAddress"] = "192.0.2.10";
            return arguments;
        }
    }
}
