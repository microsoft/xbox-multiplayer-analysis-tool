// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT
{
    public partial class MainWindow
    {
        private async void TakeOverAgentResources_Click(object sender, RoutedEventArgs e)
        {
            var model = CaptureAppModel.AppModel;
            var target = model.SelectedDeviceContext;
            if (target == null || target.IsReadOnly || !model.HasAgentAccess)
                return;
            try
            {
                var capture = model.CaptureCatalog.DescribeTarget(target);
                var scripts = target.CaptureType == CaptureType.WebProxy
                    ? await model.CaptureScripts.GetScriptsAsync(capture.Id) : [];
                var owned = model.AgentAccess.Runtime.GetOwnedResources().Where(item =>
                    item.Resource.Kind == AgentResourceKind.Capture && item.Resource.Id == capture.Id &&
                        item.Revision == capture.CaptureEpoch.ToString(CultureInfo.InvariantCulture) ||
                    item.Resource.Kind == AgentResourceKind.Script && scripts.Any(script => script.Id == item.Resource.Id &&
                        item.Revision == script.Revision.ToString(CultureInfo.InvariantCulture))).ToArray();
                if (owned.Length == 0)
                {
                    MessageBox.Show(this, Localization.GetLocalizedString("AGENT_OPERATOR_NO_OWNERSHIP"),
                        Localization.GetLocalizedString("AGENT_OPERATOR_TAKEOVER"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(this, Localization.GetLocalizedString("AGENT_OPERATOR_TAKEOVER_WARNING", owned.Length),
                    Localization.GetLocalizedString("AGENT_OPERATOR_TAKEOVER"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                var changed = owned.Count(item => model.AgentAccess.Runtime.TakeOver(item.Resource, item.SessionId, item.Revision));
                MessageBox.Show(this, Localization.GetLocalizedString("AGENT_OPERATOR_TAKEOVER_RESULT", changed, owned.Length),
                    Localization.GetLocalizedString("AGENT_OPERATOR_TAKEOVER"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Agent ownership takeover failed: {exception.GetType().Name}");
            }
        }

        private void SetupProxyTrust_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, Localization.GetLocalizedString("PROXY_TRUST_SETUP_WARNING"),
                Localization.GetLocalizedString("PROXY_TRUST_SETUP"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                if (!WebServiceProxy.Initialize(PublicUtilities.StorageDirectoryPath))
                    throw new InvalidOperationException();
                MessageBox.Show(this, Localization.GetLocalizedString("PROXY_TRUST_SETUP_COMPLETE"),
                    Localization.GetLocalizedString("PROXY_TRUST_SETUP"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                MessageBox.Show(this, Localization.GetLocalizedString("PROXY_TRUST_SETUP_FAILED"),
                    Localization.GetLocalizedString("PROXY_TRUST_SETUP"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

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
            model.HostLifecycle.Configure(RequestPreparedAgentClose,
                blocked => IsEnabled = !blocked,
                () => !_isCloseInProgress && !_closeConfirmed && !_analysisReportExportWorkflow.IsRunning);
            model.PreferencesModel.PropertyChanged += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(AppPreferencesModel.Theme))
                    ToggleDarkMode.IsChecked = model.PreferencesModel.Theme == "Dark";
            };
        }

        private async void RequestPreparedAgentClose()
        {
            try
            {
                _isCloseInProgress = true;
                if (Application.Current is App { Runtime: not null } application)
                    await application.Runtime.DisposeAsync();
                else
                {
                    await _agentAccess.DisableAsync();
                    await CaptureAppModel.AppModel.DisposeSqlWorkspaceAsync();
                    await CaptureAppModel.AppModel.DisposeInvestigationAsync();
                }
                _closeConfirmed = true;
                Close();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                CaptureAppModel.AppModel.HostLifecycle.CancelPrepared();
                _agentAccess.ResumeRequests();
                _isCloseInProgress = false;
                PublicUtilities.AppLog(LogLevel.ERROR, $"Prepared agent close failed: {exception.GetType().Name}");
            }
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
