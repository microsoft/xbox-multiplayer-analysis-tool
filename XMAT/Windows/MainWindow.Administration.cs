// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT
{
    public partial class MainWindow
    {
        private void ConfigureAgentDesktopActions()
        {
            var model = CaptureAppModel.AppModel;
            model.DesktopActions.Configure((context, token) =>
            {
                token.ThrowIfCancellationRequested();
                WorkspaceTabs.SelectedItem = CapturesWorkspaceTab;
                model.SelectedDeviceContext = context;
                Activate();
                return Task.FromResult(true);
            }, Close, () => MessageBox.Show(this,
                Localization.GetLocalizedString("APP_ELEVATED_RESTART_INSTRUCTIONS"),
                Localization.GetLocalizedString("APP_ELEVATED_RESTART_TITLE"),
                MessageBoxButton.OK, MessageBoxImage.Information),
                () => !_isCloseInProgress && !_closeConfirmed && !model.IsCaptureFileLoadRunning &&
                    !model.IsCaptureDataOperationRunning && !_analysisReportExportWorkflow.IsRunning,
                () => !_isCloseInProgress && !_closeConfirmed);
            model.DesktopActions.ConfigureSourceNavigation(RevealAgentRecordAsync, RevealAgentReportAsync);
            model.PreferencesModel.PropertyChanged += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(AppPreferencesModel.Theme))
                    ToggleDarkMode.IsChecked = model.PreferencesModel.Theme == "Dark";
            };
        }

        private async Task<bool> RevealAgentRecordAsync(DesktopTargetProvenance provenance, CancellationToken token)
        {
            var model = CaptureAppModel.AppModel;
            return await model.RevealInvestigationRecordAsync(provenance, target =>
            {
                WorkspaceTabs.SelectedItem = CapturesWorkspaceTab;
                model.SelectedDeviceContext = target.Context;
                if (target.WebProxyRecord != null)
                    ((WebServiceDeviceCaptureController)target.Context.CaptureController).SelectedConnectionModel = target.WebProxyRecord;
                else if (target.NetworkPacket != null)
                    ((NetworkTraceCaptureController)target.Context.CaptureController).SelectedPacket = target.NetworkPacket;
                else
                    return false;
                Activate();
                return true;
            }, token);
        }

        private Task<bool> RevealAgentReportAsync(DesktopTargetProvenance provenance, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return CaptureInvestigationReportWindow.ShowReportAsync(this, CaptureAppModel.AppModel, provenance, token);
        }

        private async Task ExportAdministrationArtifactAsync(AdministrationArtifactKind kind)
        {
            var logs = kind == AdministrationArtifactKind.DiagnosticLogs;
            var dialog = new SaveFileDialog
            {
                FileName = logs ? $"XMAT-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip" : "XMAT-root.cer",
                Filter = logs ? "ZIP (*.zip)|*.zip" : "Public certificate (*.cer)|*.cer",
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true)
                return;
            if (logs && MessageBox.Show(this, Localization.GetLocalizedString("APP_LOG_EXPORT_CONSENT"),
                Localization.GetLocalizedString("LOGS_COLLECTED_TITLE"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                var model = CaptureAppModel.AppModel;
                var folder = await model.CaptureFileExports.RegisterFolderAsync(Path.GetDirectoryName(dialog.FileName));
                var state = await model.ApplicationDiagnostics.InspectAsync();
                var plan = await model.ApplicationDiagnostics.PrepareExportAsync(state.Revision, kind,
                    new AdministrationDestination(folder.Id, Path.GetFileName(dialog.FileName)));
                await model.ApplicationDiagnostics.ExportAsync(plan, 512L * 1024 * 1024, TimeSpan.FromSeconds(60),
                    token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
                MessageBox.Show(this, Localization.GetLocalizedString("APP_ARTIFACT_EXPORTED"),
                    Localization.GetLocalizedString("LOGS_COLLECTED_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                MessageBox.Show(this, Localization.GetLocalizedString("APP_ARTIFACT_EXPORT_FAILED"),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
