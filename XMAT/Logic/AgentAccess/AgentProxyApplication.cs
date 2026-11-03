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
using XMAT.WebServiceCapture;

namespace XMAT.AgentAccess
{
    internal sealed class AgentProxyApplication(
        AgentRuntime runtime, CaptureCatalog catalog, ICaptureProxyOperations operations,
        AgentCaptureApplication captures, Func<bool> accepting)
    {
        private static readonly AgentPermission Summary =
            new(AgentCapability.SummaryRead, AgentScope.Application);

        internal async Task<JsonElement> OptionsAsync(
            AgentSession session, Guid captureId, int addressOffset, CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, Summary);
            if (addressOffset < 0)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var addresses = await TranslateAsync(() => operations.GetSourceAddressesAsync(captureId, cancellationToken));
            var capture = await catalog.WithTargetAsync(captureId, null, (_, snapshot) => Task.FromResult(snapshot));
            var limit = runtime.GetSession(session).Limits.ResponseRows;
            var page = addresses.Skip(addressOffset).Take(limit).ToArray();
            DemandAccepting();
            runtime.Demand(session, Summary);
            return AgentJson.Encode(new
            {
                capture,
                sourceAddresses = page,
                totalAddresses = addresses.Count,
                nextAddressOffset = addressOffset + page.Length < addresses.Count ? (int?)(addressOffset + page.Length) : null,
                addressFingerprint = AgentOperationDescriptor.Hash(addresses),
                manualSetup = capture.DeviceType == DeviceType.GenericProxyDevice,
                canConfigure = runtime.GetSession(session).Permissions.Contains(
                    new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(captureId))),
                configurationRequiresApproval = true
            });
        }

        internal async Task<Guid> SubmitAsync(
            AgentSession session, Guid captureId, long expectedRevision, CaptureProxyAction action,
            string sourceAddress, string idempotencyKey, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var configuration = new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(captureId));
            var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId));
            runtime.Demand(session, [configuration, control]);
            var name = action switch
            {
                CaptureProxyAction.Enable => "proxy.enable",
                CaptureProxyAction.Disable => "proxy.disable",
                _ => "proxy.apply_port"
            };
            var hash = AgentOperationDescriptor.Hash(new { captureId, expectedRevision, action, sourceAddress });
            var replay = runtime.FindReplay(session, idempotencyKey, name, hash);
            if (replay.HasValue)
                return replay.Value;
            var plan = await TranslateAsync(() => operations.PrepareAsync(
                captureId, expectedRevision, action, sourceAddress, cancellationToken));
            var policy = await catalog.WithTargetAsync(captureId, expectedRevision, (target, snapshot) =>
            {
                runtime.ForgetSupersededCapture(captureId, snapshot.CaptureEpoch);
                runtime.EnsureOwnershipAvailable(session, new(AgentResourceKind.Capture, captureId));
                var requirements = new[] { configuration, control };
                var effects = AgentOperationEffects.StateChange | AgentOperationEffects.NetworkConfiguration;
                var scriptDetails = string.Empty;
                if (plan.MayRestartConsole)
                    effects |= AgentOperationEffects.ConsoleRestart;
                if (plan.RequiresManualSetup || snapshot.RecoveryRequired ||
                    (!plan.StartsListener && !runtime.IsOwned(session, new(AgentResourceKind.Capture, captureId),
                        snapshot.CaptureEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                    effects |= AgentOperationEffects.HumanOwnedResource;
                if (action != CaptureProxyAction.Disable &&
                    target.CaptureController is WebServiceDeviceCaptureController proxy && proxy.Scripts.HasActiveScripts)
                {
                    requirements = [configuration, control,
                        new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(captureId))];
                    effects |= AgentOperationEffects.TrustedCodeExecution;
                    scriptDetails = string.Join("\n\n", proxy.Scripts.ActiveScripts.Select(script =>
                        $"{script.DisplayName}\n{script.Script}"));
                }
                return Task.FromResult((requirements, effects, scriptDetails));
            });
            runtime.Demand(session, policy.requirements);
            DemandAccepting();
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentOperationDescriptor(name, policy.requirements, policy.effects, hash,
                Localization.GetLocalizedString("AGENT_PROXY_APPROVAL", plan.TargetAddress, action,
                    plan.SourceAddress ?? "-", plan.Port, plan.PendingPort, plan.Configuration,
                    plan.Endpoint?.ToString() ?? "-", plan.MayRestartConsole, plan.RequiresManualSetup,
                    expectedRevision, policy.scriptDetails));
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, policy.requirements);
                await catalog.WithTargetAsync(captureId, expectedRevision, (_, snapshot) =>
                {
                    runtime.EnsureOwnershipAvailable(session, new(AgentResourceKind.Capture, captureId));
                    return Task.FromResult(true);
                });
                var result = await TranslateAsync(() => operations.ExecuteAsync(plan, context.CancellationToken));
                if (result.IsRunning && (plan.StartsListener || result.PortCommitted))
                    captures.Own(context, captureId, result.CaptureEpoch);
                if (!result.IsRunning && result.ConfigurationConfirmed)
                    runtime.ForgetSupersededCapture(captureId, result.CaptureEpoch);
                var encoded = AgentJson.Encode(result);
                if (!result.Succeeded)
                    throw new AgentAccessException(Error(result.Failure), encoded);
                return encoded;
            }, async token =>
            {
                try
                {
                    return await operations.PrepareAsync(captureId, expectedRevision, action, sourceAddress, token) == plan;
                }
                catch (CaptureProxyException)
                {
                    return false;
                }
            });
        }

        private static async Task<T> TranslateAsync<T>(Func<Task<T>> action)
        {
            try
            { return await action(); }
            catch (CaptureProxyException exception)
            { throw new AgentAccessException(Error(exception.Failure)); }
        }

        private static AgentAccessError Error(CaptureProxyFailure failure) => failure switch
        {
            CaptureProxyFailure.SourceChanged => AgentAccessError.SourceChanged,
            CaptureProxyFailure.TargetNotFound => AgentAccessError.OperationNotFound,
            CaptureProxyFailure.ReadOnlyCapture or CaptureProxyFailure.UnsupportedCapture => AgentAccessError.UnsupportedOperation,
            CaptureProxyFailure.InvalidRequest or CaptureProxyFailure.NoLocalAddress => AgentAccessError.InvalidRequest,
            CaptureProxyFailure.GdkRequired => AgentAccessError.GdkRequired,
            CaptureProxyFailure.PersistenceFailed => AgentAccessError.PersistenceFailed,
            CaptureProxyFailure.RecoveryRequired or CaptureProxyFailure.ConfigurationUnknown or
                CaptureProxyFailure.ForeignEndpoint or CaptureProxyFailure.ConfigurationTimeout => AgentAccessError.RecoveryRequired,
            _ => AgentAccessError.OperationUnavailable
        };

        private void DemandAccepting()
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
        }
    }
}
