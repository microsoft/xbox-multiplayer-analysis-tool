// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Cli
{
    // xmat replay INPUT --request SPEC --out DIR [...] and xmat compose --url URL --out DIR [...]: the command-line
    // adapter of the shared replay operation (the GUI's adapter is the request composer). Nothing is sent unless every
    // selected request passes the operation's checks. A run that sent its requests publishes summary.json and each
    // request and response under replays/, whatever the responses were: exit code 12 when a request got no complete
    // response, otherwise 11 when --verify found a response that differs from the capture. CliStagedRun owns the
    // publication boundary.
    internal static class CliReplayCommand
    {
        internal const string SummaryPath = "summary.json";
        internal const string ReplaysPath = "replays";
        internal const string RequestFile = "request.http";
        internal const string ResponseFile = "response.http";

        // A larger --body-file is refused (exit code 3).
        internal const long MaxBodyFileBytes = 64L * 1024 * 1024;

        // replays/00001, replays/00002, ...: one folder per send, in plan order.
        internal static string FolderOf(int id) => ReplaysPath + "/" + id.ToString("D5", CultureInfo.InvariantCulture);

        internal static async Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken)
        {
            bool fromCapture = request.Command == CliCommands.Replay;
            var report = new CliReport(request.Command, runId)
            {
                Replay = new CliReplayReport(request.Replay, fromCapture ? request.Limits ?? CliLimits.Default : null),
            };

            byte[] body = null;
            if (request.Replay.BodyFile != null && !TryReadBody(request.Replay.BodyFile, report, out body))
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
                token => SendAsync(request, body, output, report, token),
                null,
                cancellationToken).ConfigureAwait(false);
            if (report.Artifacts.Count != 0)
            {
                int sent = report.Replay.Items.Count;
                int completed = report.Replay.Items.Count(item => item.Outcome == ReplayOutcome.Completed);
                report.HumanLines.Add(
                    $"xmat: sent {sent} request(s): {completed} answered, {sent - completed} without a complete response; "
                    + $"wrote {SummaryPath} and {ReplaysPath} to the --out folder.");
                if (report.ExitCode == CliExitCodes.RequestsFailed)
                {
                    report.HumanLines.Add("xmat: some requests got no complete response (exit code 12).");
                }
                else if (report.ExitCode == CliExitCodes.VerifyMismatch)
                {
                    report.HumanLines.Add("xmat: --verify: some responses differ from the capture (exit code 11).");
                }
            }

            return report;
        }

        // The body file is read before anything is reserved or sent; its path and content never reach the output.
        private static bool TryReadBody(string path, CliReport report, out byte[] body)
        {
            body = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxBodyFileBytes)
                {
                    report.Fail(CliExitCodes.InvalidInput, "replay.body.too-large", "The --body-file is larger than 64 MiB.");
                    return false;
                }

                body = new byte[stream.Length];
                stream.ReadExactly(body);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                report.Fail(CliExitCodes.InvalidInput, "replay.body.not-found", "The --body-file was not found.");
                return false;
            }
            catch (Exception ex) when (
                CliOutputFolder.IsFileSystemFailure(ex) || ex is ArgumentException or NotSupportedException)
            {
                report.Fail(CliExitCodes.InvalidInput, "replay.body.unreadable", "The --body-file could not be read.");
                return false;
            }
        }

        // Produces the results inside the staging folder; null when the run did not send its requests (the report says
        // why).
        private static async Task<IReadOnlyList<CliStagedFile>> SendAsync(
            CliRequest request,
            byte[] body,
            CliOutputFolder output,
            CliReport report,
            CancellationToken cancellationToken)
        {
            // Isolation comes first, as for analyze: from here on, legacy statics that keep files use the private staging
            // folder and never the GUI's folder under %APPDATA%.
            HeadlessStorage.Configure(output.WorkPath);
            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                CreateRequest(request, body, new FileSink(output)),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            Apply(report, result);
            if (!CliExitCodes.IsPublished(report.ExitCode))
            {
                return null;
            }

            return new[]
            {
                new CliStagedFile("summary", SummaryPath, CliJson.SerializeReplaySummary(report)),
                new CliStagedFile("replays", ReplaysPath, null),
            };
        }

        private static ReplayRequest CreateRequest(CliRequest request, byte[] body, IReplayExchangeSink sink)
        {
            CliReplay replay = request.Replay;
            var options = new ReplayOptions(
                Repeat: replay.Repeat,
                Parallelism: replay.Parallel,
                Verify: replay.Verify,
                Timeout: TimeSpan.FromSeconds(replay.TimeoutSeconds),
                MaxResponseBytes: replay.MaxResponseMib * AnalysisLimits.BytesPerMib,
                MaxRedirects: replay.FollowRedirects,
                AllowUnsafeMethods: replay.AllowUnsafeMethods,
                AllowOriginChange: replay.AllowOriginChange);
            if (request.Command == CliCommands.Compose)
            {
                // The operation applies and checks the method, headers and body of a composed request like any edit.
                return new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = replay.Url }),
                    options,
                    new ReplayEdits(Method: replay.Method, SetHeaders: replay.Headers, Body: body),
                    sink);
            }

            ReplaySelection.TryParse(replay.Selection, out ReplaySelection selection);
            return new ReplayRequest(
                ReplaySource.FromFile(
                    request.Input,
                    selection,
                    request.DeviceId,
                    (request.Limits ?? CliLimits.Default).ToAnalysisLimits()),
                options,
                new ReplayEdits(
                    Url: replay.Url,
                    Origin: replay.Origin,
                    Method: replay.Method,
                    SetHeaders: replay.Headers,
                    RemoveHeaders: replay.RemoveHeaders,
                    Body: body,
                    Unconditional: replay.Unconditional),
                sink);
        }

        // Copies the allow-listed results and maps the outcome to the exit code. On failure an informational
        // diagnostic (for example replay.cancelled) explains the exit code, so it is reported as an error.
        private static void Apply(CliReport report, OperationResult<ReplayResult> result)
        {
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
            if (failed)
            {
                if (report.Errors.Count == 0)
                {
                    report.Errors.Add(new OperationDiagnostic(
                        "cli.replay.failed", OperationDiagnosticSeverity.Error, "The replay did not complete."));
                }

                return;
            }

            ReplayResult value = result.Value;
            report.Replay.InputKind = value.InputKind;
            report.Replay.InputSha256 = value.InputSha256;
            report.Replay.Items = value.Items;
            if (value.Failed > 0)
            {
                report.SetExitCode(CliExitCodes.RequestsFailed);
            }
            else if (value.Mismatched > 0)
            {
                report.SetExitCode(CliExitCodes.VerifyMismatch);
            }
        }

        // Writes the messages of each send into the staging folder as it completes, so the run holds one at a time.
        private sealed class FileSink : IReplayExchangeSink
        {
            private readonly CliOutputFolder _output;

            internal FileSink(CliOutputFolder output)
            {
                _output = output;
            }

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                string folder = FolderOf(result.Id);
                if (exchange.Request != null)
                {
                    _output.WriteFile(folder + "/" + RequestFile, exchange.Request.WriteTo);
                }

                if (exchange.Response != null)
                {
                    _output.WriteFile(folder + "/" + ResponseFile, exchange.Response.WriteTo);
                }
            }
        }
    }
}
