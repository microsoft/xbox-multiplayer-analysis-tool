// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capabilities;

namespace XMAT.Cli
{
    internal static class CliCleanupStates
    {
        // The run created nothing that needed cleaning up.
        internal const string NotNeeded = "notNeeded";

        // The staging folder was published as --out or removed.
        internal const string Completed = "completed";

        // The staging folder could not be removed (exit code 7).
        internal const string Failed = "failed";
    }

    internal sealed record CliArtifact(string Kind, string Path);

    internal sealed record CliFailure(int ExitCode, string Code, string Message);

    internal sealed record CliCheck(string Id, bool Passed, string Code = null);

    // The allow-listed results of an analysis, copied from AnalysisSummary; never AnalysisResult.Legacy.
    internal sealed class CliAnalysisReport
    {
        internal string InputKind { get; set; }

        internal string InputSha256 { get; set; }

        // The effective limits of the run, once its command line was valid.
        internal CliLimits Limits { get; set; }

        internal IReadOnlyList<DeviceSummary> Devices { get; set; } = Array.Empty<DeviceSummary>();

        internal int TotalCalls { get; set; }

        internal int TotalFindings { get; set; }
    }

    internal sealed class CliDoctorReport
    {
        internal CliDoctorReport(string target)
        {
            Target = target;
        }

        internal string Target { get; }

        internal List<CliCheck> Checks { get; } = new();

        internal IReadOnlyList<Capability> Capabilities { get; set; } = Array.Empty<Capability>();

        internal string Framework { get; set; }

        internal string Architecture { get; set; }
    }

    // Everything one xmat run reports: the JSON document on standard output (CliJson) and the text on standard error.
    internal sealed class CliReport
    {
        internal const string Schema = "xmat-cli/1";

        internal CliReport(string command, string runId)
        {
            Command = command;
            RunId = runId;
        }

        // The version of xmat: the version of the XMAT assembly that holds the command line.
        internal static string ToolVersion { get; } = typeof(CliReport).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        internal string Command { get; }

        internal string RunId { get; }

        // Changed only through SetExitCode and Fail, which never replace exit code 7.
        internal int ExitCode { get; private set; }

        internal string Status => CliExitCodes.Status(ExitCode);

        internal string CleanupState { get; set; } = CliCleanupStates.NotNeeded;

        internal List<CliArtifact> Artifacts { get; } = new();

        internal List<OperationDiagnostic> Warnings { get; } = new();

        internal List<OperationDiagnostic> Errors { get; } = new();

        // Lines for people on standard error. Never captured data, exception messages or paths.
        internal List<string> HumanLines { get; } = new();

        internal CliAnalysisReport Analysis { get; set; }

        internal CliDoctorReport Doctor { get; set; }

        internal IReadOnlyList<CliCommandHelp> Commands { get; set; }

        internal string Version { get; set; }

        // Records an error and its exit code. A cleanup failure (exit code 7) wins over every other outcome, so a later
        // failure or cancellation never replaces it.
        internal void Fail(int exitCode, string code, string message)
        {
            SetExitCode(exitCode);
            Errors.Add(new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, message));
        }

        // Cancellation before the results were published (exit code 130, unless cleanup failed).
        internal void Cancel() =>
            Fail(CliExitCodes.Cancelled, "cli.cancelled", "The run was cancelled before its results were published; nothing was published.");

        internal void SetExitCode(int exitCode)
        {
            if (ExitCode != CliExitCodes.CleanupFailed)
            {
                ExitCode = exitCode;
            }
        }

        internal static CliReport ForParseError(string runId, CliParseResult parsed)
        {
            var report = new CliReport(parsed.Command, runId) { ExitCode = CliExitCodes.Usage };
            if (parsed.Command == CliCommands.Analyze)
            {
                report.Analysis = new CliAnalysisReport();
            }

            report.Errors.Add(parsed.Error);
            return report;
        }

        internal static CliReport ForVersion(string runId)
        {
            var report = new CliReport(CliCommands.Version, runId) { ExitCode = CliExitCodes.Success, Version = ToolVersion };
            report.HumanLines.Add("xmat " + ToolVersion);
            return report;
        }

        // parsed is null when the error happened while the command line was parsed. An analyze run whose command line
        // was valid still reports its effective limits, as every other analyze failure does.
        internal static CliReport ForInternalError(string runId, CliParseResult parsed, Exception exception)
        {
            var report = new CliReport(parsed?.Command, runId);
            if (parsed is { Succeeded: true, Request.Command: CliCommands.Analyze })
            {
                report.Analysis = new CliAnalysisReport { Limits = parsed.Request.Limits ?? CliLimits.Default };
            }

            report.Fail(CliExitCodes.Internal, "cli.internal", "xmat stopped because of an internal error.");

            // The exception type helps a bug report; its message can hold paths or captured data, so it is never shown.
            report.HumanLines.Add($"xmat: internal error ({exception.GetType().FullName}).");
            return report;
        }
    }
}
