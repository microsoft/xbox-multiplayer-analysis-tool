// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
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

        // A running capture can be exported: its sessions without a complete response are skipped and counted.
        private void Export_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = CaptureAppModel.AppModel.CanExportSelectedCapture;

        private void ExportSanitized_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = CaptureAppModel.AppModel.CanExportSelectedCapture;

        private async void Export_Executed(object sender, ExecutedRoutedEventArgs e) =>
            await CaptureExportWorkflow.For(this).RunForSelectedTabAsync(CaptureAppModel.AppModel, sanitizedOnly: false);

        private async void ExportSanitized_Executed(object sender, ExecutedRoutedEventArgs e) =>
            await CaptureExportWorkflow.For(this).RunForSelectedTabAsync(CaptureAppModel.AppModel, sanitizedOnly: true);

        Task ICaptureExportDialogService.ShowBusyAsync(object owner) =>
            ShowExportMessage(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"), MessageBoxImage.Information);

        Task ICaptureExportDialogService.ShowNothingToExportAsync(
            object owner, CaptureExportScope scope, CaptureExportCounts counts) =>
            ShowExportMessage(
                Localization.GetLocalizedString("EXPORT_NOTHING") + SkippedLiveSessions(counts), MessageBoxImage.Information);

        Task<bool> ICaptureExportDialogService.ConfirmAsync(
            object owner, CaptureExportScope scope, string tabName, CaptureExportCounts counts) =>
            Task.FromResult(MessageBox.Show(
                this,
                Localization.GetLocalizedString("EXPORT_CONFIRM", counts.Eligible, counts.Total, tabName) +
                    SkippedLiveSessions(counts),
                Localization.GetLocalizedString("EXPORT_TITLE"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) == MessageBoxResult.OK);

        // The filter chooses the format; the dialog asks before an existing file is replaced.
        Task<CaptureExportDestination> ICaptureExportDialogService.ChooseDestinationAsync(object owner, bool sanitizedOnly)
        {
            var dialog = new SaveFileDialog
            {
                Filter = sanitizedOnly ? SanitizedFilter : $"{XMATFilter}|{FiddlerFilter}|{SanitizedFilter}",
                Title = Localization.GetLocalizedString(sanitizedOnly ? "FILE_EXPORT_SANITIZED" : "EXPORT_TITLE"),
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

        Task<bool> ICaptureExportDialogService.ConfirmSanitizedAsync(object owner) =>
            Task.FromResult(MessageBox.Show(
                this,
                Localization.GetLocalizedString("SANITIZED_EXPORT_CONFIRM"),
                Localization.GetLocalizedString("FILE_EXPORT_SANITIZED"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK);

        // The export runs on a worker; after a short delay a progress window offers Cancel until it ends.
        async Task<OperationResult<ExportCaptureResult>> ICaptureExportDialogService.RunWithProgressAsync(
            object owner,
            Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export)
        {
            using var cancellation = new CancellationTokenSource();
            using (PublicUtilities.BlockingOperation())
            {
                Task<OperationResult<ExportCaptureResult>> running = Task.Run(() => export(null, cancellation.Token));
                if (await Task.WhenAny(running, Task.Delay(250)) != running)
                {
                    new SanitizedExportProgressWindow(running, cancellation.Cancel) { Owner = this }.ShowDialog();
                }

                return await running;
            }
        }

        Task ICaptureExportDialogService.ShowResultAsync(
            object owner, CaptureExportScope scope, OperationResult<ExportCaptureResult> result)
        {
            if (result.Outcome == OperationOutcome.Cancelled)
            {
                return ShowExportMessage(Localization.GetLocalizedString("EXPORT_CANCELLED"), MessageBoxImage.Information);
            }

            if (!result.Succeeded || result.Value?.Counts is not { } counts)
            {
                OperationDiagnostic error = result.Diagnostics.FirstOrDefault(
                    diagnostic => diagnostic.Severity == OperationDiagnosticSeverity.Error) ??
                    (result.Diagnostics.Count > 0 ? result.Diagnostics[0] : null);
                return ShowExportMessage(
                    Localization.GetLocalizedString("EXPORT_FAILED", error?.Message, error?.Code), MessageBoxImage.Error);
            }

            var message = new StringBuilder(Localization.GetLocalizedString("EXPORT_COMPLETE", counts.Exported, counts.Selected));
            if (counts.SkippedIncomplete + counts.Unavailable != 0)
            {
                message.AppendLine().AppendLine().Append(Localization.GetLocalizedString(
                    "EXPORT_COMPLETE_SKIPPED", counts.SkippedIncomplete, counts.Unavailable));
            }

            if (result.Value.Format == CaptureExportFormat.SanitizedSaz && result.Value.Sanitization is { } sanitized)
            {
                message.AppendLine().AppendLine().Append(Localization.GetLocalizedString(
                    "EXPORT_SANITIZED_SUMMARY", sanitized.RedactedValues, sanitized.OmittedBodies, sanitized.OmittedHeaders));
            }

            return ShowExportMessage(message.ToString(), MessageBoxImage.Information);
        }

        // A live capture's sessions without a complete response are skipped; stopping the capture includes them.
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
