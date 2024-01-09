// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.Models;
using System.Windows;

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
        }

        private void Save_Executed(object sender, RoutedEventArgs e)
        {
            // settings and preferences models
            CaptureAppSettings.Serialize(CaptureAppModel.AppModel);

            MessageBox.Show(Localization.GetLocalizedString("PREFERENCES_SAVED_MESSAGE"), Localization.GetLocalizedString("PREFERENCES_SAVED_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
