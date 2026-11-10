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
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.WebServiceCapture;

namespace XMAT
{
    // How an export ended (CaptureExportWorkflow.RunAsync): the operation's result, null when the export ended before
    // ExecuteAsync; the code of its refusal or of that result, null when nothing refused it or the user declined; and the
    // keys of what it showed, in order: each question's and text's keys, the pre-copy notice's, and the destination
    // dialog's by its title's key (FILE_EXPORT_CAPTURES or FILE_EXPORT_SANITIZED).
    internal sealed record CaptureExportOutcome(
        SazExportResult Result,
        string Code,
        IReadOnlyList<string> Shown);

    // One en-us text, by its key, with the values of its placeholders.
    internal sealed class CaptureExportText
    {
        private readonly object[] _args;

        internal CaptureExportText(string key, params object[] args)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            Key = key;
            _args = args ?? Array.Empty<object>();
        }

        internal string Key { get; }

        internal IReadOnlyList<object> Args => _args;

        internal string Localize() => Localization.GetLocalizedString(Key, _args);
    }

    internal enum CaptureExportAnswers
    {
        OkCancel,
        YesNo,
    }

    // A question about an export: Yes or OK goes on, No or Cancel ends the export.
    internal sealed class CaptureExportQuestion
    {
        internal CaptureExportQuestion(
            string titleKey,
            CaptureExportAnswers answers,
            bool defaultYes,
            params CaptureExportText[] parts)
        {
            ArgumentException.ThrowIfNullOrEmpty(titleKey);
            ArgumentNullException.ThrowIfNull(parts);
            TitleKey = titleKey;
            Answers = answers;
            DefaultYes = defaultYes;
            Parts = parts;
        }

        internal string TitleKey { get; }

        internal CaptureExportAnswers Answers { get; }

        // Whether Yes or OK is the default answer; otherwise No or Cancel is.
        internal bool DefaultYes { get; }

        // The question's texts, shown one after the other as one question with one answer.
        internal IReadOnlyList<CaptureExportText> Parts { get; }
    }

    internal enum CaptureExportSeverity
    {
        Information,
        Warning,
        Error,
    }

    // The texts that end an export, shown together.
    internal sealed class CaptureExportMessage
    {
        internal CaptureExportMessage(
            string titleKey,
            CaptureExportSeverity severity,
            IReadOnlyList<CaptureExportText> parts)
        {
            ArgumentException.ThrowIfNullOrEmpty(titleKey);
            ArgumentNullException.ThrowIfNull(parts);
            TitleKey = titleKey;
            Severity = severity;
            Parts = parts;
        }

        internal string TitleKey { get; }

        internal CaptureExportSeverity Severity { get; }

        internal IReadOnlyList<CaptureExportText> Parts { get; }
    }

    // The steps of an export that run on a worker, each under the progress window.
    internal enum CaptureExportStep
    {
        Binding,
        Preparing,
        Admitting,
        Writing,
    }

    // What the progress window shows for one step, and its Cancel. Used on the UI thread only.
    internal sealed class CaptureExportProgress
    {
        private readonly Action _cancel;

        internal CaptureExportProgress(
            CaptureExportStep step,
            string titleKey,
            string statusKey,
            TimeSpan delay,
            CaptureExportText notice,
            Action cancel)
        {
            ArgumentException.ThrowIfNullOrEmpty(titleKey);
            ArgumentException.ThrowIfNullOrEmpty(statusKey);
            ArgumentNullException.ThrowIfNull(cancel);
            Step = step;
            TitleKey = titleKey;
            StatusKey = statusKey;
            Delay = delay;
            Notice = notice;
            _cancel = cancel;
        }

        internal event EventHandler Changed;

        internal CaptureExportStep Step { get; }

        internal string TitleKey { get; }

        internal string StatusKey { get; }

        // How long the step runs before the window opens. Zero opens it before the step starts: the copy kind's pre-copy
        // notice is shown first, and the step goes on without waiting for an answer to it.
        internal TimeSpan Delay { get; }

        // The copy kind's pre-copy notice, EXPORT_COPY_NOTICE, shown above the count line; otherwise null.
        internal CaptureExportText Notice { get; }

        // The count line: rows processed, rows left out included, of the rows planned (S2-V13).
        internal bool HasCount { get; private set; }

        internal int Processed { get; private set; }

        internal int Total { get; private set; }

        // False once the publication has started, which nothing cancels (3.8).
        internal bool CanCancel { get; private set; } = true;

        internal bool IsCancellationRequested { get; private set; }

        // Cancel, Escape or closing the window: cancels the export once, until its publication starts. False when that
        // did nothing.
        internal bool RequestCancellation()
        {
            if (!CanCancel || IsCancellationRequested)
            {
                return false;
            }

            IsCancellationRequested = true;
            _cancel();
            OnChanged();
            return true;
        }

        // The operation's row progress.
        internal void ReportRows(SazExportProgress rows)
        {
            HasCount = true;
            Processed = rows.Processed;
            Total = rows.Total;
            OnChanged();
        }

        // The operation's phases: once the publication has started, Cancel is off.
        internal void ReportPhase(OperationProgress phase)
        {
            if (CanCancel &&
                string.Equals(phase?.Phase, SazExportCodes.PublishingPhase, StringComparison.Ordinal))
            {
                CanCancel = false;
                OnChanged();
            }
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    // The dialogs of an export, which tests replace. Every method is called on the UI thread.
    internal interface ICaptureExportDialogService
    {
        // True for Yes or OK.
        Task<bool> AskAsync(object owner, CaptureExportQuestion question);

        // The destination dialog: the Fiddler filter, .saz as the default extension and OverwritePrompt off, since XMAT asks
        // its own question once admission has found an existing file. Null when the user cancels it.
        Task<string> ChooseDestinationAsync(object owner, SazExportKind kind);

        // Runs one worker step under the progress window: start begins the step, once; the window opens after
        // progress.Delay, or before start when that is zero, and closes once the step has ended. Cancel, Escape and closing
        // the window call progress.RequestCancellation. Completes once the step has ended, without its exception.
        Task RunAsync(object owner, CaptureExportProgress progress, Func<Task> start);

        // The texts that end an export.
        Task ShowAsync(object owner, CaptureExportMessage message);
    }

    // File > Export and File > Export Sanitized Capture... (S2, design 4.1): one workflow for both. On the UI thread it
    // takes the lease and freezes the selected tab's source before anything else; binds an unmarked source on a worker
    // and only then asks about it; plans on a worker before the destination dialog; admits the destination on a worker
    // and asks before replacing only a file that admission found; shows the copy kind's notice before the copy begins;
    // and runs the shared SazExportOperation with the plan. Its texts are fixed, with only XMAT's numbers and the user's
    // own file and folder names, through UntrustedText; its log lines hold only a code or an exception's type; and
    // nothing that an export throws reaches the dispatcher.
    internal sealed class CaptureExportWorkflow
    {
        internal const string LogPrefix = "Export failed: ";

        private const int ShownNameLimit = 260;
        private const long Mebibyte = 1_048_576;
        private const string PreparingKey = "EXPORT_PROGRESS_PREPARING";
        private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(250);

        private readonly SazExportOperation _operation;
        private readonly IOperation<SazExportRequest, SazExportResult> _runner;
        private readonly ICaptureExportDialogService _dialogs;

        // The application passes one operation twice; tests may replace the run (G16).
        internal CaptureExportWorkflow(
            SazExportOperation preparationAndAdmission,
            IOperation<SazExportRequest, SazExportResult> runner,
            ICaptureExportDialogService dialogs)
        {
            ArgumentNullException.ThrowIfNull(preparationAndAdmission);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(dialogs);
            _operation = preparationAndAdmission;
            _runner = runner;
            _dialogs = dialogs;
        }

        // XMAT's work folder, where the copy kind prepares its archive privately (3.8), after the convention of XMAT's
        // other local folders.
        internal static string DefaultWorkFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "XboxMultiplayerAnalysisTool",
            "ExportStaging");

        // The work folder that an export passes to the operation, created when it is missing. Tests use their own.
        internal string WorkFolder { get; set; } = DefaultWorkFolder;

        // The budgets of every export's plan request (3.7). Tests lower them (G17).
        internal SazExportBudgets Budgets { get; set; } = SazExportBudgets.Default;

        // Test seam (G4, G13): sees each export's frozen source on the UI thread, before anything reads it, and may wrap it.
        internal Func<SazExportSource, SazExportSource> SourceFrozen { get; set; }

        // Where an export's failures are logged: LogPrefix and a code, or an exception's type.
        internal Action<string> LogError { get; set; } =
            line => PublicUtilities.AppLog(LogLevel.ERROR, line);

        // Exports the selected tab of model; owner is the dialogs' owner. Returns how the export ended, and never throws for
        // anything but a fatal exception.
        internal Task<CaptureExportOutcome> RunAsync(
            CaptureAppModel model,
            SazExportKind kind,
            object owner)
        {
            ArgumentNullException.ThrowIfNull(model);
            return new ExportRun(this, model, kind, owner).RunAsync();
        }

        private sealed class ExportRun
        {
            private readonly CaptureExportWorkflow _workflow;
            private readonly CaptureAppModel _model;
            private readonly SazExportKind _kind;
            private readonly object _owner;
            private readonly string _titleKey;
            private readonly SazExportBudgets _budgets;
            private readonly List<string> _shown = new();
            private CancellationTokenSource _cancellation;
            private IDisposable _lease;

            internal ExportRun(
                CaptureExportWorkflow workflow,
                CaptureAppModel model,
                SazExportKind kind,
                object owner)
            {
                _workflow = workflow;
                _model = model;
                _kind = kind;
                _owner = owner;
                _titleKey = kind == SazExportKind.Sanitized ? "FILE_EXPORT_SANITIZED" : "FILE_EXPORT_CAPTURES";
                _budgets = workflow.Budgets ?? SazExportBudgets.Default;
            }

            internal async Task<CaptureExportOutcome> RunAsync()
            {
                using var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;

                // Step 1: the lease, then the frozen source, before any question or dialog. Without the lease nothing
                // happens.
                if (!_model.BeginCaptureExport(_kind, cancellation, out IDisposable lease, out SazExportSource source))
                {
                    return Outcome(null, null);
                }

                _lease = lease;
                try
                {
                    return await ExportAsync(source);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    return await FailAsync(exception);
                }
                finally
                {
                    lease.Dispose();
                }
            }

            private async Task<CaptureExportOutcome> ExportAsync(SazExportSource source)
            {
                source = _workflow.SourceFrozen?.Invoke(source) ?? source;
                CancellationToken token = _cancellation.Token;
                CaptureTextSource text = source.TextSource;
                bool unmarked = text.Semantics == CaptureTextSemantics.AmbiguousUnmarked;

                // Step 2: an unmarked source is bound before anyone is asked about it. One that cannot be bound is refused
                // at once, without a worker or a question (Q 3.2).
                CaptureTextSource bound = null;
                if (unmarked)
                {
                    if (!text.IsBindable)
                    {
                        return await RefuseAsync(SazExportCodes.SourceUnavailable);
                    }

                    try
                    {
                        bound = await StepAsync(
                            ProgressFor(CaptureExportStep.Binding, PreparingKey),
                            () => Task.Run(() => text.BindAsync(token).AsTask(), CancellationToken.None));
                    }
                    catch (CaptureTextSourceException)
                    {
                        return await RefuseAsync(SazExportCodes.SourceUnavailable);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return await RefuseAsync(SazExportCodes.Cancelled);
                    }
                }

                // Step 3: the questions about the frozen, bound source. Only a Yes or OK makes an acceptance, for that
                // source and this export (section 9).
                if (_kind == SazExportKind.Sanitized)
                {
                    // With the acceptance in it, the confirmation defaults to Cancel, as EXPORT_UNMARKED_QUESTION defaults
                    // to No; on its own it keeps its default, OK.
                    CaptureExportQuestion confirmation = unmarked
                        ? new CaptureExportQuestion(
                            _titleKey,
                            CaptureExportAnswers.OkCancel,
                            false,
                            new CaptureExportText("SANITIZED_EXPORT_CONFIRM"),
                            new CaptureExportText("SANITIZED_EXPORT_CONFIRM_UNMARKED"))
                        : new CaptureExportQuestion(
                            _titleKey,
                            CaptureExportAnswers.OkCancel,
                            true,
                            new CaptureExportText("SANITIZED_EXPORT_CONFIRM"));
                    if (!await AskAsync(confirmation))
                    {
                        return Outcome(null, null);
                    }
                }
                else if (unmarked &&
                    !await AskAsync(new CaptureExportQuestion(
                        "CAPTURE_TEXT_AMBIGUOUS_TITLE",
                        CaptureExportAnswers.YesNo,
                        false,
                        new CaptureExportText("EXPORT_UNMARKED_QUESTION"))))
                {
                    return Outcome(null, null);
                }

                StoredTextAcceptance acceptance = unmarked ? StoredTextAcceptance.For(bound) : null;

                // Step 4: the gate and the plan, on a worker, before the destination dialog (R3-3).
                var planRequest = new SazExportPlanRequest(source, _kind, acceptance, _budgets);
                OperationResult<SazExportPlan> prepared = await StepAsync(
                    ProgressFor(CaptureExportStep.Preparing, PreparingKey),
                    () => _model.PrepareCaptureExportAsync(
                        _workflow._operation,
                        planRequest,
                        OperationContext.None,
                        token));
                if (prepared.Outcome != OperationOutcome.Succeeded || prepared.Value == null)
                {
                    return await RefuseAsync(
                        prepared.Outcome == OperationOutcome.Cancelled
                            ? SazExportCodes.Cancelled
                            : CodeOf(prepared.Diagnostics) ?? prepared.Outcome.ToString());
                }

                // Step 5: the destination dialog.
                string path = await ChooseDestinationAsync();
                if (string.IsNullOrEmpty(path))
                {
                    return Outcome(null, null);
                }

                // Step 6: admission, on a worker, which writes nothing (3.8).
                string workFolder = _workflow.WorkFolder;
                CaptureArchiveTarget target;
                try
                {
                    target = await StepAsync(
                        ProgressFor(CaptureExportStep.Admitting, PreparingKey),
                        () => Task.Run(
                            () =>
                            {
                                CreateWorkFolder(workFolder);
                                return _workflow._operation.AdmitTargetAsync(
                                    path,
                                    new OperationContext(workFolder),
                                    token);
                            },
                            CancellationToken.None));
                }
                catch (SazExportException refusal)
                {
                    return await RefuseAsync(
                        refusal.Code,
                        Shown(Path.GetFileName(path)),
                        Shown(Path.GetDirectoryName(path)));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return await RefuseAsync(SazExportCodes.Cancelled);
                }

                // Steps 7 to 10, with the target held until the result's texts have been shown.
                using (target)
                {
                    return await PublishAsync(prepared.Value, target, workFolder, acceptance != null);
                }
            }

            private async Task<CaptureExportOutcome> PublishAsync(
                SazExportPlan plan,
                CaptureArchiveTarget target,
                string workFolder,
                bool asStored)
            {
                CancellationToken token = _cancellation.Token;
                bool copy = target.Kind == CaptureArchiveKind.Copy;
                string name = Shown(target.FileName);
                string folder = Shown(target.FolderName);

                // Step 7: only a regular file that admission found is asked about, and only a Yes makes the decision, bound
                // to this target (S2-V1).
                CaptureArchiveReplaceDecision decision = null;
                if (target.Entry == CaptureArchiveEntry.File)
                {
                    if (!await AskAsync(new CaptureExportQuestion(
                        _titleKey,
                        CaptureExportAnswers.YesNo,
                        false,
                        new CaptureExportText(
                            copy ? "EXPORT_COPY_REPLACE_QUESTION" : "EXPORT_REPLACE_QUESTION",
                            name,
                            folder))))
                    {
                        return Outcome(null, null);
                    }

                    decision = target.ConfirmReplace();
                }

                // Steps 8 and 9: the copy kind's notice opens the progress window before the run starts, and needs no
                // answer; then the run, with the plan, the target and its decision.
                CaptureExportProgress progress = ProgressFor(
                    CaptureExportStep.Writing,
                    _kind == SazExportKind.Sanitized ? "SANITIZED_EXPORT_SCANNING" : "EXPORT_PROGRESS_STATUS",
                    copy ? new CaptureExportText("EXPORT_COPY_NOTICE", folder) : null);
                var rows = new Progress<SazExportProgress>(progress.ReportRows);
                var phases = new Progress<OperationProgress>(progress.ReportPhase);
                var request = new SazExportRequest(
                    plan,
                    new SazExportDestination.ToTarget(target, decision),
                    rows);
                OperationResult<SazExportResult> executed;
                try
                {
                    executed = await StepAsync(
                        progress,
                        () => _model.ExportCapturesAsync(
                            _workflow._runner,
                            request,
                            new OperationContext(workFolder, phases),
                            token));
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    return await FailAsync(exception);
                }

                // Step 10: the result's texts, while the target is still held.
                return await ShowResultAsync(executed, copy, name, folder, asStored);
            }

            private async Task<CaptureExportOutcome> ShowResultAsync(
                OperationResult<SazExportResult> executed,
                bool copy,
                string name,
                string folder,
                bool asStored)
            {
                SazExportResult result = executed.Value;
                if (result == null)
                {
                    return await RefuseAsync(CodeOf(executed.Diagnostics) ?? executed.Outcome.ToString());
                }

                var parts = new List<CaptureExportText>();
                bool published = result.Publication is SazExportPublication.Created or SazExportPublication.Replaced;
                string code = result.Code ??
                    (executed.Outcome == OperationOutcome.Cancelled ? SazExportCodes.Cancelled : null);
                switch (result.Publication)
                {
                    case SazExportPublication.Created:
                        parts.Add(new CaptureExportText(
                            copy ? "EXPORT_CREATED_COPY" : "EXPORT_CREATED",
                            result.Summary?.Sessions ?? 0,
                            name));
                        break;
                    case SazExportPublication.Replaced:
                        parts.Add(new CaptureExportText(
                            copy ? "EXPORT_REPLACED_COPY" : "EXPORT_REPLACED",
                            result.Summary?.Sessions ?? 0,
                            name));
                        break;
                    case SazExportPublication.Uncertain:
                        parts.Add(UncertainText(result.Uncertainty, name, folder));
                        break;
                    default:
                        // Not published, or a written stream, which a target never gives.
                        parts.Add(TextFor(result.Publication == SazExportPublication.NotPublished ? code : null, name, folder));
                        break;
                }

                foreach (SazExportResidue residue in result.Residues ?? Array.Empty<SazExportResidue>())
                {
                    string residueName = Shown(residue.Name);
                    string residueFolder = Shown(residue.FolderName);
                    parts.Add(residue.Kind == SazExportResidueKind.Staging
                        ? new CaptureExportText("EXPORT_RESIDUE_STAGING", residueName, residueFolder)
                        : new CaptureExportText(
                            result.Publication == SazExportPublication.NotPublished
                                ? "EXPORT_RESIDUE_RESTORE"
                                : "EXPORT_RESIDUE_EARLIER",
                            residueName,
                            residueFolder));
                }

                if (published)
                {
                    AddPublishedTexts(parts, result.Summary, asStored);
                }

                if (!published && code != SazExportCodes.Cancelled)
                {
                    Log(code ?? executed.Outcome.ToString());
                }

                if (result.Residues?.Count > 0)
                {
                    Log(SazExportCodes.CleanupFailed);
                }

                CaptureExportSeverity severity = CaptureExportSeverity.Error;
                if (published || result.Publication == SazExportPublication.Uncertain)
                {
                    severity = published && !(result.Residues?.Count > 0)
                        ? CaptureExportSeverity.Information
                        : CaptureExportSeverity.Warning;
                }
                else if (code == SazExportCodes.Cancelled)
                {
                    severity = CaptureExportSeverity.Information;
                }

                await ShowEndAsync(severity, parts);
                return Outcome(result, code);
            }

            // After a published archive: the sanitized export's own summary, or the plain export's reminder, then a note
            // for each count above zero (4.4).
            private void AddPublishedTexts(List<CaptureExportText> parts, SazExportSummary summary, bool asStored)
            {
                if (_kind == SazExportKind.Sanitized)
                {
                    if (summary?.Sanitized is SanitizedExportSummary sanitized)
                    {
                        parts.Add(new CaptureExportText(
                            "SANITIZED_EXPORT_COMPLETE",
                            sanitized.Sessions,
                            sanitized.RedactedValues,
                            sanitized.OmittedBodies,
                            sanitized.OmittedHeaders));
                    }
                }
                else
                {
                    parts.Add(new CaptureExportText("EXPORT_NOT_SANITIZED"));
                }

                if (summary != null)
                {
                    AddNote(parts, "EXPORT_NOTE_SKIPPED", summary.RowsWithoutRequest);
                    AddNote(parts, "EXPORT_NOTE_OVERSIZED", summary.RowsOversized);
                    AddNote(parts, "EXPORT_NOTE_UNREADABLE", summary.RowsUnreadable);
                    AddNote(parts, "EXPORT_NOTE_BODIES", summary.BodiesNotKept);
                    AddNote(parts, "EXPORT_NOTE_UNDECODABLE", summary.BodiesUndecodable);
                    AddNote(parts, "EXPORT_NOTE_RENUMBERED", summary.RenumberedSessions);
                    AddNote(parts, "EXPORT_NOTE_REPLACED", summary.ReplacedHeadCharacters);
                    AddNote(parts, "EXPORT_NOTE_DROPPED", summary.DroppedHeaderLines);
                    AddNote(parts, "EXPORT_NOTE_REFORMATTED", summary.ReformattedHeaderLines);
                    AddNote(parts, "EXPORT_NOTE_CLIENT", summary.DiscardedClientAddresses);
                    AddNote(parts, "EXPORT_NOTE_TIMES", summary.AdjustedTimes);
                    if (summary.ArchiveBytes > SazExportOperation.AnalyzeInputCeilingBytes)
                    {
                        parts.Add(new CaptureExportText("EXPORT_NOTE_ANALYZE_INPUT"));
                    }
                }

                if (asStored)
                {
                    parts.Add(new CaptureExportText("EXPORT_NOTE_AS_STORED"));
                }
            }

            private static void AddNote(List<CaptureExportText> parts, string key, long count)
            {
                if (count > 0)
                {
                    parts.Add(new CaptureExportText(key, count));
                }
            }

            private CaptureExportText TextFor(string code, string name, string folder) => code switch
            {
                SazExportCodes.SazRequired => new CaptureExportText("EXPORT_SAZ_REQUIRED"),
                SazExportCodes.UnsupportedLocation => new CaptureExportText("EXPORT_UNSUPPORTED"),
                SazExportCodes.NoPrivateStaging => new CaptureExportText("EXPORT_NO_PRIVATE_STAGING"),
                SazExportCodes.AmbiguousExport => new CaptureExportText("EXPORT_UNMARKED_SOURCE"),
                SazExportCodes.SourceUnavailable => new CaptureExportText("EXPORT_SOURCE_UNAVAILABLE"),
                SazExportCodes.TooManyRows => new CaptureExportText("EXPORT_TOO_MANY_ROWS", _budgets.MaxRows),
                SazExportCodes.TooLarge => new CaptureExportText(
                    "EXPORT_TOO_LARGE",
                    _budgets.MaxTotalStoredBytes / Mebibyte),
                SazExportCodes.InvalidStartLine => new CaptureExportText("EXPORT_INVALID_START_LINE"),
                SazExportCodes.SanitizerInvalidHttp => new CaptureExportText("SANITIZED_EXPORT_INVALID_HTTP"),
                SazExportCodes.Exists => new CaptureExportText("EXPORT_EXISTS", name),
                SazExportCodes.Changed => new CaptureExportText("EXPORT_CHANGED", name),
                SazExportCodes.NotAFile => new CaptureExportText("EXPORT_NOT_A_FILE", name),
                SazExportCodes.ReadOnly => new CaptureExportText("EXPORT_READ_ONLY", name),
                SazExportCodes.InUse => new CaptureExportText("EXPORT_IN_USE", name),
                SazExportCodes.AccessDenied => new CaptureExportText("EXPORT_ACCESS_DENIED", name),
                SazExportCodes.LocationUnverified => new CaptureExportText("EXPORT_LOCATION_UNVERIFIED", name, folder),
                SazExportCodes.Cancelled => new CaptureExportText("EXPORT_CANCELLED"),
                _ => new CaptureExportText("EXPORT_FAILED"),
            };

            private static CaptureExportText UncertainText(
                SazExportUncertainty uncertainty,
                string name,
                string folder) => uncertainty switch
                {
                    SazExportUncertainty.CopyCreated => new CaptureExportText("EXPORT_UNCERTAIN_COPY_CREATED", name),
                    SazExportUncertainty.CopyReplaced => new CaptureExportText("EXPORT_UNCERTAIN_COPY_REPLACED", name),
                    SazExportUncertainty.CopyUnverified => new CaptureExportText(
                        "EXPORT_UNCERTAIN_COPY_UNVERIFIED",
                        name,
                        folder),
                    _ => new CaptureExportText("EXPORT_UNCERTAIN", name, folder),
                };

            // A refusal or a failure before the run: its text, and a log line unless it was a cancellation.
            private async Task<CaptureExportOutcome> RefuseAsync(
                string code,
                string name = null,
                string folder = null)
            {
                bool cancelled = code == SazExportCodes.Cancelled;
                if (!cancelled)
                {
                    Log(code);
                }

                await ShowEndAsync(
                    cancelled ? CaptureExportSeverity.Information : CaptureExportSeverity.Error,
                    new[] { TextFor(code, name ?? string.Empty, folder ?? string.Empty) });
                return Outcome(null, code);
            }

            // Anything that an export throws: EXPORT_FAILED, and a log line with the exception's type only.
            private async Task<CaptureExportOutcome> FailAsync(Exception exception)
            {
                Log(exception.GetType().FullName);
                await ShowEndAsync(
                    CaptureExportSeverity.Error,
                    new[] { new CaptureExportText("EXPORT_FAILED") });
                return Outcome(null, null);
            }

            // The texts that end the export, unless XMAT has taken the export back to close, or is shutting down: then
            // it shows nothing.
            private async Task ShowEndAsync(CaptureExportSeverity severity, IReadOnlyList<CaptureExportText> parts)
            {
                if (_model.IsShuttingDown ||
                    (_lease != null && _model.IsCaptureDataOperationRevoked(_lease)))
                {
                    return;
                }

                _shown.AddRange(parts.Select(part => part.Key));
                try
                {
                    await _workflow._dialogs.ShowAsync(_owner, new CaptureExportMessage(_titleKey, severity, parts));
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    Log(exception.GetType().FullName);
                }
            }

            private async Task<bool> AskAsync(CaptureExportQuestion question)
            {
                _shown.AddRange(question.Parts.Select(part => part.Key));
                return await _workflow._dialogs.AskAsync(_owner, question);
            }

            private async Task<string> ChooseDestinationAsync()
            {
                _shown.Add(_titleKey);
                return await _workflow._dialogs.ChooseDestinationAsync(_owner, _kind);
            }

            // Runs one worker step under the progress window and returns its result. A step that has started always ends
            // before the export goes on, whatever the window does.
            private async Task<T> StepAsync<T>(CaptureExportProgress progress, Func<Task<T>> start)
            {
                if (progress.Notice != null)
                {
                    _shown.Add(progress.Notice.Key);
                }

                Task<T> work = null;
                try
                {
                    await _workflow._dialogs.RunAsync(_owner, progress, () => work ??= start());
                }
                catch (Exception exception) when (work != null && !ExceptionUtilities.IsFatal(exception))
                {
                    Log(exception.GetType().FullName);
                }

                if (work == null)
                {
                    throw new InvalidOperationException("The progress window did not start the export step.");
                }

                return await work;
            }

            private CaptureExportProgress ProgressFor(
                CaptureExportStep step,
                string statusKey,
                CaptureExportText notice = null) =>
                new(
                    step,
                    _titleKey,
                    statusKey,
                    notice == null ? ProgressDelay : TimeSpan.Zero,
                    notice,
                    Cancel);

            private void Cancel()
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The export has ended.
                }
            }

            private void Log(string codeOrType)
            {
                try
                {
                    _workflow.LogError(LogPrefix + codeOrType);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    // A log that fails must not change how the export ends.
                }
            }

            private CaptureExportOutcome Outcome(SazExportResult result, string code) =>
                new(result, code, _shown.ToArray());

            private static string CodeOf(IReadOnlyList<OperationDiagnostic> diagnostics) =>
                diagnostics?.FirstOrDefault(diagnostic => diagnostic.Severity == OperationDiagnosticSeverity.Error)?.Code ??
                (diagnostics is { Count: > 0 } ? diagnostics[0].Code : null);

            private static string Shown(string text) =>
                UntrustedText.Show(text ?? string.Empty, ShownNameLimit);

            // Admission refuses a work folder that it cannot hold (no-private-staging), so one that cannot be created is
            // still passed.
            private static void CreateWorkFolder(string folder)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Admission refuses it.
                }
            }
        }
    }
}
