// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Capture;

namespace XMAT.Tests.Fixtures
{
    // The answers that a test gives an export's questions and dialogs, and what the export showed.
    internal sealed class CaptureExportScript
    {
        private readonly List<string> _shown = new();

        // What the destination dialog returns; null cancels the dialog.
        internal string Destination { get; set; }

        // The answer to File > Export Sanitized Capture...'s confirmation, which for an unmarked capture also asks for the
        // acceptance of its text as stored (SANITIZED_EXPORT_CONFIRM_UNMARKED).
        internal bool ConfirmSanitized { get; set; } = true;

        // The answer to File > Export's EXPORT_UNMARKED_QUESTION.
        internal bool AcceptAsStored { get; set; }

        // The answer to EXPORT_REPLACE_QUESTION or EXPORT_COPY_REPLACE_QUESTION.
        internal bool Replace { get; set; }

        // Runs when the export shows the texts that end it: after any publication, before its lease ends.
        internal Action OnResult { get; set; }

        // The keys of what the export showed, in order: each question's and text's keys, the pre-copy notice's, and the
        // destination dialog's by its title's key (FILE_EXPORT_CAPTURES or FILE_EXPORT_SANITIZED).
        internal IReadOnlyList<string> Shown => _shown;

        internal void Record(string key) => _shown.Add(key);
    }

    // An ICaptureExportDialogService that answers from a script, without windows, and records every call.
    internal sealed class ScriptedCaptureExportDialogs : ICaptureExportDialogService
    {
        private readonly CaptureExportScript _script;

        internal ScriptedCaptureExportDialogs(CaptureExportScript script)
        {
            ArgumentNullException.ThrowIfNull(script);
            _script = script;
        }

        // Each call, in order: "ask:KEY", "destination", "notice:KEY", "step:STEP" and "show:KEY", one per key.
        internal List<string> Events { get; } = new();

        internal List<CaptureExportQuestion> Questions { get; } = new();

        internal List<CaptureExportProgress> Steps { get; } = new();

        internal List<CaptureExportMessage> Messages { get; } = new();

        // Presses Cancel in the progress window of this step, before the step starts.
        internal CaptureExportStep? CancelBefore { get; set; }

        // Runs on the UI thread when a step's window opens, before the step starts.
        internal Action<CaptureExportProgress> BeforeStep { get; set; }

        // Runs on the UI thread while a step runs, after it has started; the step's end is awaited after it.
        internal Func<CaptureExportProgress, Task> DuringStep { get; set; }

        // Runs on the UI thread after a question is answered, with the question and its answer.
        internal Action<CaptureExportQuestion, bool> AfterQuestion { get; set; }

        // Runs on the UI thread before the destination dialog returns.
        internal Action BeforeDestination { get; set; }

        public Task<bool> AskAsync(object owner, CaptureExportQuestion question)
        {
            Questions.Add(question);
            foreach (CaptureExportText part in question.Parts)
            {
                _script.Record(part.Key);
                Events.Add("ask:" + part.Key);
            }

            bool answer = question.Parts[0].Key switch
            {
                "SANITIZED_EXPORT_CONFIRM" => _script.ConfirmSanitized,
                "EXPORT_UNMARKED_QUESTION" => _script.AcceptAsStored,
                "EXPORT_REPLACE_QUESTION" or "EXPORT_COPY_REPLACE_QUESTION" => _script.Replace,
                _ => throw new InvalidOperationException("The export asked an unexpected question: " + question.Parts[0].Key),
            };
            AfterQuestion?.Invoke(question, answer);
            return Task.FromResult(answer);
        }

        public Task<string> ChooseDestinationAsync(object owner, SazExportKind kind)
        {
            _script.Record(kind == SazExportKind.Sanitized ? "FILE_EXPORT_SANITIZED" : "FILE_EXPORT_CAPTURES");
            Events.Add("destination");
            BeforeDestination?.Invoke();
            return Task.FromResult(_script.Destination);
        }

        public async Task RunAsync(object owner, CaptureExportProgress progress, Func<Task> start)
        {
            Steps.Add(progress);
            if (progress.Notice != null)
            {
                _script.Record(progress.Notice.Key);
                Events.Add("notice:" + progress.Notice.Key);
            }

            Events.Add("step:" + progress.Step);
            BeforeStep?.Invoke(progress);
            if (CancelBefore == progress.Step)
            {
                progress.RequestCancellation();
            }

            Task work = start();
            if (DuringStep != null)
            {
                await DuringStep(progress);
            }

            await Task.WhenAny(work);
        }

        public Task ShowAsync(object owner, CaptureExportMessage message)
        {
            Messages.Add(message);
            foreach (CaptureExportText part in message.Parts)
            {
                _script.Record(part.Key);
                Events.Add("show:" + part.Key);
            }

            _script.OnResult?.Invoke();
            return Task.CompletedTask;
        }
    }

    internal static class CaptureExportDriver
    {
        // Where exports in tests prepare their copies: under the test run's own storage folder, which the run deletes.
        internal static string WorkFolder => Path.Join(PublicUtilities.StorageDirectoryPath, "ExportStaging");

        // Runs File > Export (Plain) or File > Export Sanitized Capture... (Sanitized) of the model's selected tab through the
        // real CaptureExportWorkflow, on the shared application's UI thread, with the script's answers, and with a real
        // SazExportOperation unless one is given. Completes once the export has ended and its lease is released.
        internal static async Task<CaptureExportOutcome> RunAsync(
            CaptureUiApplicationFixture application,
            CaptureAppModel model,
            SazExportKind kind,
            CaptureExportScript script,
            SazExportOperation operation = null)
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(script);
            CaptureExportOutcome outcome = null;
            await application.RunAsync(async () =>
            {
                CaptureExportWorkflow workflow = Workflow(new ScriptedCaptureExportDialogs(script), operation);
                outcome = await workflow.RunAsync(model, kind, owner: null);
            });
            return outcome;
        }

        // A workflow with the given dialogs, the test run's work folder, and a real SazExportOperation unless one is
        // given; runner replaces the operation's run when it is given.
        internal static CaptureExportWorkflow Workflow(
            ICaptureExportDialogService dialogs,
            SazExportOperation operation = null,
            IOperation<SazExportRequest, SazExportResult> runner = null)
        {
            operation ??= new SazExportOperation();
            return new CaptureExportWorkflow(operation, runner ?? operation, dialogs)
            {
                WorkFolder = WorkFolder,
            };
        }
    }
}
