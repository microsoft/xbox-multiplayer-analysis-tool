// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.AgentAccess
{
    internal sealed class AgentCaptureApplication(
        AgentRuntime runtime,
        CaptureCatalog catalog,
        ICaptureOperations operations,
        Func<bool> accepting,
        ICaptureRecoveryOperations recoveryOperations = null)
    {
        private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
        private static readonly AgentPermission SummaryRead =
            new(AgentCapability.SummaryRead, AgentScope.Application);

        internal async Task<JsonElement> ListCapturesAsync(
            AgentSession session,
            string cursor,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, SummaryRead);
            var captures = await catalog.GetCapturesAsync(cancellationToken);
            var fingerprint = AgentOperationDescriptor.Hash(captures.Select(capture => new { capture.Id, capture.Revision }));
            var offset = 0;
            if (cursor != null)
            {
                var position = ReadCursor(cursor);
                if (position.Session != session.Id)
                {
                    throw new AgentAccessException(AgentAccessError.OperationNotFound);
                }
                if (position.Fingerprint != fingerprint)
                {
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                }
                offset = position.Offset;
            }
            if (offset < 0 || offset > captures.Count)
            {
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }

            var limits = runtime.GetSession(session).Limits;
            var rows = new List<CaptureDescriptor>();
            string next = null;
            JsonElement page = default;
            do
            {
                if (offset + rows.Count < captures.Count && rows.Count < limits.ResponseRows)
                {
                    var item = captures[offset + rows.Count];
                    var name = item.DeviceName ?? string.Empty;
                    if (name.Length > 256)
                    {
                        var length = char.IsHighSurrogate(name[255]) ? 255 : 256;
                        item = item with { DeviceName = name[..length], NameIsTruncated = true };
                    }
                    rows.Add(item);
                }
                var more = offset + rows.Count < captures.Count;
                next = more ? MakeCursor(new Cursor(session.Id, fingerprint, offset + rows.Count)) : null;
                var candidate = AgentJson.Encode(new
                {
                    captures = rows,
                    totalCount = captures.Count,
                    hasMore = more,
                    nextCursor = next
                });
                if (Encoding.UTF8.GetByteCount(candidate.GetRawText()) > limits.ResponseBytes - 256)
                {
                    if (rows.Count <= 1)
                    {
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    }
                    rows.RemoveAt(rows.Count - 1);
                    next = MakeCursor(new Cursor(session.Id, fingerprint, offset + rows.Count));
                    page = AgentJson.Encode(new
                    {
                        captures = rows,
                        totalCount = captures.Count,
                        hasMore = true,
                        nextCursor = next
                    });
                    break;
                }
                page = candidate;
                if (!more || rows.Count == limits.ResponseRows)
                {
                    break;
                }
            } while (true);

            DemandAccepting();
            runtime.Demand(session, SummaryRead);
            return page;
        }

        internal async Task<Guid> SetRunningAsync(
            AgentSession session,
            Guid captureId,
            long expectedRevision,
            bool running,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            DemandAccepting();
            var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId));
            runtime.Demand(session, control);
            var action = running ? "capture.start" : "capture.stop";
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision, running });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
            {
                return replay.Value;
            }
            Plan plan;
            try
            {
                plan = await catalog.WithTargetAsync(captureId, expectedRevision, async (target, snapshot) =>
            {
                if (target.CaptureType is not (CaptureType.WebProxy or CaptureType.NetworkTrace))
                {
                    throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
                }
                var availability = await operations.GetAvailabilityAsync(target, running);
                if (availability != CaptureOperationFailure.None)
                {
                    throw new AgentAccessException(AvailabilityError(availability));
                }
                var requirements = new List<AgentPermission> { control };
                runtime.ForgetSupersededCapture(captureId, snapshot.CaptureEpoch);
                var effects = AgentOperationEffects.None;
                var scriptDetails = string.Empty;
                if (snapshot.IsRunning != running)
                {
                    if (running)
                    {
                        runtime.EnsureOwnershipAvailable(session,
                            new AgentResourceKey(AgentResourceKind.Capture, captureId));
                    }
                    effects = AgentOperationEffects.StateChange;
                    if (target.CaptureType == CaptureType.NetworkTrace && running)
                        effects |= AgentOperationEffects.DataDeletion;
                    if (target.CaptureType == CaptureType.WebProxy &&
                        (target.DeviceType == DeviceType.LocalPC || !running))
                    {
                        effects |= AgentOperationEffects.NetworkConfiguration;
                    }
                    if (!running && (snapshot.RecoveryRequired || !runtime.IsOwned(session,
                        new AgentResourceKey(AgentResourceKind.Capture, captureId),
                        snapshot.CaptureEpoch.ToString(CultureInfo.InvariantCulture))))
                    {
                        effects |= AgentOperationEffects.HumanOwnedResource;
                    }
                    if (running && target.CaptureController is WebServiceDeviceCaptureController proxy &&
                        proxy.Scripts.HasActiveScripts)
                    {
                        requirements.Add(new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(captureId)));
                        effects |= AgentOperationEffects.TrustedCodeExecution;
                        scriptDetails = string.Join("\n\n", proxy.Scripts.ActiveScripts.Select(script =>
                            $"{script.DisplayName}\n{script.Script}"));
                    }
                }
                var details = target.CaptureType == CaptureType.NetworkTrace
                    ? Localization.GetLocalizedString("AGENT_TRACE_APPROVAL", snapshot.DeviceName, running,
                        expectedRevision, effects, snapshot.TraceRecoveryPhase, snapshot.TraceMayBeRunning, snapshot.PacketCount ?? 0)
                    :
                    Localization.GetLocalizedString("AGENT_CAPTURE_APPROVAL", snapshot.DeviceName,
                        running ? Localization.GetLocalizedString("AGENT_START") : Localization.GetLocalizedString("AGENT_STOP"),
                        snapshot.ProxyPort?.ToString(CultureInfo.InvariantCulture) ?? "-",
                        expectedRevision, effects, scriptDetails);
                return new Plan(requirements.ToArray(), effects, details);
            });
            }
            catch (AgentAccessException exception) when (exception.Error is
                AgentAccessError.SourceChanged or AgentAccessError.OperationNotFound)
            {
                replay = runtime.FindReplay(session, idempotencyKey, action, hash);
                if (replay.HasValue)
                {
                    return replay.Value;
                }
                throw;
            }
            runtime.Demand(session, plan.Requirements);
            DemandAccepting();
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor(
                action, plan.Requirements, plan.Effects, hash, plan.Details);

            return runtime.Submit(session, descriptor, idempotencyKey, context =>
                catalog.WithTargetAsync(captureId, expectedRevision, async (target, snapshot) =>
                {
                    DemandAccepting();
                    runtime.Demand(session, plan.Requirements);
                    if (running && !snapshot.IsRunning)
                    {
                        runtime.EnsureOwnershipAvailable(session,
                            new AgentResourceKey(AgentResourceKind.Capture, captureId));
                    }
                    CaptureOperationResult result;
                    try
                    {
                        result = await operations.SetRunningAsync(
                            target, running, cancellationToken: context.CancellationToken);
                    }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                    {
                        var failure = CaptureTransitionFailure.Get(exception);
                        if (running && failure?.IsRunning == true)
                        {
                            Own(context, captureId, failure.CaptureEpoch);
                        }
                        throw new AgentAccessException(failure != null && (!running || failure.IsRunning)
                            ? AgentAccessError.RecoveryRequired : exception is UnauthorizedAccessException
                                ? AgentAccessError.ElevationRequired : AgentAccessError.ExecutionFailed);
                    }
                    if (!result.Succeeded)
                    {
                        throw new AgentAccessException(AvailabilityError(result.Failure));
                    }
                    if (result.Changed && running)
                    {
                        if (!result.CaptureEpoch.HasValue)
                        {
                            throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                        }
                        Own(context, captureId, result.CaptureEpoch.Value);
                    }
                    return AgentJson.Encode(result);
                }),
                async cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await catalog.WithTargetAsync(captureId, expectedRevision, (_, _) => Task.FromResult(true));
                    }
                    catch (AgentAccessException exception) when (exception.Error is
                        AgentAccessError.SourceChanged or AgentAccessError.OperationNotFound)
                    {
                        return false;
                    }
                });
        }

        internal async Task<JsonElement> AvailabilityAsync(
            AgentSession session, Guid captureId, bool running, CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, SummaryRead);
            var result = await catalog.WithTargetAsync(captureId, null, async (target, snapshot) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = await operations.GetAvailabilityAsync(target, running, cancellationToken);
                return AgentJson.Encode(new
                {
                    captureId,
                    snapshot.Revision,
                    snapshot.CaptureType,
                    snapshot.DeviceType,
                    snapshot.IsRunning,
                    requestedRunning = running,
                    available = failure == CaptureOperationFailure.None,
                    failure,
                    clearsPacketData = running && !snapshot.IsRunning && snapshot.CaptureType == CaptureType.NetworkTrace,
                    snapshot.RecoveryRequired,
                    snapshot.TraceRecoveryPhase,
                    snapshot.TraceMayBeRunning,
                    snapshot.PacketCount,
                    snapshot.LocalTraceSession,
                    canControl = runtime.GetSession(session).Permissions.Contains(
                        new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId)))
                });
            });
            runtime.Demand(session, SummaryRead);
            DemandAccepting();
            return result;
        }

        internal async Task<Guid> AcknowledgeRecoveryAsync(
            AgentSession session, Guid captureId, long expectedRevision, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId));
            runtime.Demand(session, control);
            if (recoveryOperations == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision });
            var replay = runtime.FindReplay(session, idempotencyKey, "capture.ackRecovery", hash);
            if (replay.HasValue)
                return replay.Value;
            var details = await catalog.WithTargetAsync(captureId, expectedRevision, (target, snapshot) =>
            {
                if (target.IsReadOnly || target.CaptureController is not NetworkTraceCaptureController { RecoveryRequired: true, IsTransitioning: false })
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                return Task.FromResult(Localization.GetLocalizedString("AGENT_TRACE_ACK_APPROVAL", snapshot.DeviceName,
                    snapshot.TraceRecoveryPhase, snapshot.TraceMayBeRunning, snapshot.LocalTraceSession ?? "-"));
            });
            var descriptor = new AgentOperationDescriptor("capture.ackRecovery", [control],
                AgentOperationEffects.StateChange | AgentOperationEffects.DataDeletion | AgentOperationEffects.HumanOwnedResource,
                hash, details);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, context =>
                catalog.WithTargetAsync(captureId, expectedRevision, async (target, snapshot) =>
                {
                    DemandAccepting();
                    runtime.Demand(session, control);
                    var result = await recoveryOperations.AcknowledgeExternalTraceRecoveryAsync(target, context.CancellationToken);
                    if (!result.Acknowledged)
                        throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                    if (result.CaptureEpoch.HasValue)
                        runtime.ForgetSupersededCapture(captureId, result.CaptureEpoch.Value);
                    return AgentJson.Encode(result);
                }), async token =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        return await catalog.WithTargetAsync(captureId, expectedRevision, (target, snapshot) =>
                            Task.FromResult(target.CaptureController is NetworkTraceCaptureController { RecoveryRequired: true, IsTransitioning: false }));
                    }
                    catch (AgentAccessException) { return false; }
                });
        }

        private static AgentAccessError AvailabilityError(CaptureOperationFailure failure) => failure switch
        {
            CaptureOperationFailure.ElevationRequired => AgentAccessError.ElevationRequired,
            CaptureOperationFailure.GdkRequired => AgentAccessError.GdkRequired,
            CaptureOperationFailure.RecoveryRequired => AgentAccessError.RecoveryRequired,
            CaptureOperationFailure.UnsupportedCapture => AgentAccessError.UnsupportedOperation,
            CaptureOperationFailure.TargetNotFound => AgentAccessError.OperationNotFound,
            _ => AgentAccessError.OperationUnavailable
        };

        internal void Own(AgentExecutionContext context, Guid captureId, long epoch) =>
            context.OwnResource(new AgentResourceKey(AgentResourceKind.Capture, captureId),
                epoch.ToString(CultureInfo.InvariantCulture), () => CleanupAsync(captureId, epoch));

        private async Task<AgentCleanupOutcome> CleanupAsync(Guid captureId, long epoch)
        {
            try
            {
                return await catalog.WithTargetAsync(captureId, null, async (target, snapshot) =>
                {
                    if (snapshot.RecoveryRequired)
                    {
                        return AgentCleanupOutcome.RecoveryRequired;
                    }
                    if (snapshot.CaptureEpoch != epoch || !snapshot.IsRunning)
                    {
                        return AgentCleanupOutcome.NoLongerOwned;
                    }
                    if (target.DeviceType != DeviceType.LocalPC)
                    {
                        return AgentCleanupOutcome.RecoveryRequired;
                    }
                    var result = await operations.SetRunningAsync(target, false);
                    return result.Succeeded ? AgentCleanupOutcome.Released : AgentCleanupOutcome.RecoveryRequired;
                });
            }
            catch (AgentAccessException exception) when (exception.Error == AgentAccessError.OperationNotFound)
            {
                return AgentCleanupOutcome.NoLongerOwned;
            }
        }

        private void DemandAccepting()
        {
            if (!accepting())
            {
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            }
        }

        private string MakeCursor(Cursor cursor)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(cursor);
            var signature = HMACSHA256.HashData(_cursorKey, payload);
            return Convert.ToBase64String(payload.Concat(signature).ToArray());
        }

        private Cursor ReadCursor(string value)
        {
            try
            {
                if (value.Length > 512)
                {
                    throw new FormatException();
                }
                var bytes = Convert.FromBase64String(value);
                if (bytes.Length <= 32 ||
                    !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(bytes.Length - 32),
                        HMACSHA256.HashData(_cursorKey, bytes.AsSpan(0, bytes.Length - 32))))
                {
                    throw new FormatException();
                }
                return JsonSerializer.Deserialize<Cursor>(bytes.AsSpan(0, bytes.Length - 32))
                    ?? throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
        }

        private sealed record Cursor(Guid Session, string Fingerprint, int Offset);
        private sealed record Plan(AgentPermission[] Requirements, AgentOperationEffects Effects, string Details);
    }
}
