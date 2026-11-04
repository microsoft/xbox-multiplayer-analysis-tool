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
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.AgentAccess;

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
        private readonly AnalysisReportExportWorkflow
            _analysisReportExportWorkflow;
        private readonly AgentAccessHost _agentAccess;

        private Cursor _originalCursor;
        private bool _isCloseInProgress;
        private bool _closeConfirmed;

        public MainWindow()
        {
            CaptureAppSettings.Deserialize(CaptureAppModel.AppModel);

            // Apply theme after preferences are deserialized
            ThemeManager.ApplyTheme(CaptureAppModel.AppModel.PreferencesModel.Theme);

            _analysisReportExportWorkflow =
                new AnalysisReportExportWorkflow(
                    this,
                    new WpfAnalysisReportExportDialogService(),
                    ExportAnalysisReportAsync);

            InitializeComponent();
            ConfigureAgentDesktopActions();
            _agentAccess = CaptureAppModel.AppModel.AgentAccess;
            _agentAccess.Changed += (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.BeginInvoke(new Action(UpdateAgentStatus));
                }
            };
            UpdateAgentStatus();

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
            AnalysisExportReport.Header = Localization.GetLocalizedString("ANALYSIS_EXPORT_REPORT");
            AnalysisExportReport.ToolTip = Localization.GetLocalizedString("ANALYSIS_EXPORT_REPORT_TOOLTIP");
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

        private void ManageAgentAccess_Executed(object sender, ExecutedRoutedEventArgs e) =>
            AgentAccessWindow.ShowOrActivate(this, _agentAccess);

        private void UpdateAgentStatus() =>
            AgentAccessIndicator.Content = Localization.GetLocalizedString(
                _agentAccess.IsEnabled ? "AGENT_ACCESS_ON" : "AGENT_ACCESS_OFF");

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _originalCursor = Cursor;

            DataContext = CaptureAppModel.AppModel;
            _agentAccess.MarkReady();

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

            if (!await _analysisReportExportWorkflow.RequestCloseAsync())
            {
                return;
            }

            _isCloseInProgress = true;
            CaptureAppModel.AppModel.DesktopActions.NotifyLifecycleChanged();
            _agentAccess.PauseRequests();
            var wasEnabled = IsEnabled;
            IsEnabled = false;

            try
            {
                // Let WPF finish the synchronous Closing event before showing dialogs or closing again.
                await Task.Yield();

                if (!await CanCloseSqlWorkspaceAsync())
                    return;
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
                await _agentAccess.DisableAsync();
                await CaptureAppModel.AppModel.DisposeInvestigationAsync();
                await CaptureAppModel.AppModel.DisposeSqlWorkspaceAsync();
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
                if (!_closeConfirmed)
                {
                    CancelSqlWorkspaceClosing();
                    _analysisReportExportWorkflow.CancelClosing();
                    _agentAccess.ResumeRequests();
                }
                _isCloseInProgress = false;
                CaptureAppModel.AppModel.DesktopActions.NotifyLifecycleChanged();
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
            if (captureDevice?.CaptureController == null)
            {
                return;
            }

            try
            {
                var result = await CaptureAppModel.AppModel.CaptureDevices.RemoveDesktopAsync(
                    captureDevice, captureDevice.CaptureController.CanCloseAsync);
                if (result.Failure == CaptureDeviceFailure.PersistenceFailed)
                {
                    ShowDevicePersistenceFailure();
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
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

        private void CaptureErrorsPanel_TabAdded(object sender, RoutedEventArgs e)
        {
            // is there anything that needs doing here?
        }

        private void CaptureErrorsPanel_TabClosed(object sender, RoutedEventArgs e)
        {
            var args = e as AnalysisTabClosedRoutedEventArgs;
            CaptureAppModel.AppModel.RemoveAnalysisRun(args.AnalysisRun);
        }

        private async void New_Executed(object sender, RoutedEventArgs e)
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

            if (confirmed != true)
                return;
            try
            {
                var model = CaptureAppModel.AppModel;
                var creation = await model.CaptureDevices.PrepareAddAsync(deviceSelector.Request);
                var result = await model.CaptureDevices.AddAsync(creation);
                await model.CaptureCatalog.WithTargetAsync(result.CaptureId, null, (context, _) =>
                {
                    model.SelectedDeviceContext = context;
                    return Task.FromResult(true);
                });
                if (!result.Changed)
                    MessageBox.Show(Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_MESSAGE"), Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
                if (!result.Persisted)
                    ShowDevicePersistenceFailure();
            }
            catch (CaptureDeviceException exception) when (exception.Failure == CaptureDeviceFailure.NoDefaultConsole)
            {
                MessageBox.Show(Localization.GetLocalizedString("NO_DEFAULT_CONSOLE_MESSAGE"), Localization.GetLocalizedString("NO_DEFAULT_CONSOLE_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (CaptureDeviceException exception) when (exception.Failure == CaptureDeviceFailure.GdkRequired)
            {
                GDKXHelper.IsGDKXInstalled(true);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                MessageBox.Show(Localization.GetLocalizedString("DEVICE_OPERATION_FAILED", exception.Message),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ShowDevicePersistenceFailure() =>
            MessageBox.Show(Localization.GetLocalizedString("DEVICE_CHANGE_PERSISTENCE_FAILED"),
                Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);

        private async void Open_Executed(object sender, RoutedEventArgs e)
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
                await LoadCaptureFileAsync(filename);
            }
        }

        internal static async Task LoadCaptureFileAsync(string path)
        {
            try
            {
                var model = CaptureAppModel.AppModel;
                var receipt = await model.CaptureFileLoads.LoadDesktopAsync(path);
                if (model.CaptureDeviceContexts.Any())
                    model.SelectedDeviceContext = model.CaptureDeviceContexts.Last();
                if (!receipt.ViewsNotified)
                    MessageBox.Show(Localization.GetLocalizedString("CAPTURE_LOAD_VIEW_WARNING"),
                        Localization.GetLocalizedString("CAPTURE_LOAD_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                var key = exception is CaptureFileException file ? file.Failure switch
                {
                    CaptureFileFailure.SchemaLimit => "CAPTURE_OPEN_SCHEMA_LIMIT",
                    CaptureFileFailure.LiveDatabase => "CAPTURE_OPEN_SIDECAR",
                    CaptureFileFailure.UnsupportedLocation => "CAPTURE_OPEN_LOCATION",
                    CaptureFileFailure.InvalidCapture => "CAPTURE_OPEN_SCHEMA_ERROR",
                    _ => "CAPTURE_LOAD_ERROR"
                } : "CAPTURE_LOAD_ERROR";
                PublicUtilities.AppLog(LogLevel.ERROR, $"Capture open failed ({exception.GetType().FullName}).");
                MessageBox.Show(Localization.GetLocalizedString(key),
                    Localization.GetLocalizedString("CAPTURE_LOAD_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal static string CaptureOpenErrorKey(SqLtSchemaFailure failure) =>
            failure switch
            {
                SqLtSchemaFailure.Limit => "CAPTURE_OPEN_SCHEMA_LIMIT",
                SqLtSchemaFailure.Sidecar => "CAPTURE_OPEN_SIDECAR",
                SqLtSchemaFailure.UnsupportedLocation => "CAPTURE_OPEN_LOCATION",
                _ => "CAPTURE_OPEN_SCHEMA_ERROR"
            };

        private async void Save_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanBeginCaptureDataOperation)
            {
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = XMATFilter
            };

            if (saveFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(saveFileDialog.FileName))
            {
                await SaveCaptureFileAsync(saveFileDialog.FileName, CaptureArchiveFormat.CaptureDatabase);
            }
        }

        internal static string CaptureSaveFailureMessageKey(
            CaptureSaveFailure failure) =>
            failure switch
            {
                CaptureSaveFailure.Refused =>
                    "CAPTURE_SAVE_ERROR_REFUSED",
                CaptureSaveFailure.DestinationLocked =>
                    "CAPTURE_SAVE_ERROR_LOCKED",
                _ =>
                    "CAPTURE_SAVE_ERROR_FAILED",
            };

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
                var etl = filename.EndsWith($".{NetTraceFileExtension}", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var model = CaptureAppModel.AppModel;
                    ICaptureFileImports imports = etl ? model.CaptureEtlImports : model.CaptureSazImports;
                    var receipt = await imports.ImportDesktopAsync(filename);
                    await model.CaptureCatalog.WithTargetAsync(receipt.CaptureId, null, (target, _) =>
                    {
                        model.SelectedDeviceContext = target;
                        return Task.FromResult(true);
                    });
                    if (!receipt.ViewsNotified)
                        MessageBox.Show(Localization.GetLocalizedString("SAZ_IMPORT_VIEW_WARNING"),
                            Localization.GetLocalizedString("WEBCAP_IMPORT_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    else if (etl && receipt.RecordCount == 0)
                        MessageBox.Show(Localization.GetLocalizedString("NETCAP_IMPORT_NO_PACKETS"),
                            Localization.GetLocalizedString("NETCAP_ERROR"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Capture import failed: {exception.GetType().Name}");
                    MessageBox.Show(Localization.GetLocalizedString(etl ? "ETL_IMPORT_FAILED" : "SAZ_IMPORT_FAILED"),
                        Localization.GetLocalizedString("WEBCAP_IMPORT_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void Export_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanBeginCaptureDataOperation)
            {
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = FiddlerFilter
            };

            if (saveFileDialog.ShowDialog() == true && !string.IsNullOrEmpty(saveFileDialog.FileName))
            {
                await SaveCaptureFileAsync(saveFileDialog.FileName, CaptureArchiveFormat.FiddlerSaz);
            }
        }

        private static async Task SaveCaptureFileAsync(string path, CaptureArchiveFormat format)
        {
            try
            {
                var model = CaptureAppModel.AppModel;
                var source = (await model.CaptureQueries.GetSourcesAsync()).Single(item => item.Kind == QuerySourceKind.Active);
                await model.CaptureFiles.SaveDesktopAsync(source.Id, format, path);
            }
            catch (CaptureSaveException exception)
            {
                MessageBox.Show(Localization.GetLocalizedString(CaptureSaveFailureMessageKey(exception.Failure)),
                    Localization.GetLocalizedString("CAPTURE_SAVE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                MessageBox.Show(Localization.GetLocalizedString("CAPTURE_FILE_FAILED", exception.Message),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CaptureFile_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
            e.CanExecute = CaptureAppModel.AppModel.CanBeginCaptureDataOperation;

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

        private async void CollectLogs_Executed(object sender, RoutedEventArgs e)
        {
            await ExportAdministrationArtifactAsync(AdministrationArtifactKind.DiagnosticLogs);
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
                          !CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController.IsRunning &&
                          CaptureAppModel.AppModel.CanModifyCaptureData &&
                          (CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController is not
                              XMAT.NetworkTrace.NetworkTraceCaptureController trace || trace.CanClearCaptures);
        }

        private async void ClearAllCaptures_Executed(object sender, RoutedEventArgs e)
        {
            if (!CaptureAppModel.AppModel.CanModifyCaptureData ||
                CaptureAppModel.AppModel.SelectedDeviceContext?.CaptureController is
                    XMAT.NetworkTrace.NetworkTraceCaptureController { CanClearCaptures: false })
            {
                return;
            }

            var model = CaptureAppModel.AppModel;
            if (model.SelectedDeviceContext == null)
                return;
            try
            {
                var target = model.CaptureCatalog.DescribeTarget(model.SelectedDeviceContext);
                await model.CaptureDataOperations.ClearAsync(target.Id, target.Revision);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                MessageBox.Show(Localization.GetLocalizedString("ETL_CLEAR_UNAVAILABLE"),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

                if (!CaptureAppModel.AppModel.TryBeginAnalysis(captureDeviceContext, out var analysisLease))
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

                case ECaptureAnalyzerResult.WorkLimit:
                {
                    OnError(Localization.GetLocalizedString(
                        "WEBCAP_ANALYSIS_ERROR_WORK_LIMIT"));
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
        private async void ExportRootCert_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            await ExportAdministrationArtifactAsync(AdministrationArtifactKind.PublicRootCertificateDer);
        }

        private async void ToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            var model = CaptureAppModel.AppModel;
            try
            {
                var state = await model.ApplicationPreferences.InspectAsync();
                var change = await model.ApplicationPreferences.ApplyAsync(state.Revision,
                    state.Values with { Theme = ToggleDarkMode.IsChecked ? "Dark" : "Light" });
                if (!change.Persisted)
                    MessageBox.Show(Localization.GetLocalizedString("CTRL_PREF_COMMITTED_NOT_SAVED"),
                        Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                ToggleDarkMode.IsChecked = model.PreferencesModel.Theme == "Dark";
                MessageBox.Show(Localization.GetLocalizedString("APP_PREFERENCE_CHANGE_FAILED"),
                    Localization.GetLocalizedString("CAPTURE_CLOSE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportAnalysisReport_CanExecute(
            object sender,
            CanExecuteRoutedEventArgs e)
        {
            e.CanExecute =
                !_analysisReportExportWorkflow.IsRunning &&
                !_analysisReportExportWorkflow.ClosingStarted &&
                AnalysisReportExportCommand.CanExecute(
                    CaptureAppModel.AppModel.AnalysisRuns);
        }

        private async void ExportAnalysisReport_Executed(
            object sender,
            ExecutedRoutedEventArgs e)
        {
            try
            {
                AnalysisExportSource source =
                    AnalysisReportExportCommand.SnapshotSelected(
                    CaptureAppModel.AppModel.AnalysisRuns);
                if (source == null ||
                    _analysisReportExportWorkflow.ClosingStarted)
                {
                    return;
                }

                await _analysisReportExportWorkflow.RunAsync(
                    source);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Analysis report export workflow failed: {exception}");
            }
        }

        private async Task<string> ExportAnalysisReportAsync(
            AnalysisExportSource source,
            string destinationParent)
        {
            using (PublicUtilities.BlockingOperation())
            {
                return await Task.Run(() =>
                    new AnalysisReportExporter().Export(
                        source,
                        destinationParent));
            }
        }
    }
}
