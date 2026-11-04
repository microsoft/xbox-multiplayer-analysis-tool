// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class DesktopActions : IDesktopActions
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<Guid, Registration> _registrations = new();
        private Func<ICaptureDeviceContext, CancellationToken, Task<bool>> _revealCapture;
        private Func<DesktopTargetProvenance, CancellationToken, Task<bool>> _revealRecord;
        private Func<DesktopTargetProvenance, CancellationToken, Task<bool>> _revealReport;
        private Action _close;
        private Action _requestElevatedRestart;
        private Func<bool> _canClose;
        private Func<bool> _canRequestElevatedRestart;
        private long _lifecycleRevision = 1;
        private long _navigationRevision;
        private (bool Close, bool Restart)? _lastAvailability;

        internal DesktopActions(CaptureAppModel model, Dispatcher dispatcher)
        {
            _model = model;
            _dispatcher = dispatcher;
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(CaptureAppModel.IsAnalysisRunning) or
                    nameof(CaptureAppModel.IsCaptureDataOperationRunning) or nameof(CaptureAppModel.IsAnyCaptureRunning) or
                    nameof(CaptureAppModel.IsCaptureFileLoadRunning))
                    Interlocked.Increment(ref _lifecycleRevision);
            };
        }

        internal void Configure(
            Func<ICaptureDeviceContext, CancellationToken, Task<bool>> revealCapture,
            Action requestNormalClose, Action requestElevatedRestart,
            Func<bool> canClose = null, Func<bool> canRequestElevatedRestart = null)
        {
            App.VerifyUiAccess(_dispatcher);
            _revealCapture = revealCapture;
            _close = requestNormalClose;
            _requestElevatedRestart = requestElevatedRestart;
            _canClose = canClose;
            _canRequestElevatedRestart = canRequestElevatedRestart;
            NotifyLifecycleChanged();
        }

        internal void NotifyLifecycleChanged() => Interlocked.Increment(ref _lifecycleRevision);

        internal void ConfigureSourceNavigation(
            Func<DesktopTargetProvenance, CancellationToken, Task<bool>> revealRecord,
            Func<DesktopTargetProvenance, CancellationToken, Task<bool>> revealReport)
        {
            App.VerifyUiAccess(_dispatcher);
            _revealRecord = revealRecord;
            _revealReport = revealReport;
        }

        internal DesktopNavigationTarget RegisterRecord(
            DesktopTargetProvenance provenance, long epoch, Func<bool> isCurrent,
            Func<CancellationToken, Task<bool>> reveal = null, Guid? ownerSessionId = null) =>
            Register(DesktopNavigationKind.Record, provenance, epoch, isCurrent,
                reveal ?? (_revealRecord == null ? null : token => _revealRecord(provenance, token)), ownerSessionId);

        internal DesktopNavigationTarget RegisterReport(
            DesktopTargetProvenance provenance, long epoch, Func<bool> isCurrent,
            Func<CancellationToken, Task<bool>> reveal = null, Guid? ownerSessionId = null) =>
            Register(DesktopNavigationKind.Report, provenance, epoch, isCurrent,
                reveal ?? (_revealReport == null ? null : token => _revealReport(provenance, token)), ownerSessionId);

        private DesktopNavigationTarget Register(
            DesktopNavigationKind kind, DesktopTargetProvenance provenance, long epoch,
            Func<bool> isCurrent, Func<CancellationToken, Task<bool>> reveal, Guid? ownerSessionId)
        {
            App.VerifyUiAccess(_dispatcher);
            ArgumentNullException.ThrowIfNull(isCurrent);
            if (reveal == null)
                throw new AdministrationException(AdministrationFailure.Unavailable);
            if (provenance == null || epoch <= 0 || ownerSessionId == Guid.Empty ||
                provenance.SourceId is null || provenance.SourceId == Guid.Empty ||
                provenance.SnapshotId is null || provenance.SnapshotId == Guid.Empty ||
                (kind == DesktopNavigationKind.Record ? provenance.RecordId is null or < 0 :
                    provenance.ReportId is null || provenance.ReportId == Guid.Empty) ||
                (provenance.CaptureId.HasValue &&
                    (provenance.CaptureId == Guid.Empty || !provenance.CaptureRevision.HasValue || !provenance.CaptureEpoch.HasValue)))
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            if (_registrations.Count >= 1024)
                throw new AdministrationException(AdministrationFailure.LimitExceeded);
            var target = new DesktopNavigationTarget(Guid.NewGuid(), kind, ++_navigationRevision, epoch, provenance, ownerSessionId);
            _registrations.Add(target.Id, new(target, isCurrent, reveal));
            return target;
        }

        internal bool Unregister(Guid targetId)
        {
            App.VerifyUiAccess(_dispatcher);
            return _registrations.Remove(targetId);
        }

        public Task<IReadOnlyList<DesktopNavigationTarget>> GetNavigationTargetsAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync<IReadOnlyList<DesktopNavigationTarget>>(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captures = _revealCapture == null ? [] : await _model.CaptureCatalog.GetCapturesAsync(cancellationToken);
                var result = captures.Select(DescribeCapture).ToList();
                foreach (var registration in _registrations.Values.ToArray())
                {
                    if (await IsCurrentAsync(registration))
                        result.Add(registration.Target);
                }
                return result.ToArray();
            });

        public Task<DesktopNavigationTarget> PrepareNavigationAsync(
            DesktopNavigationRequest request, CancellationToken cancellationToken = default) =>
            InvokeAsync(() => PrepareNavigationCoreAsync(request, cancellationToken));

        public Task<DesktopNavigationReceipt> RevealAsync(
            DesktopNavigationRequest request, CancellationToken cancellationToken = default) =>
            InvokeAsync(async () =>
            {
                var target = await PrepareNavigationCoreAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool revealed;
                if (target.Kind == DesktopNavigationKind.Capture)
                {
                    revealed = await _model.CaptureCatalog.WithTargetAsync(target.Id, request.ExpectedRevision,
                        (context, capture) =>
                        {
                            if (capture.CaptureEpoch != request.ExpectedEpoch)
                                throw new AdministrationException(AdministrationFailure.SourceChanged);
                            return _revealCapture(context, cancellationToken);
                        });
                }
                else
                {
                    var registration = _registrations.GetValueOrDefault(target.Id)
                        ?? throw new AdministrationException(AdministrationFailure.SourceChanged);
                    revealed = await registration.Reveal(cancellationToken);
                }
                return new DesktopNavigationReceipt(target.Id, target.Kind, revealed);
            });

        private async Task<DesktopNavigationTarget> PrepareNavigationCoreAsync(
            DesktopNavigationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null || request.TargetId == Guid.Empty || request.ExpectedRevision <= 0 || request.ExpectedEpoch <= 0)
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            DesktopNavigationTarget target;
            if (_registrations.TryGetValue(request.TargetId, out var registration))
            {
                if (!await IsCurrentAsync(registration))
                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                target = registration.Target;
            }
            else
            {
                if (_revealCapture == null)
                    throw new AdministrationException(AdministrationFailure.Unavailable);
                target = await _model.CaptureCatalog.WithTargetAsync(request.TargetId, request.ExpectedRevision,
                    (_, capture) => Task.FromResult(DescribeCapture(capture)));
            }
            if (target.Revision != request.ExpectedRevision || target.Epoch != request.ExpectedEpoch)
                throw new AdministrationException(AdministrationFailure.SourceChanged);
            return target;
        }

        private async Task<bool> IsCurrentAsync(Registration registration)
        {
            if (!registration.IsCurrent())
                return false;
            var provenance = registration.Target.Provenance;
            if (!provenance.CaptureId.HasValue)
                return true;
            try
            {
                return await _model.CaptureCatalog.WithTargetAsync(provenance.CaptureId.Value, provenance.CaptureRevision,
                    (_, capture) => Task.FromResult(capture.CaptureEpoch == provenance.CaptureEpoch));
            }
            catch (AgentAccessException) { return false; }
        }

        private static DesktopNavigationTarget DescribeCapture(CaptureDescriptor capture) =>
            new(capture.Id, DesktopNavigationKind.Capture, capture.Revision, capture.CaptureEpoch,
                new(capture.Id, capture.Revision, capture.CaptureEpoch));

        public Task<DesktopLifecycleSnapshot> InspectLifecycleAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(InspectLifecycle());
            });

        private DesktopLifecycleSnapshot InspectLifecycle()
        {
            var available = !_model.IsAnalysisRunning && !_model.IsCaptureDataOperationRunning && !_model.IsCaptureFileLoadRunning;
            var close = available && _close != null && (_canClose?.Invoke() ?? true);
            var restart = available && !_model.IsAnyCaptureRunning && _requestElevatedRestart != null &&
                (_canRequestElevatedRestart?.Invoke() ?? true);
            if (_lastAvailability != (close, restart))
            {
                _lastAvailability = (close, restart);
                NotifyLifecycleChanged();
            }
            return new(Interlocked.Read(ref _lifecycleRevision), close, restart);
        }

        public Task<DesktopLifecyclePlan> PrepareLifecycleAsync(
            long expectedRevision, DesktopLifecycleAction action, CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(PrepareLifecycle(expectedRevision, action));
            });

        private DesktopLifecyclePlan PrepareLifecycle(long expectedRevision, DesktopLifecycleAction action)
        {
            if (!Enum.IsDefined(action))
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            var current = InspectLifecycle();
            if (current.Revision != expectedRevision)
                throw new AdministrationException(AdministrationFailure.SourceChanged);
            if (action == DesktopLifecycleAction.Close ? !current.CanRequestClose : !current.CanRequestElevatedRestart)
                throw new AdministrationException(AdministrationFailure.Unavailable);
            return new(expectedRevision, action);
        }

        public Task RequestLifecycleAsync(
            DesktopLifecyclePlan plan, Func<bool> stillAuthorized, CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(stillAuthorized);
                if (plan == null)
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                PrepareLifecycle(plan.ExpectedRevision, plan.Action);
                if (!stillAuthorized())
                    throw new AdministrationException(AdministrationFailure.Unavailable);
                // Only trusted desktop callbacks may present UAC/restart UI or request normal Close.
                if (plan.Action == DesktopLifecycleAction.Close)
                    _close();
                else
                    _requestElevatedRestart();
                return Task.FromResult(true);
            });

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) =>
            App.RunOnUiAsync(_dispatcher, action);

        private sealed record Registration(
            DesktopNavigationTarget Target, Func<bool> IsCurrent, Func<CancellationToken, Task<bool>> Reveal);
    }
}
