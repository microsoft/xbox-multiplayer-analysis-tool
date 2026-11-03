// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentDeviceApplication(
        AgentRuntime runtime,
        ICaptureDevices devices,
        CaptureCatalog catalog,
        Func<bool> accepting)
    {
        private static readonly AgentPermission Summary =
            new(AgentCapability.SummaryRead, AgentScope.Application);
        private static readonly AgentPermission Creation =
            new(AgentCapability.DeviceCreation, AgentScope.Application);

        internal Task<JsonElement> DescribeAsync(AgentSession session)
        {
            DemandAccepting();
            runtime.Demand(session, Summary);
            var result = AgentJson.Encode(new
            {
                kinds = new
                {
                    LocalPC = new { captureTypes = new[] { CaptureType.WebProxy, CaptureType.NetworkTrace }, requiresGdk = false },
                    XboxConsole = new { captureTypes = new[] { CaptureType.WebProxy, CaptureType.NetworkTrace }, requiresGdk = true },
                    DefaultXboxConsole = new { captureTypes = new[] { CaptureType.WebProxy, CaptureType.NetworkTrace }, requiresGdk = true },
                    GenericProxyDevice = new { captureTypes = new[] { CaptureType.WebProxy }, requiresGdk = false },
                    WindowsHandheldOrRemoteDevice = new { captureTypes = new[] { CaptureType.WebProxy }, requiresGdk = false }
                },
                canCreate = runtime.GetSession(session).Permissions.Contains(Creation),
                creationStartsCapture = false,
                creationConfiguresDevice = false,
                creationGrantsControl = false,
                pendingPortConfiguresDevice = false,
                requiresStoppedCaptureForRemoval = true
            });
            DemandAccepting();
            runtime.Demand(session, Summary);
            return Task.FromResult(result);
        }

        internal async Task<JsonElement> GetDeviceAsync(
            AgentSession session, Guid captureId, bool inspectProxy, CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, Summary);
            var state = await TranslateAsync(() => devices.GetDeviceAsync(captureId, inspectProxy, cancellationToken));
            DemandAccepting();
            runtime.Demand(session, Summary);
            return AgentJson.Encode(state);
        }

        internal async Task<Guid> AddAsync(
            AgentSession session, CaptureDeviceRequest request, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, Creation);
            const string action = "device.add";
            var hash = AgentOperationDescriptor.Hash(request);
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return replay.Value;
            var creation = await TranslateAsync(() => devices.PrepareAddAsync(request, cancellationToken));
            DemandAccepting();
            runtime.Demand(session, Creation);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor(action, [Creation],
                AgentOperationEffects.StateChange, hash,
                Localization.GetLocalizedString("AGENT_DEVICE_ADD_APPROVAL",
                    creation.DeviceName, request.Kind, request.CaptureType, creation.ExistingCaptureId?.ToString() ?? "-"));
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, Creation);
                return Result(await TranslateAsync(() => devices.AddAsync(creation, context.CancellationToken)));
            }, async token =>
            {
                try
                {
                    return await devices.PrepareAddAsync(request, token) == creation;
                }
                catch (CaptureDeviceException)
                {
                    return false;
                }
            });
        }

        internal async Task<Guid> RemoveAsync(
            AgentSession session, Guid captureId, long expectedRevision, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var configuration = Configuration(captureId);
            var navigation = new AgentPermission(AgentCapability.Navigation, AgentScope.Application);
            var permission = runtime.GetSession(session).Permissions.Contains(configuration) ? configuration : navigation;
            runtime.Demand(session, permission);
            const string action = "device.remove";
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return replay.Value;
            var state = await TranslateAsync(() => devices.PrepareRemoveAsync(
                captureId, expectedRevision, cancellationToken));
            permission = state.Capture.IsReadOnly ? navigation : configuration;
            runtime.Demand(session, permission);
            if (state.RemovalFailure != CaptureDeviceFailure.None)
                throw new AgentAccessException(Error(state.RemovalFailure));
            EnsureOwnership(session, state.Capture);
            DemandAccepting();
            runtime.Demand(session, permission);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor(action, [permission],
                AgentOperationEffects.StateChange | AgentOperationEffects.DataDeletion | AgentOperationEffects.HumanOwnedResource,
                hash, Localization.GetLocalizedString("AGENT_DEVICE_REMOVE_APPROVAL",
                    state.Capture.DeviceName, state.Capture.CaptureType, expectedRevision, state.ProxyState));
            return runtime.Submit(session, descriptor, idempotencyKey, context =>
                catalog.WithTargetAsync(captureId, expectedRevision, async (_, capture) =>
                {
                    DemandAccepting();
                    runtime.Demand(session, permission);
                    EnsureOwnership(session, capture);
                    var result = await TranslateAsync(() => devices.RemoveAsync(
                        captureId, expectedRevision, context.CancellationToken));
                    if (result.Removed)
                        runtime.ForgetSupersededCapture(captureId, result.CaptureEpoch);
                    return Result(result);
                }), token => IsCurrentAsync(captureId, expectedRevision, token));
        }

        internal async Task<Guid> SetPendingPortAsync(
            AgentSession session, Guid captureId, long expectedRevision, ushort? port,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var permission = Configuration(captureId);
            runtime.Demand(session, permission);
            if (port == 0)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            const string action = "device.pending_port";
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision, port });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return replay.Value;
            var state = await catalog.WithTargetAsync(captureId, expectedRevision,
                (_, _) => TranslateAsync(() => devices.GetDeviceAsync(captureId, cancellationToken: cancellationToken)));
            if (state.PendingPortFailure != CaptureDeviceFailure.None)
                throw new AgentAccessException(Error(state.PendingPortFailure));
            EnsureOwnership(session, state.Capture);
            DemandAccepting();
            runtime.Demand(session, permission);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor(action, [permission],
                AgentOperationEffects.StateChange, hash,
                Localization.GetLocalizedString("AGENT_DEVICE_PORT_APPROVAL", state.Capture.DeviceName,
                    state.Capture.ProxyPort ?? 0, state.PendingProxyPort ?? 0, port?.ToString() ?? "-", expectedRevision));
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, permission);
                await catalog.WithTargetAsync(captureId, expectedRevision, (_, capture) =>
                {
                    EnsureOwnership(session, capture);
                    return Task.FromResult(true);
                });
                return Result(await TranslateAsync(() => devices.SetPendingProxyPortAsync(
                    captureId, expectedRevision, port, context.CancellationToken)));
            }, token => IsCurrentAsync(captureId, expectedRevision, token));
        }

        private void EnsureOwnership(AgentSession session, CaptureDescriptor capture)
        {
            runtime.ForgetSupersededCapture(capture.Id, capture.CaptureEpoch);
            runtime.EnsureOwnershipAvailable(session, new(AgentResourceKind.Capture, capture.Id));
        }

        private async Task<bool> IsCurrentAsync(Guid id, long revision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await catalog.WithTargetAsync(id, revision, (_, _) => Task.FromResult(true));
            }
            catch (AgentAccessException exception) when (exception.Error is
                AgentAccessError.SourceChanged or AgentAccessError.OperationNotFound)
            {
                return false;
            }
        }

        private static JsonElement Result(CaptureDeviceChange change)
        {
            var result = AgentJson.Encode(change);
            if (!change.Succeeded)
                throw new AgentAccessException(Error(change.Failure), result);
            return result;
        }

        private static async Task<T> TranslateAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action();
            }
            catch (CaptureDeviceException exception)
            {
                throw new AgentAccessException(Error(exception.Failure));
            }
        }

        private static AgentPermission Configuration(Guid id) =>
            new(AgentCapability.DeviceConfiguration, AgentScope.Capture(id));

        private static AgentAccessError Error(CaptureDeviceFailure failure) => failure switch
        {
            CaptureDeviceFailure.InvalidRequest => AgentAccessError.InvalidRequest,
            CaptureDeviceFailure.TargetNotFound => AgentAccessError.OperationNotFound,
            CaptureDeviceFailure.SourceChanged => AgentAccessError.SourceChanged,
            CaptureDeviceFailure.GdkRequired => AgentAccessError.GdkRequired,
            CaptureDeviceFailure.NoDefaultConsole => AgentAccessError.NoDefaultConsole,
            CaptureDeviceFailure.PersistenceFailed => AgentAccessError.PersistenceFailed,
            CaptureDeviceFailure.RecoveryRequired or CaptureDeviceFailure.ProxyStateUnknown or
                CaptureDeviceFailure.ProxyEndpointInUse => AgentAccessError.RecoveryRequired,
            CaptureDeviceFailure.UnsupportedCapture or CaptureDeviceFailure.ReadOnlyCapture => AgentAccessError.UnsupportedOperation,
            _ => AgentAccessError.OperationUnavailable
        };

        private void DemandAccepting()
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
        }
    }
}
