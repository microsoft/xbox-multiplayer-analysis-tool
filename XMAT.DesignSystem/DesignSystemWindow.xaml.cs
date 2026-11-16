// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XMAT.DesignSystem
{
    // The design system: the new look's foundations and components, a page each, under their sections (the pages are in
    // Stories). The window keeps the new look's resources itself, so its theme, contrast theme and Comfort view
    // settings preview the pages without changing XMAT's settings, which the tests share.
    public partial class DesignSystemWindow : Window
    {
        // The first choice of the contrast theme list.
        private const string NoContrastTheme = "None";

        private readonly ResourceDictionary _look = StoryPreview.CreateLook();
        private readonly IReadOnlyList<Story> _stories;
        private string _theme;
        private ContrastTheme _contrast;
        private bool _comfortView;
        private bool _ready;

        // THEME and COMFORT VIEW are the settings the preview starts from.
        internal DesignSystemWindow(string theme, bool comfortView)
        {
            _theme = theme;
            _comfortView = comfortView;
            Resources = _look;
            ApplyPreview();
            InitializeComponent();

            ContrastChoice.ItemsSource = new object[] { NoContrastTheme }.Concat(ContrastTheme.All).ToList();
            ContrastChoice.SelectedIndex = 0;
            LightTheme.IsChecked = theme != "Dark";
            DarkTheme.IsChecked = theme == "Dark";
            ComfortViewChoice.IsChecked = comfortView;
            UpdatePreviewChoices();

            _stories = StoryCatalog.Pages.Select(create => create()).ToList();
            var pages = new ListCollectionView(_stories.Select(story => new PageEntry(story)).ToList());
            pages.GroupDescriptions.Add(new SectionGrouping());
            PageList.ItemsSource = pages;
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

        private void ApplyPreview() => StoryPreview.Apply(
            _look, _theme, _contrast, _comfortView, SystemParameters.HighContrast, TextScale.ReadFactor());

        // A contrast theme replaces Light and Dark, and while Windows high contrast is on, Windows sets the colors.
        private void UpdatePreviewChoices()
        {
            bool windowsHighContrast = SystemParameters.HighContrast;
            ContrastChoice.IsEnabled = !windowsHighContrast;
            ContrastChoice.ToolTip = windowsHighContrast
                ? "Windows high contrast is on, so the window shows its colors"
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
            PageDefinedIn.Text = $"Defined in {string.Join(", ", page.Story.DefinedInFiles)}";
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

        // Windows high contrast was turned on or off: Windows sets the colors, or the preview's theme returns.
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

        // Groups the list of pages under their sections. StoryCatalog lists the pages section by section, so the list
        // keeps their order.
        private sealed class SectionGrouping : GroupDescription
        {
            public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
                ((PageEntry)item).Story.Section;
        }
    }
}
