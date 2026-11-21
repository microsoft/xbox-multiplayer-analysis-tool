// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void TraceAvailabilityPreventsUnprivilegedResetAndStartNeedsExactDataDeletionApproval()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                var factory = new FakeTraceEngineFactory { Prerequisite = CaptureOperationFailure.ElevationRequired };
                var engine = new FakeTraceEngine();
                factory.Engines.Enqueue(engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = TraceTarget(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [summary, control]));
                    var available = Payload(Complete(client.CallToolAsync("xmat_get_capture_availability",
                        new Dictionary<string, object> { ["captureId"] = capture.Id, ["running"] = true }).AsTask()));
                    Assert.False(available.GetProperty("available").GetBoolean());
                    Assert.Equal("ElevationRequired", available.GetProperty("failure").GetString());
                    var args = TraceArguments(capture, true, "trace-start");
                    var denied = Payload(Complete(client.CallToolAsync("xmat_set_capture_running", args).AsTask()));
                    Assert.Equal("ElevationRequired", denied.GetProperty("error").GetString());
                    Assert.Empty(host.Runtime.GetOperations());
                    Assert.Single(target.Controller.NetworkTracePackets);
                    Assert.Equal(0, engine.StartCount);
                    factory.Prerequisite = CaptureOperationFailure.None;
                    capture = Complete(host.GetCapturesAsync()).Single();
                    args = TraceArguments(capture, true, "trace-start");
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_capture_running", args).AsTask()));
                    var operation = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.True(host.Runtime.GetOperation(owner, operation).Effects.HasFlag(AgentOperationEffects.DataDeletion));
                    Assert.Contains("clears", host.Runtime.GetApprovalDetails(operation), StringComparison.OrdinalIgnoreCase);
                    Assert.Single(target.Controller.NetworkTracePackets);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.True(target.Controller.IsRunning);
                    Assert.Empty(target.Controller.NetworkTracePackets);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_set_capture_running", args).AsTask())).GetProperty("id").GetGuid());
                    Assert.Equal(1, engine.StartCount);
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.False(target.Controller.IsRunning);
                    Assert.Equal(1, engine.StopCount);
                    Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
                    Assert.Equal(2, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Count());
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
        public void TraceRecoveryStopRequiresApprovalAndRetriesOnlyTheFailedDownload()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine
                {
                    Download = source => { source.Emit(NetworkTraceTestData.Event(1)); throw new IOException("private retrieval failure"); }
                };
                factory.Engines.Enqueue(engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = TraceTarget(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var operation = TraceRequest(client, capture, true, "start");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    capture = Complete(host.GetCapturesAsync()).Single();
                    operation = TraceRequest(client, capture, false, "stop");
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentOperationState.RecoveryRequired, host.Runtime.GetOperation(owner, operation).State);
                    Assert.DoesNotContain("private retrieval", JsonSerializer.Serialize(host.Runtime.GetOperation(owner, operation)));
                    Assert.Equal(CaptureTraceRecoveryPhase.Download, target.Controller.RecoveryPhase);
                    Assert.Empty(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                    capture = Complete(host.GetCapturesAsync()).Single();
                    Assert.True(capture.RecoveryRequired);
                    Assert.False(capture.TraceMayBeRunning);
                    engine.Download = source =>
                    {
                        source.Emit(NetworkTraceTestData.Event(1));
                        source.Emit(NetworkTraceTestData.Event(2));
                        return Task.CompletedTask;
                    };
                    var args = TraceArguments(capture, false, "retry-download");
                    operation = TraceRequest(client, capture, false, "retry-download");
                    Assert.Equal(AgentOperationState.AwaitingApproval, host.Runtime.GetOperation(owner, operation).State);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, operation).State);
                    Assert.Equal(1, engine.StartCount);
                    Assert.Equal(1, engine.StopCount);
                    Assert.Equal(2, engine.DownloadCount);
                    Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
                    Assert.Equal(operation, Payload(Complete(client.CallToolAsync("xmat_set_capture_running", args).AsTask())).GetProperty("id").GetGuid());
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
        public void ConsoleTraceIsRetainedForHumanRecoveryInsteadOfSilentlyStoppedOnDisconnect()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine();
                factory.Engines.Enqueue(engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = TraceTarget(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName), DeviceType.XboxConsole);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var operation = TraceRequest(client, capture, true, "console-trace");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Finish(host.RevokeAsync(owner.Id));
                    Assert.True(target.Controller.IsRunning);
                    Assert.Equal(0, engine.StopCount);
                    Assert.Equal(AgentResourceState.RecoveryRequired, Assert.Single(host.Runtime.GetOwnedResources()).State);
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
        public void ExternalRecoveryAcknowledgmentCannotClaimTheApplicationStoppedAnUnknownTrace()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine { Start = () => Task.FromException(new IOException("uncertain start")) };
                factory.Engines.Enqueue(engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = TraceTarget(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var operation = TraceRequest(client, capture, true, "unknown-start");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.True(target.Controller.RecoveryRequired);
                    capture = Complete(host.GetCapturesAsync()).Single();
                    var response = Payload(Complete(client.CallToolAsync("xmat_acknowledge_trace_recovery", new Dictionary<string, object>
                    {
                        ["captureId"] = capture.Id,
                        ["expectedRevision"] = capture.Revision,
                        ["idempotencyKey"] = "human-verified"
                    }).AsTask()));
                    operation = response.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", response.GetProperty("state").GetString());
                    Assert.Contains("NOT stop", host.Runtime.GetApprovalDetails(operation));
                    Assert.True(target.Controller.RecoveryRequired);
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    Assert.True(receipt.GetProperty("acknowledged").GetBoolean());
                    Assert.False(receipt.GetProperty("traceStoppedByApp").GetBoolean());
                    Assert.True(receipt.GetProperty("capturedDataRetained").GetBoolean());
                    Assert.Equal(0, engine.StopCount);
                    Assert.False(target.Controller.IsRunning);
                    Assert.False(target.Controller.RecoveryRequired);
                    Assert.True(Complete(target.Controller.CanCloseAsync()));
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
        public void RevokeDuringCommittedTraceStartDrainsAndCleansOnlyItsOwnedLocalSession()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var factory = new FakeTraceEngineFactory();
                var engine = new FakeTraceEngine { Start = () => { started.TrySetResult(); return release.Task; } };
                factory.Engines.Enqueue(engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = TraceTarget(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var human = new Context("human running capture");
                human.Controller.SetRunning(true);
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
                    var capture = Complete(host.GetCapturesAsync()).Single(item => item.CaptureType == CaptureType.NetworkTrace);
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var operation = TraceRequest(client, capture, true, "late-trace-start");
                    Assert.True(host.Runtime.Approve(operation));
                    Finish(started.Task);
                    var revoking = host.RevokeAsync(owner.Id);
                    Assert.False(revoking.IsCompleted);
                    Assert.True(model.IsCaptureDataOperationRunning);
                    release.TrySetResult();
                    Finish(revoking);
                    Assert.False(target.Controller.IsRunning);
                    Assert.True(human.Controller.IsRunning);
                    Assert.Same(human, model.SelectedDeviceContext);
                    Assert.Equal(1, engine.StartCount);
                    Assert.Equal(1, engine.StopCount);
                    Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
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

        private static TraceCaptureContext TraceTarget(
            CaptureAppModel model, FakeTraceEngineFactory factory, IDataTable table, DeviceType type = DeviceType.LocalPC)
        {
            var target = new TraceCaptureContext(factory, deviceType: type);
            target.Controller.Initialize(table);
            target.Controller.LoadCaptures([table.Dataset]);
            model.CaptureDeviceContexts.Add(target);
            return target;
        }

        private static Dictionary<string, object> TraceArguments(CaptureDescriptor capture, bool running, string key) =>
            new() { ["captureId"] = capture.Id, ["expectedRevision"] = capture.Revision, ["running"] = running, ["idempotencyKey"] = key };

        private static Guid TraceRequest(McpClient client, CaptureDescriptor capture, bool running, string key)
        {
            var response = Complete(client.CallToolAsync("xmat_set_capture_running", TraceArguments(capture, running, key)).AsTask());
            Assert.False(response.IsError ?? false, Payload(response).GetRawText());
            return Payload(response).GetProperty("id").GetGuid();
        }
    }
}
