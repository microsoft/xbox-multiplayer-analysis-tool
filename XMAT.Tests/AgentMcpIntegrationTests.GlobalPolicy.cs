// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void ClientContinuationIsSessionBoundIdempotentAndFiltersBothStructuredAndFallbackResults()
        {
            WithGlobalPolicy((host, _, _, _) =>
            {
                var client = Complete(Connect(host));
                var other = Complete(Connect(host, "other"));
                try
                {
                    var current = Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(Payload(current).GetProperty("session").GetProperty("id").GetGuid());
                    var executions = 0;
                    var operation = host.Runtime.Submit(owner, new("test.export",
                        [new(AgentCapability.QueryResultExport, AgentScope.Source(Guid.NewGuid()))],
                        AgentOperationEffects.SensitiveDataExport, "original-arguments", "Authorization: Bearer warning-private-731924",
                        confirmationRequired: true),
                        "accepted", _ =>
                        {
                            Interlocked.Increment(ref executions);
                            return Task.FromResult(AgentJson.Encode(new { value = "Authorization: Bearer result-private-813957" }));
                        }, _ => Task.FromResult(true));
                    var pending = Complete(client.CallToolAsync("xmat_get_operation",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask());
                    Assert.False(pending.IsError ?? false);
                    var payload = Payload(pending);
                    var confirmation = payload.GetProperty("confirmation");
                    var token = confirmation.GetProperty("confirmationToken").GetString();
                    Assert.Equal("xmat_confirm_operation", payload.GetProperty("continueWith").GetString());
                    Assert.DoesNotContain("warning-private-731924", pending.StructuredContent.ToString());
                    Assert.False(confirmation.GetProperty("humanAttestation").GetBoolean());
                    var args = new Dictionary<string, object>
                    {
                        ["operationId"] = operation,
                        ["confirmationToken"] = token,
                        ["accept"] = true
                    };
                    Assert.True(Complete(other.CallToolAsync("xmat_confirm_operation", args).AsTask()).IsError);
                    Assert.Equal(0, executions);
                    var accepted = Complete(client.CallToolAsync("xmat_confirm_operation", args).AsTask());
                    Assert.False(accepted.IsError ?? false);
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(1, executions);
                    Assert.False(Complete(client.CallToolAsync("xmat_confirm_operation", args).AsTask()).IsError ?? false);
                    Assert.Equal(1, executions);
                    var result = Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask());
                    Assert.False(result.IsError ?? false);
                    Assert.DoesNotContain("result-private-813957", Payload(result).GetRawText());
                    Assert.Equal(Payload(result).GetRawText(), Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
                    Assert.Contains("[REDACTED]", Payload(result).GetProperty("value").GetString());
                }
                finally
                {
                    Finish(client.DisposeAsync().AsTask());
                    Finish(other.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("accept", true)]
        [InlineData("decline", false)]
        [InlineData("cancel", false)]
        public void NegotiatedFormConfirmationControlsStoppingAnotherClientsCapture(string action, bool accepted)
        {
            WithGlobalPolicy((host, _, context, operations) =>
            {
                context.Controller.SetRunning(true);
                var target = Complete(host.GetCapturesAsync()).Single();
                var originalOwner = host.Runtime.Connect("original capturing client");
                var owned = host.Runtime.Submit(originalOwner,
                    new("test.capture", [new(AgentCapability.CaptureControl, AgentScope.Capture(target.Id))],
                        AgentOperationEffects.StateChange, "owned"), "owned", execution =>
                    {
                        execution.OwnResource(new(AgentResourceKind.Capture, target.Id), target.CaptureEpoch.ToString(),
                            () => Task.FromResult(AgentCleanupOutcome.NoLongerOwned));
                        return Task.FromResult(AgentJson.Encode(new { owned = true }));
                    }, _ => Task.FromResult(true));
                Finish(host.Runtime.WaitForCompletionAsync(originalOwner, owned));
                var prompts = 0;
                var client = Complete(Connect(host, "form client", new()
                {
                    Capabilities = new() { Elicitation = new() { Form = new() } },
                    Handlers = new()
                    {
                        ElicitationHandler = (request, _) =>
                        {
                            Assert.Equal("form", request.Mode);
                            Assert.NotNull(request.RequestedSchema);
                            Assert.Contains("accept", request.RequestedSchema.Required);
                            Interlocked.Increment(ref prompts);
                            return ValueTask.FromResult(new ElicitResult
                            {
                                Action = action,
                                Content = new Dictionary<string, System.Text.Json.JsonElement>
                                { ["accept"] = AgentJson.Encode(accepted) }
                            });
                        }
                    }
                }));
                try
                {
                    var result = Complete(client.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = target.Id,
                            ["expectedRevision"] = target.Revision,
                            ["running"] = false,
                            ["idempotencyKey"] = "form stop"
                        }).AsTask());
                    Assert.False(result.IsError ?? false);
                    var operationId = Payload(result).GetProperty("id").GetGuid();
                    var owner = host.FindSession(Payload(result).GetProperty("sessionId").GetGuid());
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operationId));
                    Assert.Equal(1, prompts);
                    Assert.Equal(accepted ? 1 : 0, operations.StopCount);
                    Assert.Equal(!accepted, context.Controller.IsRunning);
                    Assert.Equal(accepted ? AgentOperationState.Succeeded : AgentOperationState.Denied,
                        host.Runtime.GetOperation(owner, operationId).State);
                }
                finally { Finish(client.DisposeAsync().AsTask()); }
            });
        }

        private void WithGlobalPolicy(
            Action<AgentAccessHost, AgentAccessPolicyService, Context, TestOperations> action,
            DeviceType deviceType = DeviceType.LocalPC, CaptureType captureType = CaptureType.WebProxy)
        {
            _application.Run(_ =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "XMAT.McpPolicy." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory, "policy.json")));
                var model = new CaptureAppModel();
                var context = new Context("global private target", deviceType, captureType);
                model.CaptureDeviceContexts.Add(context);
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations, policy: policy);
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0));
                    host.MarkReady();
                    Finish(host.SynchronizePolicyAsync());
                    action(host, policy, context, operations);
                }
                finally
                {
                    Finish(host.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                    Directory.Delete(directory, true);
                }
            });
        }

        [Fact]
        public void SavedGlobalPolicyEnablesEveryClientWithoutSessionGrantsAndSurvivesHostShutdown()
        {
            _application.Run(_ =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "XMAT.McpPolicy." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var store = new AgentAccessSettingsStore(Path.Combine(directory, "policy.json"));
                var policy = new AgentAccessPolicyService(store);
                var model = new CaptureAppModel();
                model.CaptureDeviceContexts.Add(new Context("global target"));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher,
                    new TestOperations(model, Dispatcher.CurrentDispatcher), policy: policy);
                McpClient first = null;
                McpClient second = null;
                try
                {
                    host.MarkReady();
                    Finish(host.SynchronizePolicyAsync());
                    Assert.False(host.IsEnabled);
                    Complete(policy.SaveAsync(new() { Enabled = true }, 0));
                    Finish(host.SynchronizePolicyAsync());
                    Assert.True(host.IsEnabled);
                    first = Complete(Connect(host, "global first"));
                    second = Complete(Connect(host, "global second"));
                    foreach (var client in new[] { first, second })
                    {
                        var page = Complete(client.CallToolAsync("xmat_list_captures").AsTask());
                        Assert.False(page.IsError ?? false);
                        Assert.Single(Payload(page).GetProperty("captures").EnumerateArray());
                    }
                    var target = Complete(host.GetCapturesAsync()).Single();
                    var denied = Complete(first.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = target.Id,
                            ["expectedRevision"] = target.Revision,
                            ["running"] = true,
                            ["idempotencyKey"] = "denied inspector"
                        }).AsTask());
                    Assert.True(denied.IsError);
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                    Complete(policy.SaveAsync(policy.Current.Settings with
                    { Actions = AgentActionAccess.FullControl }, policy.Current.Settings.Revision));
                    Finish(host.SynchronizePolicyAsync());
                    foreach (var session in host.Runtime.GetSessions())
                        host.Runtime.Demand(host.FindSession(session.Id),
                            new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(target.Id)));
                    Finish(host.DisposeAsync().AsTask());
                    Assert.True(store.Read().Settings.Enabled);
                }
                finally
                {
                    if (first != null)
                        Finish(first.DisposeAsync().AsTask());
                    if (second != null)
                        Finish(second.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                    Directory.Delete(directory, recursive: true);
                }
            });
        }
    }
}
