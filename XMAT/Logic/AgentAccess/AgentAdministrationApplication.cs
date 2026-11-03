// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentAdministrationApplication : IAsyncDisposable
    {
        private readonly AgentRuntime _runtime;
        private readonly IHostPolicyOperations _hosts;
        private readonly IApplicationPreferences _preferences;
        private readonly IApplicationDiagnostics _diagnostics;
        private readonly IDesktopActions _desktop;
        private readonly Func<bool> _accepting;
        private readonly ICaptureFileExports _folders;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, PendingLifecycle> _pendingLifecycle = new();
        private readonly Dictionary<Guid, AgentSession> _registeredLifecycle = new();
        private readonly HashSet<Task> _scheduledLifecycle = new();
        private readonly Dictionary<AgentSession, int> _diagnosticExports = new();
        private readonly CancellationTokenSource _lifetime = new();
        private volatile bool _disposed;
        private Task _disposing;

        internal AgentAdministrationApplication(
            AgentRuntime runtime, IHostPolicyOperations hosts, IApplicationPreferences preferences,
            IApplicationDiagnostics diagnostics, IDesktopActions desktop, Func<bool> accepting,
            ICaptureFileExports folders = null)
        {
            _runtime = runtime;
            _hosts = hosts;
            _preferences = preferences;
            _diagnostics = diagnostics;
            _desktop = desktop;
            _accepting = accepting;
            _folders = folders;
            runtime.Changed += RuntimeChanged;
        }

        internal async Task<JsonElement> HostsAsync(
            AgentSession session, int offset, long? expectedRevision, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.HostPolicy);
            Demand(session, permission);
            var snapshot = await TranslateAsync(() => _hosts.InspectAsync(session.Id, cancellationToken));
            if (expectedRevision.HasValue && expectedRevision != snapshot.Revision)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            var limits = _runtime.GetSession(session).Limits;
            var result = Page(snapshot.Entries, offset, limits, entries => AgentJson.Encode(new
            {
                snapshot.Revision,
                entries,
                offset,
                totalCount = snapshot.Entries.Count,
                returnedCount = entries.Count,
                hasMore = offset + entries.Count < snapshot.Entries.Count,
                nextOffset = offset + entries.Count < snapshot.Entries.Count ? (int?)(offset + entries.Count) : null,
                snapshot.SessionOnly,
                snapshot.RetainedOnDisconnect
            }));
            Demand(session, permission);
            return result;
        }

        internal async Task<Guid> UpdateHostsAsync(
            AgentSession session, HostPolicyRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.HostPolicy);
            Demand(session, permission);
            if (request?.Action is not (HostPolicyAction.Add or HostPolicyAction.Remove or HostPolicyAction.Clear))
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var hash = AgentOperationDescriptor.Hash(request);
            var replay = _runtime.FindReplay(session, idempotencyKey, "host_policy.update", hash);
            if (replay.HasValue)
                return replay.Value;
            var plan = await TranslateAsync(() => _hosts.PrepareAsync(request, session.Id, cancellationToken));
            EnsureBudget(_runtime.GetSession(session).Limits, AgentJson.Encode(new HostPolicyChange(long.MaxValue, true,
                request.Action == HostPolicyAction.Clear ? null :
                    plan.Entry ?? new(Guid.NewGuid(), request.List, plan.Request.Pattern, long.MaxValue, HostPolicyOwnership.ThisSession),
                AffectedEntries: plan.AffectedEntries)));
            Demand(session, permission);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor("host_policy.update", [permission],
                AgentOperationEffects.StateChange | AgentOperationEffects.NetworkConfiguration |
                (request.Action == HostPolicyAction.Clear || plan.Entry?.Ownership == HostPolicyOwnership.Desktop
                    ? AgentOperationEffects.HumanOwnedResource : AgentOperationEffects.None),
                hash, Localization.GetLocalizedString("CTRL_HOST_APPROVAL",
                    request.List, request.Action, plan.Request.Pattern ?? "-", plan.Request.ExpectedRevision,
                    plan.Entry?.Id.ToString() ?? "-", plan.Entry?.Epoch.ToString() ?? "-",
                    plan.Entry?.Ownership.ToString() ?? "-", plan.ChangesState, plan.AffectedEntries));
            return Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, permission);
                var change = await TranslateAsync(() => _hosts.ExecuteAsync(plan, session.Id, context.CancellationToken));
                return AgentJson.Encode(change);
            }, token => RevalidateAsync(async () =>
                await _hosts.PrepareAsync(request, session.Id, token) == plan));
        }

        internal async Task<JsonElement> PreferencesAsync(
            AgentSession session, int reservedPortOffset, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Preferences);
            Demand(session, permission);
            var snapshot = await TranslateAsync(() => _preferences.InspectAsync(cancellationToken));
            var result = Page(snapshot.ReservedProxyPorts, reservedPortOffset, _runtime.GetSession(session).Limits,
                ports => AgentJson.Encode(new
                {
                    snapshot.Revision,
                    snapshot.Values,
                    snapshot.Options,
                    snapshot.ChangeInProgress,
                    reservedProxyPorts = ports,
                    reservedPortOffset,
                    totalReservedPorts = snapshot.ReservedProxyPorts.Count,
                    returnedReservedPorts = ports.Count,
                    hasMoreReservedPorts = reservedPortOffset + ports.Count < snapshot.ReservedProxyPorts.Count,
                    nextReservedPortOffset = reservedPortOffset + ports.Count < snapshot.ReservedProxyPorts.Count
                        ? (int?)(reservedPortOffset + ports.Count) : null,
                    reservationsAreRetained = true,
                    changesPersistOnDisconnect = true,
                    languageChangesRequireManualRestart = true
                }));
            Demand(session, permission);
            return result;
        }

        internal async Task<Guid> UpdatePreferencesAsync(
            AgentSession session, long expectedRevision, ApplicationPreferenceValues values,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Preferences);
            Demand(session, permission);
            var hash = AgentOperationDescriptor.Hash(new { expectedRevision, values });
            var replay = _runtime.FindReplay(session, idempotencyKey, "preferences.update", hash);
            if (replay.HasValue)
                return replay.Value;
            var plan = await TranslateAsync(() => _preferences.PrepareAsync(expectedRevision, values, cancellationToken));
            Demand(session, permission);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor("preferences.update", [permission],
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("CTRL_PREF_APPROVAL", expectedRevision,
                    string.Join("\n", plan.Differences.Select(difference => $"{difference.Field}: {difference.Before} -> {difference.After}")),
                    plan.LanguageRestartRequired));
            return Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, permission);
                var change = await TranslateAsync(() => _preferences.ExecuteAsync(plan, context.CancellationToken));
                var encoded = AgentJson.Encode(change);
                if (!change.Persisted)
                    throw new AgentAccessException(AgentAccessError.PersistenceFailed, encoded);
                return encoded;
            }, token => RevalidateAsync(async () =>
            {
                var current = await _preferences.PrepareAsync(expectedRevision, values, token);
                return current.Before == plan.Before && current.After == plan.After &&
                    current.Differences.SequenceEqual(plan.Differences);
            }));
        }

        internal async Task<JsonElement> DiagnosticsAsync(AgentSession session, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Diagnostics);
            Demand(session, permission);
            var snapshot = await TranslateAsync(() => _diagnostics.InspectAsync(cancellationToken));
            Demand(session, permission);
            return EnsureBudget(_runtime.GetSession(session).Limits, AgentJson.Encode(snapshot));
        }

        internal async Task<Guid> ExportDiagnosticsAsync(
            AgentSession session, string expectedRevision, AdministrationArtifactKind kind, Guid folderId,
            string fileName, string idempotencyKey, CancellationToken cancellationToken)
        {
            if (folderId == Guid.Empty)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var requirements = new[] { Permission(AgentCapability.Diagnostics),
                new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folderId)) };
            Demand(session, requirements);
            var hash = AgentOperationDescriptor.Hash(new { expectedRevision, kind, folderId, fileName });
            var replay = _runtime.FindReplay(session, idempotencyKey, "diagnostics.export", hash);
            if (replay.HasValue)
                return replay.Value;
            var plan = await TranslateAsync(() => _diagnostics.PrepareExportAsync(
                expectedRevision, kind, new(folderId, fileName), cancellationToken));
            EnsureBudget(_runtime.GetSession(session).Limits, AgentJson.Encode(
                new AdministrationExportReceipt(kind, folderId, fileName, long.MaxValue, 64, true,
                    kind != AdministrationArtifactKind.DiagnosticLogs)));
            Demand(session, requirements);
            cancellationToken.ThrowIfCancellationRequested();
            var folder = _folders?.GetFolders().FirstOrDefault(folder => folder.Id == folderId)?.Path ?? folderId.ToString();
            var descriptor = new AgentOperationDescriptor("diagnostics.export", requirements,
                AgentOperationEffects.StateChange |
                (kind == AdministrationArtifactKind.DiagnosticLogs ? AgentOperationEffects.SensitiveDataExport : AgentOperationEffects.None),
                hash, Localization.GetLocalizedString("CTRL_DIAGNOSTICS_APPROVAL", kind, folder, fileName, expectedRevision,
                    plan.CertificateSha256 ?? "-"));
            return Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                var limits = _runtime.GetSession(session).Limits;
                ReserveDiagnosticExport(session, limits.MaximumQueryResults);
                try
                {
                    var receipt = await TranslateAsync(() => _diagnostics.ExportAsync(plan, limits.MaximumQueryResultBytes,
                        TimeSpan.FromSeconds(limits.QueryTimeoutSeconds), token =>
                        {
                            token.ThrowIfCancellationRequested();
                            Demand(session, requirements);
                            var currentLimits = _runtime.GetSession(session).Limits;
                            if (currentLimits.MaximumQueryResultBytes < limits.MaximumQueryResultBytes ||
                                currentLimits.QueryTimeoutSeconds < limits.QueryTimeoutSeconds)
                                throw new AgentAccessException(AgentAccessError.LimitExceeded);
                            return Task.CompletedTask;
                        }, context.CancellationToken));
                    return AgentJson.Encode(receipt);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (--_diagnosticExports[session] == 0)
                            _diagnosticExports.Remove(session);
                    }
                }
            }, token => RevalidateAsync(async () =>
                await _diagnostics.PrepareExportAsync(expectedRevision, kind, new(folderId, fileName), token) == plan));
        }

        internal async Task<JsonElement> NavigationTargetsAsync(
            AgentSession session, int offset, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Navigation);
            Demand(session, permission);
            var registered = await TranslateAsync(() => _desktop.GetNavigationTargetsAsync(cancellationToken));
            var granted = _runtime.GetSession(session).Permissions;
            var targets = registered.Where(target =>
                (!target.OwnerSessionId.HasValue || target.OwnerSessionId == session.Id) &&
                (target.Kind == DesktopNavigationKind.Capture || granted.Contains(SourceSummary(target)))).ToArray();
            var result = Page(targets, offset, _runtime.GetSession(session).Limits, rows => AgentJson.Encode(new
            {
                targets = rows,
                offset,
                totalCount = targets.Length,
                returnedCount = rows.Count,
                hasMore = offset + rows.Count < targets.Length,
                nextOffset = offset + rows.Count < targets.Length ? (int?)(offset + rows.Count) : null
            }));
            Demand(session, targets.Where(target => target.Kind != DesktopNavigationKind.Capture)
                .Select(SourceSummary).Append(permission).Distinct().ToArray());
            return result;
        }

        internal async Task<Guid> RevealAsync(
            AgentSession session, DesktopNavigationRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Navigation);
            Demand(session, permission);
            var hash = AgentOperationDescriptor.Hash(request);
            var replay = _runtime.FindReplay(session, idempotencyKey, "desktop.reveal", hash);
            if (replay.HasValue)
                return replay.Value;
            var target = await TranslateAsync(() => _desktop.PrepareNavigationAsync(request, cancellationToken));
            if (target.OwnerSessionId.HasValue && target.OwnerSessionId != session.Id)
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var requirements = target.Kind == DesktopNavigationKind.Capture
                ? new[] { permission } : new[] { permission, SourceSummary(target) };
            Demand(session, requirements);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor("desktop.reveal", requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("CTRL_NAVIGATION_APPROVAL", target.Kind, target.Id, target.Revision,
                    target.Epoch, JsonSerializer.Serialize(target.Provenance, AgentJson.Options)));
            return Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                return AgentJson.Encode(await TranslateAsync(() => _desktop.RevealAsync(request, context.CancellationToken)));
            }, token => RevalidateAsync(async () =>
                await _desktop.PrepareNavigationAsync(request, token) == target));
        }

        internal async Task<JsonElement> LifecycleAsync(AgentSession session, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Lifecycle);
            Demand(session, permission);
            var snapshot = await TranslateAsync(() => _desktop.InspectLifecycleAsync(cancellationToken));
            Demand(session, permission);
            return EnsureBudget(_runtime.GetSession(session).Limits, AgentJson.Encode(snapshot));
        }

        internal async Task<Guid> RequestLifecycleAsync(
            AgentSession session, long expectedRevision, DesktopLifecycleAction action,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            var permission = Permission(AgentCapability.Lifecycle);
            Demand(session, permission);
            var hash = AgentOperationDescriptor.Hash(new { expectedRevision, action });
            var replay = _runtime.FindReplay(session, idempotencyKey, "desktop.lifecycle", hash);
            if (replay.HasValue)
                return replay.Value;
            var plan = await TranslateAsync(() => _desktop.PrepareLifecycleAsync(expectedRevision, action, cancellationToken));
            Demand(session, permission);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor("desktop.lifecycle", [permission], AgentOperationEffects.StateChange,
                hash, Localization.GetLocalizedString("CTRL_LIFECYCLE_APPROVAL", action, expectedRevision));
            var operationId = Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, permission);
                await TranslateAsync(() => _desktop.PrepareLifecycleAsync(expectedRevision, action, context.CancellationToken));
                return AgentJson.Encode(new DesktopLifecycleReceipt(action, true));
            }, token => RevalidateAsync(async () =>
                await _desktop.PrepareLifecycleAsync(expectedRevision, action, token) == plan));
            lock (_gate)
            {
                if (_registeredLifecycle.TryAdd(operationId, session))
                    _pendingLifecycle.Add(operationId, new(session, plan));
            }
            RuntimeChanged(this, EventArgs.Empty);
            return operationId;
        }

        private void RuntimeChanged(object sender, EventArgs args)
        {
            lock (_gate)
            {
                if (_disposed || _registeredLifecycle.Count == 0)
                    return;
            }
            var connected = _runtime.GetSessions().Where(session => session.State == AgentSessionState.Connected)
                .Select(session => session.Id).ToHashSet();
            KeyValuePair<Guid, PendingLifecycle>[] pending;
            lock (_gate)
            {
                foreach (var ended in _registeredLifecycle.Where(item => !connected.Contains(item.Value.Id)).ToArray())
                {
                    _registeredLifecycle.Remove(ended.Key);
                    _pendingLifecycle.Remove(ended.Key);
                }
                pending = _disposed ? [] : _pendingLifecycle.ToArray();
            }
            foreach (var item in pending)
            {
                AgentOperationState state;
                try
                { state = _runtime.GetOperation(item.Value.Session, item.Key).State; }
                catch (AgentAccessException)
                {
                    lock (_gate)
                    { _pendingLifecycle.Remove(item.Key); }
                    continue;
                }
                if (state is AgentOperationState.AwaitingApproval or AgentOperationState.Queued or
                    AgentOperationState.Running or AgentOperationState.CancellationRequested)
                    continue;
                lock (_gate)
                {
                    if (!_pendingLifecycle.Remove(item.Key) || _disposed)
                        continue;
                    if (state == AgentOperationState.Succeeded)
                    {
                        var scheduled = Task.Run(() => ScheduleLifecycleAsync(item.Key, item.Value));
                        _scheduledLifecycle.Add(scheduled);
                        _ = scheduled.ContinueWith(completed =>
                        {
                            lock (_gate)
                            { _scheduledLifecycle.Remove(completed); }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }

        private async Task ScheduleLifecycleAsync(Guid operationId, PendingLifecycle request)
        {
            try
            {
                // Never close from the executing job: normal close drains that same runtime.
                await _runtime.WaitForCompletionAsync(request.Session, operationId).WaitAsync(_lifetime.Token);
                if (_runtime.GetOperation(request.Session, operationId).State != AgentOperationState.Succeeded)
                    return;
                await _desktop.RequestLifecycleAsync(request.Plan, () =>
                {
                    try
                    {
                        Demand(request.Session, Permission(AgentCapability.Lifecycle));
                        return true;
                    }
                    catch (AgentAccessException) { return false; }
                }, _lifetime.Token);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // The receipt promises a request, never successful close, UAC, or restart.
            }
        }

        internal Task DrainAsync()
        {
            Task[] lifecycle;
            lock (_gate)
            { lifecycle = _scheduledLifecycle.ToArray(); }
            return Task.WhenAll(lifecycle.Append(_preferences.DrainAsync()).Append(_diagnostics.DrainAsync()));
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposing != null)
                    return new(_disposing);
                _disposed = true;
                _pendingLifecycle.Clear();
                _registeredLifecycle.Clear();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposing = completion.Task;
            }
            _ = DisposeCoreAsync(completion);
            return new(completion.Task);
        }

        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                _runtime.Changed -= RuntimeChanged;
                await _lifetime.CancelAsync();
                await DrainAsync();
                _lifetime.Dispose();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private void Demand(AgentSession session, params AgentPermission[] permissions)
        {
            if (_disposed || !_accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            _runtime.Demand(session, permissions);
        }

        private void ReserveDiagnosticExport(AgentSession session, int maximum)
        {
            lock (_gate)
            {
                var active = _diagnosticExports.GetValueOrDefault(session);
                if (active >= maximum)
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                _diagnosticExports[session] = active + 1;
            }
        }

        private static AgentPermission Permission(AgentCapability capability) => new(capability, AgentScope.Application);

        private static AgentPermission SourceSummary(DesktopNavigationTarget target) =>
            new(AgentCapability.SummaryRead, AgentScope.Source(target.Provenance.SourceId ??
                throw new AgentAccessException(AgentAccessError.OperationNotFound)));

        private static JsonElement Page<T>(
            IReadOnlyList<T> rows, int offset, AgentLimits limits, Func<IReadOnlyList<T>, JsonElement> encode)
        {
            if (offset < 0 || offset > rows.Count)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var count = Math.Min(limits.ResponseRows, rows.Count - offset);
            var result = Encode(count);
            if (Fits(result))
                return result;
            var lower = 0;
            var upper = count - 1;
            while (lower < upper)
            {
                var middle = lower + (upper - lower + 1) / 2;
                if (Fits(Encode(middle)))
                    lower = middle;
                else
                    upper = middle - 1;
            }
            if (lower == 0)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            return Encode(lower);

            JsonElement Encode(int size) => encode(rows.Skip(offset).Take(size).ToArray());
            bool Fits(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText()) <= limits.ResponseBytes - 256;
        }

        private static JsonElement EnsureBudget(AgentLimits limits, JsonElement result)
        {
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) > limits.ResponseBytes - 256)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            return result;
        }

        private Guid Submit(
            AgentSession session, AgentOperationDescriptor descriptor, string idempotencyKey,
            Func<AgentExecutionContext, Task<JsonElement>> execute, Func<CancellationToken, Task<bool>> revalidate)
        {
            EnsureBudget(_runtime.GetSession(session).Limits, AgentJson.Encode(new AgentOperationSnapshot(
                Guid.NewGuid(), session.Id, descriptor.Action, AgentOperationState.AwaitingApproval, null,
                descriptor.Effects, descriptor.Requirements, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                true, true, true)));
            return _runtime.Submit(session, descriptor, idempotencyKey, execute, revalidate);
        }

        private static async Task<bool> RevalidateAsync(Func<Task<bool>> action)
        {
            try
            { return await action(); }
            catch (Exception exception) when (exception is AdministrationException or CaptureFileException or AgentAccessException)
            { return false; }
        }

        private static async Task<T> TranslateAsync<T>(Func<Task<T>> action)
        {
            try
            { return await action(); }
            catch (AdministrationException exception)
            {
                throw new AgentAccessException(exception.Failure switch
                {
                    AdministrationFailure.InvalidRequest => AgentAccessError.InvalidRequest,
                    AdministrationFailure.SourceChanged => AgentAccessError.SourceChanged,
                    AdministrationFailure.TargetNotFound => AgentAccessError.OperationNotFound,
                    AdministrationFailure.PersistenceFailed => AgentAccessError.PersistenceFailed,
                    AdministrationFailure.LimitExceeded => AgentAccessError.LimitExceeded,
                    AdministrationFailure.Timeout => AgentAccessError.QueryTimeout,
                    _ => AgentAccessError.OperationUnavailable
                });
            }
            catch (CaptureFileException exception)
            {
                throw new AgentAccessException(exception.Failure switch
                {
                    CaptureFileFailure.FolderChanged => AgentAccessError.SourceChanged,
                    CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
                    CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
                    _ => AgentAccessError.OperationUnavailable
                });
            }
        }

        private sealed record PendingLifecycle(AgentSession Session, DesktopLifecyclePlan Plan);
    }
}
