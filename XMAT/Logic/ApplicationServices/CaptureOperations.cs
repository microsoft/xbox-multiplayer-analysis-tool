// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureOperations : ICaptureOperations, ICaptureRecoveryOperations
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly Func<ICaptureDeviceContext, ICaptureLifecycleAdapter> _adapterFactory;

        internal CaptureOperations(
            CaptureAppModel model,
            Dispatcher dispatcher,
            Func<ICaptureDeviceContext, ICaptureLifecycleAdapter> adapterFactory = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _adapterFactory = adapterFactory ?? CreateAdapter;
        }

        public Task<CaptureOperationFailure> GetAvailabilityAsync(
            ICaptureDeviceContext target,
            bool running,
            CancellationToken cancellationToken = default)
        {
            return InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(GetAvailability(target, running, out _));
            });
        }

        public Task<CaptureOperationResult> SetRunningAsync(
            ICaptureDeviceContext target,
            bool running,
            IProgress<WebProxyStopProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return InvokeAsync(() => SetRunningCoreAsync(
                target, running, progress, cancellationToken));
        }

        public Task<CaptureRecoveryResult> AcknowledgeExternalTraceRecoveryAsync(
            ICaptureDeviceContext target, CancellationToken cancellationToken = default) =>
            InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_model.CaptureDeviceContexts.Contains(target) || target.IsReadOnly ||
                    target.CaptureController is not NetworkTraceCaptureController { RecoveryRequired: true, IsTransitioning: false } controller ||
                    !_model.TryBeginCaptureDataOperation(out var lease))
                    return new CaptureRecoveryResult(false, false, true, null);
                using (lease)
                using (controller.BeginOperation())
                {
                    await controller.AcknowledgeExternalRecoveryAsync();
                    _model.CaptureCatalog.SetRecoveryRequired(target, false);
                    return new CaptureRecoveryResult(true, false, true, _model.CaptureCatalog.DescribeTarget(target).CaptureEpoch);
                }
            });

        private async Task<CaptureOperationResult> SetRunningCoreAsync(
            ICaptureDeviceContext target,
            bool running,
            IProgress<WebProxyStopProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = GetAvailability(target, running, out var adapter);
            if (failure != CaptureOperationFailure.None)
            {
                return new(false, target?.CaptureController?.IsRunning == true, failure);
            }

            if (target.CaptureController.IsRunning == running)
            {
                return new(false, running, CaptureOperationFailure.None);
            }

            IDisposable operationLease = null;
            if ((running || adapter.StopRequiresDataOperation) &&
                !_model.TryBeginCaptureDataOperation(out operationLease))
            {
                return new(false, target.CaptureController.IsRunning, CaptureOperationFailure.CaptureBusy);
            }

            using (operationLease)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var transition = adapter.BeginTransition(running);

                // Once a transition starts it must finish its rollback/drain even if the caller leaves.
                try
                {
                    if (running)
                    {
                        await adapter.StartAsync();
                    }
                    else
                    {
                        await adapter.StopAsync(progress);
                    }
                    _model.CaptureCatalog.SetRecoveryRequired(target, false);
                    return new(true, target.CaptureController.IsRunning, CaptureOperationFailure.None,
                        _model.CaptureCatalog.DescribeTarget(target).CaptureEpoch);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    _model.CaptureCatalog.SetRecoveryRequired(target,
                        !running || target.CaptureController.IsRunning);
                    CaptureTransitionFailure.Attach(exception, new CaptureTransitionFailure(
                        target.CaptureController.IsRunning,
                        _model.CaptureCatalog.DescribeTarget(target).CaptureEpoch));
                    throw;
                }
            }
        }

        private CaptureOperationFailure GetAvailability(
            ICaptureDeviceContext target,
            bool running,
            out ICaptureLifecycleAdapter adapter)
        {
            adapter = null;
            if (target?.CaptureController == null ||
                !_model.CaptureDeviceContexts.Contains(target))
            {
                return CaptureOperationFailure.TargetNotFound;
            }
            if (target.IsReadOnly)
            {
                return CaptureOperationFailure.ReadOnlyCapture;
            }

            adapter = _adapterFactory(target);
            if (adapter == null)
            {
                return CaptureOperationFailure.UnsupportedCapture;
            }

            var failure = adapter.GetAvailability(running);
            if (failure != CaptureOperationFailure.None)
            {
                return failure;
            }
            if (target.CaptureController.IsRunning == running)
            {
                return CaptureOperationFailure.None;
            }
            if (running ? !_model.CanModifyCaptureData :
                !_model.CanStopCaptures ||
                (adapter.StopRequiresDataOperation && !_model.CanModifyCaptureData))
            {
                return CaptureOperationFailure.CaptureBusy;
            }

            return CaptureOperationFailure.None;
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            return _dispatcher.CheckAccess()
                ? action()
                : _dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private static ICaptureLifecycleAdapter CreateAdapter(ICaptureDeviceContext target)
        {
            return target.CaptureController switch
            {
                WebServiceDeviceCaptureController proxy => new ProxyLifecycleAdapter(target, proxy),
                NetworkTraceCaptureController trace => new TraceLifecycleAdapter(target, trace),
                _ => null
            };
        }

        private sealed class ProxyLifecycleAdapter(
            ICaptureDeviceContext target,
            WebServiceDeviceCaptureController controller) : ICaptureLifecycleAdapter
        {
            public bool StopRequiresDataOperation => true;

            public CaptureOperationFailure GetAvailability(bool running)
            {
                if (controller.IsTransitioning)
                {
                    return CaptureOperationFailure.CaptureBusy;
                }
                if (running && controller.HasPendingPort)
                {
                    return CaptureOperationFailure.PendingPortChange;
                }
                if (running && target.DeviceType == DeviceType.XboxConsole &&
                    !GDKXHelper.IsGDKXInstalled(false))
                {
                    return CaptureOperationFailure.GdkRequired;
                }
                return CaptureOperationFailure.None;
            }

            public IDisposable BeginTransition(bool running) => running
                ? controller.BeginTransition()
                : controller.BeginStopTransition();

            public async Task StartAsync()
            {
                await controller.StartProxyAsync(false);
                if (target.DeviceType == DeviceType.LocalPC)
                {
                    await controller.EnableDeviceProxyingOrRollBackAsync(async () =>
                    {
                        await Task.Run(controller.EnableProxyingPC);
                        return true;
                    });
                }
            }

            public async Task StopAsync(IProgress<WebProxyStopProgress> progress)
            {
                var stopwatch = Stopwatch.StartNew();
                await controller.StopProxyAsync(progress);
                if (target.DeviceType == DeviceType.LocalPC)
                {
                    progress?.Report(new WebProxyStopProgress(
                        WebProxyStopPhase.DisablingSystemProxy, stopwatch.Elapsed));
                    await controller.DisableProxying();
                }
                progress?.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.Complete, stopwatch.Elapsed));
            }
        }

        private sealed class TraceLifecycleAdapter(
            ICaptureDeviceContext target,
            NetworkTraceCaptureController controller) : ICaptureLifecycleAdapter
        {
            public bool StopRequiresDataOperation => false;

            public CaptureOperationFailure GetAvailability(bool running)
            {
                return controller.GetAvailability(running);
            }

            public IDisposable BeginTransition(bool running) => controller.BeginOperation();

            public async Task StartAsync()
            {
                await controller.StartCapture(target);
            }

            public Task StopAsync(IProgress<WebProxyStopProgress> progress) =>
                controller.StopCapture(target);
        }
    }

    internal interface ICaptureLifecycleAdapter
    {
        bool StopRequiresDataOperation { get; }
        CaptureOperationFailure GetAvailability(bool running);
        IDisposable BeginTransition(bool running);
        Task StartAsync();
        Task StopAsync(IProgress<WebProxyStopProgress> progress);
    }
}
