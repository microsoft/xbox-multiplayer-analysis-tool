// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT
{
    // The save dialog's answer: the file, the format that its filter chose, and whether the user agreed to replace an
    // existing file.
    internal sealed record CaptureExportDestination(string Path, CaptureExportFormat Format, bool ReplaceExisting);

    // The windows of an export, so the workflow runs without them in tests.
    internal interface ICaptureExportDialogService
    {
        Task ShowBusyAsync();

        Task ShowNothingToExportAsync(CaptureExportCounts counts);

        Task<bool> ConfirmAsync(string tabName, CaptureExportCounts counts);

        Task<CaptureExportDestination> ChooseDestinationAsync(bool sanitizedOnly);

        Task<bool> ConfirmSanitizedAsync();

        // Asks, for this export only, whether to publish where Windows cannot restrict access to the file.
        Task<bool> ConfirmUnprotectedAsync(string path);

        Task<OperationResult<ExportCaptureResult>> RunWithProgressAsync(
            Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export);

        Task ShowResultAsync(OperationResult<ExportCaptureResult> result);
    }

    // Exports sessions of one capture tab. It holds the data-operation lease from the snapshot to the publication, asks
    // for confirmation with counts, a destination named for its format and, for a sanitized archive, consent, then runs
    // the shared export operation with progress and Cancel. Where Windows cannot restrict access to the destination it
    // asks once whether to publish anyway. The snapshot is always disposed and the lease always released.
    internal sealed class CaptureExportWorkflow
    {
        private readonly ICaptureExportDialogService _dialogs;
        private readonly Func<ICaptureDeviceContext, ICaptureExportSnapshot> _snapshotOf;
        private readonly IOperation<ExportCaptureRequest, ExportCaptureResult> _operation;

        internal CaptureExportWorkflow(
            ICaptureExportDialogService dialogs,
            Func<ICaptureDeviceContext, ICaptureExportSnapshot> snapshotOf,
            IOperation<ExportCaptureRequest, ExportCaptureResult> operation)
        {
            _dialogs = dialogs;
            _snapshotOf = snapshotOf;
            _operation = operation;
        }

        internal static CaptureExportWorkflow For(ICaptureExportDialogService dialogs) =>
            new(
                dialogs,
                tab => ((WebServiceDeviceCaptureController)tab.CaptureController).CreateExportSnapshot(
                    CaptureDeviceIds.Ordinal(0)),
                new ExportCaptureSessionsOperation());

        internal async Task RunAsync(
            CaptureAppModel app, ICaptureDeviceContext tab, CaptureSelectionSpec selection, bool sanitizedOnly)
        {
            if (!app.TryBeginCaptureDataOperation(out IDisposable lease))
            {
                await _dialogs.ShowBusyAsync();
                return;
            }

            ICaptureExportSnapshot snapshot = null;
            try
            {
                snapshot = _snapshotOf(tab);
                OperationResult<CaptureSessionSelection> preflight =
                    CaptureSessionSelector.Select(snapshot.Sessions, selection, null, snapshot.IsLive);
                if (!preflight.Succeeded)
                {
                    await _dialogs.ShowResultAsync(
                        OperationResult<ExportCaptureResult>.Failure(preflight.Outcome, preflight.Diagnostics.ToArray()));
                    return;
                }

                CaptureExportCounts counts = preflight.Value.Counts;
                if (counts.Eligible == 0)
                {
                    await _dialogs.ShowNothingToExportAsync(counts);
                    return;
                }

                if (!await _dialogs.ConfirmAsync(tab.DeviceName, counts))
                {
                    return;
                }

                CaptureExportDestination destination = await _dialogs.ChooseDestinationAsync(sanitizedOnly);
                while (destination != null &&
                    !CaptureExportFormats.HasExpectedExtension(destination.Format, destination.Path))
                {
                    await ShowFailureAsync(OperationOutcome.InvalidInput, CaptureExportErrorCodes.FormatMismatch);
                    destination = await _dialogs.ChooseDestinationAsync(sanitizedOnly);
                }

                if (destination == null ||
                    (destination.Format == CaptureExportFormat.SanitizedSaz && !await _dialogs.ConfirmSanitizedAsync()))
                {
                    return;
                }

                var request = new ExportCaptureRequest(
                    null,
                    snapshot,
                    null,
                    selection,
                    destination.Format,
                    destination.Path,
                    destination.ReplaceExisting,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false);
                Task<OperationResult<ExportCaptureResult>> Run(ExportCaptureRequest export) =>
                    _dialogs.RunWithProgressAsync((progress, cancellationToken) =>
                        _operation.ExecuteAsync(export, new OperationContext(progress: progress), cancellationToken));
                OperationResult<ExportCaptureResult> result = await Run(request);
                if (result.Outcome == OperationOutcome.Failed &&
                    result.Diagnostics.Any(diagnostic => diagnostic.Code == CaptureExportErrorCodes.OutputUnprotected))
                {
                    if (!await _dialogs.ConfirmUnprotectedAsync(destination.Path))
                    {
                        return;
                    }

                    result = await Run(request with { AllowUnprotectedOutput = true });
                }

                await _dialogs.ShowResultAsync(result);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Capture export failed: {ex}");
                await ShowFailureAsync(OperationOutcome.InternalError, "export.gui.failed");
            }
            finally
            {
                try
                {
                    snapshot?.Dispose();
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Capture export snapshot could not be closed: {ex}");
                }
                finally
                {
                    lease.Dispose();
                }
            }
        }

        // File > Export and Export Sanitized: every session of the selected tab, never another tab's.
        internal Task RunForSelectedTabAsync(CaptureAppModel app, bool sanitizedOnly) =>
            RunAsync(
                app,
                app.SelectedDeviceContext,
                new CaptureSelectionSpec(
                    ProcessFilterSpec.Empty,
                    new HostSelection(HostMatchMode.Exact, null, ImmutableArray<string>.Empty),
                    null,
                    null,
                    null,
                    false),
                sanitizedOnly);

        // A failure that the GUI finds itself; like every failure, the user sees it described by its code.
        private Task ShowFailureAsync(OperationOutcome outcome, string code) =>
            _dialogs.ShowResultAsync(OperationResult<ExportCaptureResult>.Failure(
                outcome, new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, "The export did not complete.")));
    }
}
