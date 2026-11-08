// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.CaptureFiles;

namespace XMAT.Cli
{
    internal static class CliMarkCaptureTextCommand
    {
        internal const string CapturePath =
            "capture.xmatcap";
        internal const string SummaryPath =
            "summary.json";

        internal static async Task<CliReport> RunAsync(
            CliRequest request,
            string runId,
            CancellationToken cancellationToken,
            Func<SafeFileHandle, (string FileSystemName, string DevicePath)> probeStorageFacts = null,
            ICliCommitObserver commitObserver = null,
            Action<string> configureHeadlessStorage = null,
            Action<MarkCaptureTextOperation> configureOperation = null,
            Action<CliOutputFolder> outputPrepared = null,
            Action<string> reserveCheckpoint = null)
        {
            var report =
                new CliReport(
                    CliCommands.MarkCaptureText,
                    runId)
                {
                    MarkCaptureText =
                        new CliMarkCaptureTextReport
                        {
                            Interpretation =
                                request.Interpretation.Value,
                            MaxInputMib =
                                request.MaxInputMib,
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
                        target,
                        probeStorageFacts);
            }
            if (failure != null)
            {
                report.Fail(
                    failure.ExitCode,
                    failure.Code,
                    failure.Message);
                return report;
            }

            var operation =
                new MarkCaptureTextOperation();
            configureOperation?.Invoke(
                operation);
            long maxInputBytes =
                checked(
                    request.MaxInputMib *
                    AnalysisLimits.BytesPerMib);
            OperationResult<MarkCaptureTextPlan> prepared =
                await operation.PrepareAsync(
                    request.Input,
                    request.Interpretation.Value,
                    maxInputBytes,
                    OperationContext.None,
                    cancellationToken).ConfigureAwait(false);
            if (operation.InputSha256 != null)
            {
                report.MarkCaptureText.InputKind =
                    "xmatcap";
                report.MarkCaptureText.InputSha256 =
                    operation.InputSha256;
            }
            Apply(
                report,
                prepared);
            if (!prepared.Succeeded)
            {
                return report;
            }

            using MarkCaptureTextPlan plan =
                prepared.Value;
            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return report;
            }

            CliOutputFolder output;
            try
            {
                failure =
                    CliOutputFolder.TryReserve(
                        target,
                        runId,
                        out output,
                        reserveCheckpoint);
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
                CliOutputFolder.IsFileSystemFailure(
                    exception))
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
                        plan,
                        output,
                        report,
                        configureHeadlessStorage,
                        outputPrepared,
                        token),
                    commitObserver,
                    cancellationToken).ConfigureAwait(false);
            }

            if (report.Artifacts.Count != 0)
            {
                report.HumanLines.Add(
                    "xmat: created a marked capture copy and wrote capture.xmatcap and summary.json to the --out folder.");
            }

            return report;
        }

        private static async Task<IReadOnlyList<CliStagedFile>> ExecuteAsync(
            MarkCaptureTextOperation operation,
            MarkCaptureTextPlan plan,
            CliOutputFolder output,
            CliReport report,
            Action<string> configureHeadlessStorage,
            Action<CliOutputFolder> outputPrepared,
            CancellationToken cancellationToken)
        {
            // Test seam; production always configures the real isolated headless storage.
            (configureHeadlessStorage ??
                HeadlessStorage.Configure)(
                output.WorkPath);
            using (output.CreateFile(
                CapturePath))
            {
                // The operation opens this closed, empty entry relative to the held staging folder.
            }
            outputPrepared?.Invoke(
                output);

            using var markOutput =
                new MarkCaptureTextOutput(
                    output.DuplicateStagingHandle(),
                    CapturePath);
            OperationResult<MarkCaptureTextResult> result =
                await operation.ExecuteAsync(
                    new MarkCaptureTextRequest(
                        plan,
                        markOutput),
                    OperationContext.None,
                    cancellationToken).ConfigureAwait(false);
            Apply(
                report,
                result);
            if (!result.Succeeded)
            {
                return null;
            }

            report.MarkCaptureText.InputKind =
                "xmatcap";
            report.MarkCaptureText.InputSha256 =
                result.Value.InputSha256;
            report.MarkCaptureText.OutputSha256 =
                result.Value.OutputSha256;
            report.MarkCaptureText.Counts =
                result.Value.Counts;
            return new[]
            {
                new CliStagedFile(
                    "capture",
                    CapturePath,
                    null),
                new CliStagedFile(
                    "summary",
                    SummaryPath,
                    CliJson.SerializeMarkCaptureTextSummary(
                        report)),
            };
        }

        private static void Apply<T>(
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
                    report.Warnings.Add(
                        diagnostic);
                }
                else
                {
                    report.Errors.Add(
                        diagnostic);
                }
            }
        }
    }
}
