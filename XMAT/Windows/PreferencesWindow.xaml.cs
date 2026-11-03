// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using System.Windows;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for PreferencesWindow.xaml
    /// </summary>
    public partial class PreferencesWindow : Window
    {
        private readonly IApplicationPreferences _preferences;
        private AppPreferencesModel _draft;
        private long _expectedRevision;
        private bool _saving;
        private bool _closed;

        public PreferencesWindow() : this(CaptureAppModel.AppModel) { }

        internal PreferencesWindow(CaptureAppModel model, IApplicationPreferences preferences = null)
        {
            _preferences = preferences ?? model.ApplicationPreferences;
            InitializeComponent();

            ProxyTab.Header = Localization.GetLocalizedString("PREFS_PROXY_TAB");
            TraceTab.Header = Localization.GetLocalizedString("PREFS_TRACE_TAB");
            Cancel.Content = Localization.GetLocalizedString("PREFS_BUTTON_CLOSE");
            Save.Content = Localization.GetLocalizedString("PREFS_BUTTON_SAVE");
            PreferencesWindowTitle.Title = Localization.GetLocalizedString("PREFS_WINDOW_TITLE");
            GeneralTab.Header = Localization.GetLocalizedString("CTRL_PREF_GENERAL");
            LanguageLabel.Text = Localization.GetLocalizedString("CTRL_PREF_LANGUAGE");
            ThemeLabel.Text = Localization.GetLocalizedString("CTRL_PREF_THEME");
            LanguageRestart.Text = Localization.GetLocalizedString("CTRL_PREF_LANGUAGE_RESTART");
            Save.IsEnabled = false;
            Loaded += async (_, _) => await LoadDraftAsync();
            Closed += (_, _) => _closed = true;
        }

        private async Task LoadDraftAsync()
        {
            try
            {
                var snapshot = await _preferences.InspectAsync();
                if (_closed)
                    return;
                _expectedRevision = snapshot.Revision;
                _draft = ApplicationPreferences.CreateDraft(snapshot.Values);
                DataContext = _draft;
                LanguageChoice.ItemsSource = snapshot.Options.Languages;
                LanguageChoice.SelectedItem = snapshot.Values.Language;
                ThemeChoice.ItemsSource = snapshot.Options.Themes;
                ThemeChoice.SelectedItem = snapshot.Values.Theme;
                Save.IsEnabled = !_saving;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                if (_closed)
                    return;
                Save.IsEnabled = false;
                MessageBox.Show(this, Localization.GetLocalizedString("CTRL_PREF_UNAVAILABLE"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal async Task<ApplicationPreferenceChange> SaveDraftAsync()
        {
            ProxyOptions.CommitPortRange();
            TraceOptions.CommitDraft();
            _draft.Language = LanguageChoice.SelectedItem as string;
            _draft.Theme = ThemeChoice.SelectedItem as string;
            var result = await _preferences.ApplyAsync(
                _expectedRevision, ApplicationPreferences.ReadValues(_draft));
            await LoadDraftAsync();
            return result;
        }

        private async void Save_Executed(object sender, RoutedEventArgs e)
        {
            if (_saving || _draft == null)
                return;
            _saving = true;
            Save.IsEnabled = false;
            try
            {
                var result = await SaveDraftAsync();
                if (_closed)
                    return;
                var message = Localization.GetLocalizedString(
                    result.Persisted ? "PREFERENCES_SAVED_MESSAGE" : "CTRL_PREF_COMMITTED_NOT_SAVED");
                if (result.LanguageRestartRequired)
                    message += "\n\n" + Localization.GetLocalizedString("CTRL_PREF_LANGUAGE_RESTART");
                MessageBox.Show(this, message,
                    Title, MessageBoxButton.OK, result.Persisted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                if (_closed)
                    return;
                var key = exception switch
                {
                    AdministrationException { Failure: AdministrationFailure.SourceChanged } => "CTRL_PREF_STALE",
                    ArgumentException or AdministrationException { Failure: AdministrationFailure.InvalidRequest } => "CTRL_PREF_INVALID",
                    _ => "CTRL_PREF_UNAVAILABLE"
                };
                MessageBox.Show(this, Localization.GetLocalizedString(key), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _saving = false;
                if (!_closed)
                    Save.IsEnabled = true;
            }
        }
    }
}
