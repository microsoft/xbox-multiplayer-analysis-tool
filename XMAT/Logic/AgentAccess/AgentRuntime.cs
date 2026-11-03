// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentRuntime : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<AgentSession, SessionData> _sessions = new();
        private readonly Dictionary<Guid, Operation> _operations = new();
        private readonly Dictionary<AgentResourceKey, OwnedResource> _resources = new();
        private readonly HashSet<(AgentSession Session, AgentResourceKey Resource, string Revision)> _adopted = new();
        private bool _accepting;
        private bool _disposed;
        private bool _faulted;
        private Task _disposing;
        private Task _disabling;

        internal event EventHandler Changed;

        internal bool IsEnabled
        {
            get { lock (_gate) { return _accepting; } }
        }

        internal void Enable()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_faulted || _disabling is { IsCompletedSuccessfully: false })
                {
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                }
                _disabling = null;
                _accepting = true;
            }
            NotifyChanged();
        }

        internal AgentSession Connect(string declaredClientName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(declaredClientName);
            if (declaredClientName.Length > 128 || declaredClientName.Any(char.IsControl))
            {
                throw new ArgumentOutOfRangeException(nameof(declaredClientName));
            }
            AgentSession session;
            lock (_gate)
            {
                if (!_accepting || _disposed)
                {
                    throw new AgentAccessException(AgentAccessError.AccessDisabled);
                }
                session = new AgentSession();
                _sessions.Add(session, new SessionData(session, declaredClientName));
            }
            NotifyChanged();
            return session;
        }

        internal AgentSessionSnapshot GetSession(AgentSession session)
        {
            lock (_gate)
            { return Snapshot(GetSessionData(session)); }
        }

        internal void SetDeclaredClientName(AgentSession session, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            {
                return;
            }
            lock (_gate)
            {
                var data = GetSessionData(session);
                if (data.Name == name)
                {
                    return;
                }
                data.Name = name;
            }
            NotifyChanged();
        }

        internal bool IsOwned(AgentSession session, AgentResourceKey key, string revision)
        {
            lock (_gate)
            {
                GetSessionData(session);
                return _resources.TryGetValue(key, out var resource) &&
                    ReferenceEquals(resource.Session, session) &&
                    resource.State == AgentResourceState.Owned && resource.Revision == revision;
            }
        }

        internal void EnsureOwnershipAvailable(AgentSession session, AgentResourceKey key)
        {
            lock (_gate)
            {
                DemandConnected(session);
                if (_resources.TryGetValue(key, out var resource) &&
                    (!ReferenceEquals(resource.Session, session) || resource.State == AgentResourceState.Cleaning))
                {
                    throw new AgentAccessException(AgentAccessError.OwnershipConflict);
                }
            }
        }

        internal void ForgetSupersededScript(Guid scriptId, long currentRevision) =>
            ForgetSupersededResource(new AgentResourceKey(AgentResourceKind.Script, scriptId), currentRevision);

        internal void ForgetSupersededCapture(Guid captureId, long currentEpoch) =>
            ForgetSupersededResource(new AgentResourceKey(AgentResourceKind.Capture, captureId), currentEpoch);

        private void ForgetSupersededResource(AgentResourceKey key, long currentRevision)
        {
            var changed = false;
            lock (_gate)
            {
                if (_resources.TryGetValue(key, out var resource) && resource.State != AgentResourceState.Cleaning &&
                    long.TryParse(resource.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var ownedRevision) &&
                    ownedRevision < currentRevision)
                {
                    _adopted.Add((resource.Session, key, resource.Revision));
                    _resources.Remove(key);
                    changed = true;
                }
            }
            if (changed)
                NotifyChanged();
        }

        internal IReadOnlyList<AgentSessionSnapshot> GetSessions()
        {
            lock (_gate)
            { return _sessions.Values.Select(Snapshot).ToArray(); }
        }

        internal void TrimDisconnectedHistory(int retain = 32)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(retain);
            lock (_gate)
            {
                var finished = _sessions.Values.Where(data =>
                    data.State != AgentSessionState.Connected && !data.PermissionsUpdating &&
                    data.Ending?.IsCompletedSuccessfully == true &&
                    !_resources.Values.Any(resource => ReferenceEquals(resource.Session, data.Session))).ToArray();
                foreach (var data in finished.Take(Math.Max(0, finished.Length - retain)))
                {
                    foreach (var operation in data.Operations.Values)
                    {
                        operation.Cancellation.Dispose();
                        _operations.Remove(operation.Id);
                    }
                    _adopted.RemoveWhere(item => ReferenceEquals(item.Session, data.Session));
                    _sessions.Remove(data.Session);
                }
            }
        }

        internal async Task SetPermissionsAsync(
            AgentSession session,
            IEnumerable<AgentPermission> permissions,
            AgentLimits limits = null)
        {
            ArgumentNullException.ThrowIfNull(permissions);
            var granted = permissions.ToHashSet();
            if (granted.Contains(null))
            {
                throw new ArgumentException("Permissions cannot contain null.", nameof(permissions));
            }
            Operation[] affected;
            OwnedResource[] owned;
            SessionData data;
            lock (_gate)
            {
                data = DemandConnected(session);
                var effectiveLimits = limits ?? data.Limits;
                if (data.Permissions.SetEquals(granted) && data.Limits == effectiveLimits)
                {
                    return;
                }
                data.Permissions = granted;
                data.Limits = effectiveLimits;
                data.PermissionsUpdating = true;
                affected = data.Operations.Values.Where(operation => !HasPermissions(data, operation.Descriptor)).ToArray();
                owned = _resources.Values.Where(resource =>
                    ReferenceEquals(resource.Session, session) &&
                    !HasPermissions(data, resource.Descriptor)).ToArray();
                foreach (var operation in affected)
                {
                    operation.Result = null;
                    RequestCancellation(operation);
                }
                // Any approval belongs to the old grant set, even if its requested scope remains allowed.
                foreach (var pending in data.Operations.Values.Where(operation =>
                    operation.State == AgentOperationState.AwaitingApproval))
                {
                    FinishPending(pending, AgentOperationState.Denied, AgentAccessError.PermissionDenied);
                }
            }
            NotifyChanged();
            try
            {
                await CancelAndDrainAsync(affected);
                lock (_gate)
                {
                    owned = owned.Concat(_resources.Values.Where(resource =>
                        ReferenceEquals(resource.Session, session) &&
                        !HasPermissions(data, resource.Descriptor))).Distinct().ToArray();
                }
                await CleanResourcesAsync(owned);
            }
            finally
            {
                lock (_gate)
                { data.PermissionsUpdating = false; }
                NotifyChanged();
            }
        }

        internal void Demand(AgentSession session, params AgentPermission[] permissions)
        {
            lock (_gate)
            {
                var data = DemandConnected(session);
                if (permissions.Any(permission => permission == null || !data.Permissions.Contains(permission)))
                {
                    throw new AgentAccessException(AgentAccessError.PermissionDenied);
                }
            }
        }

        internal Guid Submit(
            AgentSession session,
            AgentOperationDescriptor descriptor,
            string idempotencyKey,
            Func<AgentExecutionContext, Task<JsonElement>> execute,
            Func<CancellationToken, Task<bool>> revalidate)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(execute);
            ArgumentNullException.ThrowIfNull(revalidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            if (idempotencyKey.Length > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
            }
            Operation operation;
            lock (_gate)
            {
                var data = DemandConnected(session);
                if (!HasPermissions(data, descriptor))
                {
                    throw new AgentAccessException(AgentAccessError.PermissionDenied);
                }
                if (data.Idempotency.TryGetValue(idempotencyKey, out var existing))
                {
                    if (existing.Descriptor.Fingerprint != descriptor.Fingerprint)
                    {
                        throw new AgentAccessException(AgentAccessError.IdempotencyConflict);
                    }
                    return existing.Id;
                }
                if (data.Operations.Count >= data.Limits.MaximumOperations ||
                    (descriptor.RequiresApproval && data.Operations.Values.Any(item =>
                        item.State == AgentOperationState.AwaitingApproval)))
                {
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                }
                operation = new Operation(data, descriptor, execute, revalidate);
                data.Idempotency.Add(idempotencyKey, operation);
                data.Operations.Add(operation.Id, operation);
                _operations.Add(operation.Id, operation);
                if (!descriptor.RequiresApproval)
                {
                    Start(operation);
                }
            }
            NotifyChanged();
            return operation.Id;
        }

        internal Guid? FindReplay(AgentSession session, string key, string action, string argumentsHash)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            lock (_gate)
            {
                var data = DemandConnected(session);
                if (!data.Idempotency.TryGetValue(key, out var operation))
                {
                    return null;
                }
                if (operation.Descriptor.Action != action || operation.Descriptor.ArgumentsHash != argumentsHash)
                {
                    throw new AgentAccessException(AgentAccessError.IdempotencyConflict);
                }
                if (!HasPermissions(data, operation.Descriptor))
                {
                    throw new AgentAccessException(AgentAccessError.PermissionDenied);
                }
                return operation.Id;
            }
        }

        // Only the desktop approval adapter calls this; it is not an agent operation.
        internal bool Approve(Guid operationId)
        {
            lock (_gate)
            {
                if (!_operations.TryGetValue(operationId, out var operation) ||
                    operation.State != AgentOperationState.AwaitingApproval)
                {
                    return false;
                }
                var data = operation.Session;
                if (data.State != AgentSessionState.Connected || data.PermissionsUpdating ||
                    !HasPermissions(data, operation.Descriptor))
                {
                    FinishPending(operation, AgentOperationState.Denied, AgentAccessError.PermissionDenied);
                }
                else
                {
                    Start(operation);
                }
            }
            NotifyChanged();
            return true;
        }

        internal bool Deny(Guid operationId)
        {
            lock (_gate)
            {
                if (!_operations.TryGetValue(operationId, out var operation) ||
                    operation.State != AgentOperationState.AwaitingApproval)
                {
                    return false;
                }
                FinishPending(operation, AgentOperationState.Denied);
            }
            NotifyChanged();
            return true;
        }

        internal string GetApprovalDetails(Guid operationId)
        {
            lock (_gate)
            {
                return _operations.TryGetValue(operationId, out var operation) &&
                    operation.State == AgentOperationState.AwaitingApproval
                    ? operation.ApprovalDetails : null;
            }
        }

        internal AgentOperationSnapshot GetOperation(AgentSession session, Guid operationId)
        {
            lock (_gate)
            { return Snapshot(GetOperationData(session, operationId)); }
        }

        internal IReadOnlyList<AgentOperationSnapshot> GetOperations()
        {
            lock (_gate)
            { return _operations.Values.Select(Snapshot).ToArray(); }
        }

        internal JsonElement GetResult(AgentSession session, Guid operationId)
        {
            lock (_gate)
            {
                var data = DemandConnected(session);
                var operation = GetOperationData(session, operationId);
                if (!HasPermissions(data, operation.Descriptor))
                {
                    throw new AgentAccessException(AgentAccessError.PermissionDenied);
                }
                if (operation.State is not (AgentOperationState.Succeeded or AgentOperationState.Failed or
                    AgentOperationState.SourceChanged or AgentOperationState.RecoveryRequired))
                {
                    throw new AgentAccessException(AgentAccessError.OperationNotFinished);
                }
                if (!operation.Result.HasValue)
                {
                    throw new AgentAccessException(AgentAccessError.ResultUnavailable);
                }
                if (Encoding.UTF8.GetByteCount(operation.Result.Value.GetRawText()) > data.Limits.ResponseBytes)
                {
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                }
                return operation.Result.Value.Clone();
            }
        }

        internal Task WaitForCompletionAsync(AgentSession session, Guid operationId)
        {
            lock (_gate)
            {
                var operation = GetOperationData(session, operationId);
                if (operation.State == AgentOperationState.AwaitingApproval)
                {
                    throw new AgentAccessException(AgentAccessError.ApprovalRequired);
                }
                return operation.Worker;
            }
        }

        internal async Task<bool> CancelAsync(AgentSession session, Guid operationId)
        {
            Operation operation;
            lock (_gate)
            {
                operation = GetOperationData(session, operationId);
                if (!RequestCancellation(operation))
                {
                    return false;
                }
            }
            NotifyChanged();
            await CancelTokenAsync(operation);
            return true;
        }

        internal Task EndSessionAsync(AgentSession session, bool revoked = false)
        {
            SessionData data;
            Operation[] operations;
            TaskCompletionSource completion;
            lock (_gate)
            {
                data = GetSessionData(session);
                if (data.Ending != null)
                {
                    return data.Ending;
                }
                data.State = revoked ? AgentSessionState.Revoked : AgentSessionState.Disconnected;
                data.Permissions.Clear();
                operations = data.Operations.Values.ToArray();
                foreach (var operation in operations)
                {
                    operation.Result = null;
                    RequestCancellation(operation);
                }
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                data.Ending = completion.Task;
            }
            NotifyChanged();
            _ = EndCoreAsync(data, operations, completion);
            return completion.Task;
        }

        internal IReadOnlyList<AgentOwnedResourceSnapshot> GetOwnedResources()
        {
            lock (_gate)
            {
                return _resources.Values.Select(resource => new AgentOwnedResourceSnapshot(
                    resource.Key, resource.Session.Id, resource.Revision, resource.State)).ToArray();
            }
        }

        internal bool TakeOver(AgentResourceKey resource)
        {
            lock (_gate)
            {
                if (!_resources.TryGetValue(resource, out var owned) ||
                    owned.State == AgentResourceState.Cleaning)
                {
                    return false;
                }
                _adopted.Add((owned.Session, owned.Key, owned.Revision));
                _resources.Remove(resource);
            }
            NotifyChanged();
            return true;
        }

        internal async Task<bool> ReleaseOwnedResourceAsync(AgentSession session, AgentResourceKey key)
        {
            OwnedResource resource;
            lock (_gate)
            {
                DemandConnected(session);
                if (!_resources.TryGetValue(key, out resource) || !ReferenceEquals(resource.Session, session))
                {
                    return false;
                }
            }
            await CleanResourcesAsync([resource]);
            lock (_gate)
            { return !_resources.ContainsKey(key); }
        }

        internal Task DisableAsync()
        {
            AgentSession[] sessions;
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disabling != null)
                {
                    return _disabling;
                }
                _accepting = false;
                sessions = _sessions.Keys.ToArray();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disabling = completion.Task;
            }
            NotifyChanged();
            _ = DisableCoreAsync(sessions, completion);
            return completion.Task;
        }

        private async Task DisableCoreAsync(AgentSession[] sessions, TaskCompletionSource completion)
        {
            try
            {
                await Task.WhenAll(sessions.Select(session => EndSessionAsync(session, revoked: true)));
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                NotifyChanged();
            }
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposing != null)
                {
                    return new ValueTask(_disposing);
                }
                _disposed = true;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposing = completion.Task;
            }
            _ = DisposeCoreAsync(completion);
            return new ValueTask(completion.Task);
        }

        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                await DisableAsync();
                lock (_gate)
                {
                    foreach (var operation in _operations.Values)
                    {
                        operation.Cancellation.Dispose();
                    }
                }
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task EndCoreAsync(
            SessionData data,
            Operation[] operations,
            TaskCompletionSource completion)
        {
            try
            {
                try
                {
                    await CancelAndDrainAsync(operations);
                }
                finally
                {
                    OwnedResource[] owned;
                    lock (_gate)
                    {
                        owned = _resources.Values.Where(resource =>
                            ReferenceEquals(resource.Session, data.Session)).ToArray();
                    }
                    await CleanResourcesAsync(owned);
                }
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                NotifyChanged();
            }
        }

        private void Start(Operation operation)
        {
            operation.ApprovalDetails = null;
            operation.State = AgentOperationState.Queued;
            operation.Worker = Task.Run(() => ExecuteAsync(operation));
        }

        private async Task ExecuteAsync(Operation operation)
        {
            AgentExecutionContext context = null;
            try
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                if (!await operation.Revalidate(operation.Cancellation.Token))
                {
                    lock (_gate)
                    {
                        operation.State = AgentOperationState.SourceChanged;
                        operation.Error = AgentAccessError.SourceChanged;
                    }
                    return;
                }
                lock (_gate)
                {
                    operation.Cancellation.Token.ThrowIfCancellationRequested();
                    if (operation.Session.State != AgentSessionState.Connected ||
                        !HasPermissions(operation.Session, operation.Descriptor))
                    {
                        operation.State = AgentOperationState.Denied;
                        operation.Error = AgentAccessError.PermissionDenied;
                        return;
                    }
                    operation.State = AgentOperationState.Running;
                    operation.ExecutionStarted = true;
                }
                NotifyChanged();
                context = new AgentExecutionContext(
                    (key, revision, cleanup) => OwnResource(operation, key, revision, cleanup),
                    operation.Cancellation.Token);
                var result = await operation.Execute(context);
                var bytes = Encoding.UTF8.GetByteCount(result.GetRawText());
                lock (_gate)
                {
                    if (bytes > operation.Session.Limits.ResponseBytes)
                    {
                        operation.State = AgentOperationState.Failed;
                        operation.Error = AgentAccessError.LimitExceeded;
                    }
                    else
                    {
                        operation.State = AgentOperationState.Succeeded;
                        if (operation.Session.State == AgentSessionState.Connected &&
                            HasPermissions(operation.Session, operation.Descriptor))
                        {
                            operation.Result = result.Clone();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
            {
                lock (_gate)
                { operation.State = AgentOperationState.Cancelled; }
            }
            catch (AgentAccessException exception)
            {
                lock (_gate)
                {
                    operation.State = exception.Error switch
                    {
                        AgentAccessError.SourceChanged => AgentOperationState.SourceChanged,
                        AgentAccessError.RecoveryRequired => AgentOperationState.RecoveryRequired,
                        _ => AgentOperationState.Failed
                    };
                    operation.Error = exception.Error;
                    if (exception.ProtectedResult.HasValue &&
                        operation.Session.State == AgentSessionState.Connected &&
                        HasPermissions(operation.Session, operation.Descriptor) &&
                        Encoding.UTF8.GetByteCount(exception.ProtectedResult.Value.GetRawText()) <= operation.Session.Limits.ResponseBytes)
                    {
                        operation.Result = exception.ProtectedResult.Value.Clone();
                    }
                }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                lock (_gate)
                {
                    operation.State = AgentOperationState.Failed;
                    operation.Error = AgentAccessError.ExecutionFailed;
                }
            }
            catch (Exception exception) when (ExceptionUtilities.IsFatal(exception))
            {
                lock (_gate)
                {
                    _faulted = true;
                    _accepting = false;
                }
                throw;
            }
            finally
            {
                context?.Close();
                OwnedResource[] unpublished;
                lock (_gate)
                {
                    if (operation.State is AgentOperationState.Queued or AgentOperationState.Running or
                        AgentOperationState.CancellationRequested)
                    {
                        operation.State = AgentOperationState.Failed;
                        operation.Error = AgentAccessError.ExecutionFailed;
                    }
                    operation.CompletedAt = DateTimeOffset.UtcNow;
                    operation.Execute = null;
                    operation.Revalidate = null;
                    unpublished = operation.State is AgentOperationState.Failed or AgentOperationState.Cancelled or
                        AgentOperationState.Denied or AgentOperationState.SourceChanged
                        ? _resources.Values.Where(resource =>
                            ReferenceEquals(resource.Descriptor, operation.Descriptor) &&
                            (resource.Key.Kind is AgentResourceKind.QueryResult or AgentResourceKind.Artifact or AgentResourceKind.CaptureData ||
                            (resource.Key.Kind == AgentResourceKind.ScriptValidation && !operation.Result.HasValue))).ToArray()
                        : [];
                }
                await CleanResourcesAsync(unpublished);
                NotifyChanged();
            }
        }

        private void OwnResource(
            Operation operation,
            AgentResourceKey key,
            string revision,
            Func<Task<AgentCleanupOutcome>> cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            ArgumentException.ThrowIfNullOrWhiteSpace(revision);
            if (key.Id == Guid.Empty || !Enum.IsDefined(key.Kind))
            {
                throw new ArgumentException("A resource requires a valid identity.", nameof(key));
            }
            lock (_gate)
            {
                if (_adopted.Contains((operation.Session.Session, key, revision)))
                {
                    throw new AgentAccessException(AgentAccessError.OwnershipConflict);
                }
                var requiredCapability = key.Kind switch
                {
                    AgentResourceKind.Capture => AgentCapability.CaptureControl,
                    AgentResourceKind.Script => AgentCapability.ScriptExecute,
                    AgentResourceKind.QueryResult => AgentCapability.RawDataRead,
                    AgentResourceKind.Artifact => AgentCapability.Diagnostics,
                    AgentResourceKind.CaptureData => AgentCapability.SummaryRead,
                    AgentResourceKind.ScriptValidation => AgentCapability.ScriptRead,
                    _ => throw new ArgumentOutOfRangeException(nameof(key))
                };
                if (!operation.Descriptor.Requirements.Any(permission =>
                    (permission.Capability == requiredCapability ||
                    (key.Kind == AgentResourceKind.Artifact && permission.Capability == AgentCapability.QueryResultExport)) &&
                    (key.Kind != AgentResourceKind.Capture || permission.Scope.Id == key.Id)) ||
                    (key.Kind is AgentResourceKind.Capture or AgentResourceKind.Script &&
                    operation.Descriptor.Effects == AgentOperationEffects.None))
                {
                    throw new AgentAccessException(AgentAccessError.PermissionDenied);
                }
                if (_resources.TryGetValue(key, out var previous) &&
                    (!ReferenceEquals(previous.Session, operation.Session.Session) ||
                    previous.State == AgentResourceState.Cleaning))
                {
                    throw new AgentAccessException(AgentAccessError.OwnershipConflict);
                }
                _resources[key] = new OwnedResource(
                    key, operation.Session.Session, revision, operation.Descriptor, cleanup);
            }
            NotifyChanged();
        }

        private async Task CleanResourcesAsync(IEnumerable<OwnedResource> resources)
        {
            foreach (var resource in resources)
            {
                Task cleaning;
                lock (_gate)
                {
                    if (!_resources.TryGetValue(resource.Key, out var current) ||
                        !ReferenceEquals(current, resource))
                    {
                        continue;
                    }
                    if (resource.Cleaning == null)
                    {
                        resource.State = AgentResourceState.Cleaning;
                        resource.Cleaning = Task.Run(() => CleanResourceAsync(resource));
                    }
                    cleaning = resource.Cleaning;
                }
                await cleaning;
            }
        }

        private async Task CleanResourceAsync(OwnedResource resource)
        {
            var outcome = AgentCleanupOutcome.RecoveryRequired;
            try
            {
                outcome = await resource.Cleanup();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // Recovery remains visible; raw exception text can contain capture data.
            }
            finally
            {
                lock (_gate)
                {
                    if (_resources.TryGetValue(resource.Key, out var current) &&
                        ReferenceEquals(current, resource))
                    {
                        if (outcome is not (AgentCleanupOutcome.Released or AgentCleanupOutcome.NoLongerOwned))
                        {
                            resource.State = AgentResourceState.RecoveryRequired;
                        }
                        else
                        {
                            _resources.Remove(resource.Key);
                        }
                    }
                }
                NotifyChanged();
            }
        }

        private static bool RequestCancellation(Operation operation)
        {
            if (operation.State == AgentOperationState.AwaitingApproval)
            {
                FinishPending(operation, AgentOperationState.Cancelled);
                return true;
            }
            if (operation.State is AgentOperationState.Queued or AgentOperationState.Running or
                AgentOperationState.CancellationRequested)
            {
                operation.State = AgentOperationState.CancellationRequested;
                return true;
            }
            return false;
        }

        private static void FinishPending(
            Operation operation,
            AgentOperationState state,
            AgentAccessError? error = null)
        {
            operation.State = state;
            operation.Error = error;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.ApprovalDetails = null;
            operation.Execute = null;
            operation.Revalidate = null;
        }

        private static async Task CancelTokenAsync(Operation operation)
        {
            try
            { await operation.Cancellation.CancelAsync(); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                Trace.TraceError("An agent operation cancellation callback failed.");
            }
        }

        private static async Task CancelAndDrainAsync(Operation[] operations)
        {
            foreach (var operation in operations)
            {
                await CancelTokenAsync(operation);
            }
            await Task.WhenAll(operations.Select(operation => operation.Worker));
        }

        private SessionData GetSessionData(AgentSession session)
        {
            if (session == null || !_sessions.TryGetValue(session, out var data))
            {
                throw new AgentAccessException(AgentAccessError.SessionClosed);
            }
            return data;
        }

        private SessionData DemandConnected(AgentSession session)
        {
            var data = GetSessionData(session);
            if (data.State != AgentSessionState.Connected || !_accepting || _disposed)
            {
                throw new AgentAccessException(AgentAccessError.SessionClosed);
            }
            if (data.PermissionsUpdating)
            {
                throw new AgentAccessException(AgentAccessError.PermissionsUpdating);
            }
            return data;
        }

        private Operation GetOperationData(AgentSession session, Guid operationId)
        {
            var data = GetSessionData(session);
            return data.Operations.TryGetValue(operationId, out var operation)
                ? operation : throw new AgentAccessException(AgentAccessError.OperationNotFound);
        }

        private static bool HasPermissions(SessionData data, AgentOperationDescriptor descriptor) =>
            descriptor.Requirements.All(data.Permissions.Contains);

        private static AgentSessionSnapshot Snapshot(SessionData data) => new(
            data.Session.Id, data.Name, data.State, data.PermissionsUpdating,
            Array.AsReadOnly(data.Permissions.ToArray()), data.Limits);

        private static AgentOperationSnapshot Snapshot(Operation operation) => new(
            operation.Id, operation.Session.Session.Id, operation.Descriptor.Action,
            operation.State, operation.Error, operation.Descriptor.Effects, operation.Descriptor.Requirements,
            operation.CreatedAt, operation.CompletedAt, operation.ExecutionStarted,
            operation.ExecutionStarted && operation.Descriptor.Effects != AgentOperationEffects.None,
            operation.Result.HasValue);

        private void NotifyChanged()
        {
            foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try
                { ((EventHandler)handler)(this, EventArgs.Empty); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    Trace.TraceError("An agent runtime observer failed.");
                }
            }
        }

        private sealed class SessionData(AgentSession session, string name)
        {
            internal AgentSession Session { get; } = session;
            internal string Name { get; set; } = name;
            internal AgentSessionState State { get; set; }
            internal HashSet<AgentPermission> Permissions { get; set; } = new();
            internal AgentLimits Limits { get; set; } = new();
            internal bool PermissionsUpdating { get; set; }
            internal Dictionary<Guid, Operation> Operations { get; } = new();
            internal Dictionary<string, Operation> Idempotency { get; } = new(StringComparer.Ordinal);
            internal Task Ending { get; set; }
        }

        private sealed class Operation(
            SessionData session,
            AgentOperationDescriptor descriptor,
            Func<AgentExecutionContext, Task<JsonElement>> execute,
            Func<CancellationToken, Task<bool>> revalidate)
        {
            internal Guid Id { get; } = Guid.NewGuid();
            internal SessionData Session { get; } = session;
            internal AgentOperationDescriptor Descriptor { get; } = descriptor.WithoutApprovalDetails();
            internal string ApprovalDetails { get; set; } = descriptor.ApprovalDetails;
            internal Func<AgentExecutionContext, Task<JsonElement>> Execute { get; set; } = execute;
            internal Func<CancellationToken, Task<bool>> Revalidate { get; set; } = revalidate;
            internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
            internal DateTimeOffset? CompletedAt { get; set; }
            internal CancellationTokenSource Cancellation { get; } = new();
            internal Task Worker { get; set; } = Task.CompletedTask;
            internal AgentOperationState State { get; set; } = AgentOperationState.AwaitingApproval;
            internal AgentAccessError? Error { get; set; }
            internal bool ExecutionStarted { get; set; }
            internal JsonElement? Result { get; set; }
        }

        private sealed class OwnedResource(
            AgentResourceKey key,
            AgentSession session,
            string revision,
            AgentOperationDescriptor descriptor,
            Func<Task<AgentCleanupOutcome>> cleanup)
        {
            internal AgentResourceKey Key { get; } = key;
            internal AgentSession Session { get; } = session;
            internal string Revision { get; } = revision;
            internal AgentOperationDescriptor Descriptor { get; } = descriptor;
            internal Func<Task<AgentCleanupOutcome>> Cleanup { get; } = cleanup;
            internal AgentResourceState State { get; set; }
            internal Task Cleaning { get; set; }
        }
    }
}
