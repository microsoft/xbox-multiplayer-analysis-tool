// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Windows;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.NetworkTrace;
using XMAT.XboxLiveCaptureAnalysis;
using System.Threading.Tasks;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly Logger _unhandledLog = new();

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // must be first
            Localization.LoadLanguage(CaptureAppModel.AppModel.PreferencesModel.Language);

            InitializeExceptionHandlers();
            InitializeDataLayer();
            InitializeCaptureMethods();
            InitializeCaptureAnalyzers();
        }

        private void InitializeExceptionHandlers()
        {
            _unhandledLog.InitLog("unhandled.log", LogLevel.DEBUG);

            AppDomain.CurrentDomain.UnhandledException += (s,e) =>
            {
                PublicUtilities.CollectLogs();
                UnhandledExceptionHandler(e.ExceptionObject as Exception);

                MessageBox.Show(Localization.GetLocalizedString("UNHANDLED_EXCEPTION_HANDLER_MESSAGE", e.ExceptionObject), Localization.GetLocalizedString("UNHANDLED_EXCEPTION_HANDLER_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            };
            Dispatcher.UnhandledException         += (s,e) => { UnhandledExceptionHandler(e.Exception); };
            Current.DispatcherUnhandledException  += (s,e) => { UnhandledExceptionHandler(e.Exception); };
            TaskScheduler.UnobservedTaskException += (s,e) => { UnhandledExceptionHandler(e.Exception); };
        }

        private void InitializeDataLayer()
        {
            CaptureAppModel.AppModel.InitializeDatabase();
        }

        private void InitializeCaptureMethods()
        {
            WebServiceCaptureMethod.Method.Initialize(CaptureAppModel.AppModel);
            NetworkTraceCaptureMethod.Method.Initialize(CaptureAppModel.AppModel);

            // initialize the preferences
            CaptureAppModel.AppModel.PreferencesModel.WebProxyCapturePreferences =
                WebServiceCaptureMethod.Method.PreferencesModel;
            CaptureAppModel.AppModel.PreferencesModel.NetworkCapturePreferences =
                NetworkTraceCaptureMethod.Method.PreferencesModel;
            CaptureAppModel.AppModel.PreferencesModel.NetworkAnalysisPreferences =
                NetworkTraceCaptureAnalyzer.Analyzer.PreferencesModel;
        }

        private void InitializeCaptureAnalyzers()
        {
            InitializeCaptureAnalyzer(XboxLiveCaptureAnalyzer.Analyzer);
            InitializeCaptureAnalyzer(NetworkTraceCaptureAnalyzer.Analyzer);
            // TODO: initialize the analyzer preferences
        }

        private void InitializeCaptureAnalyzer(ICaptureAnalyzer analyzer)
        {
            try
            {
                CaptureAppModel.AppModel.AddCaptureAnalyzer(analyzer);
            }
            catch (Exception ex)
            {
                // some kind of application error message should be displayed
                MessageBox.Show(
                    ex.Message,
                    Localization.GetLocalizedString("RUN_ANALYZER_EXCEPTION", analyzer.Description),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ShutdownDataLayer()
        {
            CaptureAppModel.AppModel.ActiveDatabase.Shutdown();
        }

        private void ShutdownCaptureMethods()
        {
            WebServiceCaptureMethod.Method.Shutdown();
            NetworkTraceCaptureMethod.Method.Shutdown();
        }

        private void ShutdownCaptureAnalyzers()
        {
            foreach (ICaptureAnalyzer analyzer in CaptureAppModel.AppModel.CaptureAnalyzers)
            {
                analyzer.Shutdown();
            }
        }

        private void ShutdownExceptionHandlers()
        {
            if(_unhandledLog != null)
            {
                _unhandledLog.CloseLog();
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            ShutdownCaptureAnalyzers();
            ShutdownCaptureMethods();
            ShutdownDataLayer();
            ShutdownExceptionHandlers();
        }

        private void UnhandledExceptionHandler(Exception e)
        {
            _unhandledLog.Log(0, LogLevel.FATAL, $"Unhandled exception: {e}");
        }
    }
}
