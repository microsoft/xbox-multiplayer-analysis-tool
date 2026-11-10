// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capture;

namespace XMAT.Cli
{
    internal sealed record CliExportSaz(
        bool Sanitize = false,
        bool AcceptStoredText = false);

    internal sealed class CliExportSazReport
    {
        internal CliLimits Limits { get; set; }

        internal string InputKind { get; set; }

        internal string InputSha256 { get; set; }

        internal string DeviceId { get; set; }

        internal bool Sanitize { get; set; }

        internal bool AcceptStoredText { get; set; }

        internal StoredTextSummary StoredText { get; set; }

        internal SazExportSummary Export { get; set; }

        internal SanitizedExportSummary Sanitized { get; set; }
    }

    internal static class CliExportSazCommand
    {
        internal const string ArchivePath = "capture.saz";
        internal const string SummaryPath = "summary.json";
        internal const string DeviceRequiredCode =
            "capture-file.export.device-required";
        internal const string DeviceNotFoundCode =
            "capture-file.export.device-not-found";
        internal const string NoWebDeviceCode =
            "capture-file.export.no-web-device";

        private const string AmbiguousExportMessage =
            "This unmarked XMAT capture requires --accept-stored-text to export its stored text unchanged, or xmat " +
                "mark-capture-text to create a marked copy.";
        private const string AmbiguousWarningMessage =
            "This capture has no XMAT text-format marker. XMAT treats its stored text as ambiguous and will use it " +
                "unchanged; adjacent double quotation marks may be original data or may have been added by an older XMAT version.";
        private const string AnalyzeInputWarningMessage =
            "The archive is larger than 2,048 MiB, the largest input that xmat analyze reads.";

        internal static async Task<CliReport> RunAsync(
            CliRequest request,
            string runId,
            CancellationToken cancellationToken)
        {
            CliLimits limits =
                request.Limits ??
                CliLimits.Default;
            CliExportSaz options =
                request.ExportSaz ??
                new CliExportSaz();
            var report = new CliReport(
                CliCommands.ExportSaz,
                runId)
            {
                ExportSaz = new CliExportSazReport
                {
                    Limits = limits,
                    Sanitize = options.Sanitize,
                    AcceptStoredText =
                        options.AcceptStoredText,
                },
            };

            CliFailure failure =
                CliOutputFolder.CheckTarget(
                    request.Output,
                    out CliOutputTarget target);
            if (failure == null)
            {
                failure =
                    CliOutputFolder.ProbeTarget(
                        target);
            }

            if (failure != null)
            {
                report.Fail(
                    failure.ExitCode,
                    failure.Code,
                    failure.Message);
                return report;
            }

            OperationResult<CaptureRecordSet> read =
                new AnalyzeCaptureOperation()
                    .ReadCapture(
                        request.Input,
                        limits.ToAnalysisLimits(),
                        cancellationToken);
            ApplyDiagnostics(report, read);
            report.Warnings.RemoveAll(
                diagnostic =>
                    diagnostic.Code ==
                    SazExportCodes
                        .AmbiguousStoredText);
            CaptureRecordSet capture = read.Value;
            if (capture == null ||
                !read.Succeeded)
            {
                return report;
            }

            report.ExportSaz.InputKind =
                capture.InputKind;
            report.ExportSaz.InputSha256 =
                capture.InputSha256;
            CaptureDeviceRecords device =
                SelectDevice(
                    capture,
                    request.DeviceId,
                    report);
            if (device == null)
            {
                return report;
            }

            report.ExportSaz.DeviceId =
                device.Id;
            SazExportKind kind =
                options.Sanitize
                    ? SazExportKind.Sanitized
                    : SazExportKind.Plain;
            // The records were read into memory from the file that was hashed, and that file is closed now; the text
            // they hold is bound to that hash, so the export decides on it rather than on the closed file.
            CaptureTextSource textSource =
                capture.TextSource.Semantics ==
                    CaptureTextSemantics
                        .AmbiguousUnmarked &&
                capture.TextSource.Identity.HasValue &&
                !string.IsNullOrEmpty(
                    capture.InputSha256)
                    ? CaptureTextSource.CreateSnapshot(
                        capture.TextSource.Semantics,
                        capture.TextSource.Identity.Value,
                        capture.InputSha256)
                    : capture.TextSource;
            SazExportSource source =
                SazExportSource.FromRecords(
                    device.Records,
                    textSource);
            StoredTextAcceptance acceptance =
                options.AcceptStoredText &&
                textSource.Semantics ==
                    CaptureTextSemantics
                        .AmbiguousUnmarked &&
                textSource.Sha256 != null
                    ? StoredTextAcceptance.For(
                        textSource)
                    : null;
            var operation =
                new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                    new SazExportPlanRequest(
                        source,
                        kind,
                        acceptance,
                        SazExportBudgets.Default),
                    OperationContext.None,
                    cancellationToken).ConfigureAwait(false);
            ApplyDiagnostics(
                report,
                prepared);
            if (!prepared.Succeeded)
            {
                if (prepared.Diagnostics.Any(
                    diagnostic =>
                        diagnostic.Code ==
                        SazExportCodes
                            .AmbiguousExport))
                {
                    ReplaceMessage(
                        report,
                        SazExportCodes
                            .AmbiguousExport,
                        AmbiguousExportMessage);
                }

                if (prepared.Diagnostics.Any(
                    diagnostic =>
                        diagnostic.Code ==
                        SazExportCodes
                            .SourceUnavailable))
                {
                    report.SetExitCode(
                        CliExitCodes.Internal);
                }

                return report;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                report.SetExitCode(
                    CliExitCodes.Cancelled);
                report.Errors.Add(
                    new OperationDiagnostic(
                        SazExportCodes.Cancelled,
                        OperationDiagnosticSeverity.Error,
                        "The archive export was cancelled before publication."));
                return report;
            }

            CliOutputFolder output;
            try
            {
                failure =
                    CliOutputFolder.TryReserve(
                        target,
                        runId,
                        out output);
                if (failure != null)
                {
                    report.Fail(
                        failure.ExitCode,
                        failure.Code,
                        failure.Message);
                    return report;
                }
            }
            catch (Exception exception) when (
                CliOutputFolder
                    .IsFileSystemFailure(exception))
            {
                report.Fail(
                    CliExitCodes.Failed,
                    "cli.output.unavailable",
                    "xmat could not create its private staging folder next to the --out folder.");
                return report;
            }

            using (output)
            {
                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    token => ExecuteAsync(
                        operation,
                        prepared.Value,
                        capture,
                        kind,
                        options,
                        output,
                        report,
                        token),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (report.Artifacts.Count != 0)
            {
                report.HumanLines.Add(
                    "xmat: exported the selected web proxy device and wrote capture.saz and summary.json to the --out folder.");
            }

            return report;
        }

        private static async Task<
            IReadOnlyList<CliStagedFile>> ExecuteAsync(
            SazExportOperation operation,
            SazExportPlan plan,
            CaptureRecordSet capture,
            SazExportKind kind,
            CliExportSaz options,
            CliOutputFolder output,
            CliReport report,
            CancellationToken cancellationToken)
        {
            HeadlessStorage.Configure(
                output.WorkPath);
            using FileStream archive =
                output.CreateFile(
                    ArchivePath);
            OperationResult<SazExportResult> result =
                await operation.ExecuteAsync(
                    new SazExportRequest(
                        plan,
                        new SazExportDestination.ToStream(
                            archive),
                        null),
                    OperationContext.None,
                    cancellationToken).ConfigureAwait(false);
            ApplyDiagnostics(
                report,
                result);
            if (!result.Succeeded)
            {
                return null;
            }

            report.ExportSaz.StoredText =
                StoredText(
                    capture,
                    options.AcceptStoredText);
            report.ExportSaz.Export =
                result.Value.Summary;
            report.ExportSaz.Sanitized =
                kind == SazExportKind.Sanitized
                    ? result.Value.Summary.Sanitized
                    : null;
            if (capture.TextSource.Semantics ==
                    CaptureTextSemantics
                        .AmbiguousUnmarked &&
                options.AcceptStoredText)
            {
                report.Warnings.Add(
                    new OperationDiagnostic(
                        SazExportCodes
                            .AmbiguousStoredText,
                        OperationDiagnosticSeverity
                            .Warning,
                        AmbiguousWarningMessage));
            }

            if (result.Value.Summary.ArchiveBytes >
                SazExportOperation
                    .AnalyzeInputCeilingBytes)
            {
                report.Warnings.Add(
                    new OperationDiagnostic(
                        SazExportCodes
                            .AnalyzeInputExceeded,
                        OperationDiagnosticSeverity
                            .Warning,
                        AnalyzeInputWarningMessage));
            }

            return new[]
            {
                new CliStagedFile(
                    "archive",
                    ArchivePath,
                    null),
                new CliStagedFile(
                    "summary",
                    SummaryPath,
                    CliJson.SerializeExportSazSummary(
                        report)),
            };
        }

        private static CaptureDeviceRecords SelectDevice(
            CaptureRecordSet capture,
            string requested,
            CliReport report)
        {
            IReadOnlyList<CaptureDeviceRecords> devices =
                capture.Devices ??
                Array.Empty<CaptureDeviceRecords>();
            if (devices.Count == 0)
            {
                report.Fail(
                    CliExitCodes.InvalidInput,
                    NoWebDeviceCode,
                    "The input has no web proxy device to export.");
                return null;
            }

            if (requested == null)
            {
                if (devices.Count == 1)
                {
                    return devices[0];
                }

                report.Fail(
                    CliExitCodes.InvalidInput,
                    DeviceRequiredCode,
                    "The input has more than one web proxy device; use --device ID.");
                return null;
            }

            CaptureDeviceRecords selected =
                devices.FirstOrDefault(
                    device =>
                        string.Equals(
                            device.Id,
                            requested,
                            StringComparison.Ordinal));
            if (selected == null)
            {
                report.Fail(
                    CliExitCodes.InvalidInput,
                    DeviceNotFoundCode,
                    "The requested web proxy device was not found.");
            }

            return selected;
        }

        private static StoredTextSummary StoredText(
            CaptureRecordSet capture,
            bool accepted) =>
            capture.InputKind == "saz"
                ? new StoredTextSummary(
                    "notApplicable",
                    "notNeeded")
                : capture.TextSource.Semantics ==
                    CaptureTextSemantics.Literal
                    ? new StoredTextSummary(
                        "literal",
                        "notNeeded")
                    : new StoredTextSummary(
                        "ambiguousUnmarked",
                        accepted
                            ? "acceptedAsStored"
                            : "notNeeded");

        private static void ApplyDiagnostics<T>(
            CliReport report,
            OperationResult<T> result)
        {
            report.SetExitCode(
                CliExitCodes.FromOutcome(
                    result.Outcome));
            foreach (OperationDiagnostic diagnostic in
                result.Diagnostics)
            {
                if (diagnostic.Severity ==
                    OperationDiagnosticSeverity.Warning)
                {
                    report.Warnings.Add(diagnostic);
                }
                else
                {
                    report.Errors.Add(diagnostic);
                }
            }
        }

        private static void ReplaceMessage(
            CliReport report,
            string code,
            string message)
        {
            int index = report.Errors.FindIndex(
                diagnostic =>
                    diagnostic.Code == code);
            if (index >= 0)
            {
                report.Errors[index] =
                    new OperationDiagnostic(
                        code,
                        OperationDiagnosticSeverity.Error,
                        message);
            }
        }
    }
}
