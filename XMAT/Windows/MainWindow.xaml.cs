// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string XMATCaptureFileExtension = "xmatcap";
        private const string FiddlerFileExtension = "saz";
        private const string NetTraceFileExtension = "etl";
        private readonly string XMATFilter = $"{PublicUtilities.AppName} files (*.{XMATCaptureFileExtension})|*.{XMATCaptureFileExtension}";
        private readonly string FiddlerFilter = $"Fiddler SAZ files (*.{FiddlerFileExtension})|*.{FiddlerFileExtension}";
        private readonly string NetTraceFilter = $"Network trace files (*.{NetTraceFileExtension})|*.{NetTraceFileExtension}";

        private Cursor _originalCursor;
        private bool _isCloseInProgress;
        private bool _closeConfirmed;

        public MainWindow()
        {
            CaptureAppSettings.Deserialize(CaptureAppModel.AppModel);

            // Apply theme after preferences are deserialized
            ThemeManager.ApplyTheme(CaptureAppModel.AppModel.PreferencesModel.Theme);

            InitializeComponent();

            // LOC
            FileMenu.Header = Localization.GetLocalizedString("FILE_MENU");
            EditMenu.Header = Localization.GetLocalizedString("EDIT_MENU");
            FileNewCapture.Header = Localization.GetLocalizedString("FILE_NEW_CAPTURE");
            FileLoadCaptures.Header = Localization.GetLocalizedString("FILE_LOAD_CAPTURES");
            FileSaveCaptures.Header = Localization.GetLocalizedString("FILE_SAVE_CAPTURES");
            FileImportCaptures.Header = Localization.GetLocalizedString("FILE_IMPORT_CAPTURES");
            FileExportCaptures.Header = Localization.GetLocalizedString("FILE_EXPORT_CAPTURES");
            FileExportSanitizedCaptures.Header = Localization.GetLocalizedString("FILE_EXPORT_SANITIZED");
            FileExportSanitizedCaptures.ToolTip = Localization.GetLocalizedString("SANITIZED_EXPORT_TOOLTIP");
            FileExit.Header = Localization.GetLocalizedString("FILE_EXIT");
            ViewClearCaptures.Header = Localization.GetLocalizedString("VIEW_CLEAR_CAPTURES");
            EditPreferences.Header = Localization.GetLocalizedString("EDIT_PREFERENCES");
            AnalysisMenu.Header = Localization.GetLocalizedString("ANALYSIS_MENU");
            ViewAnalyzeCaptures.Header = Localization.GetLocalizedString("VIEW_ANALYZE_CAPTURES");
            ViewAnalyzeCaptures.ToolTip = Localization.GetLocalizedString("ANALYZE_CAPTURE_REQUIRES_STOP_TOOLTIP");
            HelpMenu.Header = Localization.GetLocalizedString("HELP_MENU");
            CollectLogs.Header = Localization.GetLocalizedString("COLLECT_LOGS");
            ViewGDKXInfo.Header = Localization.GetLocalizedString("GDKX_INFO");
            ActionsMenu.Header = Localization.GetLocalizedString("ACTIONS_MENU");
            ChangeProxyPort.Header = Localization.GetLocalizedString("PROXY_PORT_CHANGE");
            ChangeProxyPort.ToolTip = Localization.GetLocalizedString("PROXY_PORT_CHANGE_TOOLTIP");
            ExportRootCert.Header = Localization.GetLocalizedString("EXPORT_ROOT_CERT");

            // Sync dark mode checkbox with saved preference
            ToggleDarkMode.IsChecked = CaptureAppModel.AppModel.PreferencesModel.Theme == "Dark";
        }

        private void ChangeProxyPort_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureConnectionView.CanChangeProxyPort(CaptureAppModel.AppModel.SelectedDeviceContext);
        }

        private async void ChangeProxyPort_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (CaptureConnectionView.TryGetCaptureContext(
                CaptureAppModel.AppModel.SelectedDeviceContext, out var context, out var controller))
            {
                await CaptureConnectionView.ShowPortChangeAsync(context, controller);
            }
        }

        // One non-modal manager for the application's session host lists, brought back if open.
        private void ManageHostLists_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            HostListManagerWindow.ShowOrActivate(this, CaptureAppModel.AppModel.HostListPolicy);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _originalCursor = Cursor;

            DataContext = CaptureAppModel.AppModel;

            // if we have tabs, select the first one
            if (CaptureAppModel.AppModel.CaptureDeviceContexts.Any())
                CaptureAppModel.AppModel.SelectedDeviceContext = CaptureAppModel.AppModel.CaptureDeviceContexts.Last();

            // hook into the blocking operation event system
            PublicUtilities.BlockingOperationStarted +=
                (object sender, EventArgs args) =>
                {
                    Cursor = Cursors.Wait;
                };
            PublicUtilities.BlockingOperationEnded +=
                (object sender, EventArgs args) =>
                {
                    Cursor = _originalCursor;
                };
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_closeConfirmed)
            {
                return;
            }

            e.Cancel = true;
            if (_isCloseInProgress)
            {
                return;
            }

            _isCloseInProgress = true;
            var wasEnabled = IsEnabled;
            IsEnabled = false;

            try
            {
                // Let WPF finish the synchronous Closing event before showing dialogs or closing again.
                await Task.Yield();

                if (!await CaptureAppModel.AppModel.CanCloseAsync())
                {
                    if (CaptureAppModel.AppModel.IsCaptureDataOperationRunning)
                    {
                        MessageBox.Show(
                            Localization.GetLocalizedString("CAPTURE_DATA_BUSY_CLOSE"),
                            Localization.GetLocalizedString("FILE_EXIT"),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                if (!CaptureAppSettings.Serialize(CaptureAppModel.AppModel))
                {
                    return;
                }
                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                _closeConfirmed = false;
                PublicUtilities.AppLog(LogLevel.ERROR, $"Failed to close the application: {ex}");
                MessageBox.Show(
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_MESSAGE", ex.Message),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isCloseInProgress = false;
                IsEnabled = wasEnabled;
            }
        }

        private async void AllCaptureDevicesPanel_TabClosed(object sender, RoutedEventArgs e)
        {
            var args = (e as TabClosedRoutedEventArgs);
            if (args?.CaptureDeviceContext == null)
            {
                return;
            }

            await HandleCloseDeviceTabAsync(args.CaptureDeviceContext);
        }

        private async Task HandleCloseDeviceTabAsync(ICaptureDeviceContext captureDevice)
        {
            if (captureDevice?.CaptureController == null ||
                !CaptureAppModel.AppModel.TryBeginCaptureDataOperation(out var operationLease))
            {
                return;
            }

            using (operationLease)
            {
                try
                {
                    if (!await captureDevice.CaptureController.CanCloseAsync())
                    {
                        return;
                    }

                    await CaptureAppModel.AppModel.RemoveDeviceContextAsync(captureDevice);
                    CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
                }
                catch (Exception ex)
                {
                    PublicUtilities.AppLog(
                        LogLevel.ERROR,
                        $"Failed to close capture '{captureDevice.DeviceName}': {ex}");
                    MessageBox.Show(
                        Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_MESSAGE", ex.Message),
                        Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void CaptureErrorsPanel_TabAdded(object sender, RoutedEventArgs e)
        {
            // is there anything that needs doing here?
        }

        private void CaptureErrorsPanel_TabClosed(object sender, RoutedEventArgs e)
        {
            var args = e as AnalysisTabClosedRoutedEventArgs;
            CaptureAppModel.AppModel.RemoveAnalysisRun(args.AnalysisRun);
        }

        private void New_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            AddDeviceWindow deviceSelector = new()
            {
                Owner = Application.Current.MainWindow
            };
            var confirmed = deviceSelector.ShowDialog();

            if (confirmed ?? false)
            {
                var context = CaptureAppModel.AppModel.GetDeviceContext(deviceSelector.SelectedDeviceType, deviceSelector.SelectedDeviceName, deviceSelector.SelectedCaptureType, false);
                if (context != null)
                {
                    CaptureAppModel.AppModel.SelectedDeviceContext = context;
                    MessageBox.Show(Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_MESSAGE"), Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                else
                {
                    context = CaptureAppModel.AppModel.AddDeviceContext(
                        deviceSelector.SelectedDeviceType,
                        deviceSelector.SelectedDeviceName,
                        deviceSelector.SelectedCaptureType,
                        false,
                        null);
                    CaptureAppModel.AppModel.SelectedDeviceContext = context;
                    CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
                }
            }
            else
            {
                if (deviceSelector.Result == AddDeviceWindow.AddDeviceResult.Failed_NoDefaultConsole)
                {
                    MessageBox.Show(Localization.GetLocalizedString("NO_DEFAULT_CONSOLE_MESSAGE"), Localization.GetLocalizedString("NO_DEFAULT_CONSOLE_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                else if (deviceSelector.Result == AddDeviceWindow.AddDeviceResult.Failed_NeedGDKX)
                {
                    // Show our message box here
                    GDKXHelper.IsGDKXInstalled(true);
                    return;
                }
            }
        }

        private void Open_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            string filename = string.Empty;
            if (e is ExecutedRoutedEventArgs args)
                filename = args.Parameter as string;

            if (string.IsNullOrEmpty(filename))
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = XMATFilter
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    filename = openFileDialog.FileName;
                }
            }

            if (!string.IsNullOrEmpty(filename))
            {
                CaptureAppModel.AppModel.LoadDataCaptures(filename);
                if (CaptureAppModel.AppModel.CaptureDeviceContexts.Any())
                    CaptureAppModel.AppModel.SelectedDeviceContext = CaptureAppModel.AppModel.CaptureDeviceContexts.Last();
            }
        }

        private void Save_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = XMATFilter
            };

            if (saveFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(saveFileDialog.FileName))
            {
                CaptureAppModel.AppModel.SaveDataCaptures(saveFileDialog.FileName);
            }
        }

        private async void Import_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanBeginCaptureDataOperation)
            {
                return;
            }

            string filename = string.Empty;
            if (e is ExecutedRoutedEventArgs args)
                filename = args.Parameter as string;

            if (string.IsNullOrEmpty(filename))
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = $"{FiddlerFilter}|{NetTraceFilter}"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    filename = openFileDialog.FileName;
                }
            }

            if (!string.IsNullOrEmpty(filename))
            {
                if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(
                    out var operationLease,
                    requireAllCapturesStopped: true))
                {
                    return;
                }

                using (operationLease)
                {
                    var importType = filename.EndsWith(
                        $".{NetTraceFileExtension}",
                        StringComparison.OrdinalIgnoreCase)
                        ? ImportType.NetTrace
                        : ImportType.Fiddler;

                    try
                    {
                        await CaptureAppModel.AppModel.ImportDataCapturesAsync(importType, filename);
                    }
                    catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                    {
                        PublicUtilities.AppLog(LogLevel.ERROR, $"Capture import failed: {ex}");
                        MessageBox.Show(
                            Localization.GetLocalizedString("WEBCAP_IMPORT_ERROR", ex.Message),
                            Localization.GetLocalizedString("WEBCAP_IMPORT_ERROR_TITLE"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        private void Export_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = FiddlerFilter
            };

            if (saveFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(saveFileDialog.FileName))
            {
                // TODO: for now, hard-coding Fiddler export type.  In the future, if we have other types,
                // setup the filter above and call export with the appropriate type
                CaptureAppModel.AppModel.ExportDataCaptures(ExportType.Fiddler, saveFileDialog.FileName);
            }
        }

        private void ExportSanitized_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.CanExportSanitizedCaptures;
        }

        private async void ExportSanitized_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanExportSanitizedCaptures)
            {
                return;
            }
            if (MessageBox.Show(
                Localization.GetLocalizedString("SANITIZED_EXPORT_CONFIRM"),
                Localization.GetLocalizedString("FILE_EXPORT_SANITIZED"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = FiddlerFilter,
                DefaultExt = ".saz",
                AddExtension = true,
                FileName = "sanitized-capture.saz",
                Title = Localization.GetLocalizedString("FILE_EXPORT_SANITIZED")
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            using var cancellation = new CancellationTokenSource();
            try
            {
                WebServiceCapture.SanitizedExportSummary summary;
                using (PublicUtilities.BlockingOperation())
                {
                    var operation = CaptureAppModel.AppModel.ExportSanitizedDataCapturesAsync(dialog.FileName, cancellation.Token);
                    if (await Task.WhenAny(operation, Task.Delay(250)) != operation && !operation.IsCompleted)
                    {
                        var progress = new SanitizedExportProgressWindow(operation, cancellation.Cancel) { Owner = this };
                        progress.ShowDialog();
                    }
                    summary = await operation;
                }
                MessageBox.Show(
                    Localization.GetLocalizedString("SANITIZED_EXPORT_COMPLETE",
                        summary.Sessions, summary.RedactedValues, summary.OmittedBodies, summary.OmittedHeaders),
                    Localization.GetLocalizedString("FILE_EXPORT_SANITIZED"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                MessageBox.Show(
                    Localization.GetLocalizedString("SANITIZED_EXPORT_CANCELLED"),
                    Localization.GetLocalizedString("FILE_EXPORT_SANITIZED"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Sanitized export failed: {exception}");
                MessageBox.Show(
                    Localization.GetLocalizedString("SANITIZED_EXPORT_FAILED", exception.Message),
                    Localization.GetLocalizedString("FILE_EXPORT_SANITIZED"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Exit_Executed(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Preferences_Executed(object sender, RoutedEventArgs e)
        {
            var pw = new PreferencesWindow
            {
                Owner = this,
                DataContext = CaptureAppModel.AppModel.PreferencesModel
            };
            pw.ShowDialog();
        }

        private void CollectLogs_Executed(object sender, RoutedEventArgs e)
        {
            string strOutZipName = PublicUtilities.CollectLogs();
            MessageBox.Show(Localization.GetLocalizedString("LOGS_COLLECTED_MESSAGE", strOutZipName), Localization.GetLocalizedString("LOGS_COLLECTED_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ViewGDKXInfo_Executed(object sender, RoutedEventArgs e)
        {
            string strGDKXPath = GDKXHelper.GetGDKXPath(out string gdkxVersion);

            if (String.IsNullOrEmpty(strGDKXPath) || String.IsNullOrEmpty(gdkxVersion))
            {
                MessageBox.Show(Localization.GetLocalizedString("GDKX_NOT_FOUND"), Localization.GetLocalizedString("GDKX_DETAILS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(Localization.GetLocalizedString("GDKX_DETAILS", gdkxVersion, strGDKXPath), Localization.GetLocalizedString("GDKX_DETAILS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ClearAllCaptures_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.SelectedDeviceContext != null &&
                          !CaptureAppModel.AppModel.SelectedDeviceContext.IsReadOnly &&
                          CaptureAppModel.AppModel.CanModifyCaptureData;
        }

        private void ClearAllCaptures_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController.ClearAllCaptures();
        }

        private void AnalyzeCaptures_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.CanAnalyzeCaptures;
        }

        private void CaptureDataCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.CanModifyCaptureData;
        }

        private void Import_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.CanBeginCaptureDataOperation;
        }

        private async void AnalyzeCaptures_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanAnalyzeCaptures)
            {
                return;
            }

            var captureDeviceContext = CaptureAppModel.AppModel.SelectedDeviceContext;
            var list = from ca in CaptureAppModel.AppModel.CaptureAnalyzers
                       where ca.SupportedCaptureMethod == captureDeviceContext.CaptureController.CaptureMethod
                       select ca;

            if (list.Any())
            {
                list.First().IsSelected = true;
            }

            var selectorWindow = new CaptureAnalyzerWindow
            {
                Owner = this,
                DataContext = list
            };

            // show the list of available analyzers
            var confirmed = selectorWindow.ShowDialog();

            if (confirmed ?? false)
            {
                ICaptureAnalyzer selected = list.Where(x => x.IsSelected).First();

                if (!CaptureAppModel.AppModel.TryBeginAnalysis(out var analysisLease))
                {
                    return;
                }

                using (analysisLease)
                {
                    var analysisRun = CaptureAppModel.AppModel.AddAnalysisRun(selected);
                    CaptureErrorsPanel.AddedAnalysis(analysisRun);
                    await HandleAnalyzerChosen(
                        analysisRun,
                        captureDeviceContext.CaptureController);
                }
            }
        }

        private async Task HandleAnalyzerChosen(
            AnalysisRunModel analysisRun,
            IDeviceCaptureController captureController)
        {
            ECaptureAnalyzerResult analyzerResult = await CaptureAppModel.AppModel.RunAnalysisAsync(
                analysisRun, captureController);

            void OnError(string strErrorMsg)
            {
                MessageBox.Show(
                    strErrorMsg,
                    Localization.GetLocalizedString("WEBCAP_ANALYSIS_ERROR_TITLE"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            switch (analyzerResult)
            {
                case ECaptureAnalyzerResult.Success:
                {
                    break;
                }

                case ECaptureAnalyzerResult.NoSuitableData:
                {
                    OnError(Localization.GetLocalizedString("WEBCAP_ANALYSIS_ERROR_NOSUITABLE"));
                    break;
                }

                case ECaptureAnalyzerResult.UnknownError:
                {
                    OnError(Localization.GetLocalizedString("WEBCAP_ANALYSIS_ERROR_UNKNOWN"));
                    break;
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData)
            {
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files.Any())
                {
                    foreach (string file in files)
                    {
                        if (file.EndsWith($".{FiddlerFileExtension}", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith($".{NetTraceFileExtension}", StringComparison.OrdinalIgnoreCase))
                            AppCommands.Import.Execute(file, this);
                        else if (file.EndsWith($".{XMATCaptureFileExtension}"))
                            ApplicationCommands.Open.Execute(file, this);
                    }
                }
            }
        }
        private void ExportRootCert_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            XMAT.WebServiceCapture.Proxy.WebServiceProxy.CertManager.ExportRootCertificate(PublicUtilities.DesktopDirectoryPath);
        }

        private void ToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            string theme = ToggleDarkMode.IsChecked ? "Dark" : "Light";
            ThemeManager.ApplyTheme(theme);
            CaptureAppModel.AppModel.PreferencesModel.Theme = theme;
            CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
        }
    }
}
