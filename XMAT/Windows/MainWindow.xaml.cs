// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Win32;
using XMAT.Models;
using XMAT.SharedInterfaces;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string XMATCaptureFileExtension = "xmatcap";
        private const string FiddlerFileExtension = "saz";
        private readonly string XMATFilter = $"{PublicUtilities.AppName} files (*.{XMATCaptureFileExtension})|*.{XMATCaptureFileExtension}";
        private readonly string FiddlerFilter = $"Fiddler SAZ files (*.{FiddlerFileExtension})|*.{FiddlerFileExtension}";

        private Cursor _originalCursor;

        public MainWindow()
        {
            CaptureAppSettings.Deserialize(CaptureAppModel.AppModel);

            InitializeComponent();

            // LOC
            FileMenu.Header = Localization.GetLocalizedString("FILE_MENU");
            EditMenu.Header = Localization.GetLocalizedString("EDIT_MENU");
            FileNewCapture.Header = Localization.GetLocalizedString("FILE_NEW_CAPTURE");
            FileLoadCaptures.Header = Localization.GetLocalizedString("FILE_LOAD_CAPTURES");
            FileSaveCaptures.Header = Localization.GetLocalizedString("FILE_SAVE_CAPTURES");
            FileImportCaptures.Header = Localization.GetLocalizedString("FILE_IMPORT_CAPTURES");
            FileExportCaptures.Header = Localization.GetLocalizedString("FILE_EXPORT_CAPTURES");
            FileExit.Header = Localization.GetLocalizedString("FILE_EXIT");
            ViewClearCaptures.Header = Localization.GetLocalizedString("VIEW_CLEAR_CAPTURES");
            EditPreferences.Header = Localization.GetLocalizedString("EDIT_PREFERENCES");
            AnalysisMenu.Header = Localization.GetLocalizedString("ANALYSIS_MENU");
            ViewAnalyzeCaptures.Header = Localization.GetLocalizedString("VIEW_ANALYZE_CAPTURES");
            HelpMenu.Header = Localization.GetLocalizedString("HELP_MENU");
            CollectLogs.Header = Localization.GetLocalizedString("COLLECT_LOGS");
            ViewGDKXInfo.Header = Localization.GetLocalizedString("GDKX_INFO");
            ActionsMenu.Header = Localization.GetLocalizedString("ACTIONS_MENU");
            ExportRootCert.Header = Localization.GetLocalizedString("EXPORT_ROOT_CERT");
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

        private void AllCaptureDevicesPanel_TabClosed(object sender, RoutedEventArgs e)
        {
            var args = (e as TabClosedRoutedEventArgs);
            HandleCloseDeviceTab(args.CaptureDeviceContext);
        }

        private void HandleCloseDeviceTab(ICaptureDeviceContext captureDevice)
        {
            CaptureAppModel.AppModel.RemoveDeviceContext(captureDevice);
            CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
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
                    MessageBox.Show(Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_MESSAGE"), Localization.GetLocalizedString("DEVICE_CAPTURE_ALREADY_EXISTS_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            string filename = string.Empty;
            if (e is ExecutedRoutedEventArgs args)
                filename = args.Parameter as string;

            if (string.IsNullOrEmpty(filename))
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = FiddlerFilter
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    filename = openFileDialog.FileName;
                }
            }

            if (!string.IsNullOrEmpty(filename))
            {
                // TODO: for now, hard-coding Fiddler export type.  In the future, if we have other types,
                // setup the filter above and call export with the appropriate type
                await CaptureAppModel.AppModel.ImportDataCapturesAsync(ImportType.Fiddler, filename);
            }
        }

        private void Export_Executed(object sender, RoutedEventArgs e)
        {
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

        private void Exit_Executed(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
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
                          !CaptureAppModel.AppModel.SelectedDeviceContext.IsReadOnly;
        }

        private void ClearAllCaptures_Executed(object sender, RoutedEventArgs e)
        {
            CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController.ClearAllCaptures();
        }

        private void AnalyzeCaptures_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CaptureAppModel.AppModel.SelectedDeviceContext != null;
        }

        private async void AnalyzeCaptures_Executed(object sender, RoutedEventArgs e)
        {
            var list = from ca in CaptureAppModel.AppModel.CaptureAnalyzers
                       where ca.SupportedCaptureMethod == CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController.CaptureMethod
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

                var analysisRun = CaptureAppModel.AppModel.AddAnalysisRun(selected);
                CaptureErrorsPanel.AddedAnalysis(analysisRun);
                await HandleAnalyzerChosen(
                    analysisRun,
                    selected,
                    CaptureAppModel.AppModel.SelectedDeviceContext.CaptureController);
            }
        }

        private async Task HandleAnalyzerChosen(
            AnalysisRunModel analysisRun,
            ICaptureAnalyzer captureAnalyzer,
            IDeviceCaptureController captureController)
        {
            analysisRun.IsProcessing = true;
            ECaptureAnalyzerResult analyzerResult = await captureAnalyzer.RunAsync(analysisRun, captureController);

            void OnError(string strErrorMsg)
            {
                // remove entry
                CaptureAppModel.AppModel.RemoveAnalysisRun(analysisRun);

                // show error msg
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

            analysisRun.IsProcessing = false;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files.Any())
                {
                    foreach (string file in files)
                    {
                        if (file.EndsWith($".{FiddlerFileExtension}"))
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
    }
}
