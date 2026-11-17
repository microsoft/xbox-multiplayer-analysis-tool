// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Replay;

namespace XMAT.Cli
{
    // xmat curl (INPUT --request SPEC | --request-file FILE | --url URL) --out DIR --include-sensitive [...]: exports
    // requests as PowerShell 7 scripts that run the inbox curl.exe, and sends nothing. The requests are gathered and
    // edited as save-request gathers them (ReplayOperation.PrepareForExport), which makes no sender, and each is prepared
    // by RequestCurlExporter, which refuses what curl is not known to send the same way. A script holds its captured
    // request, credentials and cookies included, so without --include-sensitive the run is refused before anything is read
    // or reserved; a request exported for another origin than its baseline needs --allow-origin-change. A run that exported
    // every request publishes summary.json, scripts/ and, for bodies that are not empty, bodies/; CliStagedRun owns the
    // publication boundary.
    internal static class CliCurlCommand
    {
        internal const string SummaryPath = "summary.json";
        internal const string ScriptsPath = "scripts";
        internal const string BodiesPath = "bodies";

        // scripts/00001.ps1, scripts/00002.ps1, ...: one script per request, in selection order.
        internal static string ScriptOf(int id) => ScriptsPath + "/" + Ordinal(id) + ".ps1";

        // bodies/00001.bin: the body of script 00001, when it is not empty.
        internal static string BodyOf(int id) => BodiesPath + "/" + Ordinal(id) + ".bin";

        internal static async Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken)
        {
            CliReplay curl = request.Replay;
            var report = new CliReport(request.Command, runId)
            {
                Curl = new CliCurlReport(curl, request.Input != null ? request.Limits ?? CliLimits.Default : null),
            };

            // Consent comes first: without it, no input is opened and no output is reserved.
            if (!curl.IncludeSensitive)
            {
                report.Fail(
                    CliExitCodes.InvalidInput,
                    CurlExportCodes.ConsentRequired,
                    "A cURL script holds its request as captured, credentials, cookies and body included; pass " +
                        "--include-sensitive to export it. Nothing was read or written.");
                return report;
            }

            // The body (the --body-file, or the --form files) and the request file are read before anything is reserved, and
            // only after consent; their paths and content never reach the output, and a file that cannot be used is refused
            // with exit code 3.
            CliRequestBody requestBody;
            RequestFileReadResult file = null;
            try
            {
                (requestBody, CliFailure bodyFailure) =
                    await CliRequestBody.PrepareAsync(curl, cancellationToken).ConfigureAwait(false);
                if (bodyFailure != null)
                {
                    report.Fail(bodyFailure.ExitCode, bodyFailure.Code, bodyFailure.Message);
                    return report;
                }

                if (curl.RequestFile != null)
                {
                    file = await RequestFileReader.ReadFileAsync(curl.RequestFile, cancellationToken).ConfigureAwait(false);
                    report.Curl.InputKind = CliReplayCommand.RequestFileInputKind;
                    if (file.Code != null)
                    {
                        report.Fail(CliExitCodes.InvalidInput, file.Code, CliReplayCommand.RequestFileMessage(file.Code));
                        return report;
                    }

                    report.Curl.InputSha256 = file.Sha256;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return report;
            }

            CliFailure targetFailure = CliOutputFolder.CheckTarget(request.Output, out CliOutputTarget target);
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
                CliFailure refused = CliOutputFolder.TryReserve(target, runId, out output);
                if (refused != null)
                {
                    report.Fail(refused.ExitCode, refused.Code, refused.Message);
                    return report;
                }
            }
            catch (Exception ex) when (CliOutputFolder.IsFileSystemFailure(ex))
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
                    token => Task.FromResult(Export(request, requestBody, file, output, report, token)),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (report.Artifacts.Count != 0)
            {
                report.HumanLines.Add(
                    $"xmat: exported {report.Curl.Scripts.Count} cURL script(s) to {ScriptsPath} in the --out folder with " +
                    "their captured requests, which can hold credentials and cookies. Each runs the inbox curl.exe from " +
                    $"PowerShell {RequestCurlExporter.MinimumPowerShellVersion} or later.");
            }

            return report;
        }

        private static string Ordinal(int id) => id.ToString("D5", CultureInfo.InvariantCulture);

        // Writes every script, and the body file of each body that is not empty, inside the staging folder; null when the
        // run exported nothing (the report says why).
        private static IReadOnlyList<CliStagedFile> Export(
            CliRequest request,
            CliRequestBody requestBody,
            RequestFileReadResult file,
            CliOutputFolder output,
            CliReport report,
            CancellationToken cancellationToken)
        {
            // Isolation comes first, as for analyze: legacy statics that keep files use the private staging folder.
            HeadlessStorage.Configure(output.WorkPath);
            CliReplay curl = request.Replay;
            OperationResult<ReplayExport> prepared = new ReplayOperation().PrepareForExport(
                CliSaveRequestCommand.CreateRequest(request, requestBody, file?.Content), cancellationToken);
            foreach (OperationDiagnostic diagnostic in prepared.Diagnostics)
            {
                if (diagnostic.Severity == OperationDiagnosticSeverity.Warning)
                {
                    report.Warnings.Add(diagnostic);
                }
                else if (diagnostic.Severity == OperationDiagnosticSeverity.Error || !prepared.Succeeded)
                {
                    report.Errors.Add(diagnostic);
                }
            }

            if (!prepared.Succeeded)
            {
                report.SetExitCode(CliExitCodes.FromOutcome(prepared.Outcome));
                if (report.Errors.Count == 0)
                {
                    report.Errors.Add(new OperationDiagnostic(
                        "cli.curl.failed", OperationDiagnosticSeverity.Error, "The requests could not be exported."));
                }

                return null;
            }

            if (file == null)
            {
                report.Curl.InputKind = prepared.Value.InputKind;
                report.Curl.InputSha256 = prepared.Value.InputSha256;
            }

            if (!curl.AllowOriginChange && prepared.Value.Items.Any(item => item.Draft.ChangesOrigin))
            {
                report.Fail(
                    CliExitCodes.InvalidInput,
                    "replay.policy.origin-change",
                    "A request would be exported with its captured content for another origin than the one it was captured " +
                        "or saved for, and origin changes are not allowed; nothing was exported.");
                return null;
            }

            // A request file keeps the HTTP version that it asks for, unless --http-version replaces it.
            ReplayHttpVersion httpVersion =
                file != null && !curl.HttpVersionSet ? file.Content.HttpVersion : curl.HttpVersion;
            var transport = new ReplayTransportOptions(httpVersion);
            bool anyBody = false;
            int id = 0;
            foreach (ReplayExportItem item in prepared.Value.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                id++;
                CurlRequest script;
                try
                {
                    script = RequestCurlExporter.Prepare(item.Draft, transport, 0, cancellationToken);
                }
                catch (RequestIncompleteException)
                {
                    report.Fail(
                        CliExitCodes.InvalidInput,
                        RequestFileCodes.RequestIncomplete,
                        "A request still has parts that its request file left out (its method, URL, headers or body); " +
                            "supply them before exporting it. Nothing was exported.");
                    return null;
                }
                catch (CurlExportException ex)
                {
                    report.Fail(
                        CliExitCodes.InvalidInput,
                        ex.Code,
                        ex.Reason == CurlUnsupported.Http2Exact
                            ? "The request file asks for HTTP/2 only, which curl is not known to send the same way; pass " +
                                "--http-version auto or 1.1 to export it. Nothing was exported."
                            : ex.Message + " Nothing was exported.");
                    return null;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
                {
                    // The message of such an exception can name a header, so it is never shown.
                    report.Fail(
                        CliExitCodes.InvalidInput,
                        RequestFileCodes.RequestInvalid,
                        "A request is not valid for a cURL script, such as an invalid method, URL, header or body, or a " +
                            "header value that is not well-formed Unicode; nothing was exported.");
                    return null;
                }

                // The body file and the script's length and hash of it come from the same bytes.
                string sidecar = null;
                if (!script.Body.IsEmpty)
                {
                    output.WriteFile(BodyOf(id), stream => stream.Write(script.Body.Span));
                    sidecar = "../" + BodyOf(id);
                    anyBody = true;
                }

                output.WriteFile(
                    ScriptOf(id),
                    stream => RequestCurlExporter.Write(script, CurlScriptForm.File, sidecar, stream, cancellationToken));
                report.Curl.Scripts.Add(new CliCurlScript(
                    id,
                    item.DeviceId,
                    item.RequestNumber,
                    script.Body.Length,
                    httpVersion,
                    script.NeedsUnicodeCurl,
                    RequestFileWriter.Recognize(item.Draft)));
            }

            var files = new List<CliStagedFile>
            {
                new("summary", SummaryPath, CliJson.SerializeCurlSummary(report)),
                new("scripts", ScriptsPath, null),
            };
            if (anyBody)
            {
                files.Add(new CliStagedFile("bodies", BodiesPath, null));
            }

            return files;
        }
    }
}
