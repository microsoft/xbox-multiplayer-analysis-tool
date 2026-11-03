// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureProxyOperations : ICaptureProxyOperations
    {
        private readonly CaptureAppModel _model;
        private readonly Func<bool> _gdkInstalled;
        private readonly Func<IEnumerable<IPAddress>> _addresses;
        private readonly Func<DeviceType, string, Task<string>> _resolveTarget;
        private readonly Func<Task> _save;
        private readonly TimeSpan _timeout;
        private readonly Func<CancellationToken, Task> _delay;

        internal CaptureProxyOperations(
            CaptureAppModel model,
            Func<bool> gdkInstalled = null,
            Func<IEnumerable<IPAddress>> addresses = null,
            Func<DeviceType, string, Task<string>> resolveTarget = null,
            Func<Task> save = null,
            TimeSpan? timeout = null,
            Func<CancellationToken, Task> delay = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _gdkInstalled = gdkInstalled ?? (() => GDKXHelper.IsGDKXInstalled(false));
            _addresses = addresses ?? PublicUtilities.GetMyIp4Addresses;
            _resolveTarget = resolveTarget ?? ProxyDeviceOperations.ResolveTargetAsync;
            _save = save ?? (() => CaptureAppSettings.SerializeAsync(model));
            _timeout = timeout ?? TimeSpan.FromSeconds(60);
            _delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(2), token));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
        }

        public Task<IReadOnlyList<string>> GetSourceAddressesAsync(
            Guid captureId, CancellationToken cancellationToken = default) =>
            WithTargetAsync(captureId, null, (target, _) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DemandProxy(target);
                return Task.FromResult<IReadOnlyList<string>>(SourceAddresses(target).ToArray());
            });

        private IEnumerable<string> SourceAddresses(ICaptureDeviceContext target) =>
            target.DeviceType == DeviceType.LocalPC ? ["127.0.0.1"] :
                _addresses().Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address)).Select(address => address.ToString()).Distinct();

        public Task<CaptureProxyPreflight> PrepareAsync(
            Guid captureId, long expectedRevision, CaptureProxyAction action, string sourceAddress = null,
            CancellationToken cancellationToken = default) =>
            WithTargetAsync<CaptureProxyPreflight>(captureId, expectedRevision, async (target, capture) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proxy = DemandProxy(target);
                DemandAction(proxy, action);
                if (target.DeviceType == DeviceType.XboxConsole && !_gdkInstalled())
                    throw new CaptureProxyException(CaptureProxyFailure.GdkRequired);
                if (action == CaptureProxyAction.Disable)
                {
                    if (sourceAddress != null)
                        throw new CaptureProxyException(CaptureProxyFailure.InvalidRequest);
                }
                else
                {
                    sourceAddress ??= target.DeviceType == DeviceType.LocalPC ? "127.0.0.1" : null;
                    if (!IPAddress.TryParse(sourceAddress, out var address) ||
                        !SourceAddresses(target).Contains(address.ToString()))
                        throw new CaptureProxyException(CaptureProxyFailure.NoLocalAddress);
                    sourceAddress = address.ToString();
                }
                var resolved = await _resolveTarget(target.DeviceType, target.DeviceName).WaitAsync(_timeout, cancellationToken);
                var configuration = target.DeviceType == DeviceType.GenericProxyDevice
                    ? null : await ReadAsync(proxy.CreateDeviceConfiguration(resolved), cancellationToken);
                DemandRevision(target, expectedRevision);
                var state = State(configuration, target.DeviceType);
                if (action != CaptureProxyAction.Disable &&
                    state is CaptureDeviceProxyState.Unknown or CaptureDeviceProxyState.Unreachable)
                    throw new CaptureProxyException(CaptureProxyFailure.ConfigurationUnknown);
                var owned = configuration?.Endpoint != null && proxy.OwnsEndpoint(configuration.Endpoint);
                var port = action == CaptureProxyAction.ApplyPendingPort ? proxy.PendingPort : proxy.Port;
                var desired = sourceAddress == null ? null : new ProxyEndpoint(sourceAddress, port);
                if (action != CaptureProxyAction.Disable && state == CaptureDeviceProxyState.Enabled &&
                    !configuration.Endpoint.Matches(desired) &&
                    (action != CaptureProxyAction.ApplyPendingPort || !owned))
                    throw new CaptureProxyException(CaptureProxyFailure.ForeignEndpoint);
                return new(captureId, expectedRevision, action, target.DeviceType, resolved, sourceAddress,
                    state, configuration?.Endpoint == null ? null :
                        new(configuration.Endpoint.Host, configuration.Endpoint.Port),
                    owned, target.DeviceType == DeviceType.XboxConsole &&
                        (action == CaptureProxyAction.Disable ? state != CaptureDeviceProxyState.Disabled :
                            configuration?.Endpoint?.Matches(desired) != true),
                    target.DeviceType == DeviceType.GenericProxyDevice, !proxy.IsRunning &&
                        action != CaptureProxyAction.Disable, proxy.Port, proxy.PendingPort);
            });

        public async Task<CaptureProxyResult> ExecuteAsync(
            CaptureProxyPreflight preflight, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(preflight);
            var current = await PrepareAsync(preflight.CaptureId, preflight.ExpectedRevision,
                preflight.Action, preflight.SourceAddress, cancellationToken);
            if (current != preflight)
                throw new CaptureProxyException(CaptureProxyFailure.SourceChanged);
            return await WithTargetAsync(preflight.CaptureId, preflight.ExpectedRevision,
                (target, _) => ExecuteCoreAsync(target, preflight, cancellationToken));
        }

        private async Task<CaptureProxyResult> ExecuteCoreAsync(
            ICaptureDeviceContext target, CaptureProxyPreflight plan, CancellationToken token)
        {
            var proxy = DemandProxy(target);
            DemandAction(proxy, plan.Action);
            using var transition = proxy.BeginDeviceTransition();
            using var targetLease = ProxyDeviceOperations.AcquireTarget(plan.TargetAddress);
            var progress = new Progress();
            var device = plan.RequiresManualSetup ? null :
                new RecordingDevice(proxy.CreateDeviceConfiguration(plan.TargetAddress), progress);
            try
            {
                token.ThrowIfCancellationRequested();
                if (device != null && !Matches(plan, await ReadAsync(device, token)))
                    throw new CaptureProxyException(CaptureProxyFailure.SourceChanged);
                if (plan.Action == CaptureProxyAction.ApplyPendingPort)
                {
                    progress.Phase = CaptureProxyPhase.StartingListener;
                    await proxy.ApplyPreparedPortChangeAsync(plan.SourceAddress,
                        async () =>
                        {
                            progress.Phase = CaptureProxyPhase.SavingPort;
                            await SaveAsync();
                            progress.Persisted = true;
                        }, AcquireDataLease, device, configuration => Matches(plan, configuration),
                        () => progress.PortCommitted = true, token);
                    progress.Changed = true;
                    progress.Confirmed = !plan.RequiresManualSetup;
                }
                else if (plan.Action == CaptureProxyAction.Enable)
                {
                    if (!proxy.IsRunning)
                    {
                        progress.Phase = CaptureProxyPhase.StartingListener;
                        await CaptureStepAsync(() => proxy.StartProxyAsync(false));
                        progress.Changed = true;
                    }
                    if (!plan.RequiresManualSetup)
                    {
                        var desired = new ProxyEndpoint(plan.SourceAddress, proxy.Port);
                        if (plan.Configuration != CaptureDeviceProxyState.Enabled || plan.Endpoint == null ||
                            !desired.Matches(new(plan.Endpoint.Host, plan.Endpoint.Port)))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!Matches(plan, await ReadAsync(device, token)))
                                throw new CaptureProxyException(CaptureProxyFailure.SourceChanged);
                            progress.Phase = CaptureProxyPhase.EnablingDevice;
                            if (plan.StartsListener)
                            {
                                await proxy.EnableDeviceProxyingOrRollBackAsync(async () =>
                                {
                                    await device.EnableAsync(desired);
                                    return true;
                                });
                            }
                            else
                                await device.EnableAsync(desired);
                            progress.Phase = CaptureProxyPhase.WaitingForEnabled;
                            await WaitAsync(device, configuration => configuration.State == ProxyConfigurationState.Enabled &&
                                configuration.Endpoint?.Matches(desired) == true, token);
                        }
                        progress.Confirmed = true;
                    }
                    proxy.SetConfiguredProxyHost(plan.SourceAddress);
                    await SaveAsync();
                    progress.Persisted = true;
                }
                else
                {
                    if (!plan.RequiresManualSetup)
                    {
                        var previous = new ProxyEndpoint(plan.DeviceType == DeviceType.LocalPC ? "127.0.0.1" :
                            plan.Endpoint?.Host ?? proxy.ProxyHost ?? "127.0.0.1", proxy.Port);
                        if (plan.DeviceType == DeviceType.LocalPC || plan.Configuration != CaptureDeviceProxyState.Disabled)
                        {
                            progress.Phase = CaptureProxyPhase.DisablingDevice;
                            await device.DisableAsync(previous);
                            progress.Phase = CaptureProxyPhase.WaitingForDisabled;
                            await WaitAsync(device, configuration => device.IsDisableComplete(configuration, previous), token);
                        }
                        progress.Confirmed = true;
                    }
                    if (proxy.IsRunning)
                    {
                        progress.Phase = CaptureProxyPhase.StoppingListener;
                        await CaptureStepAsync(() => proxy.StopProxyAsync());
                        progress.Changed = true;
                    }
                    progress.Persisted = true;
                }
                _model.CaptureCatalog.SetRecoveryRequired(target, false);
                proxy.SetProxyOperationStatus(plan.RequiresManualSetup ? "PROXY_PORT_MANUAL" :
                    plan.Action == CaptureProxyAction.Disable ? "PROXY_DISABLE_CONFIRMED" : "PROXY_PORT_APPLIED");
                progress.Phase = CaptureProxyPhase.Complete;
                return Result(target, plan, progress, CaptureProxyFailure.None);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                var recovery = progress.WriteRequested || progress.PortCommitted || proxy.IsRunning;
                _model.CaptureCatalog.SetRecoveryRequired(target, recovery);
                proxy.SetProxyOperationStatus(recovery ? "PROXY_ENABLE_LISTENER_KEPT" : "PROXY_ENABLE_FAILED");
                var failure = exception is CaptureProxyException proxyException ? proxyException.Failure :
                    exception is TimeoutException ? CaptureProxyFailure.ConfigurationTimeout :
                    exception is OperationCanceledException && !recovery ? CaptureProxyFailure.ExecutionFailed :
                    recovery ? CaptureProxyFailure.RecoveryRequired : CaptureProxyFailure.ExecutionFailed;
                return Result(target, plan, progress, failure);
            }
        }

        private CaptureProxyResult Result(
            ICaptureDeviceContext target, CaptureProxyPreflight plan, Progress progress, CaptureProxyFailure failure)
        {
            var proxy = (WebServiceDeviceCaptureController)target.CaptureController;
            var capture = _model.CaptureCatalog.DescribeTarget(target);
            return new(capture.Id, plan.Action, progress.Changed || progress.WriteRequested || progress.PortCommitted,
                proxy.IsRunning, proxy.Port, proxy.PendingPort, capture.Revision, capture.CaptureEpoch,
                progress.Confirmed, progress.WriteRequested, progress.PortCommitted, progress.Persisted,
                plan.RequiresManualSetup, progress.Phase, failure);
        }

        private async Task SaveAsync()
        {
            using var lease = AcquireDataLease();
            try
            { await _save(); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { throw new CaptureProxyException(CaptureProxyFailure.PersistenceFailed); }
        }

        private async Task CaptureStepAsync(Func<Task> step)
        {
            using var lease = AcquireDataLease();
            await step();
        }

        private IDisposable AcquireDataLease() => _model.TryBeginCaptureDataOperation(out var lease)
            ? lease : throw new CaptureProxyException(CaptureProxyFailure.CaptureBusy);

        private WebServiceDeviceCaptureController DemandProxy(ICaptureDeviceContext target)
        {
            if (target.IsReadOnly)
                throw new CaptureProxyException(CaptureProxyFailure.ReadOnlyCapture);
            if (target.CaptureController is not WebServiceDeviceCaptureController proxy ||
                target.DeviceType is not (DeviceType.LocalPC or DeviceType.XboxConsole or DeviceType.GenericProxyDevice))
                throw new CaptureProxyException(CaptureProxyFailure.UnsupportedCapture);
            return proxy;
        }

        private void DemandAction(WebServiceDeviceCaptureController proxy, CaptureProxyAction action)
        {
            if (!Enum.IsDefined(action))
                throw new CaptureProxyException(CaptureProxyFailure.InvalidRequest);
            if (proxy.IsTransitioning || !_model.CanModifyCaptureData)
                throw new CaptureProxyException(CaptureProxyFailure.CaptureBusy);
            if (action == CaptureProxyAction.ApplyPendingPort &&
                (proxy.IsRunning || !proxy.HasPendingPortReservation))
                throw new CaptureProxyException(CaptureProxyFailure.NoPendingPort);
            if (action == CaptureProxyAction.Enable && proxy.HasPendingPort)
                throw new CaptureProxyException(CaptureProxyFailure.PendingPortChange);
            if (action != CaptureProxyAction.Disable && (!proxy.IsPortValid || !proxy.HasPortReservation))
                throw new CaptureProxyException(CaptureProxyFailure.InvalidRequest);
        }

        private async Task<ProxyConfiguration> ReadAsync(IProxyDeviceConfiguration device, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var configuration = await device.ReadAsync().WaitAsync(_timeout, token);
            token.ThrowIfCancellationRequested();
            return configuration ?? new ProxyConfiguration(ProxyConfigurationState.Unknown);
        }

        private async Task WaitAsync(
            IProxyDeviceConfiguration device, Func<ProxyConfiguration, bool> predicate, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = _timeout - stopwatch.Elapsed;
                var current = await device.ReadAsync().WaitAsync(
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, token);
                token.ThrowIfCancellationRequested();
                if (current != null && predicate(current))
                    return;
                remaining = _timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException();
                await _delay(token).WaitAsync(remaining, token);
            }
        }

        private static CaptureDeviceProxyState State(ProxyConfiguration configuration, DeviceType type) =>
            type == DeviceType.GenericProxyDevice ? CaptureDeviceProxyState.Manual : configuration?.State switch
            {
                ProxyConfigurationState.Disabled => CaptureDeviceProxyState.Disabled,
                ProxyConfigurationState.Enabled when configuration.Endpoint != null => CaptureDeviceProxyState.Enabled,
                ProxyConfigurationState.Unreachable => CaptureDeviceProxyState.Unreachable,
                _ => CaptureDeviceProxyState.Unknown
            };

        private static bool Matches(CaptureProxyPreflight plan, ProxyConfiguration configuration)
        {
            if (State(configuration, plan.DeviceType) != plan.Configuration)
                return false;
            return plan.Endpoint == null ? configuration.Endpoint == null :
                new ProxyEndpoint(plan.Endpoint.Host, plan.Endpoint.Port).Matches(configuration.Endpoint);
        }

        private void DemandRevision(ICaptureDeviceContext target, long revision)
        {
            if (!_model.CaptureDeviceContexts.Contains(target) ||
                _model.CaptureCatalog.DescribeTarget(target).Revision != revision)
                throw new CaptureProxyException(CaptureProxyFailure.SourceChanged);
        }

        private async Task<T> WithTargetAsync<T>(
            Guid id, long? revision, Func<ICaptureDeviceContext, CaptureDescriptor, Task<T>> action)
        {
            try
            { return await _model.CaptureCatalog.WithTargetAsync(id, revision, action); }
            catch (AgentAccessException exception) when (exception.Error is
                AgentAccessError.SourceChanged or AgentAccessError.OperationNotFound)
            {
                throw new CaptureProxyException(exception.Error == AgentAccessError.SourceChanged
                    ? CaptureProxyFailure.SourceChanged : CaptureProxyFailure.TargetNotFound);
            }
        }

        private sealed class Progress
        {
            internal CaptureProxyPhase Phase { get; set; }
            internal bool Changed { get; set; }
            internal bool WriteRequested { get; set; }
            internal bool PortCommitted { get; set; }
            internal bool Confirmed { get; set; }
            internal bool Persisted { get; set; }
        }

        private sealed class RecordingDevice(IProxyDeviceConfiguration inner, Progress progress) : IProxyDeviceConfiguration
        {
            public Task<ProxyConfiguration> ReadAsync() => inner.ReadAsync();
            public Task DisableAsync() => DisableAsync(null);
            public async Task DisableAsync(ProxyEndpoint previous)
            {
                progress.WriteRequested = true;
                progress.Phase = CaptureProxyPhase.DisablingDevice;
                await inner.DisableAsync(previous);
            }
            public async Task EnableAsync(ProxyEndpoint endpoint)
            {
                progress.WriteRequested = true;
                progress.Phase = CaptureProxyPhase.EnablingDevice;
                try
                { await inner.EnableAsync(endpoint); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    throw new ProxyActivationUncertainException(
                        Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"), exception);
                }
            }
            public bool IsDisableComplete(ProxyConfiguration configuration, ProxyEndpoint previous) =>
                inner.IsDisableComplete(configuration, previous);
        }
    }
}
