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
using XMAT.WebServiceCapture;

namespace XMAT.Cli
{
    // xmat save-request (INPUT --request SPEC | --request-file FILE | --url URL) --out DIR [...]: saves requests as request
    // files (xmat-request/1) and sends nothing. The requests are gathered and edited as replay gathers them
    // (ReplayOperation.PrepareForExport), which makes no sender. A template, the default, keeps no captured value;
    // --include-sensitive saves everything, and then a request whose origin an edit changed needs --allow-origin-change,
    // since its file's URL becomes the origin that a later change needs consent for. A run that saved every request
    // publishes summary.json and requests/; CliStagedRun owns the publication boundary.
    internal static class CliSaveRequestCommand
    {
        internal const string SummaryPath = "summary.json";
        internal const string RequestsPath = "requests";

        private static readonly RequestFileRecognized NothingRecognized =
            new(Array.Empty<string>(), Array.Empty<string>(), 0);

        // requests/00001.json, requests/00002.json, ...: one file per request, in selection order.
        internal static string FileOf(int id) =>
            RequestsPath + "/" + id.ToString("D5", CultureInfo.InvariantCulture) + ".json";

        internal static async Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken)
        {
            CliReplay save = request.Replay;
            var report = new CliReport(request.Command, runId)
            {
                SaveRequest = new CliSaveRequestReport(save, request.Input != null ? request.Limits ?? CliLimits.Default : null),
            };
            CliFailure targetFailure = CliOutputFolder.CheckTarget(request.Output, out CliOutputTarget target);
            if (targetFailure != null)
            {
                report.Fail(targetFailure.ExitCode, targetFailure.Code, targetFailure.Message);
                return report;
            }

            // The body (the --body-file, or the --form files) and the request file are read before anything is reserved;
            // their paths and content never reach the output, and a file that cannot be used is refused with exit code 3.
            CliRequestBody requestBody;
            RequestFileReadResult file = null;
            try
            {
                (requestBody, CliFailure bodyFailure) =
                    await CliRequestBody.PrepareAsync(save, cancellationToken).ConfigureAwait(false);
                if (bodyFailure != null)
                {
                    report.Fail(bodyFailure.ExitCode, bodyFailure.Code, bodyFailure.Message);
                    return report;
                }

                if (save.RequestFile != null)
                {
                    file = await RequestFileReader.ReadFileAsync(save.RequestFile, cancellationToken).ConfigureAwait(false);
                    report.SaveRequest.InputKind = CliReplayCommand.RequestFileInputKind;
                    if (file.Code != null)
                    {
                        report.Fail(CliExitCodes.InvalidInput, file.Code, CliReplayCommand.RequestFileMessage(file.Code));
                        return report;
                    }

                    report.SaveRequest.InputSha256 = file.Sha256;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return report;
            }

            ReplayExport prepared = Prepare(request, requestBody, file, report, cancellationToken);
            if (prepared == null)
            {
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
                    token => Task.FromResult(Save(request, file, prepared, output, report, token)),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (report.Artifacts.Count != 0)
            {
                int saved = report.SaveRequest.Requests.Count;
                report.HumanLines.Add(save.IncludeSensitive
                    ? $"xmat: saved {saved} request file(s) with their captured content to {RequestsPath} in the --out " +
                        "folder; they can hold credentials and cookies."
                    : $"xmat: saved {saved} request template(s) to {RequestsPath} in the --out folder; they keep only each " +
                        "request's shape (a standard method and the HTTP version), so supply what they leave out before " +
                        "sending them.");
            }

            return report;
        }

        // Gathers and checks every frozen request before the command reserves output.
        private static ReplayExport Prepare(
            CliRequest request,
            CliRequestBody requestBody,
            RequestFileReadResult file,
            CliReport report,
            CancellationToken cancellationToken)
        {
            CliReplay save = request.Replay;
            OperationResult<ReplayExport> result = new ReplayOperation().PrepareForExport(
                CreateRequest(request, requestBody, file?.Content, save.AcceptStoredText), cancellationToken);
            foreach (OperationDiagnostic diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == OperationDiagnosticSeverity.Warning)
                {
                    report.Warnings.Add(diagnostic);
                }
                else if (diagnostic.Severity == OperationDiagnosticSeverity.Error || !result.Succeeded)
                {
                    report.Errors.Add(diagnostic);
                }
            }

            if (!result.Succeeded)
            {
                report.SetExitCode(CliExitCodes.FromOutcome(result.Outcome));
                if (report.Errors.Count == 0)
                {
                    report.Errors.Add(new OperationDiagnostic(
                        "cli.save-request.failed", OperationDiagnosticSeverity.Error, "The requests could not be saved."));
                }

                return null;
            }

            if (file == null)
            {
                report.SaveRequest.InputKind = result.Value.InputKind;
                report.SaveRequest.InputSha256 = result.Value.InputSha256;
            }

            report.SaveRequest.StoredText = result.Value.StoredText ?? CliStoredText.NotApplicable;

            RequestFileProfile profile = save.IncludeSensitive ? RequestFileProfile.Content : RequestFileProfile.Template;
            if (profile == RequestFileProfile.Content && !save.AllowOriginChange &&
                result.Value.Items.Any(item => item.Draft.ChangesOrigin))
            {
                report.Fail(
                    CliExitCodes.InvalidInput,
                    "replay.policy.origin-change",
                    "A request would be saved with its captured content for another origin than the one it was captured or " +
                        "saved for, and origin changes are not allowed; nothing was saved.");
                return null;
            }

            return result.Value;
        }

        // Writes every prepared request file inside the staging folder.
        private static IReadOnlyList<CliStagedFile> Save(
            CliRequest request,
            RequestFileReadResult file,
            ReplayExport prepared,
            CliOutputFolder output,
            CliReport report,
            CancellationToken cancellationToken)
        {
            // Isolation comes first, as for analyze: legacy statics that keep files use the private staging folder.
            HeadlessStorage.Configure(output.WorkPath);
            CliReplay save = request.Replay;
            RequestFileProfile profile = save.IncludeSensitive ? RequestFileProfile.Content : RequestFileProfile.Template;
            ReplayHttpVersion httpVersion = file != null && !save.HttpVersionSet
                ? file.Content.HttpVersion
                : save.HttpVersion;
            int id = 0;
            foreach (ReplayExportItem item in prepared.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                id++;
                try
                {
                    output.WriteFile(
                        FileOf(id),
                        stream => RequestFileWriter.Write(item.Draft, httpVersion, profile, stream, cancellationToken));
                }
                catch (RequestFileException ex)
                {
                    report.Fail(
                        CliExitCodes.InvalidInput,
                        ex.Code,
                        ex.Code == RequestFileCodes.Incomplete
                            ? "A request still has parts that its request file left out; supply them, or save it without " +
                                "--include-sensitive as a template. Nothing was saved."
                            : "A request is not valid as a request file, such as an invalid method, URL, header or body; " +
                                "nothing was saved.");
                    return null;
                }

                report.SaveRequest.Requests.Add(new CliSavedRequest(
                    id,
                    item.DeviceId,
                    item.RequestNumber,
                    profile == RequestFileProfile.Content ? "content" : "template",
                    RequestFileWriter.Omitted(item.Draft, profile),
                    profile == RequestFileProfile.Content ? RequestFileWriter.Recognize(item.Draft) : NothingRecognized));
            }

            return new[]
            {
                new CliStagedFile("summary", SummaryPath, CliJson.SerializeSaveRequestSummary(report)),
                new CliStagedFile("requests", RequestsPath, null),
            };
        }

        // The source and edits of save-request and curl: a capture file's selection, a request file's request, or a new one
        // whose URL is the --url edit. acceptStoredText is the command's --accept-stored-text, which only a capture file's
        // unmarked text needs; every other source ignores it.
        internal static ReplayRequest CreateRequest(
            CliRequest request, CliRequestBody requestBody, RequestFileContent file, bool acceptStoredText)
        {
            CliReplay save = request.Replay;
            var options = new ReplayOptions(AcceptStoredText: acceptStoredText);
            var edits = new ReplayEdits(
                Url: save.Url,
                Method: save.Method,
                SetHeaders: save.Headers,
                RemoveHeaders: save.RemoveHeaders,
                Body: save.EmptyBody ? Array.Empty<byte>() : requestBody.Body,
                Form: requestBody.Form,
                Unconditional: save.Unconditional,
                ClearHeaders: save.ClearHeaders);
            if (request.Input != null)
            {
                ReplaySelection.TryParse(save.Selection, out ReplaySelection selection);
                return new ReplayRequest(
                    ReplaySource.FromFile(
                        request.Input,
                        selection,
                        request.DeviceId,
                        (request.Limits ?? CliLimits.Default).ToAnalysisLimits()),
                    options,
                    edits);
            }

            // A request file's request, or a new one whose URL is the --url edit.
            RequestReplayDraft draft = file != null ? RequestReplayDraft.FromRequestFile(file) : new RequestReplayDraft();
            return new ReplayRequest(ReplaySource.FromDraft(draft), options, edits);
        }
    }
}
