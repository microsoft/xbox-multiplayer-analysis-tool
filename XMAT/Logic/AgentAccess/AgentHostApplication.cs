// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentHostApplication : IAsyncDisposable
    {
        private readonly AgentRuntime _runtime;
        private readonly ApplicationShutdown _shutdown;
        private readonly Action _pause;
        private readonly Action _resume;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Pending> _pending = new();
        private readonly List<Task> _scheduled = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _disposed;

        internal AgentHostApplication(AgentRuntime runtime, ApplicationShutdown shutdown, Action pause, Action resume)
        {
            _runtime = runtime;
            _shutdown = shutdown;
            _pause = pause;
            _resume = resume;
            runtime.Changed += Changed;
        }

        internal IEnumerable<McpServerTool> Create(
            AgentSession session, Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap) =>
        [
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)(cancellationToken =>
                wrap(async () => AgentJson.Encode(await InspectAsync(session, cancellationToken)))),
                "xmat_get_host_lifecycle", "AGENT_TOOL_HOST_LIFECYCLE", true),
            AgentMcpTools.Tool((Func<string, ApplicationShutdownChoices, string, CancellationToken, Task<CallToolResult>>)
                ((expectedRevision, choices, idempotencyKey, cancellationToken) => wrap(async () =>
                    AgentJson.Encode(_runtime.GetOperation(session, await RequestAsync(
                        session, expectedRevision, choices, idempotencyKey, cancellationToken))))),
                "xmat_stop_host", "AGENT_TOOL_STOP_HOST", false)
        ];

        private async Task<ApplicationShutdownSnapshot> InspectAsync(AgentSession session, CancellationToken cancellationToken)
        {
            _runtime.Demand(session, new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application));
            return await _shutdown.InspectAsync(cancellationToken);
        }

        internal async Task<Guid> RequestAsync(
            AgentSession session, string expectedRevision, ApplicationShutdownChoices choices,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            _runtime.Demand(session, Array.Empty<AgentPermission>());
            var hash = AgentOperationDescriptor.Hash(new { expectedRevision, choices });
            var replay = _runtime.FindReplay(session, idempotencyKey, "host.shutdown", hash);
            if (replay.HasValue)
                return replay.Value;
            var snapshot = await InspectAsync(session, cancellationToken);
            var housekeeping = Housekeeping(session, snapshot, null);
            var permission = new AgentPermission(housekeeping ? AgentCapability.HostHousekeeping : AgentCapability.Lifecycle, AgentScope.Application);
            _runtime.Demand(session, permission);
            ApplicationShutdown.Plan plan;
            try
            { plan = await _shutdown.PrepareAsync(expectedRevision, choices, cancellationToken); }
            catch (CaptureFileException exception) { throw AgentFileErrors.Translate(exception); }
            var confirmationRequired = RequiresConfirmation(session, snapshot, plan);
            var effects = AgentOperationEffects.StateChange;
            if (!housekeeping)
            {
                effects |= AgentOperationEffects.HumanOwnedResource;
                if (snapshot.HasCapturedData || snapshot.UnsavedSqlDocuments.Count > 0 || snapshot.ScriptDrafts.Count > 0)
                    effects |= AgentOperationEffects.DataDeletion;
                if (snapshot.Captures.Any(capture => capture.IsRunning && capture.DeviceType == DeviceType.LocalPC) || plan.Choices.DisableXboxProxies)
                    effects |= AgentOperationEffects.NetworkConfiguration;
                if (plan.Choices.DisableXboxProxies)
                    effects |= AgentOperationEffects.ConsoleRestart;
                if (plan.Capture?.Overwrite != null || plan.Documents.Values.Any(destination => destination.Overwrite != null))
                    effects |= AgentOperationEffects.FileOverwrite;
            }
            var descriptor = new AgentOperationDescriptor("host.shutdown", [permission], effects, hash,
                Localization.GetLocalizedString("AGENT_HOST_SHUTDOWN_WARNING", snapshot.Captures.Count,
                    snapshot.UnsavedSqlDocuments.Count, snapshot.ScriptDrafts.Count, plan.Choices.SaveCapturePath ?? "-",
                    plan.Choices.DisableXboxProxies),
                confirmationRequired: confirmationRequired, rawLocalOutput: plan.Capture != null || plan.Documents.Count != 0);
            cancellationToken.ThrowIfCancellationRequested();
            var operationId = Guid.Empty;
            operationId = _runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                _runtime.Demand(session, permission);
                if (housekeeping && !Housekeeping(session, await _shutdown.InspectAsync(context.CancellationToken), operationId))
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                if (!confirmationRequired && RequiresConfirmation(session,
                    await _shutdown.InspectAsync(context.CancellationToken), plan))
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                _pause();
                try
                { return AgentJson.Encode(await _shutdown.ExecuteAsync(plan, context.CancellationToken)); }
                catch { _resume(); throw; }
            }, async token =>
            {
                var current = await _shutdown.InspectAsync(token);
                return current.Revision == expectedRevision && (!housekeeping || Housekeeping(session, current, operationId)) &&
                    (confirmationRequired || !RequiresConfirmation(session, current, plan));
            });
            lock (_gate)
                _pending.TryAdd(operationId, new(session, permission, housekeeping, confirmationRequired, plan));
            Changed(this, EventArgs.Empty);
            return operationId;
        }

        private bool RequiresConfirmation(
            AgentSession session, ApplicationShutdownSnapshot snapshot, ApplicationShutdown.Plan plan) =>
            snapshot.HasCapturedData && plan.Capture == null ||
            plan.Choices.Documents.Any(document => document.Action == ShutdownDocumentAction.Discard) ||
            snapshot.ScriptDrafts.Count != 0 ||
            plan.Choices.DisableXboxProxies && snapshot.Captures.Any(capture =>
                !capture.IsReadOnly && capture.DeviceType == DeviceType.XboxConsole && capture.CaptureType == CaptureType.WebProxy) ||
            plan.Capture?.Overwrite != null || plan.Documents.Values.Any(destination => destination.Overwrite != null) ||
            _runtime.GetSessions().Any(other => other.Id != session.Id && other.State == AgentSessionState.Connected);

        private bool Housekeeping(AgentSession session, ApplicationShutdownSnapshot snapshot, Guid? executing) =>
            snapshot.Headless && !snapshot.HasCapturedData && !snapshot.RecoveryRequired &&
            snapshot.UnsavedSqlDocuments.Count == 0 && snapshot.ScriptDrafts.Count == 0 &&
            !snapshot.Captures.Any(capture => capture.IsRunning) &&
            _runtime.GetSessions().Count(item => item.State == AgentSessionState.Connected) == 1 &&
            _runtime.GetSessions().Any(item => item.Id == session.Id && item.State == AgentSessionState.Connected) &&
            !_runtime.GetOperations().Any(item => item.Id != executing && item.State is
                    AgentOperationState.AwaitingApproval or AgentOperationState.Queued or AgentOperationState.Running or AgentOperationState.CancellationRequested) &&
            !_runtime.GetOwnedResources().Any(item => item.State == AgentResourceState.RecoveryRequired ||
                item.Resource.Kind is AgentResourceKind.Capture or AgentResourceKind.Script);

        private void Changed(object sender, EventArgs args)
        {
            KeyValuePair<Guid, Pending>[] pending;
            lock (_gate)
                pending = _disposed ? [] : _pending.ToArray();
            foreach (var item in pending)
            {
                AgentOperationState state;
                try
                { state = _runtime.GetOperation(item.Value.Session, item.Key).State; }
                catch (AgentAccessException) { state = AgentOperationState.Denied; }
                if (state is AgentOperationState.AwaitingApproval or AgentOperationState.Queued or
                    AgentOperationState.Running or AgentOperationState.CancellationRequested)
                    continue;
                lock (_gate)
                {
                    if (!_pending.Remove(item.Key))
                        continue;
                    if (state == AgentOperationState.Succeeded)
                        _scheduled.Add(Task.Run(() => ExitAfterCompletionAsync(item.Key, item.Value)));
                }
            }
        }

        private async Task ExitAfterCompletionAsync(Guid operationId, Pending pending)
        {
            try
            {
                await _runtime.WaitForCompletionAsync(pending.Session, operationId).WaitAsync(_lifetime.Token);
                var receipt = _runtime.GetResult(pending.Session, operationId);
                // Allow the initiating MCP response to leave before closing its transport.
                await Task.Delay(150, _lifetime.Token);
                await _shutdown.RequestExitAsync(receipt.GetProperty("preparedRevision").GetString(), snapshot =>
                {
                    try
                    {
                        _runtime.Demand(pending.Session, pending.Permission);
                        return (!pending.Housekeeping || Housekeeping(pending.Session, snapshot, operationId)) &&
                            (pending.ConfirmationRequired || !RequiresConfirmation(pending.Session, snapshot, pending.Plan));
                    }
                    catch (AgentAccessException) { return false; }
                }, _lifetime.Token);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                await _shutdown.CancelPreparedAsync();
                _resume();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] pending;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _pending.Clear();
                pending = _scheduled.ToArray();
            }
            _runtime.Changed -= Changed;
            await _lifetime.CancelAsync();
            await Task.WhenAll(pending);
        }

        private sealed record Pending(
            AgentSession Session, AgentPermission Permission, bool Housekeeping,
            bool ConfirmationRequired, ApplicationShutdown.Plan Plan);
    }
}
