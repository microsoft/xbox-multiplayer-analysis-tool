// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Models
{
    internal partial class CaptureAppModel
    {
        private readonly object _investigationGate = new();
        private readonly Dictionary<ICaptureDeviceContext, InvestigationSource> _investigationSources = new();
        private readonly Dictionary<Guid, QuerySourceDescriptor> _retainedInvestigationSources = new();
        private readonly Dictionary<Guid, WeakReference<CaptureDataResult>> _retainedInvestigationResults = new();
        private readonly Dictionary<ICaptureAnalysisRun, CaptureDataResult> _desktopInvestigations = new();
        private bool _investigationTracking;
        private Task _investigationCleanup = Task.CompletedTask;

        internal void InitializeInvestigationTracking()
        {
            if (_investigationTracking)
                return;
            _investigationTracking = true;
            CaptureDataRebound += InvestigationDataRebound;
            _captureDeviceContexts.CollectionChanged += InvestigationContextsChanged;
            _analysisRuns.CollectionChanged += InvestigationRunsChanged;
        }

        private void InvestigationDataRebound(ICaptureDeviceContext context, IQueryDatabase database)
        {
            if (context == null || database == null)
                return;
            var queries = (CaptureQueries)CaptureQueries;
            var descriptor = new QuerySourceDescriptor(queries.GetSourceId(database),
                ReferenceEquals(database, ActiveDatabase) ? QuerySourceKind.Active : QuerySourceKind.Loaded,
                ReferenceEquals(database, ActiveDatabase)
                    ? Localization.GetLocalizedString("AGENT_SOURCE_ACTIVE")
                    : Localization.GetLocalizedString("AGENT_SOURCE_LOADED"));
            lock (_investigationGate)
            { _investigationSources[context] = new(database, descriptor); }
        }

        private void InvestigationContextsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            lock (_investigationGate)
            {
                foreach (var removed in _investigationSources.Keys.Where(context => !_captureDeviceContexts.Contains(context)).ToArray())
                    _investigationSources.Remove(removed);
                foreach (var context in _captureDeviceContexts.Where(context => !context.IsReadOnly &&
                    !_investigationSources.ContainsKey(context)))
                {
                    if (ActiveDatabase != null)
                        InvestigationDataRebound(context, ActiveDatabase);
                }
            }
        }

        internal async Task<IReadOnlyList<QuerySourceDescriptor>> GetInvestigationSourcesAsync(CancellationToken cancellationToken = default)
        {
            var current = await CaptureQueries.GetSourcesAsync(cancellationToken);
            lock (_investigationGate)
            {
                var ids = current.Select(source => source.Id).ToHashSet();
                return current.Concat(_retainedInvestigationSources.Values.Where(source => !ids.Contains(source.Id))
                    .Select(source => source with
                    {
                        DisplayName = Localization.GetLocalizedString("DATA_SOURCE_RETAINED", source.DisplayName, source.Id)
                    }))
                    .DistinctBy(source => source.Id).ToArray();
            }
        }

        internal void TrackInvestigationResult(CaptureDataResult result, QuerySourceDescriptor source)
        {
            if (result.Details is not CaptureDataDetails details)
                return;
            lock (_investigationGate)
            {
                _retainedInvestigationSources[result.Id] = source;
                _retainedInvestigationResults[result.Id] = new(result);
            }
            details.OnReleased(() =>
            {
                lock (_investigationGate)
                {
                    _retainedInvestigationSources.Remove(result.Id);
                    _retainedInvestigationResults.Remove(result.Id);
                }
            });
        }

        internal bool HasInvestigationTarget(IDeviceCaptureController controller)
        {
            lock (_investigationGate)
            {
                return _captureDeviceContexts.Any(context => ReferenceEquals(context.CaptureController, controller) &&
                    _investigationSources.ContainsKey(context));
            }
        }

        internal bool HasInvestigationSource(ICaptureDeviceContext context, Guid sourceId)
        {
            lock (_investigationGate)
            {
                return _investigationSources.TryGetValue(context, out var source) && source.Descriptor.Id == sourceId;
            }
        }

        internal IReadOnlyList<CaptureDescriptor> GetInvestigationCaptureReferences(Guid sourceId) =>
            _captureDeviceContexts.Where(context => HasInvestigationSource(context, sourceId))
                .Select(CaptureCatalog.DescribeTarget).ToArray();

        internal Task<DesktopNavigationTarget> RegisterInvestigationNavigationAsync(
            CaptureDataResult result, CaptureDetailKind kind, long itemId, Func<bool> isOwned,
            Guid ownerSessionId, CancellationToken cancellationToken) =>
            App.QueueOnUi(_dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isOwned())
                    throw new CaptureDataException(CaptureDataError.DetailNotFound);
                if (kind == CaptureDetailKind.Report)
                {
                    if (result.Report == null)
                        throw new CaptureDataException(CaptureDataError.UnsupportedData);
                    return DesktopActions.RegisterReport(new DesktopTargetProvenance(
                        SourceId: result.SourceId, SnapshotId: result.SnapshotId, ReportId: result.Report.Id),
                        1, isOwned, ownerSessionId: ownerSessionId);
                }
                var record = result.Records.SingleOrDefault(record => record.RecordId == itemId)
                    ?? throw new CaptureDataException(CaptureDataError.DetailNotFound);
                if (kind != CaptureDetailKind.Record || record.MetadataTruncated || result.Kind == CaptureDataKind.WebSocket)
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                var captureType = result.Kind == CaptureDataKind.NetworkTrace ? CaptureType.NetworkTrace : CaptureType.WebProxy;
                var references = result.CaptureReferences.Where(capture => capture.CaptureType == captureType &&
                    capture.DeviceName == record.DeviceName).ToArray();
                if (references.Length != 1)
                    throw new CaptureDataException(CaptureDataError.SourceChanged);
                var reference = references[0];
                bool IsCurrent() => isOwned() && _captureDeviceContexts.Any(context => HasInvestigationSource(context, result.SourceId) &&
                    CaptureCatalog.DescribeTarget(context) is var current && current.Id == reference.Id &&
                    current.Revision == reference.Revision && current.CaptureEpoch == reference.CaptureEpoch);
                if (!IsCurrent())
                    throw new CaptureDataException(CaptureDataError.SourceChanged);
                return DesktopActions.RegisterRecord(new DesktopTargetProvenance(reference.Id, reference.Revision, reference.CaptureEpoch,
                    result.SourceId, result.SnapshotId, itemId), reference.CaptureEpoch, IsCurrent, ownerSessionId: ownerSessionId);
            }).Task;

        internal Task UnregisterInvestigationNavigationAsync(IEnumerable<Guid> targets) =>
            App.QueueOnUi(_dispatcher, () =>
            {
                foreach (var target in targets)
                    DesktopActions.Unregister(target);
            }).Task;

        internal Task<bool> RevealInvestigationRecordAsync(
            DesktopTargetProvenance provenance, Func<CaptureInvestigationRecordTarget, bool> reveal,
            CancellationToken cancellationToken = default) =>
            App.QueueOnUi(_dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (provenance?.CaptureId is not Guid captureId || provenance.SourceId is not Guid sourceId ||
                    provenance.SnapshotId is not Guid snapshotId || provenance.RecordId is not long recordId ||
                    !provenance.CaptureRevision.HasValue || !provenance.CaptureEpoch.HasValue)
                    return false;
                var context = _captureDeviceContexts.SingleOrDefault(context => CaptureCatalog.DescribeTarget(context).Id == captureId);
                if (context == null || !HasInvestigationSource(context, sourceId))
                    return false;
                var capture = CaptureCatalog.DescribeTarget(context);
                if (capture.Revision != provenance.CaptureRevision || capture.CaptureEpoch != provenance.CaptureEpoch)
                    return false;
                lock (_investigationGate)
                {
                    if (!_retainedInvestigationResults.Values.Any(reference => reference.TryGetTarget(out var result) &&
                        result.SourceId == sourceId && result.SnapshotId == snapshotId &&
                        ((result.Kind == CaptureDataKind.WebProxy && capture.CaptureType == CaptureType.WebProxy) ||
                            (result.Kind == CaptureDataKind.NetworkTrace && capture.CaptureType == CaptureType.NetworkTrace)) &&
                        result.Records.Any(record => record.RecordId == recordId) &&
                        result.CaptureReferences.Any(original => original.Id == captureId && original.Revision == capture.Revision &&
                            original.CaptureEpoch == capture.CaptureEpoch)))
                        return false;
                }
                var web = (context.CaptureController as WebServiceDeviceCaptureController)?.ProxyConnections
                    .SingleOrDefault(record => record.RecordRowId == recordId);
                var packet = (context.CaptureController as NetworkTrace.NetworkTraceCaptureController)?.NetworkTracePackets
                    .SingleOrDefault(record => record.RecordRowId == recordId);
                return (web != null || packet != null) && reveal(new(context, capture, web, packet));
            }).Task;

        internal Task<bool> RevealDesktopInvestigationReportAsync(
            DesktopTargetProvenance provenance, Func<ICaptureAnalysisRun, bool> reveal,
            CancellationToken cancellationToken = default) =>
            App.QueueOnUi(_dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (provenance?.ReportId is not Guid reportId || provenance.SourceId is not Guid sourceId ||
                    provenance.SnapshotId is not Guid snapshotId)
                    return false;
                ICaptureAnalysisRun run;
                lock (_investigationGate)
                {
                    run = _desktopInvestigations.Where(pair => !pair.Key.IsProcessing &&
                        pair.Value.Report?.Id == reportId && pair.Value.SourceId == sourceId && pair.Value.SnapshotId == snapshotId)
                        .Select(pair => pair.Key).SingleOrDefault();
                }
                return run != null && reveal(run);
            }).Task;

        internal Task<CaptureDataResult> RetainInvestigationReportAsync(
            Guid reportId, Guid snapshotId, CancellationToken cancellationToken = default) =>
            App.QueueOnUi(_dispatcher, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_investigationGate)
                {
                    foreach (var reference in _retainedInvestigationResults.Values)
                    {
                        if (!reference.TryGetTarget(out var result) || result.Report?.Id != reportId || result.SnapshotId != snapshotId)
                            continue;
                        try
                        { return result with { Id = Guid.NewGuid(), Details = ((CaptureDataDetails)result.Details).Retain() }; }
                        catch (ObjectDisposedException) { }
                    }
                }
                throw new CaptureDataException(CaptureDataError.DetailNotFound);
            }).Task;

        internal async Task<DesktopInvestigationSnapshot> CreateDesktopInvestigationSnapshotAsync(
            IDeviceCaptureController controller, CancellationToken cancellationToken)
        {
            if (!App.HasUiAccess(_dispatcher))
                return await App.QueueOnUi(_dispatcher, () => CreateDesktopInvestigationSnapshotAsync(controller, cancellationToken)).Task.Unwrap();
            var context = _captureDeviceContexts.SingleOrDefault(context => ReferenceEquals(context.CaptureController, controller));
            if (context == null || !IsAnalysisRunning || controller.IsRunning ||
                controller is WebServiceDeviceCaptureController { IsTransitioning: true } ||
                controller is NetworkTrace.NetworkTraceCaptureController { IsTransitioning: true })
                throw new CaptureDataException(CaptureDataError.SourceBusy);
            InvestigationSource source;
            lock (_investigationGate)
            {
                source = _investigationSources.GetValueOrDefault(context)
                    ?? throw new CaptureDataException(CaptureDataError.SourceChanged);
            }
            var target = CaptureCatalog.DescribeTarget(context);
            var snapshot = await source.Database.CreateQuerySessionAsync(cancellationToken);
            return new(snapshot, source.Descriptor, target.Id, target.CaptureEpoch, new CaptureDataFilter(deviceName: context.DeviceName));
        }

        internal void RegisterDesktopInvestigation(ICaptureAnalysisRun run, CaptureDataResult result)
        {
            lock (_investigationGate)
            {
                if (!_analysisRuns.Contains(run))
                    return;
                _desktopInvestigations.Add(run, result);
            }
        }

        internal IReadOnlyList<CaptureCompletedReportDescriptor> GetDesktopInvestigationReports()
        {
            lock (_investigationGate)
            {
                return _desktopInvestigations.Where(pair => !pair.Key.IsProcessing && pair.Value.Report != null)
                    .Select(pair => pair.Value.Report).ToArray();
            }
        }

        internal CaptureDataResult RetainDesktopInvestigation(Guid id)
        {
            lock (_investigationGate)
            {
                var result = _desktopInvestigations.Where(pair => !pair.Key.IsProcessing && pair.Value.Report?.Id == id)
                    .Select(pair => pair.Value).SingleOrDefault()
                    ?? throw new CaptureDataException(CaptureDataError.DetailNotFound);
                var retained = result with { Id = Guid.NewGuid(), Details = ((CaptureDataDetails)result.Details).Retain() };
                if (_retainedInvestigationSources.TryGetValue(result.Id, out var source))
                    TrackInvestigationResult(retained, source);
                return retained;
            }
        }

        private void InvestigationRunsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            lock (_investigationGate)
            {
                foreach (var removed in _desktopInvestigations.Keys.Where(run => !_analysisRuns.Contains(run)).ToArray())
                {
                    var result = _desktopInvestigations[removed];
                    _desktopInvestigations.Remove(removed);
                    _investigationCleanup = Task.WhenAll(_investigationCleanup, result.DisposeAsync().AsTask());
                }
            }
        }

        internal void TrackInvestigationCleanup(Task cleanup)
        {
            lock (_investigationGate)
            { _investigationCleanup = Task.WhenAll(_investigationCleanup, cleanup); }
        }

        internal async Task DisposeInvestigationAsync()
        {
            await App.RunOnUiAsync(_dispatcher, () => CaptureInvestigationReportWindow.CloseForModel(this));
            Task cleanup;
            lock (_investigationGate)
            {
                var results = _desktopInvestigations.Values.ToArray();
                _desktopInvestigations.Clear();
                _investigationCleanup = Task.WhenAll(new[] { _investigationCleanup }.Concat(
                    results.Select(result => result.DisposeAsync().AsTask())));
                cleanup = _investigationCleanup;
            }
            await cleanup;
        }

        private sealed record InvestigationSource(IQueryDatabase Database, QuerySourceDescriptor Descriptor);
        internal sealed record DesktopInvestigationSnapshot(
            IQuerySession Snapshot, QuerySourceDescriptor Source, Guid CaptureId, long CaptureEpoch, CaptureDataFilter Filter);
    }
}
