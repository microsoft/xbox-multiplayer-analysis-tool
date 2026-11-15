// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
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
        public void EveryPage_NamesAStyleFileAndShowsSectionsOfExamples()
        {
            _application.Run(_ =>
            {
                HashSet<string> productXaml = CapabilityDiscovery.ProductXaml().Select(xaml => xaml.Path).ToHashSet();
                DesignSystemWindow window = Open("Light");
                try
                {
                    Assert.Equal(StoryCatalog.Pages.Count, window.Stories.Count);
                    for (int index = 0; index < window.Stories.Count; index++)
                    {
                        window.ShowPage(index);
                        WpfTestHost.Pump();
                        Story story = window.ShownStory;
                        Assert.False(string.IsNullOrWhiteSpace(story.Title));
                        Assert.False(string.IsNullOrWhiteSpace(story.Summary), story.Title);
                        Assert.Contains(story.StyleFile, productXaml);
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
