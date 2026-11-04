// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Analysis;

namespace XMAT.Cli
{
    // xmat analyze INPUT --out DIR [--device ID] [--fail-on findings] [--max-input-mib N] [--max-data-mib N]
    // [--max-record-mib N] [--max-records N] is the command-line adapter of the shared analysis operation (the GUI's
    // adapter is XboxLiveCaptureAnalyzer). It publishes summary.json only when the run succeeds (exit code 0, or 10
    // with --fail-on findings); CliStagedRun owns the publication boundary.
    internal static class CliAnalyzeCommand
    {
        internal const string SummaryPath = "summary.json";
        private const string Language = "en-us";

        internal static async Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken)
        {
            var report = new CliReport(CliCommands.Analyze, runId)
            {
                Analysis = new CliAnalysisReport { Limits = request.Limits ?? CliLimits.Default },
            };
            AnalysisAssetSet assets = LoadPrerequisites(report);
            if (assets == null)
            {
                return report;
            }

            CliFailure targetFailure = CliOutputFolder.CheckTarget(request.Output, out string target);
            if (targetFailure != null)
            {
                report.Fail(targetFailure.ExitCode, targetFailure.Code, targetFailure.Message);
                return report;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return report;
            }

            CliOutputFolder output;
            try
            {
                output = CliOutputFolder.Reserve(target, runId);
            }
            catch (Exception ex) when (CliOutputFolder.IsFileSystemFailure(ex))
            {
                report.Fail(
                    CliExitCodes.Failed,
                    "cli.output.unavailable",
                    "xmat could not create its private staging folder next to the --out folder.");
                return report;
            }

            await CliStagedRun.CompleteAsync(
                report,
                output,
                token => AnalyzeAsync(request, output, assets, report, token),
                null,
                cancellationToken).ConfigureAwait(false);
            if (report.Artifacts.Count != 0)
            {
                report.HumanLines.Add(
                    $"xmat: analyzed {report.Analysis.TotalCalls} calls on {report.Analysis.Devices.Count} device(s) and found "
                    + $"{report.Analysis.TotalFindings} finding(s); wrote {SummaryPath} to the --out folder.");
                if (report.ExitCode == CliExitCodes.FindingsFound)
                {
                    report.HumanLines.Add("xmat: --fail-on findings: the analysis reported findings (exit code 10).");
                }
            }

            return report;
        }

        // Everything analyze needs from its installation is checked before anything is written: the English strings
        // and the analysis data, which is loaded once here (the shared loader the GUI uses) and handed to the operation.
        private static AnalysisAssetSet LoadPrerequisites(CliReport report)
        {
            CliCheck language = CliPrerequisites.CheckLanguage(AppContext.BaseDirectory);
            if (!language.Passed)
            {
                report.Fail(
                    CliExitCodes.Prerequisite,
                    language.Code,
                    "The English strings next to xmat.exe are missing or invalid. Run xmat doctor --for analyze.");
                return null;
            }

            try
            {
                return AnalysisAssets.Load(AppContext.BaseDirectory);
            }
            catch (AnalysisAssetException ex)
            {
                report.Fail(
                    CliExitCodes.Prerequisite,
                    string.IsNullOrEmpty(ex.Code) ? CliPrerequisites.InvalidDataCode : ex.Code,
                    "The analysis data next to xmat.exe is missing or invalid. Run xmat doctor --for analyze.");
                return null;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                report.Fail(
                    CliExitCodes.Prerequisite,
                    CliPrerequisites.InvalidDataCode,
                    "The analysis data next to xmat.exe is missing or invalid. Run xmat doctor --for analyze.");
                return null;
            }
        }

        // Produces the results inside the staging folder; null when the run did not succeed (the report says why).
        private static async Task<IReadOnlyList<CliStagedFile>> AnalyzeAsync(
            CliRequest request,
            CliOutputFolder output,
            AnalysisAssetSet assets,
            CliReport report,
            CancellationToken cancellationToken)
        {
            // Isolation comes first: from here on, legacy statics that keep files (the application log, settings,
            // analysis folders) use the private staging folder and never the GUI's folder under %APPDATA%.
            HeadlessStorage.Configure(output.WorkPath);
            Localization.LoadLanguage(Language);

            OperationResult<AnalysisResult> result = await new AnalyzeCaptureOperation(assets).ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromFile(request.Input),
                    new AnalysisOptions(DeviceId: request.DeviceId, Limits: report.Analysis.Limits.ToAnalysisLimits())),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            Apply(report, result, request.FailOnFindings);
            if (report.ExitCode != CliExitCodes.Success && report.ExitCode != CliExitCodes.FindingsFound)
            {
                return null;
            }

            return new[] { new CliStagedFile("summary", SummaryPath, CliJson.SerializeSummary(report)) };
        }

        // Copies the allow-listed summary and maps the outcome to the exit code. On failure an informational
        // diagnostic (for example analysis.no-data) explains the exit code, so it is reported as an error.
        private static void Apply(CliReport report, OperationResult<AnalysisResult> result, bool failOnFindings)
        {
            AnalysisSummary summary = result.Value?.Summary;
            if (summary != null)
            {
                report.Analysis.InputKind = summary.InputKind;
                report.Analysis.InputSha256 = summary.InputSha256;
                report.Analysis.Devices = summary.Devices ?? Array.Empty<DeviceSummary>();
                report.Analysis.TotalCalls = summary.TotalCalls;
                report.Analysis.TotalFindings = summary.TotalFindings;
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
                    "cli.analysis.failed", OperationDiagnosticSeverity.Error, "The analysis did not complete."));
            }
            else if (!failed && failOnFindings && report.Analysis.TotalFindings > 0)
            {
                report.SetExitCode(CliExitCodes.FindingsFound);
            }
        }
    }
}
