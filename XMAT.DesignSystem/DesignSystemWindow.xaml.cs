// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XMAT.DesignSystem
{
    // The design system: the new look's foundations and components, a page each, under their sections (the pages are in
    // Stories). The window keeps the new look's resources itself, so its theme, contrast theme and Comfort view
    // settings preview the pages without changing XMAT's settings, which the tests share. The search above the list
    // finds pages and their sections by name, style and rule.
    public partial class DesignSystemWindow : Window
    {
        // The first choice of the contrast theme list.
        private const string NoContrastTheme = "None";

        private const string NoMatchText = "No components match";

        private readonly ResourceDictionary _look = StoryPreview.CreateLook();
        private readonly IReadOnlyList<Story> _stories;
        private readonly ListCollectionView _pages;
        // What search reads on each page, made the first time someone searches.
        private IReadOnlyList<PageText> _index;
        private string _theme;
        private ContrastTheme _contrast;
        private bool _comfortView;
        private bool _ready;
        // While the list changes between the pages and the search results, its selection doesn't open a page.
        private bool _relisting;

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
            _pages = Grouped(_stories.Select(story => new PageEntry(story, null, story.Title)));
            PageList.ItemsSource = _pages;
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

        // The pages, or while searching, the pages that match and their matching sections, in the order listed.
        internal IReadOnlyList<PageEntry> Listed => PageList.Items.Cast<PageEntry>().ToList();

        internal void ShowPage(int index)
        {
            ComponentSearch.Clear();
            PageList.SelectedItem = _pages.GetItemAt(Math.Clamp(index, 0, _stories.Count - 1));
        }

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
            if (_relisting || PageList.SelectedItem is not PageEntry entry)
            {
                return;
            }

            PageList.ScrollIntoView(entry);
            Story story = entry.Story;
            PageTitle.Text = story.Title;
            PageSummary.Text = story.Summary;
            PageDefinedIn.Text = $"Defined in {string.Join(", ", story.DefinedInFiles)}";
            PageHost.Content = story;
            if (entry.Section == null)
            {
                PageScroller.ScrollToTop();
                return;
            }

            // A section opens its page with the section at the top.
            PageScroller.UpdateLayout();
            PageScroller.ScrollToVerticalOffset(entry.Section.TranslatePoint(default, PageHost).Y);
        }

        // Up from the first entry goes back to the search.
        private void PageList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None &&
                e.OriginalSource is ListBoxItem { DataContext: var item } && PageList.Items.Count > 0 &&
                ReferenceEquals(item, PageList.Items[0]))
            {
                ComponentSearch.Focus();
                e.Handled = true;
            }
        }

        // Ctrl+F moves to the search, as it does in a capture.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (!e.Handled && e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ComponentSearch.Focus();
                ComponentSearch.SelectAll();
                e.Handled = true;
            }
        }

        // Down from the search moves into the list, to the entry shown or else the first.
        private void ComponentSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None || PageList.Items.Count == 0)
            {
                return;
            }

            if (PageList.SelectedItem == null)
            {
                PageList.SelectedIndex = 0;
            }

            PageList.UpdateLayout();
            (PageList.ItemContainerGenerator.ContainerFromItem(PageList.SelectedItem) as ListBoxItem ?? (UIElement)PageList)
                .Focus();
            e.Handled = true;
        }

        // Lists the pages that match the search, each followed by its matching sections, or every page while the search
        // is empty. The entry shown stays selected when it is listed.
        private void ComponentSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string[] words = ComponentSearch.Text.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            ListCollectionView list = words.Length == 0 ? _pages : Grouped(Find(words));
            var selected = PageList.SelectedItem as PageEntry;
            PageEntry shown = list.Cast<PageEntry>().FirstOrDefault(entry =>
                    ReferenceEquals(entry.Story, selected?.Story) && ReferenceEquals(entry.Section, selected?.Section))
                ?? list.Cast<PageEntry>().FirstOrDefault(entry =>
                    ReferenceEquals(entry.Story, ShownStory) && entry.Section == null);
            _relisting = true;
            try
            {
                PageList.ItemsSource = list;
                PageList.SelectedItem = shown;
            }
            finally
            {
                _relisting = false;
            }

            AutomationProperties.SetName(PageList, words.Length == 0 ? "Pages" : "Search results");
            NoMatch.Text = words.Length > 0 && list.IsEmpty ? NoMatchText : string.Empty;
        }

        // The pages whose text has every word, ignoring case, each followed by its sections whose own text has every word.
        private IEnumerable<PageEntry> Find(string[] words)
        {
            _index ??= _stories.Select(PageText.Of).ToList();
            bool Matches(string text) => words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            foreach (PageText page in _index.Where(page => Matches(page.Text)))
            {
                yield return new PageEntry(page.Story, null, $"{page.Story.Title}, page");
                foreach ((StorySection section, _) in page.Sections.Where(section => Matches(section.Text)))
                {
                    yield return new PageEntry(page.Story, section, $"{section.Title}, section of {page.Story.Title}");
                }
            }
        }

        private static ListCollectionView Grouped(IEnumerable<PageEntry> entries)
        {
            var view = new ListCollectionView(entries.ToList());
            view.GroupDescriptions.Add(new SectionGrouping());
            return view;
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

        // An entry in the list: a page, which shows beside the list once selected, or one of its sections, which search
        // lists under it and which opens the page at the section. ACCESSIBLE NAME is what screen readers read, which
        // says whether a search result is a page or a section.
        internal sealed class PageEntry
        {
            internal PageEntry(Story story, StorySection section, string accessibleName)
            {
                Story = story;
                Section = section;
                AccessibleName = accessibleName;
            }

            internal Story Story { get; }

            // The section, or null for the page itself.
            internal StorySection Section { get; }

            public string Title => Section?.Title ?? Story.Title;

            public bool IsSection => Section != null;

            public string AccessibleName { get; }

            public override string ToString() => Title;
        }

        // What search reads on a page: its title and summary, its sections' titles and style names, and its rules' IDs and
        // text; and of each section, its title and style name.
        private sealed record PageText(Story Story, string Text, IReadOnlyList<(StorySection Section, string Text)> Sections)
        {
            internal static PageText Of(Story story)
            {
                List<(StorySection, string)> sections = LogicalDescendants(story).OfType<StorySection>()
                    .Select(section => (section, $"{section.Title}\n{section.StyleName}"))
                    .ToList();
                IEnumerable<string> rules = RuleCatalog.OnPage(story.Title).Select(rule => $"{rule.Id}\n{rule.Text}");
                string text = string.Join(
                    "\n", new[] { story.Title, story.Summary }.Concat(sections.Select(section => section.Item2)).Concat(rules));
                return new PageText(story, text, sections);
            }

            // A page's sections are in its logical tree, so they are found before the page is first shown.
            private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
            {
                foreach (DependencyObject child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                {
                    yield return child;
                    foreach (DependencyObject descendant in LogicalDescendants(child))
                    {
                        yield return descendant;
                    }
                }
            }
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
