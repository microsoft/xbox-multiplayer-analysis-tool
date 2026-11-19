// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using XMAT.DesignSystem;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class DesignSystemWindowTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public DesignSystemWindowTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void EveryPage_SaysWhereItIsDefinedAndShowsSectionsOfExamples()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = Open("Light");
                try
                {
                    Assert.Equal(StoryCatalog.Pages.Count, window.Stories.Count);
                    // The window lists the sections in order, each page under its own.
                    List<string> pageSections = window.Stories.Select(story => story.Section).ToList();
                    Assert.All(pageSections, section => Assert.Contains(section, Story.Sections));
                    Assert.Equal(pageSections.OrderBy(section => Array.IndexOf(Story.Sections, section)), pageSections);
                    for (int index = 0; index < window.Stories.Count; index++)
                    {
                        window.ShowPage(index);
                        WpfTestHost.Pump();
                        Story story = window.ShownStory;
                        Assert.Same(window.Stories[index], story);
                        Assert.False(string.IsNullOrWhiteSpace(story.Title));
                        Assert.False(string.IsNullOrWhiteSpace(story.Summary), story.Title);
                        Assert.NotEmpty(story.DefinedInFiles);
                        Assert.All(story.DefinedInFiles, file => Assert.True(
                            File.Exists(Path.Combine(RepositoryPaths.Root, file)), $"{story.Title}: {file} doesn't exist."));
                        List<StorySection> sections = Descendants(story).OfType<StorySection>().ToList();
                        Assert.NotEmpty(sections);
                        foreach (StorySection section in sections)
                        {
                            Assert.False(string.IsNullOrWhiteSpace(section.Title), story.Title);
                            Assert.False(string.IsNullOrWhiteSpace(section.StyleName), section.Title);
                            Assert.NotEmpty(section.Items.OfType<StoryExample>());
                            Assert.All(section.Items.OfType<StoryExample>(), example =>
                            {
                                Assert.NotNull(example.Content);
                                Assert.True(
                                    example.ActualHeight > 0, $"{story.Title}, {section.Title}: {example.Caption}");
                            });
                        }
                    }
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // The preview settings change the window's resources only: XMAT's theme, which the tests share, stays as it is.
        [Fact]
        public void PreviewSettings_ChangeTheWindowAndLeaveXmatsThemeAsItIs()
        {
            _application.Run(_ =>
            {
                string xmatTheme = ThemeManager.CurrentTheme;
                object applicationCanvas = Application.Current.TryFindResource("SurfaceCanvasBrush");
                DesignSystemWindow window = Open("Light");
                try
                {
                    Color light = Canvas(window);
                    Assert.Equal(ThemeColor("LightTheme", "SurfaceCanvasBrush"), light);
                    Assert.Equal(32.0, window.FindResource("MenuItemHeight"));

                    window.Preview("Dark", null, comfortView: false);
                    WpfTestHost.Pump();
                    Assert.Equal(ThemeColor("DarkTheme", "SurfaceCanvasBrush"), Canvas(window));
                    Assert.Equal(28.0, window.FindResource("MenuItemHeight"));

                    ContrastTheme nightSky = ContrastTheme.All.Single(theme => theme.Name == "Night sky");
                    window.Preview("Dark", nightSky, comfortView: false);
                    WpfTestHost.Pump();
                    Assert.Contains(Canvas(window), nightSky.Colors);
                    Assert.Equal(
                        nightSky.Colors[0], ((SolidColorBrush)window.FindResource(SystemColors.WindowBrushKey)).Color);

                    window.Preview("Light", null, comfortView: true);
                    WpfTestHost.Pump();
                    Assert.Equal(light, Canvas(window));
                    Assert.False(window.Resources.Contains(SystemColors.WindowColorKey));
                    Assert.Equal(32.0, window.FindResource("MenuItemHeight"));

                    Assert.Equal(xmatTheme, ThemeManager.CurrentTheme);
                    Assert.Same(applicationCanvas, Application.Current.TryFindResource("SurfaceCanvasBrush"));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // Searching lists the pages that match, ignoring case, each followed by its matching sections, indented, and
        // leaves the page shown as it is. Clearing the search lists every page under its section again.
        [Fact]
        public void Search_ListsThePagesThatMatchWithTheirSections_AndClearingListsEveryPage()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = Open("Light");
                try
                {
                    Story shown = window.ShownStory;
                    window.ComponentSearch.Text = "toggle";
                    WpfTestHost.Pump();
                    List<DesignSystemWindow.PageEntry> listed = window.Listed.ToList();
                    int buttons = listed.FindIndex(entry => entry.Story is ButtonsStory && !entry.IsSection);
                    Assert.True(buttons >= 0, "Searching for toggle doesn't list the Buttons page.");
                    Assert.Equal(
                        new[] { "Toggle button", "Toggle switch", "Filter toggle" },
                        listed.Skip(buttons + 1).TakeWhile(entry => entry.IsSection).Select(entry => entry.Title));
                    Assert.Equal("Buttons, page", listed[buttons].AccessibleName);
                    Assert.Equal("Toggle switch, section of Buttons", listed[buttons + 2].AccessibleName);
                    Assert.True(
                        TextLeft(window, listed[buttons + 1]) > TextLeft(window, listed[buttons]),
                        "A section isn't indented under its page.");
                    Assert.Same(shown, window.ShownStory);

                    window.ComponentSearch.Text = "HISTOGRAM";
                    WpfTestHost.Pump();
                    Assert.Contains(window.Listed, entry => entry.Story is DataVisualizationStory && !entry.IsSection);

                    window.ComponentSearch.Clear();
                    WpfTestHost.Pump();
                    Assert.Equal(window.Stories, window.Listed.Select(entry => entry.Story));
                    Assert.All(window.Listed, entry => Assert.False(entry.IsSection));
                    Assert.Equal(
                        Story.Sections.Where(section => window.Stories.Any(story => story.Section == section)),
                        window.PageList.Items.Groups.Cast<CollectionViewGroup>().Select(group => (string)group.Name));
                    Assert.Same(shown, ((DesignSystemWindow.PageEntry)window.PageList.SelectedItem).Story);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // A section that search finds opens its page with the section at the top; its page opens at the page's top.
        [Fact]
        public void ASectionFoundBySearch_OpensItsPageAtTheSection()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = Open("Light");
                try
                {
                    window.ComponentSearch.Text = "toggle switch";
                    WpfTestHost.Pump();
                    DesignSystemWindow.PageEntry section =
                        window.Listed.Single(entry => entry.IsSection && entry.Title == "Toggle switch");
                    window.PageList.SelectedItem = section;
                    WpfTestHost.Pump();

                    Assert.IsType<ButtonsStory>(window.ShownStory);
                    double top = section.Section.TranslatePoint(default, window.PageHost).Y;
                    Assert.True(top > 0, "The Toggle switch section starts its page, so the test can't tell.");
                    Assert.Equal(top, window.PageScroller.VerticalOffset, 1);

                    window.PageList.SelectedItem = window.Listed.Single(entry => entry.Story is ButtonsStory && !entry.IsSection);
                    WpfTestHost.Pump();
                    Assert.Equal(0, window.PageScroller.VerticalOffset);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void ASearchThatMatchesNothing_SaysNoComponentsMatch()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = Open("Light");
                try
                {
                    Assert.False(window.NoMatch.IsVisible);
                    window.ComponentSearch.Text = "zqxj";
                    WpfTestHost.Pump();
                    Assert.Empty(window.Listed);
                    Assert.True(window.NoMatch.IsVisible);
                    Assert.Equal("No components match", window.NoMatch.Text);

                    window.ComponentSearch.Text = "color";
                    WpfTestHost.Pump();
                    Assert.NotEmpty(window.Listed);
                    Assert.False(window.NoMatch.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        internal static DesignSystemWindow Open(string theme)
        {
            var window = new DesignSystemWindow(theme, comfortView: true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000,
                Top = -12000,
                ShowActivated = false,
            };
            window.Show();
            WpfTestHost.Pump();
            return window;
        }

        internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private static Color Canvas(Window window) => ((SolidColorBrush)window.Background).Color;

        // Where ENTRY's text starts in the list.
        private static double TextLeft(DesignSystemWindow window, DesignSystemWindow.PageEntry entry)
        {
            ListBoxItem item = Descendants(window.PageList).OfType<ListBoxItem>()
                .Single(container => container.DataContext == entry);
            TextBlock text = Descendants(item).OfType<TextBlock>().Single();
            return text.TranslatePoint(default, window.PageList).X;
        }

        private static Color ThemeColor(string theme, string key)
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{theme}.xaml")
            };
            return ((SolidColorBrush)dictionary[key]).Color;
        }
    }
}
