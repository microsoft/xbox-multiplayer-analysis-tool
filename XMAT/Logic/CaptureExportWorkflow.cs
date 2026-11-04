// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT
{
    internal enum CaptureExportScope
    {
        Selected,
        Visible,
        Tab,
    }

    internal enum CaptureExportWorkflowResult
    {
        Busy,
        Cancelled,
        NothingToExport,
        Exported,
        Failed,
    }

    // The save dialog's answer: the file, the format that its filter chose, and whether the user agreed to replace an
    // existing file.
    internal sealed record CaptureExportDestination(string Path, CaptureExportFormat Format, bool ReplaceExisting);

    // The windows of an export, so the workflow runs without them in tests.
    internal interface ICaptureExportDialogService
    {
        Task ShowBusyAsync(object owner);

        Task ShowNothingToExportAsync(object owner, CaptureExportScope scope, CaptureExportCounts counts);

        Task<bool> ConfirmAsync(object owner, CaptureExportScope scope, string tabName, CaptureExportCounts counts);

        Task<CaptureExportDestination> ChooseDestinationAsync(object owner, bool sanitizedOnly);

        Task<bool> ConfirmSanitizedAsync(object owner);

        // Shows progress with Cancel while export runs.
        Task<OperationResult<ExportCaptureResult>> RunWithProgressAsync(
            object owner,
            Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export);

        Task ShowResultAsync(object owner, CaptureExportScope scope, OperationResult<ExportCaptureResult> result);
    }

    // Exports sessions of one capture tab. It holds the data-operation lease from the snapshot to the publication, asks
    // for confirmation with counts, a destination and, for a sanitized archive, consent, then runs the shared export
    // operation with progress and Cancel. The snapshot is always disposed and the lease always released.
    internal sealed class CaptureExportWorkflow
    {
        private readonly object _owner;
        private readonly ICaptureExportDialogService _dialogs;
        private readonly Func<ICaptureDeviceContext, ICaptureExportSnapshot> _snapshotOf;
        private readonly IOperation<ExportCaptureRequest, ExportCaptureResult> _operation;

        internal CaptureExportWorkflow(
            object owner,
            ICaptureExportDialogService dialogs,
            Func<ICaptureDeviceContext, ICaptureExportSnapshot> snapshotOf,
            IOperation<ExportCaptureRequest, ExportCaptureResult> operation)
        {
            _owner = owner;
            _dialogs = dialogs;
            _snapshotOf = snapshotOf;
            _operation = operation;
        }

        // The GUI's export: the owner's dialogs, a snapshot of the tab's web capture and the shared operation.
        internal static CaptureExportWorkflow For(ICaptureExportDialogService owner) =>
            new(
                owner,
                owner,
                tab => ((WebServiceDeviceCaptureController)tab.CaptureController).CreateExportSnapshot(
                    "d1", AnalysisLimits.Default),
                new ExportCaptureSessionsOperation());

        internal async Task<CaptureExportWorkflowResult> RunAsync(
            CaptureAppModel app,
            ICaptureDeviceContext tab,
            CaptureExportScope scope,
            CaptureSelectionSpec selection,
            bool sanitizedOnly)
        {
            if (!app.TryBeginCaptureDataOperation(out IDisposable lease))
            {
                await _dialogs.ShowBusyAsync(_owner);
                return CaptureExportWorkflowResult.Busy;
            }

            ICaptureExportSnapshot snapshot = null;
            try
            {
                snapshot = _snapshotOf(tab);
                OperationResult<CaptureSessionSelection> preflight =
                    CaptureSessionSelector.Select(snapshot.Sessions, selection, null, snapshot.IsLive);
                if (!preflight.Succeeded)
                {
                    return await ShowFailureAsync(scope, preflight.Outcome, preflight.Diagnostics);
                }

                CaptureExportCounts counts = preflight.Value.Counts;
                if (counts.Eligible == 0)
                {
                    await _dialogs.ShowNothingToExportAsync(_owner, scope, counts);
                    return CaptureExportWorkflowResult.NothingToExport;
                }

                if (!await _dialogs.ConfirmAsync(_owner, scope, tab.DeviceName, counts))
                {
                    return CaptureExportWorkflowResult.Cancelled;
                }

                CaptureExportDestination destination = await _dialogs.ChooseDestinationAsync(_owner, sanitizedOnly);
                if (destination == null ||
                    (destination.Format == CaptureExportFormat.SanitizedSaz && !await _dialogs.ConfirmSanitizedAsync(_owner)))
                {
                    return CaptureExportWorkflowResult.Cancelled;
                }

                var request = new ExportCaptureRequest(
                    null,
                    snapshot,
                    null,
                    selection,
                    destination.Format,
                    destination.Path,
                    destination.ReplaceExisting,
                    AnalysisLimits.Default);
                OperationResult<ExportCaptureResult> result = await _dialogs.RunWithProgressAsync(
                    _owner,
                    (progress, cancellationToken) =>
                        _operation.ExecuteAsync(request, new OperationContext(progress: progress), cancellationToken));
                await _dialogs.ShowResultAsync(_owner, scope, result);
                return result.Outcome switch
                {
                    OperationOutcome.Succeeded => CaptureExportWorkflowResult.Exported,
                    OperationOutcome.Cancelled => CaptureExportWorkflowResult.Cancelled,
                    _ => CaptureExportWorkflowResult.Failed,
                };
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Capture export failed: {ex}");
                var failure = new OperationDiagnostic(
                    "export.gui.failed", OperationDiagnosticSeverity.Error, "The export stopped unexpectedly.");
                return await ShowFailureAsync(scope, OperationOutcome.InternalError, new[] { failure });
            }
            finally
            {
                snapshot?.Dispose();
                lease.Dispose();
            }
        }

        // File > Export and Export Sanitized: every session of the selected tab, never another tab's.
        internal Task<CaptureExportWorkflowResult> RunForSelectedTabAsync(CaptureAppModel app, bool sanitizedOnly) =>
            RunAsync(
                app,
                app.SelectedDeviceContext,
                CaptureExportScope.Tab,
                new CaptureSelectionSpec(
                    ProcessFilterSpec.Empty,
                    new HostSelection(HostMatchMode.Exact, null, ImmutableArray<string>.Empty),
                    null,
                    null,
                    null,
                    false),
                sanitizedOnly);

        private async Task<CaptureExportWorkflowResult> ShowFailureAsync(
            CaptureExportScope scope,
            OperationOutcome outcome,
            IReadOnlyList<OperationDiagnostic> diagnostics)
        {
            await _dialogs.ShowResultAsync(
                _owner,
                scope,
                OperationResult<ExportCaptureResult>.Failure(outcome, diagnostics.ToArray()));
            return CaptureExportWorkflowResult.Failed;
        }
    }
}
