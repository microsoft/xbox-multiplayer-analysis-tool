// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XMAT.DesignSystem;
using XMAT.Models;

namespace XMAT
{
    // The design system: the new look's components in the states they can be set to, a page per component (the pages
    // are in DesignSystem/Stories). Help > Design system opens it from either look. The window keeps its own copy of
    // the new look's resources, so its theme, contrast theme and Comfort view settings preview the components here and
    // leave the app as it is.
    public partial class DesignSystemWindow : Window
    {
        private static DesignSystemWindow _open;

        private readonly ResourceDictionary _look = StoryPreview.CreateLook();
        private readonly IReadOnlyList<Story> _stories;
        private string _theme;
        private ContrastTheme _contrast;
        private bool _comfortView;
        private bool _ready;

        // THEME and COMFORT VIEW are the settings the preview starts from, normally the app's.
        internal DesignSystemWindow(string theme, bool comfortView)
        {
            _theme = theme;
            _comfortView = comfortView;
            Resources = _look;
            ApplyPreview();
            InitializeComponent();

            Title = Localization.GetLocalizedString("DESIGN_SYSTEM_WINDOW_TITLE");
            Heading.Text = Localization.GetLocalizedString("DESIGN_SYSTEM_HEADING");
            Subheading.Text = Localization.GetLocalizedString("DESIGN_SYSTEM_SUBHEADING");
            AutomationProperties.SetName(PageList, Localization.GetLocalizedString("DESIGN_SYSTEM_PAGES"));
            string previewNote = Localization.GetLocalizedString("DESIGN_SYSTEM_PREVIEW");
            AutomationProperties.SetName(PreviewSettings, previewNote);
            PreviewSettings.ToolTip = previewNote;
            ThemeLabel.Text = Localization.GetLocalizedString("DESIGN_SYSTEM_THEME");
            LightTheme.Content = Localization.GetLocalizedString("THEME_LIGHT");
            DarkTheme.Content = Localization.GetLocalizedString("THEME_DARK");
            ContrastLabel.Text = Localization.GetLocalizedString("DESIGN_SYSTEM_CONTRAST");
            AutomationProperties.SetLabeledBy(ContrastChoice, ContrastLabel);
            ComfortViewChoice.Content = Localization.GetLocalizedString("COMFORT_VIEW");

            ContrastChoice.ItemsSource = new object[] { Localization.GetLocalizedString("DESIGN_SYSTEM_CONTRAST_NONE") }
                .Concat(ContrastTheme.All).ToList();
            ContrastChoice.SelectedIndex = 0;
            LightTheme.IsChecked = theme != "Dark";
            DarkTheme.IsChecked = theme == "Dark";
            ComfortViewChoice.IsChecked = comfortView;
            UpdatePreviewChoices();

            _stories = StoryCatalog.Pages.Select(create => create()).ToList();
            PageList.ItemsSource = _stories.Select(story => new PageEntry(story)).ToList();
            PageList.SelectedIndex = 0;
            _ready = true;

            SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            Closed += (_, _) =>
            {
                SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
                SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            };
            Loaded += (_, _) => PageList.Focus();
        }

        internal IReadOnlyList<Story> Stories => _stories;

        internal Story ShownStory => PageHost.Content as Story;

        // Opens the design system beside OWNER, or brings it forward if it is open.
        internal static void ShowFor(Window owner)
        {
            if (_open != null)
            {
                if (_open.WindowState == WindowState.Minimized)
                {
                    _open.WindowState = WindowState.Normal;
                }

                _open.Activate();
                return;
            }

            _open = new DesignSystemWindow(
                ThemeManager.CurrentTheme, CaptureAppModel.AppModel.PreferencesModel.ComfortView) { Owner = owner };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }

        internal void ShowPage(int index) => PageList.SelectedIndex = Math.Clamp(index, 0, _stories.Count - 1);

        // Previews THEME, CONTRAST (a Windows contrast theme, or null) and COMFORT VIEW, as the settings above a page
        // do.
        internal void Preview(string theme, ContrastTheme contrast, bool comfortView)
        {
            LightTheme.IsChecked = theme != "Dark";
            DarkTheme.IsChecked = theme == "Dark";
            ContrastChoice.SelectedItem = (object)contrast ?? ContrastChoice.Items[0];
            ComfortViewChoice.IsChecked = comfortView;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void ApplyPreview() => StoryPreview.Apply(
            _look, _theme, _contrast, _comfortView, SystemParameters.HighContrast, TextScale.ReadFactor());

        // A contrast theme replaces Light and Dark, and while Windows high contrast is on, Windows sets the colors.
        private void UpdatePreviewChoices()
        {
            bool windowsHighContrast = SystemParameters.HighContrast;
            ContrastChoice.IsEnabled = !windowsHighContrast;
            ContrastChoice.ToolTip = windowsHighContrast
                ? Localization.GetLocalizedString("DESIGN_SYSTEM_CONTRAST_WINDOWS")
                : null;
            LightTheme.IsEnabled = DarkTheme.IsEnabled = !windowsHighContrast && _contrast == null;
        }

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedItem is not PageEntry page)
            {
                return;
            }

            PageList.ScrollIntoView(page);
            PageTitle.Text = page.Story.Title;
            PageSummary.Text = page.Story.Summary;
            PageStyleFile.Text = Localization.GetLocalizedString("DESIGN_SYSTEM_STYLE_FILE", page.Story.StyleFile);
            PageHost.Content = page.Story;
            PageScroller.ScrollToTop();
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            _theme = DarkTheme.IsChecked == true ? "Dark" : "Light";
            if (_ready)
            {
                ApplyPreview();
            }
        }

        private void ContrastChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _contrast = ContrastChoice.SelectedItem as ContrastTheme;
            if (_ready)
            {
                UpdatePreviewChoices();
                ApplyPreview();
            }
        }

        private void ComfortViewChoice_Changed(object sender, RoutedEventArgs e)
        {
            _comfortView = ComfortViewChoice.IsChecked == true;
            if (_ready)
            {
                ApplyPreview();
            }
        }

        // Windows high contrast was turned on or off; the app applies its theme again on the same event.
        private void SystemParameters_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    UpdatePreviewChoices();
                    ApplyPreview();
                }));
            }
        }

        // The Windows Text size setting can change while the window is open.
        private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => TextScale.Apply(_look, TextScale.ReadFactor())));

        // A page in the list, which shows its title; the page itself is shown beside the list.
        private sealed class PageEntry
        {
            internal PageEntry(Story story) => Story = story;

            internal Story Story { get; }

            public string Title => Story.Title;

            public override string ToString() => Title;
        }
    }
}
