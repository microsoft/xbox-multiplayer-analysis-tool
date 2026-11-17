// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
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

        // The HTTP version that a run asks for, as the operation takes it (ReplayOptions.EffectiveTransport): --http-version,
        // or else a request file's, which Windows authentication makes exactly HTTP/1.1 when it is Automatic; HTTP/2 only
        // stays as asked, and the operation then refuses it. The output reports this version, never another.
        internal static ReplayHttpVersion RequestedHttpVersion(CliReplay replay, RequestFileContent file) =>
            new ReplayOptions(Transport: new ReplayTransportOptions(HttpVersionOf(replay, file), WindowsAuth: replay.WindowsAuth))
                .EffectiveTransport.HttpVersion;

        // A request file's HTTP version is the run's unless --http-version says otherwise; nothing else in the file is
        // transport or consent.
        private static ReplayHttpVersion HttpVersionOf(CliReplay replay, RequestFileContent file) =>
            file != null && !replay.HttpVersionSet ? file.HttpVersion : replay.HttpVersion;

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
            CliFailure targetFailure = CliOutputFolder.CheckTarget(request.Output, out CliOutputTarget target);
            if (targetFailure != null)
            {
                report.Fail(targetFailure.ExitCode, targetFailure.Code, targetFailure.Message);
                return report;
            }

            if (request.Replay.RawFile != null)
            {
                await RunRawAsync(request, report, target, runId, configureStorage, cancellationToken).ConfigureAwait(false);
                ReportPublished(request, report);
                return report;
            }

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
                report.Replay.HttpVersion = RequestedHttpVersion(request.Replay, file.Content);
            }

            // After the non-mutating target check, freeze and admit every parsed destination before output is reserved.
            // Execution below uses this plan without reopening the capture or request file.
            var operation = new ReplayOperation();
            ReplayOperation.PreparedRun preparedRun = await operation.PrepareRunAsync(
                CreateRequest(request, prepared, file?.Content, sink: null),
                cancellationToken).ConfigureAwait(false);
            if (preparedRun.Failure != null)
            {
                Apply(report, preparedRun.Failure);
                return report;
            }

            ReportPreparedInput(report, preparedRun);
            CliOutputFolder output = Reserve(target, report, runId, cancellationToken);
            if (output == null)
            {
                return report;
            }

            using (output)
            {
                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    token => SendAsync(
                        request, operation, preparedRun, file, output, report, configureStorage, token),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            ReportPublished(request, report);
            return report;
        }

        // Reserves the private staging folder for a target that CheckTarget accepted; null when it cannot.
        private static CliOutputFolder Reserve(
            CliOutputTarget target, CliReport report, string runId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return null;
            }

            try
            {
                CliFailure refused = CliOutputFolder.TryReserve(target, runId, out CliOutputFolder output);
                if (refused != null)
                {
                    report.Fail(refused.ExitCode, refused.Code, refused.Message);
                    return null;
                }

                return output;
            }
            catch (Exception ex) when (CliOutputFolder.IsFileSystemFailure(ex))
            {
                report.Fail(
                    CliExitCodes.Failed,
                    "cli.output.unavailable",
                    "xmat could not create its private staging folder next to the --out folder.");
                return null;
            }
        }

        // What a published run says on standard error: how many sends it made and answered, and why its exit code is not 0.
        private static void ReportPublished(CliRequest request, CliReport report)
        {
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

                if (report.ExitCode == CliExitCodes.Cancelled)
                {
                    report.HumanLines.Add(
                        "xmat: the run was cancelled after a raw send began: the sends that began were published, and no "
                        + "further send was started (exit code 130).");
                }
                else if (report.ExitCode == CliExitCodes.RequestsFailed)
                {
                    report.HumanLines.Add("xmat: some requests got no complete response (exit code 12).");
                }
                else if (report.ExitCode == CliExitCodes.VerifyMismatch)
                {
                    report.HumanLines.Add("xmat: --verify: some responses differ from the capture (exit code 11).");
                }
            }
        }

        // compose --raw: the file is read once and admitted as a raw request (RawRequest.FromBytes, with
        // --fix-content-length) before anything is reserved or sent; only its kind and SHA-256 reach the output, never its
        // path or bytes. Once a send began, the run's sends are published even if it is cancelled (CompleteAfterSendsAsync).
        private static async Task RunRawAsync(
            CliRequest request,
            CliReport report,
            CliOutputTarget target,
            string runId,
            bool configureStorage,
            CancellationToken cancellationToken)
        {
            CliReplay replay = request.Replay;
            report.Replay.InputKind = RawFileInputKind;
            RawImageResult admitted;
            try
            {
                (ReadOnlyMemory<byte> bytes, string sha256, string code) =
                    await ReadRawFileAsync(replay.RawFile, cancellationToken).ConfigureAwait(false);
                if (code != null)
                {
                    report.Fail(CliExitCodes.InvalidInput, code, RawRequest.Message(code));
                    return;
                }

                report.Replay.InputSha256 = sha256;
                admitted = RawRequest.FromBytes(replay.RawTarget, bytes, replay.FixContentLength, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
                return;
            }

            if (admitted.Code != null)
            {
                report.Fail(CliExitCodes.InvalidInput, admitted.Code, RawRequest.Message(admitted.Code));
                if (admitted.Line is int line)
                {
                    report.HumanLines.Add(
                        "xmat: --raw: the request was refused at its line " + line.ToString(CultureInfo.InvariantCulture) + ".");
                }

                return;
            }

            CliOutputFolder output = Reserve(target, report, runId, cancellationToken);
            if (output == null)
            {
                return;
            }

            using (output)
            {
                await CliStagedRun.CompleteAfterSendsAsync(
                    report,
                    output,
                    (token, finalization) => SendRawAsync(request, admitted.Image, output, report, configureStorage, token, finalization),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // The --raw file, read once through a stream whose reads never go past RawRequest.MaxFileBytes (64 KiB + 65 MiB):
        // a larger file, or one that grows past it while it is read, is refused (replay.raw.input-too-large), and only the
        // bytes read are used. Its SHA-256 is computed as they are read.
        internal static async Task<(ReadOnlyMemory<byte> Bytes, string Sha256, string Code)> ReadRawFileAsync(
            string path, CancellationToken cancellationToken)
        {
            try
            {
                await using var file = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > RawRequest.MaxFileBytes)
                {
                    return (default, null, RawCodes.InputTooLarge);
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var bytes = new MemoryStream((int)file.Length);
                var chunk = new byte[81_920];
                while (true)
                {
                    int allowed = (int)Math.Min(chunk.Length, RawRequest.MaxFileBytes + 1 - bytes.Length);
                    int count = await file.ReadAsync(chunk.AsMemory(0, allowed), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    if (bytes.Length + count > RawRequest.MaxFileBytes)
                    {
                        return (default, null, RawCodes.InputTooLarge);
                    }

                    bytes.Write(chunk, 0, count);
                    hash.AppendData(chunk, 0, count);
                }

                return (bytes.GetBuffer().AsMemory(0, (int)bytes.Length), Convert.ToHexStringLower(hash.GetHashAndReset()), null);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
                    NotSupportedException)
            {
                return (default, null, RawCodes.FileUnreadable);
            }
        }

        // The raw run's sends on the run's token, then its files on finalization, a token that the run's cancellation does
        // not cancel (CliStagedRun.CompleteAfterSendsAsync): the SAZ sessions too, so a send that began is in every output.
        // --allow-raw consents to this image and target only, through the plan that Preview checked. Without it the
        // operation refuses the send (replay.policy.raw).
        private static async Task<IReadOnlyList<CliStagedFile>> SendRawAsync(
            CliRequest request,
            RawRequestImage image,
            CliOutputFolder output,
            CliReport report,
            bool configureStorage,
            CancellationToken cancellationToken,
            CancellationToken finalization)
        {
            if (configureStorage)
            {
                HeadlessStorage.Configure(output.WorkPath);
            }

            CliReplay replay = request.Replay;
            using SazSink saz = replay.SaveSaz ? SazSink.Create(output, finalization) : null;
            var sink = new FileSink(output, report.Replay, saz, image);
            var operation = new ReplayOperation();
            RawOperationSeam.Value?.Invoke(operation);
            // A raw file is independent input: its bytes are the request as written, so its text is Literal.
            ReplaySource source = ReplaySource.FromRaw(image, global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal);
            var options = new ReplayOptions(
                Repeat: replay.Repeat,
                Parallelism: replay.Parallel,
                Timeout: TimeSpan.FromSeconds(replay.TimeoutSeconds),
                MaxResponseBytes: replay.MaxResponseMib * AnalysisLimits.BytesPerMib,
                AllowUnsafeMethods: replay.AllowUnsafeMethods,
                AllowConnectTo: replay.AllowConnectTo,
                Transport: new ReplayTransportOptions(ReplayHttpVersion.Http11, replay.ConnectTo));
            if (replay.AllowRaw)
            {
                OperationResult<ReplayPlanPreview> plan = operation.Preview(source, options, cancellationToken);
                if (!plan.Succeeded)
                {
                    report.SetExitCode(CliExitCodes.FromOutcome(plan.Outcome));
                    report.Errors.AddRange(plan.Diagnostics);
                    return null;
                }

                options = options with { AllowRaw = ReplayRawConsent.For(plan.Value) };
            }

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(source, options, Sink: sink), OperationContext.None, cancellationToken).ConfigureAwait(false);
            string inputSha256 = report.Replay.InputSha256;
            Apply(report, result);

            // The run's input is the raw file, whatever the operation reports for its source.
            report.Replay.InputKind = RawFileInputKind;
            report.Replay.InputSha256 = inputSha256;
            if (result.Outcome == OperationOutcome.Cancelled)
            {
                // Only a run whose every send that began is in its outputs publishes them. With none, or
                // with one whose writing did not complete, such as an archive session that the finalization's bound
                // stopped, the run is discarded as cancelled, and its archive is never completed. A write that failed is
                // the operation's to report, cancelled or not, and ends the run with exit code 6.
                if (sink.Items.Count == 0 || sink.Incomplete)
                {
                    report.Cancel();
                    return null;
                }

                // Every send that began is in the sink once the operation has returned.
                report.Replay.Items = sink.Items.OrderBy(item => item.Id).ToArray();
                report.Errors.Add(new OperationDiagnostic(
                    CancelledAfterSendCode, OperationDiagnosticSeverity.Error, CancelledAfterSendMessage));
            }
            else if (!CliExitCodes.IsPublished(report.ExitCode))
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
                saz.Finish(finalization);
                files.Add(new CliStagedFile("saz", SazPath, null));
            }

            return files;
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

        // The input kind that compose --raw reports, with the SHA-256 of its file, never of the prepared request.
        internal const string RawFileInputKind = "raw-file";

        // A raw run cancelled after a send began: it published the sends that began (exit code 130).
        internal const string CancelledAfterSendCode = "cli.cancelled-after-send";

        internal const string CancelledAfterSendMessage =
            "The run was cancelled after a raw send began; the sends that began were published, and no further send was " +
                "started.";

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
            ReplayOperation operation,
            ReplayOperation.PreparedRun prepared,
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
            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                prepared,
                new FileSink(output, report.Replay, saz),
                OperationContext.None,
                cancellationToken).ConfigureAwait(false);
            Apply(report, result, prepared.Diagnostics);
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
                Transport: new ReplayTransportOptions(
                    HttpVersionOf(replay, file), replay.ConnectTo, replay.Proxy, replay.ClientCertificate, replay.WindowsAuth),
                VerifyDiff: replay.VerifyDiff,
                AllowProxy: replay.AllowProxy,
                AllowClientCertificate: replay.AllowClientCertificate,
                AllowWindowsAuth: replay.AllowWindowsAuth,
                AcceptStoredText: replay.AcceptStoredText);
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

        // A successfully admitted capture is reportable before output reservation: its kind, hash and read warnings are
        // known. StoredText remains publication-only (CliJson).
        private static void ReportPreparedInput(CliReport report, ReplayOperation.PreparedRun prepared)
        {
            if (prepared.Plan.InputKind != null)
            {
                report.Replay.InputKind = prepared.Plan.InputKind;
                report.Replay.InputSha256 = prepared.Plan.InputSha256;
            }

            foreach (OperationDiagnostic diagnostic in prepared.Diagnostics.Where(
                item => item.Severity == OperationDiagnosticSeverity.Warning))
            {
                report.Warnings.Add(diagnostic);
            }
        }

        // Copies the allow-listed results and maps the outcome to the exit code. alreadyReported is the warnings that
        // ReportPreparedInput reported before reservation; those same objects are skipped here. On failure an informational
        // diagnostic (for example replay.cancelled) explains the exit code, so it is reported as an error.
        private static void Apply(
            CliReport report,
            OperationResult<ReplayResult> result,
            IReadOnlyList<OperationDiagnostic> alreadyReported = null)
        {
            bool failed = result.Outcome != OperationOutcome.Succeeded;
            foreach (OperationDiagnostic diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == OperationDiagnosticSeverity.Warning)
                {
                    if (alreadyReported == null ||
                        !alreadyReported.Any(reported => ReferenceEquals(reported, diagnostic)))
                    {
                        report.Warnings.Add(diagnostic);
                    }
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
            report.Replay.StoredText = value.StoredText;
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
        // send's session (SazSink). A raw send's request.http is its image, the attempted request, whatever its write state
        // may be. Items keeps every send that reached the sink, which a raw run cancelled after a send began reports.
        private sealed class FileSink : IReplayExchangeSink
        {
            private readonly CliOutputFolder _output;
            private readonly CliReplayReport _report;
            private readonly SazSink _saz;
            private readonly RawRequestImage _raw;
            private readonly List<ReplayItemResult> _items = new();
            private int _begun;

            internal FileSink(CliOutputFolder output, CliReplayReport report, SazSink saz, RawRequestImage raw = null)
            {
                _output = output;
                _report = report;
                _saz = saz;
                _raw = raw;
            }

            internal IReadOnlyList<ReplayItemResult> Items => _items;

            // Whether a send was given and its writing did not complete, its files or its archive session: the outputs then
            // lack that send for good. The operation gives the sends one at a time.
            internal bool Incomplete => _begun != _items.Count;

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                _begun++;
                exchange = ExchangeSeam.Value?.Invoke(exchange) ?? exchange;

                // Every send that reaches the outputs has its request, which summary.json names: a raw send's image, or the
                // request as replay described it. One without it would name a file that was never written, so the run fails
                // as an internal error and publishes nothing (CliStagedRun).
                if (exchange.Raw ? _raw == null : exchange.Request == null)
                {
                    throw new InvalidOperationException("A send reached the outputs without its request.");
                }

                string folder = FolderOf(result.Id);
                if (exchange.Raw)
                {
                    _output.WriteFile(folder + "/" + RequestFile, _raw.WriteTo);
                }
                else
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
                _items.Add(result);
            }
        }

        // replays.saz, written as the sends complete: each is a session as ReplaySaz makes it, added as it completes, in
        // completion order with its plan id, so the run holds only the send at hand (SazArchiveWriter). Finish completes the
        // archive and closes it, once the run has results to publish; disposing it first abandons the archive, which the
        // discard of the staging folder then removes. Its token stops the writing (OperationCanceledException): the run's,
        // or a raw run's finalization token, which the run's cancellation does not cancel. A failure to write is the run's
        // (cli.output.write-failed).
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

        // Test seam, null in production: given compose --raw's operation before it runs, so a test can order its sends'
        // events (ReplayOperation.RawSenderFactory and RawSendEnded). AsyncLocal, as SazStreamSeam.
        internal static readonly AsyncLocal<Action<ReplayOperation>> RawOperationSeam = new();

        // Test seam, null in production: gives the file sink another exchange in place of each one that a send gives it,
        // so a test can hand it one without its request. AsyncLocal, as SazStreamSeam.
        internal static readonly AsyncLocal<Func<ReplayExchange, ReplayExchange>> ExchangeSeam = new();
    }
}
