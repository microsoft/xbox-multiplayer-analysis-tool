// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Export;

namespace XMAT
{
    // The windows of an export (CaptureExportWorkflow). File > Export and Export Sanitized export every session of the
    // selected tab; the capture list's row menu exports its selected or visible sessions (CaptureConnectionView).
    public partial class MainWindow : ICaptureExportDialogService
    {
        private const string SanitizedFilter = "Sanitized Fiddler SAZ files (*.saz)|*.saz";

        private void Export_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = CaptureAppModel.AppModel.CanExportSelectedCapture;

        private async void Export_Executed(object sender, ExecutedRoutedEventArgs e) =>
            await CaptureExportWorkflow.For(this).RunForSelectedTabAsync(
                CaptureAppModel.AppModel, sanitizedOnly: e.Command == AppCommands.ExportSanitized);

        Task ICaptureExportDialogService.ShowBusyAsync() =>
            ShowExportMessage(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"), MessageBoxImage.Information);

        Task ICaptureExportDialogService.ShowNothingToExportAsync(CaptureExportCounts counts) =>
            ShowExportMessage(
                Localization.GetLocalizedString("EXPORT_NOTHING") + SkippedLiveSessions(counts), MessageBoxImage.Information);

        Task<bool> ICaptureExportDialogService.ConfirmAsync(string tabName, CaptureExportCounts counts) =>
            Task.FromResult(MessageBox.Show(
                this,
                Localization.GetLocalizedString("EXPORT_CONFIRM", counts.Eligible, counts.Total, tabName) +
                    SkippedLiveSessions(counts),
                Localization.GetLocalizedString("EXPORT_TITLE"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) == MessageBoxResult.OK);

        // The filter chooses the format; the dialog asks before an existing file is replaced.
        Task<CaptureExportDestination> ICaptureExportDialogService.ChooseDestinationAsync(bool sanitizedOnly)
        {
            var dialog = new SaveFileDialog
            {
                Filter = sanitizedOnly ? SanitizedFilter : $"{XMATFilter}|{FiddlerFilter}|{SanitizedFilter}",
                Title = Localization.GetLocalizedString(sanitizedOnly ? "EXPORT_SANITIZED_TITLE" : "EXPORT_TITLE"),
            };
            if (dialog.ShowDialog(this) != true)
            {
                return Task.FromResult<CaptureExportDestination>(null);
            }

            CaptureExportFormat format = sanitizedOnly
                ? CaptureExportFormat.SanitizedSaz
                : (CaptureExportFormat)(dialog.FilterIndex - 1);
            return Task.FromResult(new CaptureExportDestination(dialog.FileName, format, File.Exists(dialog.FileName)));
        }

        Task<bool> ICaptureExportDialogService.ConfirmSanitizedAsync() =>
            Task.FromResult(MessageBox.Show(
                this,
                Localization.GetLocalizedString("SANITIZED_EXPORT_CONFIRM"),
                Localization.GetLocalizedString("EXPORT_SANITIZED_TITLE"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK);

        Task<bool> ICaptureExportDialogService.ConfirmUnprotectedAsync(string path) =>
            Task.FromResult(MessageBox.Show(
                this,
                Localization.GetLocalizedString("EXPORT_UNPROTECTED_CONFIRM", path),
                Localization.GetLocalizedString("EXPORT_UNPROTECTED_TITLE"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);

        // The export runs on a worker and reports its phases to a progress window, which appears after a short delay with
        // the latest phase and Cancel; an export that ends sooner closes it unseen.
        async Task<OperationResult<ExportCaptureResult>> ICaptureExportDialogService.RunWithProgressAsync(
            Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export)
        {
            using var cancellation = new CancellationTokenSource();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new ExportProgressWindow(finished.Task, cancellation.Cancel) { Owner = this };
            using (PublicUtilities.BlockingOperation())
            {
                Task<OperationResult<ExportCaptureResult>> running = Task.Run(() => export(window, cancellation.Token));
                _ = running.ContinueWith(
                    _ => finished.TrySetResult(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                if (await Task.WhenAny(running, Task.Delay(250)) != running)
                {
                    window.ShowDialog();
                }
                else
                {
                    finished.TrySetResult();
                    window.Close();
                }

                return await running;
            }
        }

        Task ICaptureExportDialogService.ShowResultAsync(OperationResult<ExportCaptureResult> result) =>
            ShowExportMessage(
                ExportResultMessage(result),
                result.Outcome == OperationOutcome.Cancelled || (result.Succeeded && result.Value?.OutputProtected != false)
                    ? MessageBoxImage.Information
                    : result.Value?.Published == true ? MessageBoxImage.Warning : MessageBoxImage.Error);

        // An export's last message, from the result's facts and codes and never the operation's English text: a published
        // file as N of the capture's M sessions, otherwise the reason and, unless the GUI itself failed, an unchanged file.
        internal static string ExportResultMessage(OperationResult<ExportCaptureResult> result)
        {
            if (result.Outcome == OperationOutcome.Cancelled)
            {
                return Localization.GetLocalizedString("EXPORT_CANCELLED");
            }

            ExportCaptureResult value = result.Value;
            var lines = new List<string>();
            if (value?.Published == true)
            {
                if (value.Counts is { } counts)
                {
                    lines.Add(Localization.GetLocalizedString("EXPORT_COMPLETE", counts.Exported, counts.Total));
                    if (counts.SkippedIncomplete + counts.Unavailable != 0)
                    {
                        lines.Add(Localization.GetLocalizedString(
                            "EXPORT_COMPLETE_SKIPPED", counts.SkippedIncomplete, counts.Unavailable));
                    }
                }

                if (value.Format == CaptureExportFormat.SanitizedSaz && value.Sanitization is { } sanitized)
                {
                    lines.Add(Localization.GetLocalizedString(
                        "EXPORT_SANITIZED_SUMMARY", sanitized.RedactedValues, sanitized.OmittedBodies, sanitized.OmittedHeaders));
                }

                if (value.OutputProtected == false)
                {
                    lines.Add(Localization.GetLocalizedString("EXPORT_UNPROTECTED_WARNING"));
                }

                if (!result.Succeeded)
                {
                    lines.Add(Localization.GetLocalizedString("EXPORT_PUBLISHED_CLEANUP_FAILED"));
                }
            }
            else
            {
                string code = result.Outcome == OperationOutcome.CleanupFailed
                    ? "export.cleanup.failed"
                    : result.Diagnostics.FirstOrDefault(
                        diagnostic => diagnostic.Severity == OperationDiagnosticSeverity.Error)?.Code;
                lines.Add(Localization.GetLocalizedString(ExportErrorKey(code)));
                if (code != "export.gui.failed")
                {
                    lines.Add(Localization.GetLocalizedString("EXPORT_UNCHANGED"));
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static string ExportErrorKey(string code) => code switch
        {
            "export.cleanup.failed" => "EXPORT_ERROR_CLEANUP",
            "export.output.exists" or "export.output.is-source" or "export.output.open-capture" => "EXPORT_ERROR_DESTINATION",
            "export.output.locked" => "EXPORT_ERROR_LOCKED",
            CaptureExportErrorCodes.FormatMismatch => "EXPORT_ERROR_FORMAT",
            CaptureExportErrorCodes.InputLimit => "EXPORT_ERROR_LIMIT",
            CaptureExportErrorCodes.InvalidHttp or CaptureExportErrorCodes.InvalidBody => "EXPORT_ERROR_INPUT",
            CaptureExportErrorCodes.TextAmbiguous => "CAPTURE_TEXT_EXPORT_POLICY_REQUIRED",
            "export.snapshot.changed" or "export.no-eligible-rows" => "EXPORT_ERROR_CHANGED",
            CaptureExportErrorCodes.WriteFailed or CaptureExportErrorCodes.SanitizerFailed => "EXPORT_ERROR_WRITE",
            _ => "EXPORT_ERROR_UNKNOWN",
        };

        private static string SkippedLiveSessions(CaptureExportCounts counts) =>
            counts.SkippedIncomplete == 0
                ? string.Empty
                : Environment.NewLine + Environment.NewLine +
                    Localization.GetLocalizedString("EXPORT_SKIPPED_LIVE", counts.SkippedIncomplete);

        private Task ShowExportMessage(string message, MessageBoxImage image)
        {
            MessageBox.Show(this, message, Localization.GetLocalizedString("EXPORT_TITLE"), MessageBoxButton.OK, image);
            return Task.CompletedTask;
        }
    }
}
