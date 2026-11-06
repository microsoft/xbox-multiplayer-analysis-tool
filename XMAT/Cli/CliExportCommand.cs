// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Export;

namespace XMAT.Cli
{
    // xmat export INPUT --out FILE [...] is the command-line adapter of the shared export operation (the GUI's adapter is
    // CaptureExportWorkflow). The operation stages FILE in a folder that only the current user can open, then hashes and
    // publishes it; it never replaces an existing file, never publishes where access cannot be restricted, and never
    // changes INPUT. The JSON reports the publication in its export block, so artifacts stays empty.
    internal static class CliExportCommand
    {
        // A test passes the operation, with its own publisher; xmat uses the shared one.
        internal static async Task<CliReport> RunAsync(
            CliRequest request,
            string runId,
            CancellationToken cancellationToken,
            IOperation<ExportCaptureRequest, ExportCaptureResult> operation = null)
        {
            var report = new CliReport(CliCommands.Export, runId) { Export = CliExportReport.From(request) };
            CaptureExportFormat format = request.Format ?? throw new InvalidOperationException("Export format was not parsed.");
            operation ??= new ExportCaptureSessionsOperation();
            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                new ExportCaptureRequest(
                    request.Input,
                    null,
                    request.DeviceId,
                    request.Selection,
                    format,
                    request.Output,
                    ReplaceExisting: false,
                    report.Export.Limits.ToAnalysisLimits(),
                    AllowUnprotectedOutput: false),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            report.Export.Result = result.Value;
            if (result.Outcome == OperationOutcome.Cancelled)
            {
                report.Cancel();
                return report;
            }

            report.ApplyOutcome(result, "cli.export.failed", "The export did not complete; nothing was published.");
            if (result.Outcome == OperationOutcome.CleanupFailed)
            {
                report.CleanupState = CliCleanupStates.Failed;
            }

            if (result.Value?.Counts is { } counts)
            {
                report.HumanLines.Add(
                    $"xmat: exported {counts.Exported} of {counts.Selected} selected session(s) ({counts.Total} in the input); " +
                    $"{counts.SkippedIncomplete} skipped without a complete response, {counts.Unavailable} no longer available.");
            }

            return report;
        }
    }
}
