// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using XMAT.Models;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for PreferencesWindow.xaml
    /// </summary>
    public partial class PreferencesWindow : Window
    {
        public PreferencesWindow()
        {
            InitializeComponent();

            ProxyTab.Header = Localization.GetLocalizedString("PREFS_PROXY_TAB");
            TraceTab.Header = Localization.GetLocalizedString("PREFS_TRACE_TAB");
            Cancel.Content = Localization.GetLocalizedString("PREFS_BUTTON_CLOSE");
            Save.Content = Localization.GetLocalizedString("PREFS_BUTTON_SAVE");
            PreferencesWindowTitle.Title = Localization.GetLocalizedString("PREFS_WINDOW_TITLE");

            // The window opens on its tallest tab, Web Proxy, and keeps that height when another tab is selected.
            Loaded += (_, _) => SizeToContent = SizeToContent.Manual;
        }

        private void Save_Executed(object sender, RoutedEventArgs e)
        {
            try
            {
                ProxyOptions.CommitPortRange();
                ProxyOptions.CommitCaptureLimit();
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, Localization.GetLocalizedString("PROXY_CAPTURE_ERROR_TITLE"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // settings and preferences models
            if (!CaptureAppSettings.Serialize(CaptureAppModel.AppModel))
            {
                return;
            }

            MessageBox.Show(Localization.GetLocalizedString("PREFERENCES_SAVED_MESSAGE"), Localization.GetLocalizedString("PREFERENCES_SAVED_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
