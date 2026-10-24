// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly Logger _unhandledLog = new();
        private XboxProxySessions _xboxSessions;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // must be first
            Localization.LoadLanguage(CaptureAppModel.AppModel.PreferencesModel.Language);

            ThemeManager.ApplyTheme(CaptureAppModel.AppModel.PreferencesModel.Theme);

            InitializeExceptionHandlers();
            // Before any proxy, certificate or database initialization.
            RecoverProxySettings();
            InitializeDataLayer();
            InitializeCaptureMethods();
            InitializeCaptureAnalyzers();
            TrackXboxCaptures();
        }

        private void InitializeExceptionHandlers()
        {
            _unhandledLog.InitLog("unhandled.log", LogLevel.DEBUG);

            // These end the process, so the Windows proxy is restored before any logging or dialog.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                PcProxyRecovery.Current.TryRecoverAfterFatalError();
                PublicUtilities.CollectLogs();
                UnhandledExceptionHandler(e.ExceptionObject as Exception);

                MessageBox.Show(Localization.GetLocalizedString("UNHANDLED_EXCEPTION_HANDLER_MESSAGE", e.ExceptionObject), Localization.GetLocalizedString("UNHANDLED_EXCEPTION_HANDLER_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
            };
            Dispatcher.UnhandledException += (s, e) =>
            {
                PcProxyRecovery.Current.TryRecoverAfterFatalError();
                UnhandledExceptionHandler(e.Exception);
            };
            Current.DispatcherUnhandledException += (s, e) =>
            {
                PcProxyRecovery.Current.TryRecoverAfterFatalError();
                UnhandledExceptionHandler(e.Exception);
            };
            // Unobserved task exceptions do not end the process, so capture keeps its proxy.
            TaskScheduler.UnobservedTaskException += (s, e) => { UnhandledExceptionHandler(e.Exception); };
        }

        private void RecoverProxySettings()
        {
            try
            {
                PcProxyRecovery.Current.GuardLost += (result, nonce) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    // A capture started since then has its own guard, so the notice no longer applies.
                    if (PcProxyRecovery.Current.IsSuperseded(nonce))
                    {
                        return;
                    }
                    MessageBox.Show(
                        Localization.GetLocalizedString(result switch
                        {
                            RecoveryResult.Restored or RecoveryResult.NotNeeded => "PROXY_RECOVERY_GUARD_LOST",
                            RecoveryResult.SettingsChanged => "PROXY_RECOVERY_GUARD_LOST_CHANGED",
                            _ => "PROXY_RECOVERY_GUARD_LOST_UNRESTORED"
                        }),
                        Localization.GetLocalizedString("PROXY_RECOVERY_TITLE"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }));
                var abandoned = PcProxyRecovery.Current.HealAtStartup();
                if (abandoned != null &&
                    MessageBox.Show(
                        Localization.GetLocalizedString("PROXY_RECOVERY_ABANDONED_MESSAGE", abandoned.Server),
                        Localization.GetLocalizedString("PROXY_RECOVERY_TITLE"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    PcProxyRecovery.Current.DisableAbandonedProxy(abandoned);
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not recover the Windows proxy settings at startup: {ex}");
            }
            try
            {
                var abandoned = XboxProxySessions.FindAbandoned(PcProxyRecovery.DefaultDirectory);
                if (abandoned.Consoles.Count > 0)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => OfferXboxProxyDisable(abandoned)));
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not check for Xbox captures that ended unexpectedly: {ex}");
            }
        }

        // Consoles are only contacted, and restarted, after the user agrees. The records are removed once the
        // offer is answered, so a start that ends before then offers it again.
        private async void OfferXboxProxyDisable(AbandonedXboxSessions abandoned)
        {
            var answer = MessageBox.Show(
                Localization.GetLocalizedString("PROXY_RECOVERY_XBOX_MESSAGE", string.Join(", ", abandoned.Consoles)),
                Localization.GetLocalizedString("PROXY_RECOVERY_XBOX_TITLE"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            XboxProxySessions.Acknowledge(abandoned);
            if (answer != MessageBoxResult.Yes || !GDKXHelper.IsGDKXInstalled(true))
            {
                return;
            }
            foreach (var console in abandoned.Consoles)
            {
                try
                {
                    if (await CaptureUtilities.DisableXboxProxyAsync(console))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Could not disable the proxy on {console}: {ex}");
                }
                MessageBox.Show(
                    Localization.GetLocalizedString("PROXY_RECOVERY_XBOX_FAILED", console),
                    Localization.GetLocalizedString("PROXY_ERROR_CONNECTION"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void TrackXboxCaptures()
        {
            _xboxSessions = new XboxProxySessions(PcProxyRecovery.DefaultDirectory, ProcessIdentity.Current);
            void Track(ICaptureDeviceContext context)
            {
                if (context.DeviceType == DeviceType.XboxConsole &&
                    context.CaptureController is WebServiceDeviceCaptureController controller)
                {
                    controller.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(WebServiceDeviceCaptureController.IsRunning))
                        {
                            _xboxSessions.SetCapturing(controller.DeviceName, controller.IsRunning);
                        }
                    };
                }
            }
            foreach (var context in CaptureAppModel.AppModel.CaptureDeviceContexts)
            {
                Track(context);
            }
            CaptureAppModel.AppModel.CaptureDeviceContexts.CollectionChanged += (s, e) =>
            {
                foreach (var context in e.NewItems?.OfType<ICaptureDeviceContext>() ?? Enumerable.Empty<ICaptureDeviceContext>())
                {
                    Track(context);
                }
            };
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
            if (_unhandledLog != null)
            {
                _unhandledLog.CloseLog();
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            ShutdownCaptureAnalyzers();
            ShutdownCaptureMethods();
            _xboxSessions?.Clear();
            ShutdownDataLayer();
            ShutdownExceptionHandlers();
        }

        private void UnhandledExceptionHandler(Exception e)
        {
            _unhandledLog.Log(0, LogLevel.FATAL, $"Unhandled exception: {e}");
        }
    }
}
