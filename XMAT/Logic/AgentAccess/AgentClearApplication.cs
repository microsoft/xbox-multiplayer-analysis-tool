// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentClearApplication(
        AgentRuntime runtime, CaptureCatalog catalog, ICaptureDataOperations operations, Func<bool> accepting)
    {
        internal async Task<Guid> ClearAsync(
            AgentSession session, Guid captureId, long expectedRevision, string idempotencyKey, CancellationToken token)
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            var permission = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId));
            runtime.Demand(session, permission);
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision });
            var replay = runtime.FindReplay(session, idempotencyKey, "capture.clear", hash);
            if (replay.HasValue)
                return replay.Value;
            var capture = await catalog.WithTargetAsync(captureId, expectedRevision, (_, descriptor) => Task.FromResult(descriptor));
            if (capture.IsReadOnly || capture.IsRunning || capture.RecoveryRequired)
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            var descriptor = new AgentOperationDescriptor("capture.clear", [permission],
                AgentOperationEffects.StateChange | AgentOperationEffects.DataDeletion | AgentOperationEffects.HumanOwnedResource,
                hash, Localization.GetLocalizedString("ETL_CLEAR_APPROVAL", capture.DeviceName, capture.CaptureType, expectedRevision));
            token.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                if (!accepting())
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                runtime.Demand(session, permission);
                try
                { return AgentJson.Encode(await operations.ClearAsync(captureId, expectedRevision, context.CancellationToken)); }
                catch (QueryException exception)
                {
                    throw new AgentAccessException(exception.Error == QueryError.SourceChanged
                    ? AgentAccessError.SourceChanged : AgentAccessError.OperationUnavailable);
                }
            }, async cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    return await catalog.WithTargetAsync(captureId, expectedRevision,
                        (_, current) => Task.FromResult(!current.IsRunning && !current.IsReadOnly && !current.RecoveryRequired));
                }
                catch (AgentAccessException) { return false; }
            });
        }
    }
}
