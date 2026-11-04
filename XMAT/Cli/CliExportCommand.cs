// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Export;

namespace XMAT.Cli
{
    // xmat export INPUT --out FILE [...] is the command-line adapter of the shared export operation (the GUI's adapter is
    // CaptureExportWorkflow). The operation stages, hashes and publishes FILE; it never replaces an existing file and
    // never changes INPUT. The JSON reports the publication in its export block, so artifacts stays empty.
    internal static class CliExportCommand
    {
        internal static async Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken)
        {
            var report = new CliReport(CliCommands.Export, runId) { Export = CliExportReport.From(request) };
            OperationResult<ExportCaptureResult> result = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    request.Input,
                    null,
                    request.DeviceId,
                    request.Selection,
                    request.Format.Value,
                    request.Output,
                    ReplaceExisting: false,
                    report.Export.Limits.ToAnalysisLimits()),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            report.Export.Result = result.Value;
            if (result.Outcome == OperationOutcome.Cancelled)
            {
                report.Cancel();
                return report;
            }

            bool failed = result.Outcome != OperationOutcome.Succeeded;
            foreach (OperationDiagnostic diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == OperationDiagnosticSeverity.Warning)
                {
                    report.Warnings.Add(diagnostic);
                }
                else if (diagnostic.Severity == OperationDiagnosticSeverity.Error || failed)
                {
                    report.Errors.Add(diagnostic);
                }
            }

            report.SetExitCode(CliExitCodes.FromOutcome(result.Outcome));
            if (failed && report.Errors.Count == 0)
            {
                report.Errors.Add(new OperationDiagnostic(
                    "cli.export.failed", OperationDiagnosticSeverity.Error, "The export did not complete; nothing was published."));
            }

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
