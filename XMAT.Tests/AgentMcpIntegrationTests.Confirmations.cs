// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Theory]
        [InlineData(DeviceType.LocalPC, CaptureType.WebProxy)]
        [InlineData(DeviceType.XboxConsole, CaptureType.WebProxy)]
        [InlineData(DeviceType.GenericProxyDevice, CaptureType.WebProxy)]
        [InlineData(DeviceType.LocalPC, CaptureType.NetworkTrace)]
        public void RoutineCaptureStartAndStopNeitherElicitNorRequireAContinuation(DeviceType device, CaptureType type)
        {
            WithGlobalPolicy((host, _, _, operations) =>
            {
                var prompts = 0;
                var client = Complete(Connect(host, "routine capture", new()
                {
                    Capabilities = new() { Elicitation = new() { Form = new() } },
                    Handlers = new()
                    {
                        ElicitationHandler = (_, _) =>
                        {
                            prompts++;
                            return ValueTask.FromResult(new ElicitResult { Action = "decline" });
                        }
                    }
                }));
                try
                {
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    foreach (var running in new[] { true, false })
                    {
                        var capture = Complete(host.GetCapturesAsync()).Single();
                        RunWithoutConfirmation(client, host, owner, "xmat_set_capture_running",
                            new()
                            {
                                ["captureId"] = capture.Id,
                                ["expectedRevision"] = capture.Revision,
                                ["running"] = running,
                                ["idempotencyKey"] = running ? "start" : "stop"
                            });
                    }
                    Assert.Equal(0, prompts);
                    Assert.Equal(1, operations.StartCount);
                    Assert.Equal(1, operations.StopCount);
                }
                finally { Finish(client.DisposeAsync().AsTask()); }
            }, device, type);
        }

        [Fact]
        public void ANewCaptureOwnerAfterPreflightCannotBeStoppedWithoutConfirmation()
        {
            WithGlobalPolicy((host, _, context, operations) =>
            {
                context.Controller.SetRunning(true);
                var capture = Complete(host.GetCapturesAsync()).Single();
                var originalOwner = host.Runtime.Connect("late resource owner");
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                AgentExecutionContext claiming = null;
                var ownerOperation = host.Runtime.Submit(originalOwner,
                    new("test.capture", [new(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))],
                        AgentOperationEffects.StateChange, "owner"), "owner", async execution =>
                    {
                        claiming = execution;
                        entered.TrySetResult();
                        await release.Task;
                        return AgentJson.Encode(new { owned = true });
                    }, _ => Task.FromResult(true));
                Finish(entered.Task);
                var registered = false;
                EventHandler attachOwner = (_, _) =>
                {
                    if (!registered && host.Runtime.GetOperations().Any(operation =>
                        operation.Action == "capture.stop" && operation.State == AgentOperationState.Queued))
                    {
                        registered = true;
                        claiming.OwnResource(new(AgentResourceKind.Capture, capture.Id), capture.CaptureEpoch.ToString(),
                            () => Task.FromResult(AgentCleanupOutcome.NoLongerOwned));
                    }
                };
                var client = Complete(Connect(host, "stop requester"));
                host.Runtime.Changed += attachOwner;
                try
                {
                    var response = Complete(client.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["running"] = false,
                            ["idempotencyKey"] = "stop"
                        }).AsTask());
                    Assert.False(response.IsError ?? false);
                    var requested = Payload(response);
                    var owner = host.FindSession(requested.GetProperty("sessionId").GetGuid());
                    var operation = requested.GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.True(registered);
                    Assert.Equal(AgentOperationState.SourceChanged, host.Runtime.GetOperation(owner, operation).State);
                    Assert.True(context.Controller.IsRunning);
                    Assert.Equal(0, operations.StopCount);
                }
                finally
                {
                    host.Runtime.Changed -= attachOwner;
                    release.TrySetResult();
                    Finish(host.Runtime.WaitForCompletionAsync(originalOwner, ownerOperation));
                    Finish(client.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void QueryDownloadsAndNewFilesRunDirectlyButOverwriteStillWaits()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel { ActiveDatabase = database };
                WithConfirmationClient(model, (host, owner, client, _) =>
                {
                    var source = Complete(host.GetSourcesAsync()).Single();
                    var result = Query(client, host, owner, source.Id, "SELECT 42 AS Answer", "query");
                    var exported = RunWithoutConfirmation(client, host, owner, "xmat_export_query_result",
                        new() { ["resultId"] = result, ["format"] = "Csv", ["idempotencyKey"] = "download" });
                    Assert.Contains("42", System.Text.Encoding.UTF8.GetString(
                        Download(client, exported.GetProperty("artifactId").GetGuid(), 64 * 1024)));
                    var path = Path.Combine(directory.Path, "result.csv");
                    RunWithoutConfirmation(client, host, owner, "xmat_save_query_result",
                        new() { ["resultId"] = result, ["path"] = path, ["format"] = "Csv", ["idempotencyKey"] = "save" });
                    var original = File.ReadAllBytes(path);
                    var pending = RequestConfirmation(client, "xmat_save_query_result",
                        new()
                        {
                            ["resultId"] = result,
                            ["path"] = path,
                            ["format"] = "Csv",
                            ["overwrite"] = true,
                            ["idempotencyKey"] = "overwrite"
                        });
                    Assert.Equal(original, File.ReadAllBytes(path));
                    ConfirmThroughClient(client, pending, false);
                    Assert.Equal(original, File.ReadAllBytes(path));
                });
            });
        }

        [Fact]
        public void FirstImportIsRoutineButReplacingExistingRowsStillRequiresConfirmation()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path);
                try
                {
                    WithConfirmationClient(setup.Model, (host, owner, client, _) =>
                    {
                        var source = Complete(host.GetSourcesAsync()).Single();
                        Assert.False(Complete(setup.Model.CaptureSazImports.GetTargetAsync("capture.saz")).ReplacesStoredRows);
                        RunWithoutConfirmation(client, host, owner, "xmat_import_saz",
                            new() { ["sourceId"] = source.Id, ["path"] = path, ["idempotencyKey"] = "first import" });
                        Assert.True(Complete(setup.Model.CaptureSazImports.GetTargetAsync("capture.saz")).ReplacesStoredRows);
                        CaptureSazImportTests.WriteSaz(path, "replacement.example");
                        var pending = RequestConfirmation(client, "xmat_import_saz",
                            new() { ["sourceId"] = source.Id, ["path"] = path, ["idempotencyKey"] = "replace" });
                        ConfirmThroughClient(client, pending, false);
                        Assert.DoesNotContain(setup.Database.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName)
                            .Dataset.DataRecords, row => row.Str("RequestHost") == "replacement.example");
                    });
                }
                finally { Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void ClearSkipsEmptyScopesButStillProtectsActualStoredData()
        {
            _application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                WithConfirmationClient(setup.Model, (host, owner, client, _) =>
                {
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    RunWithoutConfirmation(client, host, owner, "xmat_clear_capture",
                        new()
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["idempotencyKey"] = "empty clear"
                        });
                    CaptureInvestigationTestData.AddWeb(setup.Database, 1, device: capture.DeviceName);
                    capture = Complete(host.GetCapturesAsync()).Single();
                    var pending = RequestConfirmation(client, "xmat_clear_capture",
                        new()
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["idempotencyKey"] = "data clear"
                        });
                    ConfirmThroughClient(client, pending, false);
                    Assert.Single(setup.Database.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                });
            });
        }

        [Fact]
        public void HostListEditsAreRoutineButClearingExistingOrAllHostRulesStillWaits()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                WithConfirmationClient(model, (host, owner, client, _) =>
                {
                    long Revision() => Payload(Complete(client.CallToolAsync("xmat_get_host_policy").AsTask()))
                        .GetProperty("revision").GetInt64();
                    RunWithoutConfirmation(client, host, owner, "xmat_update_host_policy",
                        new()
                        {
                            ["request"] = new HostPolicyRequest(Revision(), HostPolicyList.Block, HostPolicyAction.Clear),
                            ["idempotencyKey"] = "empty list"
                        });
                    RunWithoutConfirmation(client, host, owner, "xmat_update_host_policy",
                        new()
                        {
                            ["request"] = new HostPolicyRequest(Revision(), HostPolicyList.Block, HostPolicyAction.Add, "one.example"),
                            ["idempotencyKey"] = "one host"
                        });
                    var broad = RequestConfirmation(client, "xmat_update_host_policy",
                        new()
                        {
                            ["request"] = new HostPolicyRequest(Revision(), HostPolicyList.Bypass, HostPolicyAction.Add, "*"),
                            ["idempotencyKey"] = "all hosts"
                        });
                    ConfirmThroughClient(client, broad, false);
                    var clear = RequestConfirmation(client, "xmat_update_host_policy",
                        new()
                        {
                            ["request"] = new HostPolicyRequest(Revision(), HostPolicyList.Block, HostPolicyAction.Clear),
                            ["idempotencyKey"] = "existing rules"
                        });
                    ConfirmThroughClient(client, clear, false);
                    Assert.Equal("one.example", Assert.Single(model.HostListPolicy.BlockList.BlockedUrls));
                    Assert.Empty(model.HostListPolicy.BypassList.BypassedUrls);
                });
            });
        }

        [Theory]
        [InlineData(CaptureDeviceProxyState.Disabled, false, false, false)]
        [InlineData(CaptureDeviceProxyState.Enabled, true, false, false)]
        [InlineData(CaptureDeviceProxyState.Enabled, false, false, true)]
        [InlineData(CaptureDeviceProxyState.Unknown, false, false, true)]
        [InlineData(CaptureDeviceProxyState.Disabled, false, true, true)]
        public void ProxyConfirmationDependsOnActualRestartAndEndpointConsequences(
            CaptureDeviceProxyState state, bool owned, bool restart, bool confirmationRequired)
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                model.CaptureDeviceContexts.Add(new Context("fake proxy"));
                var operations = new ConfirmationProxyOperations(state, owned, restart);
                WithConfirmationClient(model, (host, owner, client, _) =>
                {
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var arguments = ProxyArguments(capture.Id, capture.Revision, "proxy", true);
                    if (confirmationRequired)
                    {
                        var pending = RequestConfirmation(client, "xmat_set_device_proxy", arguments);
                        ConfirmThroughClient(client, pending, false);
                        Assert.Equal(0, operations.Calls);
                    }
                    else
                    {
                        RunWithoutConfirmation(client, host, owner, "xmat_set_device_proxy", arguments);
                        Assert.Equal(1, operations.Calls);
                    }
                    var options = Payload(Complete(client.CallToolAsync("xmat_get_proxy_options",
                        new Dictionary<string, object> { ["captureId"] = capture.Id }).AsTask()));
                    Assert.True(options.GetProperty("canConfigure").GetBoolean());
                    Assert.False(options.GetProperty("configurationRequiresApproval").GetBoolean());
                    Assert.True(options.GetProperty("consequentialChangesRequireConfirmation").GetBoolean());
                }, proxyOperations: operations);
            });
        }

        [Fact]
        public void InspectorCanOpenAndCloseAnIsolatedSourceWithoutConfirmingOrGainingWriteControl()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var original = File.ReadAllBytes(path);
                var model = new CaptureAppModel { ActiveDatabase = database };
                try
                {
                    WithConfirmationClient(model, (host, owner, client, policy) =>
                    {
                        Complete(policy.SaveAsync(policy.Current.Settings with { Actions = AgentActionAccess.InspectAndExport },
                            policy.Current.Settings.Revision));
                        Finish(host.SynchronizePolicyAsync());
                        RunWithoutConfirmation(client, host, owner, "xmat_open_capture",
                            new() { ["path"] = path, ["idempotencyKey"] = "open" });
                        var capture = Complete(host.GetCapturesAsync()).Single();
                        var denied = Complete(client.CallToolAsync("xmat_set_capture_running",
                            new Dictionary<string, object>
                            {
                                ["captureId"] = capture.Id,
                                ["expectedRevision"] = capture.Revision,
                                ["running"] = true,
                                ["idempotencyKey"] = "not authorized"
                            }).AsTask());
                        Assert.True(denied.IsError);
                        Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());
                        RunWithoutConfirmation(client, host, owner, "xmat_remove_device",
                            new()
                            {
                                ["captureId"] = capture.Id,
                                ["expectedRevision"] = capture.Revision,
                                ["idempotencyKey"] = "close read-only view"
                            });
                        Assert.Empty(Complete(host.GetCapturesAsync()));
                        Assert.Equal(original, File.ReadAllBytes(path));
                    });
                }
                finally { Finish(model.CaptureFileLoads.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void ScriptsConfirmNewCodeButNotEditsDisableReenableOrCaptureWithAcceptedCode()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var operations = new NoEffectsCaptureOperations();
                WithConfirmationClient(model, (host, owner, client, _) =>
                {
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var script = Complete(model.CaptureScripts.GetScriptsAsync(capture.Id))
                        .Single(item => item.Event == CaptureScriptEvent.WebRequest);
                    var edited = RunWithoutConfirmation(client, host, owner, "xmat_update_script",
                        new(ScriptArguments(capture.Id, script, "edit")) { ["source"] = "Params.Continue = false;" });
                    script = edited.GetProperty("script").Deserialize<CaptureScriptDescriptor>(AgentJson.Options);
                    var first = RequestConfirmation(client, "xmat_set_script_enabled",
                        new(ScriptArguments(capture.Id, script, "first activation")) { ["enabled"] = true });
                    ConfirmThroughClient(client, first, true);
                    Finish(host.Runtime.WaitForCompletionAsync(owner, first.GetProperty("id").GetGuid()));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, first.GetProperty("id").GetGuid()).State);
                    capture = Complete(host.GetCapturesAsync()).Single();
                    RunWithoutConfirmation(client, host, owner, "xmat_set_capture_running",
                        new()
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["running"] = true,
                            ["idempotencyKey"] = "accepted scripted capture"
                        });
                    Assert.Equal(1, operations.Calls);
                    script = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    RunWithoutConfirmation(client, host, owner, "xmat_set_script_enabled",
                        new(ScriptArguments(capture.Id, script, "disable")) { ["enabled"] = false });
                    script = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    RunWithoutConfirmation(client, host, owner, "xmat_set_script_enabled",
                        new(ScriptArguments(capture.Id, script, "same accepted code")) { ["enabled"] = true });
                    script = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script;
                    edited = RunWithoutConfirmation(client, host, owner, "xmat_update_script",
                        new(ScriptArguments(capture.Id, script, "new code")) { ["source"] = "Params.Continue = true;" });
                    script = edited.GetProperty("script").Deserialize<CaptureScriptDescriptor>(AgentJson.Options);
                    var changed = RequestConfirmation(client, "xmat_set_script_enabled",
                        new(ScriptArguments(capture.Id, script, "changed activation")) { ["enabled"] = true });
                    ConfirmThroughClient(client, changed, false);
                    Assert.False(Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script.IsEnabled);
                }, operations);
            });
        }

        [Fact]
        public void ScriptRawOutputRequiresConfirmationOnlyWhenItExceedsTheSelectedSharingMode()
        {
            _application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                model.CaptureDeviceContexts.Add(new ScriptCaptureContext());
                WithConfirmationClient(model, (host, owner, client, policy) =>
                {
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var script = Complete(model.CaptureScripts.GetScriptsAsync(capture.Id)).First();
                    var path = Path.Combine(directory.Path, "script.txt");
                    var pending = RequestConfirmation(client, "xmat_save_script",
                        new(ScriptArguments(capture.Id, script, "raw redacted export")) { ["path"] = path });
                    Assert.Contains("unredacted", pending.GetProperty("confirmation").GetProperty("warning").GetString(),
                        StringComparison.OrdinalIgnoreCase);
                    Assert.False(File.Exists(path));
                    ConfirmThroughClient(client, pending, false);
                    Complete(policy.SaveAsync(policy.Current.Settings with { DataSharing = AgentDataSharing.Complete },
                        policy.Current.Settings.Revision));
                    Finish(host.SynchronizePolicyAsync());
                    RunWithoutConfirmation(client, host, owner, "xmat_save_script",
                        new(ScriptArguments(capture.Id, script, "complete export")) { ["path"] = path });
                    Assert.Equal(Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Source, File.ReadAllText(path));
                });
            });
        }

        [Fact]
        public void McpHintsDoNotLabelRoutineControlsAndTemporaryCleanupAsDestructive()
        {
            WithGlobalPolicy((host, _, _, _) =>
            {
                var client = Complete(Connect(host));
                try
                {
                    var tools = Complete(client.ListToolsAsync().AsTask()).ToDictionary(tool => tool.Name);
                    foreach (var name in new[]
                    {
                        "xmat_add_device", "xmat_set_pending_proxy_port", "xmat_update_preferences",
                        "xmat_reveal_desktop_target", "xmat_open_capture", "xmat_load_capture", "xmat_cancel_operation",
                        "xmat_release_query_result", "xmat_release_query_export", "xmat_release_data_result", "xmat_release_script_validation"
                    })
                        Assert.False(tools[name].ProtocolTool.Annotations.DestructiveHint);
                    foreach (var name in new[] { "xmat_clear_capture", "xmat_import_saz", "xmat_save_query_result", "xmat_set_capture_running" })
                        Assert.True(tools[name].ProtocolTool.Annotations.DestructiveHint);
                    Assert.False(tools["xmat_update_preferences"].ProtocolTool.Annotations.OpenWorldHint);
                    Assert.True(tools["xmat_open_capture"].ProtocolTool.Annotations.OpenWorldHint);
                    Assert.True(tools["xmat_get_device"].ProtocolTool.Annotations.ReadOnlyHint);
                    Assert.True(tools["xmat_get_device"].ProtocolTool.Annotations.OpenWorldHint);
                }
                finally { Finish(client.DisposeAsync().AsTask()); }
            });
        }

        private static JsonElement RunWithoutConfirmation(
            McpClient client, AgentAccessHost host, AgentSession owner, string tool, Dictionary<string, object> arguments)
        {
            var response = Complete(client.CallToolAsync(tool, arguments).AsTask());
            Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
            var requested = Payload(response);
            Assert.False(requested.TryGetProperty("confirmation", out _), requested.GetRawText());
            Assert.NotEqual("AwaitingApproval", requested.GetProperty("state").GetString());
            var operation = requested.GetProperty("id").GetGuid();
            Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
            Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, operation).State);
            return Payload(Complete(client.CallToolAsync("xmat_get_result",
                new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
        }

        private static JsonElement RequestConfirmation(
            McpClient client, string tool, Dictionary<string, object> arguments)
        {
            var response = Complete(client.CallToolAsync(tool, arguments).AsTask());
            Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
            var requested = Payload(response);
            Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
            Assert.True(requested.TryGetProperty("confirmation", out _));
            return requested;
        }

        private static void ConfirmThroughClient(McpClient client, JsonElement requested, bool accept)
        {
            var response = Complete(client.CallToolAsync("xmat_confirm_operation", new Dictionary<string, object>
            {
                ["operationId"] = requested.GetProperty("id").GetGuid(),
                ["confirmationToken"] = requested.GetProperty("confirmation").GetProperty("confirmationToken").GetString(),
                ["accept"] = accept
            }).AsTask());
            Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
        }

        private static void WithConfirmationClient(
            CaptureAppModel model, Action<AgentAccessHost, AgentSession, McpClient, AgentAccessPolicyService> action,
            ICaptureOperations operations = null, ICaptureProxyOperations proxyOperations = null)
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations,
                proxyOperations: proxyOperations, policy: policy);
            McpClient client = null;
            try
            {
                Complete(policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0));
                host.MarkReady();
                Finish(host.SynchronizePolicyAsync());
                client = Complete(Connect(host));
                var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                action(host, owner, client, policy);
            }
            finally
            {
                if (client != null)
                    Finish(client.DisposeAsync().AsTask());
                Finish(host.DisposeAsync().AsTask());
                Finish(policy.DisposeAsync().AsTask());
            }
        }

        private sealed class NoEffectsCaptureOperations : ICaptureOperations
        {
            internal int Calls { get; private set; }
            public Task<CaptureOperationFailure> GetAvailabilityAsync(
                ICaptureDeviceContext target, bool running, CancellationToken cancellationToken = default) =>
                Task.FromResult(CaptureOperationFailure.None);

            public Task<CaptureOperationResult> SetRunningAsync(
                ICaptureDeviceContext target, bool running, IProgress<WebProxyStopProgress> progress = null,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new CaptureOperationResult(false, target.CaptureController.IsRunning, CaptureOperationFailure.None));
            }
        }

        private sealed class ConfirmationProxyOperations(
            CaptureDeviceProxyState state, bool owned, bool restart) : ICaptureProxyOperations
        {
            internal int Calls { get; private set; }
            public Task<IReadOnlyList<string>> GetSourceAddressesAsync(
                Guid captureId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<string>>(["127.0.0.1"]);

            public Task<CaptureProxyPreflight> PrepareAsync(
                Guid captureId, long expectedRevision, CaptureProxyAction action, string sourceAddress = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new CaptureProxyPreflight(captureId, expectedRevision, action, DeviceType.LocalPC,
                    "127.0.0.1", "127.0.0.1", state, new("127.0.0.1", 3333), owned, restart, false, false, 3333, 0));

            public Task<CaptureProxyResult> ExecuteAsync(
                CaptureProxyPreflight preflight, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new CaptureProxyResult(preflight.CaptureId, preflight.Action,
                    false, false, preflight.Port, preflight.PendingPort, preflight.ExpectedRevision, 1,
                    true, false, false, true, false, CaptureProxyPhase.Complete, CaptureProxyFailure.None));
            }
        }
    }
}
