// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureDevices : ICaptureDevices
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly Func<DeviceType, string, CaptureType, ICaptureDeviceContext> _create;
        private readonly Func<bool> _gdkInstalled;
        private readonly Func<Task<string>> _defaultConsole;
        private readonly Func<Task> _save;
        private readonly TimeSpan _inspectionTimeout;

        internal CaptureDevices(
            CaptureAppModel model,
            Dispatcher dispatcher,
            Func<DeviceType, string, CaptureType, ICaptureDeviceContext> create = null,
            Func<bool> gdkInstalled = null,
            Func<Task<string>> defaultConsole = null,
            Func<Task> save = null,
            TimeSpan? inspectionTimeout = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _create = create ?? ((type, name, capture) => new CaptureDeviceContextModel(
                type, name, capture, false, null, hostListPolicy: model.HostListPolicy));
            _gdkInstalled = gdkInstalled ?? (() => GDKXHelper.IsGDKXInstalled(false));
            _defaultConsole = defaultConsole ?? (() => Task.Run(
                XboxClient.XboxClientConnection.GetDefaultConsoleAddress));
            _save = save ?? (() => CaptureAppSettings.SerializeAsync(model));
            _inspectionTimeout = inspectionTimeout ?? TimeSpan.FromSeconds(60);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_inspectionTimeout, TimeSpan.Zero);
        }

        public Task<CaptureDeviceCreation> PrepareAddAsync(
            CaptureDeviceRequest request, CancellationToken cancellationToken = default) =>
            InvokeAsync(() => PrepareAddCoreAsync(request, cancellationToken));

        private async Task<CaptureDeviceCreation> PrepareAddCoreAsync(
            CaptureDeviceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.CaptureType) ||
                (request.Kind != CaptureDeviceKind.XboxConsole && !string.IsNullOrEmpty(request.Address)))
                throw new CaptureDeviceException(CaptureDeviceFailure.InvalidRequest);
            if (request.CaptureType == CaptureType.NetworkTrace &&
                request.Kind is CaptureDeviceKind.GenericProxyDevice or CaptureDeviceKind.WindowsHandheldOrRemoteDevice)
                throw new CaptureDeviceException(CaptureDeviceFailure.UnsupportedCapture);
            DemandMutable();
            var type = request.Kind switch
            {
                CaptureDeviceKind.LocalPC => DeviceType.LocalPC,
                CaptureDeviceKind.XboxConsole or CaptureDeviceKind.DefaultXboxConsole => DeviceType.XboxConsole,
                _ => DeviceType.GenericProxyDevice
            };
            if (type == DeviceType.XboxConsole && !_gdkInstalled())
                throw new CaptureDeviceException(CaptureDeviceFailure.GdkRequired);
            var name = request.Kind switch
            {
                CaptureDeviceKind.LocalPC => Localization.GetLocalizedString("LOCAL_PC"),
                CaptureDeviceKind.GenericProxyDevice => Localization.GetLocalizedString("GENERIC_DEVICE"),
                CaptureDeviceKind.WindowsHandheldOrRemoteDevice =>
                    Localization.GetLocalizedString("WINDOWS_HANDHELD_REMOTE_DEVICE"),
                CaptureDeviceKind.DefaultXboxConsole => await _defaultConsole().WaitAsync(
                    _inspectionTimeout, cancellationToken),
                _ => request.Address
            };
            if (request.Kind == CaptureDeviceKind.DefaultXboxConsole && string.IsNullOrWhiteSpace(name))
                throw new CaptureDeviceException(CaptureDeviceFailure.NoDefaultConsole);
            if (type == DeviceType.XboxConsole)
            {
                if (!IsValidConsoleAddress(name))
                    throw new CaptureDeviceException(CaptureDeviceFailure.InvalidRequest);
                name = ProxyEndpoint.NormalizeHost(name).ToLowerInvariant();
            }
            cancellationToken.ThrowIfCancellationRequested();
            DemandMutable();
            var existing = _model.GetDeviceContext(type, name, request.CaptureType, false);
            return new(request, type, name, existing == null ? null : _model.CaptureCatalog.DescribeTarget(existing).Id);
        }

        internal static bool IsValidConsoleAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || address.Length > 255)
                return false;
            address = address.Trim().TrimEnd('.');
            var kind = Uri.CheckHostName(address);
            return kind == UriHostNameType.Dns ||
                (kind == UriHostNameType.IPv4 && address.Split('.').Length == 4 &&
                 IPAddress.TryParse(address, out _));
        }

        public Task<CaptureDeviceChange> AddAsync(
            CaptureDeviceCreation creation, CancellationToken cancellationToken = default) =>
            InvokeAsync(async () =>
            {
                ArgumentNullException.ThrowIfNull(creation);
                var current = await PrepareAddCoreAsync(creation.Request, cancellationToken);
                if (current != creation)
                    throw new CaptureDeviceException(CaptureDeviceFailure.SourceChanged);
                if (current.ExistingCaptureId.HasValue)
                    return await WithTargetAsync(current.ExistingCaptureId.Value, null,
                        (target, _) => Task.FromResult(Change(target, false, true)));
                if (!_model.TryBeginCaptureDataOperation(out var lease))
                    throw new CaptureDeviceException(CaptureDeviceFailure.CaptureBusy);
                using (lease)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = _model.AddDeviceContext(current.DeviceType, current.DeviceName,
                        current.Request.CaptureType, false, null,
                        createContext: () => _create(current.DeviceType, current.DeviceName, current.Request.CaptureType));
                    return await PersistChangeAsync(target);
                }
            });

        public Task<CaptureDeviceState> GetDeviceAsync(
            Guid captureId, bool inspectProxy = false, CancellationToken cancellationToken = default) =>
            WithTargetAsync(captureId, null,
                (target, _) => DescribeAsync(target, inspectProxy, cancellationToken));

        public Task<CaptureDeviceState> PrepareRemoveAsync(
            Guid captureId, long expectedRevision, CancellationToken cancellationToken = default) =>
            WithTargetAsync(captureId, expectedRevision, async (target, _) =>
            {
                var state = await DescribeAsync(target, true, cancellationToken, forRemoval: true);
                DemandRevision(target, expectedRevision);
                return state;
            });

        public Task<CaptureDeviceChange> RemoveAsync(
            Guid captureId, long expectedRevision, CancellationToken cancellationToken = default) =>
            WithTargetAsync(captureId, expectedRevision, async (target, _) =>
            {
                // External reads happen before taking the global data lease, so other devices can still stop.
                var state = await DescribeAsync(target, true, cancellationToken, forRemoval: true);
                DemandRevision(target, expectedRevision);
                if (state.RemovalFailure != CaptureDeviceFailure.None)
                    throw new CaptureDeviceException(state.RemovalFailure);
                return await RemoveCoreAsync(target, null, cancellationToken);
            });

        internal Task<CaptureDeviceChange> RemoveDesktopAsync(
            ICaptureDeviceContext target, Func<Task<bool>> confirmCloseAsync,
            CancellationToken cancellationToken = default) =>
            InvokeAsync(() => RemoveCoreAsync(target, confirmCloseAsync, cancellationToken));

        private async Task<CaptureDeviceChange> RemoveCoreAsync(
            ICaptureDeviceContext target, Func<Task<bool>> confirmCloseAsync,
            CancellationToken cancellationToken)
        {
            DemandRegistered(target);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_model.TryBeginCaptureDataOperation(out var lease))
                throw new CaptureDeviceException(CaptureDeviceFailure.CaptureBusy);
            using (lease)
            {
                if (confirmCloseAsync != null && !await confirmCloseAsync())
                    return Change(target, false, false, failure: CaptureDeviceFailure.Cancelled);
                DemandRegistered(target);
                cancellationToken.ThrowIfCancellationRequested();
                await _model.RemoveDeviceContextAsync(target);
                var removed = _model.CaptureCatalog.RecordRemoval(target);
                if (target.IsReadOnly)
                    return new(removed.Id, removed.Revision, removed.CaptureEpoch, true, true, Removed: true);
                try
                {
                    await _save();
                    return new(removed.Id, removed.Revision, removed.CaptureEpoch, true, true, Removed: true);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    return new(removed.Id, removed.Revision, removed.CaptureEpoch, true, false, Removed: true,
                        Failure: CaptureDeviceFailure.PersistenceFailed);
                }
            }
        }

        public Task<CaptureDeviceChange> SetPendingProxyPortAsync(
            Guid captureId, long expectedRevision, ushort? port,
            CancellationToken cancellationToken = default) =>
            WithTargetAsync(captureId, expectedRevision, async (target, _) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (port == 0)
                    throw new CaptureDeviceException(CaptureDeviceFailure.InvalidRequest);
                var failure = PendingPortAvailability(target);
                if (failure != CaptureDeviceFailure.None)
                    throw new CaptureDeviceException(failure);
                var proxy = (WebServiceDeviceCaptureController)target.CaptureController;
                var desired = port == proxy.Port ? null : port;
                if ((desired ?? 0) == proxy.PendingPort &&
                    (!desired.HasValue || proxy.HasPendingPortReservation))
                    return Change(target, false, true);
                if (!_model.TryBeginCaptureDataOperation(out var lease))
                    throw new CaptureDeviceException(CaptureDeviceFailure.CaptureBusy);
                using (lease)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (desired.HasValue)
                        await proxy.QueuePortChangeAsync(desired.Value, SavePortDraftAsync);
                    else
                        await proxy.DiscardPortChangeAsync(SavePortDraftAsync);
                    return Change(target, true, true);
                }
            });

        private async Task<CaptureDeviceState> DescribeAsync(
            ICaptureDeviceContext target, bool inspectProxy, CancellationToken cancellationToken,
            bool forRemoval = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DemandRegistered(target);
            var proxy = target.CaptureController as WebServiceDeviceCaptureController;
            var state = proxy == null ? CaptureDeviceProxyState.NotApplicable :
                target.IsReadOnly ? CaptureDeviceProxyState.NotApplicable :
                target.DeviceType == DeviceType.GenericProxyDevice ? CaptureDeviceProxyState.Manual :
                CaptureDeviceProxyState.NotInspected;
            ProxyConfiguration configuration = null;
            if (inspectProxy && state == CaptureDeviceProxyState.NotInspected &&
                !IsTransitioning(target) && (!forRemoval || !target.CaptureController.IsRunning))
            {
                if (target.DeviceType == DeviceType.XboxConsole && !_gdkInstalled())
                    state = CaptureDeviceProxyState.GdkRequired;
                else
                {
                    try
                    {
                        configuration = await proxy.CreateDeviceConfiguration().ReadAsync().WaitAsync(
                            _inspectionTimeout, cancellationToken);
                        state = configuration.State switch
                        {
                            ProxyConfigurationState.Disabled => CaptureDeviceProxyState.Disabled,
                            ProxyConfigurationState.Enabled when configuration.Endpoint != null => CaptureDeviceProxyState.Enabled,
                            ProxyConfigurationState.Unreachable => CaptureDeviceProxyState.Unreachable,
                            _ => CaptureDeviceProxyState.Unknown
                        };
                    }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception) &&
                        exception is not OperationCanceledException)
                    {
                        state = CaptureDeviceProxyState.Unknown;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            DemandRegistered(target);
            var endpoint = state == CaptureDeviceProxyState.Enabled ? configuration?.Endpoint : null;
            var ownedEndpoint = endpoint != null && proxy.OwnsEndpoint(endpoint);
            var removalFailure = RemovalAvailability(target, state, ownedEndpoint);
            return new(_model.CaptureCatalog.DescribeTarget(target), IsTransitioning(target),
                proxy?.PendingPort, proxy?.ProxyHost, proxy?.HasPortReservation == true,
                proxy?.HasPendingPortReservation == true, state,
                endpoint == null ? null : new(endpoint.Host, endpoint.Port),
                ownedEndpoint, removalFailure, PendingPortAvailability(target));
        }

        private CaptureDeviceFailure RemovalAvailability(
            ICaptureDeviceContext target, CaptureDeviceProxyState proxyState, bool ownsEndpoint)
        {
            if (_model.CaptureCatalog.DescribeTarget(target).RecoveryRequired)
                return CaptureDeviceFailure.RecoveryRequired;
            if (!_model.CanModifyCaptureData || IsTransitioning(target) || target.CaptureController.IsRunning)
                return CaptureDeviceFailure.CaptureBusy;
            if (target.IsReadOnly)
                return CaptureDeviceFailure.None;
            if (target.CaptureController is not (WebServiceDeviceCaptureController or NetworkTraceCaptureController))
                return CaptureDeviceFailure.UnsupportedCapture;
            if (proxyState == CaptureDeviceProxyState.GdkRequired)
                return CaptureDeviceFailure.GdkRequired;
            if (proxyState is CaptureDeviceProxyState.Unknown or CaptureDeviceProxyState.Unreachable or CaptureDeviceProxyState.NotInspected)
                return CaptureDeviceFailure.ProxyStateUnknown;
            return ownsEndpoint ? CaptureDeviceFailure.ProxyEndpointInUse : CaptureDeviceFailure.None;
        }

        private CaptureDeviceFailure PendingPortAvailability(ICaptureDeviceContext target)
        {
            if (target.IsReadOnly)
                return CaptureDeviceFailure.ReadOnlyCapture;
            if (target.CaptureController is not WebServiceDeviceCaptureController)
                return CaptureDeviceFailure.UnsupportedCapture;
            if (_model.CaptureCatalog.DescribeTarget(target).RecoveryRequired)
                return CaptureDeviceFailure.RecoveryRequired;
            return !_model.CanModifyCaptureData || IsTransitioning(target) || target.CaptureController.IsRunning
                ? CaptureDeviceFailure.CaptureBusy : CaptureDeviceFailure.None;
        }

        private static bool IsTransitioning(ICaptureDeviceContext target) =>
            target.CaptureController is WebServiceDeviceCaptureController { IsTransitioning: true } or
                NetworkTraceCaptureController { IsTransitioning: true };

        private async Task<CaptureDeviceChange> PersistChangeAsync(ICaptureDeviceContext target)
        {
            try
            {
                await _save();
                return Change(target, true, true);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                return Change(target, true, false, failure: CaptureDeviceFailure.PersistenceFailed);
            }
        }

        private async Task SavePortDraftAsync()
        {
            try
            {
                await _save();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                throw new CaptureDeviceException(CaptureDeviceFailure.PersistenceFailed);
            }
        }

        private CaptureDeviceChange Change(
            ICaptureDeviceContext target, bool changed, bool persisted,
            CaptureDeviceFailure failure = CaptureDeviceFailure.None)
        {
            var capture = _model.CaptureCatalog.DescribeTarget(target);
            return new(capture.Id, capture.Revision, capture.CaptureEpoch, changed, persisted, Failure: failure);
        }

        private void DemandMutable()
        {
            if (_model.CaptureDevicesTable == null)
                throw new CaptureDeviceException(CaptureDeviceFailure.NotInitialized);
            if (!_model.CanModifyCaptureData)
                throw new CaptureDeviceException(CaptureDeviceFailure.CaptureBusy);
        }

        private void DemandRegistered(ICaptureDeviceContext target)
        {
            if (target?.CaptureController == null || !_model.CaptureDeviceContexts.Contains(target))
                throw new CaptureDeviceException(CaptureDeviceFailure.TargetNotFound);
        }

        private void DemandRevision(ICaptureDeviceContext target, long expectedRevision)
        {
            DemandRegistered(target);
            if (_model.CaptureCatalog.DescribeTarget(target).Revision != expectedRevision)
                throw new CaptureDeviceException(CaptureDeviceFailure.SourceChanged);
        }

        private async Task<T> WithTargetAsync<T>(
            Guid id, long? expectedRevision, Func<ICaptureDeviceContext, CaptureDescriptor, Task<T>> action)
        {
            try
            {
                return await _model.CaptureCatalog.WithTargetAsync(id, expectedRevision, action);
            }
            catch (AgentAccessException exception) when (exception.Error is
                AgentAccessError.OperationNotFound or AgentAccessError.SourceChanged)
            {
                throw new CaptureDeviceException(exception.Error == AgentAccessError.SourceChanged
                    ? CaptureDeviceFailure.SourceChanged : CaptureDeviceFailure.TargetNotFound);
            }
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => App.RunOnUiAsync(_dispatcher, action);
    }
}
