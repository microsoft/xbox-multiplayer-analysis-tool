// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;

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
    // operation with progress and Cancel.
    internal sealed class CaptureExportWorkflow
    {
        internal CaptureExportWorkflow(
            object owner,
            ICaptureExportDialogService dialogs,
            Func<ICaptureDeviceContext, ICaptureExportSnapshot> snapshotOf,
            IOperation<ExportCaptureRequest, ExportCaptureResult> operation)
        {
        }

        internal Task<CaptureExportWorkflowResult> RunAsync(
            CaptureAppModel app,
            ICaptureDeviceContext tab,
            CaptureExportScope scope,
            CaptureSelectionSpec selection,
            bool sanitizedOnly) =>
            throw new NotImplementedException();

        // File > Export and Export Sanitized: every session of the selected tab, never another tab's.
        internal Task<CaptureExportWorkflowResult> RunForSelectedTabAsync(CaptureAppModel app, bool sanitizedOnly) =>
            throw new NotImplementedException();
    }
}
