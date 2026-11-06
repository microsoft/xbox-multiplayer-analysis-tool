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
using XMAT.Operations.Capture;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Cli
{
    // xmat replay INPUT --request SPEC --out DIR [...] and xmat compose --url URL --out DIR [...]: the command-line
    // adapter of the shared replay operation (the GUI's adapter is the request composer). Nothing is sent unless every
    // selected request passes the operation's checks. A run that sent its requests publishes summary.json and each
    // request and response under replays/, whatever the responses were, with --verify-diff each verified send's
    // diff.json, and with --save-saz every send as a session of replays.saz, a Fiddler archive: exit code 12 when a request
    // got no complete response, otherwise 11 when --verify found a response that differs from the capture. CliStagedRun
    // owns the publication boundary.
    internal static class CliReplayCommand
    {
        internal const string SummaryPath = "summary.json";
        internal const string ReplaysPath = "replays";
        internal const string SazPath = "replays.saz";
        internal const string RequestFile = "request.http";
        internal const string ResponseFile = "response.http";
        internal const string DiffFile = "diff.json";

        // replays/00001, replays/00002, ...: one folder per send, in plan order.
        internal static string FolderOf(int id) => ReplaysPath + "/" + id.ToString("D5", CultureInfo.InvariantCulture);

        internal static Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken) =>
            RunAsync(request, runId, configureStorage: true, cancellationToken);

        // configureStorage is false only for a test that runs the command in its own process, whose legacy statics keep their
        // files where they already do: HeadlessStorage is configured once, before any of them is used.
        internal static async Task<CliReport> RunAsync(
            CliRequest request, string runId, bool configureStorage, CancellationToken cancellationToken)
        {
            bool fromCapture = request.Command == CliCommands.Replay;
            var report = new CliReport(request.Command, runId)
            {
                Replay = new CliReplayReport(request.Replay, fromCapture ? request.Limits ?? CliLimits.Default : null),
            };

            // The body is made before anything is reserved or sent: the --body-file, or the --form files, are read, and their
            // paths and contents never reach the output. A larger file than ReplayBodyFile.MaxBytes (64 MiB), or a form
            // larger than that, is refused with exit code 3 (CliRequestBody).
            CliRequestBody prepared;
            try
            {
                (prepared, CliFailure bodyFailure) =
                    await CliRequestBody.PrepareAsync(request.Replay, cancellationToken).ConfigureAwait(false);
                if (bodyFailure != null)
                {
                    report.Fail(bodyFailure.ExitCode, bodyFailure.Code, bodyFailure.Message);
                    return report;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return report;
            }

            // A request file (compose --request-file) is read next, as untrusted data: only its kind and SHA-256 reach the
            // output. A file that cannot be read, or is not a valid request file, is refused with exit code 3.
            RequestFileReadResult file = null;
            if (request.Replay.RequestFile != null)
            {
                try
                {
                    file = await RequestFileReader.ReadFileAsync(request.Replay.RequestFile, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    report.Cancel();
                    return report;
                }

                report.Replay.InputKind = RequestFileInputKind;
                if (file.Code != null)
                {
                    report.Fail(CliExitCodes.InvalidInput, file.Code, RequestFileMessage(file.Code));
                    return report;
                }

                report.Replay.InputSha256 = file.Sha256;
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
                    token => SendAsync(request, prepared, file, output, report, configureStorage, token),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (report.Artifacts.Count != 0)
            {
                int sent = report.Replay.Items.Count;
                int completed = report.Replay.Items.Count(item => item.Outcome == ReplayOutcome.Completed);
                string written = request.Replay.SaveSaz
                    ? $"{SummaryPath}, {ReplaysPath} and {SazPath}"
                    : $"{SummaryPath} and {ReplaysPath}";
                report.HumanLines.Add(
                    $"xmat: sent {sent} request(s): {completed} answered, {sent - completed} without a complete response; "
                    + $"wrote {written} to the --out folder.");
                if (request.Replay.VerifyDiff)
                {
                    report.HumanLines.Add(
                        $"xmat: --verify-diff: wrote {report.Replay.Details.Count} {DiffFile} file(s), one for each verified "
                        + "request; they can hold captured content.");
                }

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

        internal static string BodyFileMessage(string code) =>
            code switch
            {
                ReplayBodyFile.TooLargeCode => "The --body-file is larger than 64 MiB.",
                ReplayBodyFile.NotFoundCode => "The --body-file was not found.",
                _ => "The --body-file could not be read.",
            };

        // The input kind that a run from a request file reports.
        internal const string RequestFileInputKind = "request-file";

        // What each request file failure says, never what the file holds.
        internal static string RequestFileMessage(string code) =>
            code switch
            {
                RequestFileCodes.NotFound => "The --request-file was not found.",
                RequestFileCodes.TooLarge => "The --request-file is larger than 96 MiB.",
                RequestFileCodes.Unsupported => "The --request-file is a request file of a version that xmat does not read.",
                RequestFileCodes.Invalid =>
                    "The --request-file is not a valid request file (xmat-request/1): it has an unknown, missing or repeated " +
                        "member, or a value that a request file cannot hold.",
                _ => "The --request-file could not be read.",
            };

        // Produces the results inside the staging folder; null when the run did not send its requests (the report says
        // why). With --save-saz the archive is completed only when the run has results to publish, and closed, completed
        // or abandoned, before this returns, so that a discard can remove it.
        private static async Task<IReadOnlyList<CliStagedFile>> SendAsync(
            CliRequest request,
            CliRequestBody prepared,
            RequestFileReadResult file,
            CliOutputFolder output,
            CliReport report,
            bool configureStorage,
            CancellationToken cancellationToken)
        {
            // Isolation comes first, as for analyze: from here on, legacy statics that keep files use the private staging
            // folder and never the GUI's folder under %APPDATA%.
            if (configureStorage)
            {
                HeadlessStorage.Configure(output.WorkPath);
            }

            using SazSink saz = request.Replay.SaveSaz ? SazSink.Create(output, cancellationToken) : null;
            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                CreateRequest(request, prepared, file?.Content, new FileSink(output, report.Replay, saz)),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            Apply(report, result);
            if (file != null)
            {
                report.Replay.InputKind = RequestFileInputKind;
                report.Replay.InputSha256 = file.Sha256;
            }

            if (!CliExitCodes.IsPublished(report.ExitCode))
            {
                return null;
            }

            var files = new List<CliStagedFile>
            {
                new CliStagedFile("summary", SummaryPath, CliJson.SerializeReplaySummary(report)),
                new CliStagedFile("replays", ReplaysPath, null),
            };
            if (saz != null)
            {
                saz.Finish(cancellationToken);
                files.Add(new CliStagedFile("saz", SazPath, null));
            }

            return files;
        }

        private static ReplayRequest CreateRequest(
            CliRequest request, CliRequestBody prepared, RequestFileContent file, IReplayExchangeSink sink)
        {
            CliReplay replay = request.Replay;

            // A request file's HTTP version is the run's unless --http-version says otherwise; nothing else in the file
            // is transport or consent.
            ReplayHttpVersion httpVersion =
                file != null && !replay.HttpVersionSet ? file.HttpVersion : replay.HttpVersion;
            var options = new ReplayOptions(
                Repeat: replay.Repeat,
                Parallelism: replay.Parallel,
                Verify: replay.Verify,
                Timeout: TimeSpan.FromSeconds(replay.TimeoutSeconds),
                MaxResponseBytes: replay.MaxResponseMib * AnalysisLimits.BytesPerMib,
                MaxRedirects: replay.FollowRedirects,
                AllowUnsafeMethods: replay.AllowUnsafeMethods,
                AllowOriginChange: replay.AllowOriginChange,
                AllowConnectTo: replay.AllowConnectTo,
                Transport: new ReplayTransportOptions(httpVersion, replay.ConnectTo, replay.Proxy),
                VerifyDiff: replay.VerifyDiff,
                AllowProxy: replay.AllowProxy);
            if (request.Command == CliCommands.Compose)
            {
                // The operation applies and checks the method, headers and body of a composed request like any edit; with
                // --sequence it expands the URL template once per number after checking the plan size. A request file's
                // request is the draft instead, and the edits complete what it left out: --url, --method, --clear-headers
                // and --body-file or --empty-body; the operation refuses to send anything that is still left out.
                RequestReplayDraft draft = file != null
                    ? RequestReplayDraft.FromRequestFile(file)
                    : new RequestReplayDraft { Url = replay.Url };
                return new ReplayRequest(
                    ReplaySequence.TryParse(replay.Sequence, out ReplaySequence sequence)
                        ? ReplaySource.FromTemplate(draft, sequence)
                        : ReplaySource.FromDraft(draft),
                    options,
                    new ReplayEdits(
                        Url: file != null ? replay.Url : null,
                        Method: replay.Method,
                        SetHeaders: replay.Headers,
                        RemoveHeaders: replay.RemoveHeaders,
                        Body: replay.EmptyBody ? Array.Empty<byte>() : prepared.Body,
                        Form: prepared.Form,
                        ClearHeaders: replay.ClearHeaders),
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
                    Body: prepared.Body,
                    Form: prepared.Form,
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

        // Writes the messages of each send into the staging folder as it completes, so the run holds one at a time, with
        // --verify-diff each verified send's detail (diff.json), whose state alone the report keeps, and with --save-saz the
        // send's session (SazSink).
        private sealed class FileSink : IReplayExchangeSink
        {
            private readonly CliOutputFolder _output;
            private readonly CliReplayReport _report;
            private readonly SazSink _saz;

            internal FileSink(CliOutputFolder output, CliReplayReport report, SazSink saz)
            {
                _output = output;
                _report = report;
                _saz = saz;
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

                if (exchange.VerifyDiff is VerifyDiff diff)
                {
                    _output.WriteFile(folder + "/" + DiffFile, stream => VerifyDiffJson.Write(diff, stream));
                    _report.Details[result.Id] = diff.State;
                }

                _saz?.Add(result, exchange);
            }
        }

        // replays.saz, written as the sends complete: each is a session as ReplaySaz makes it, added as it completes, in
        // completion order with its plan id, so the run holds only the send at hand (SazArchiveWriter). Finish completes the
        // archive and closes it, once the run has results to publish; disposing it first abandons the archive, which the
        // discard of the staging folder then removes. The run's token stops the writing (OperationCanceledException), and
        // a failure to write is the run's (cli.output.write-failed).
        private sealed class SazSink : IDisposable
        {
            private readonly FileStream _file;
            private readonly Stream _stream;
            private readonly SazArchiveWriter _writer;
            private readonly CancellationToken _cancellationToken;

            private SazSink(FileStream file, Stream stream, SazArchiveWriter writer, CancellationToken cancellationToken)
            {
                _file = file;
                _stream = stream;
                _writer = writer;
                _cancellationToken = cancellationToken;
            }

            internal static SazSink Create(CliOutputFolder output, CancellationToken cancellationToken)
            {
                FileStream file = output.CreateFile(SazPath);
                try
                {
                    Stream stream = SazStreamSeam.Value?.Invoke(file) ?? file;
                    return new SazSink(file, stream, new SazArchiveWriter(stream), cancellationToken);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }

            internal void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                SazSession session = ReplaySaz.ToSession(result, exchange);
                if (session != null)
                {
                    _writer.Add(session, _cancellationToken);
                }
            }

            internal void Finish(CancellationToken cancellationToken)
            {
                _writer.Complete(cancellationToken);
                _stream.Flush();
                _file.Flush(flushToDisk: true);
                Dispose();
            }

            public void Dispose()
            {
                _writer.Dispose();
                _stream.Dispose();
                _file.Dispose();
            }
        }

        // Test seam, null in production: wraps the stream that replays.saz is written through, so a test can fail or cancel
        // a run at a point of its writing. AsyncLocal, so it reaches only the run of the test that sets it.
        internal static readonly AsyncLocal<Func<Stream, Stream>> SazStreamSeam = new();
    }
}
