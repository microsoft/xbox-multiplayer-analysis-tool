// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Media;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class HighContrastThemeTests
    {
        private static readonly string[] ThemeFiles =
            { "LightTheme", "DarkTheme", "Original/LightTheme", "Original/DarkTheme" };

        private static readonly string[] Statuses = { "Neutral", "Success", "Info", "Warning", "Error" };

        // The status role that colors each analysis issue level in the new look: Info counts are good news.
        private static readonly string[] IssueRoles = { "Error", "Warning", "Success" };

        private static readonly string[] IssueLevels = { "Error", "Warning", "Info" };

        // Colors of the Windows 11 contrast themes, in the order of PaletteKeys.
        private static readonly Dictionary<string, Color[]> ContrastThemes = new()
        {
            ["Aquatic"] = Palette("#202020", "#FFFFFF", "#8EE3F0", "#263B50", "#A6A6A6", "#75E9FC", "#202020", "#FFFFFF"),
            ["Desert"] = Palette("#FFFAEF", "#3D3D3D", "#903909", "#FFF5E3", "#676767", "#1C5E75", "#FFFAEF", "#202020"),
            ["Dusk"] = Palette("#2D3236", "#B6F6F0", "#A1BFDE", "#212D3B", "#A6A6A6", "#70EBDE", "#2D3236", "#B6F6F0"),
            ["Night sky"] = Palette("#000000", "#FFFFFF", "#D6B4FD", "#2B2B2B", "#A6A6A6", "#8080FF", "#000000", "#FFEE32")
        };

        private static readonly ResourceKey[] PaletteKeys =
        {
            SystemColors.WindowColorKey, SystemColors.WindowTextColorKey, SystemColors.HighlightColorKey,
            SystemColors.HighlightTextColorKey, SystemColors.GrayTextColorKey, SystemColors.HotTrackColorKey,
            SystemColors.ControlColorKey, SystemColors.ControlTextColorKey
        };

        private readonly CaptureUiApplicationFixture _application;

        public HighContrastThemeTests(CaptureUiApplicationFixture application) => _application = application;

        // A key added to the Light and Dark themes needs a high contrast color too.
        [Fact]
        public void HighContrastTheme_GivesEveryThemeKeyASystemColorAndLeavesSystemColorsToWindows()
        {
            _application.Run(window =>
            {
                var highContrast = Theme("HighContrastTheme");
                var themeKeys = ThemeFiles.SelectMany(name => Theme(name).Keys.OfType<string>()).ToHashSet();
                Assert.Empty(themeKeys.Except(highContrast.Keys.OfType<string>()).OrderBy(key => key));
                Assert.All(highContrast.Keys.Cast<object>(), key => Assert.IsType<string>(key));

                UseHighContrastTheme(window, ContrastThemes["Desert"]);
                foreach (string key in highContrast.Keys)
                {
                    Color color = Assert.IsType<SolidColorBrush>(window.FindResource(key)).Color;
                    Assert.True(color == Colors.Transparent || ContrastThemes["Desert"].Contains(color), $"{key} is {color}");
                }
            });
        }

        [Fact]
        public void PrepareTheme_UsesTheHighContrastThemeInBothLooksAndKeepsTheSavedTheme()
        {
            _application.Run(_ =>
            {
                string previous = ThemeManager.CurrentTheme;
                try
                {
                    foreach (bool newLook in new[] { true, false })
                    {
                        var resources = new ResourceDictionary();
                        ThemeManager.PrepareTheme(resources, "Dark", newLook, highContrast: false);
                        Assert.True(resources.Contains(SystemColors.HighlightBrushKey));

                        ThemeManager.PrepareTheme(resources, "Dark", newLook, highContrast: true);
                        Assert.EndsWith("/HighContrastTheme.xaml",
                            Assert.Single(resources.MergedDictionaries).Source.OriginalString, StringComparison.Ordinal);
                        Assert.False(resources.Contains(SystemColors.HighlightBrushKey));
                        Assert.False(resources.Contains(SystemColors.WindowBrushKey));
                        Assert.Equal("Dark", ThemeManager.CurrentTheme);

                        ThemeManager.PrepareTheme(resources, "Dark", newLook, highContrast: false);
                        Assert.EndsWith(newLook ? "Themes/DarkTheme.xaml" : "Themes/Original/DarkTheme.xaml",
                            Assert.Single(resources.MergedDictionaries).Source.OriginalString, StringComparison.Ordinal);
                        Assert.True(resources.Contains(SystemColors.HighlightBrushKey));
                    }
                    Assert.Throws<ArgumentOutOfRangeException>(
                        () => ThemeManager.PrepareTheme(new ResourceDictionary(), "Unknown", true, highContrast: true));
                }
                finally
                {
                    ThemeManager.PrepareTheme(new ResourceDictionary(), previous, true, highContrast: false);
                }
            });
        }

        [Theory]
        [InlineData("Aquatic")]
        [InlineData("Desert")]
        [InlineData("Dusk")]
        [InlineData("Night sky")]
        public void HighContrastTheme_KeepsTextLegibleInEachContrastTheme(string name)
        {
            _application.Run(window =>
            {
                UseHighContrastTheme(window, ContrastThemes[name]);
                Brush Find(string key) => (Brush)window.FindResource(key);

                // Button triggers change only the fill, so text must stay legible on the hover and pressed fills.
                var pairs = new List<(string Text, string Background)>
                {
                    ("TextPrimaryBrush", "SurfacePanelBrush"),
                    ("ButtonTextBrush", "SurfaceHoverBrush"),
                    ("ButtonTextBrush", "AccentSoftBrush"),
                    ("AccentOnFillBrush", "AccentFillBrush"),
                    ("AccentOnFillBrush", "CaptureActionHoverBrush")
                };
                pairs.AddRange(Statuses.Select(status => ($"{status}OnFillBrush", $"{status}FillBrush")));
                pairs.AddRange(IssueRoles.SelectMany(role => new[]
                {
                    ($"{role}TextBrush", $"{role}SoftBrush"),
                    ($"{role}TextBrush", "SurfaceHoverBrush")
                }));

                // The original look's names; remove these with the original look.
                pairs.AddRange(new[]
                {
                    ("PrimaryForegroundBrush", "PanelBackgroundBrush"),
                    ("ButtonForegroundBrush", "ButtonHoverBackgroundBrush"),
                    ("ButtonForegroundBrush", "ButtonPressedBackgroundBrush"),
                    ("ActiveSelectionForegroundBrush", "ActiveSelectionBrush")
                });
                pairs.AddRange(IssueLevels.SelectMany(level => new[]
                {
                    ($"Issue{level}ForegroundBrush", $"Issue{level}BackgroundBrush"),
                    ($"Issue{level}ForegroundBrush", "ButtonHoverBackgroundBrush")
                }));
                foreach (var (text, background) in pairs)
                {
                    double contrast = ContrastRatio(Find(text), Find(background));
                    Assert.True(contrast >= 4.5, $"{name}: {text} on {background} is {contrast:F2}:1");
                }
                foreach (string status in Statuses)
                {
                    double contrast = ContrastRatio(Find($"{status}FillBrush"), Find("SurfaceWellBrush"));
                    Assert.True(contrast >= 3, $"{name}: {status} bar on the timeline is {contrast:F2}:1");
                }
            });
        }

        [Theory]
        [InlineData("LightTheme")]
        [InlineData("DarkTheme")]
        public void NewLookThemes_KeepTimelineLabelsIssueCountsAndEndpointHeadersReadable(string name)
        {
            _application.Run(_ =>
            {
                var theme = Theme(name);
                Brush Find(string key) => (Brush)theme[key];
                foreach (string status in Statuses)
                {
                    AssertContrast(name, Find($"{status}OnFillBrush"), Find($"{status}FillBrush"), 4.5);
                    AssertContrast(name, Find($"{status}FillBrush"), Find("SurfaceWellBrush"), 3);
                }
                foreach (string role in IssueRoles)
                {
                    foreach (string background in new[] { $"{role}SoftBrush", "SurfaceHoverBrush", "AccentSoftBrush" })
                    {
                        AssertContrast(name, Find($"{role}TextBrush"), Find(background), 4.5);
                    }
                }
                foreach (string expander in new[] { "ExpanderHeaderBrush", "ExpanderSubHeaderBrush", "ExpanderDetailBrush" })
                {
                    AssertContrast(name, Find("TextPrimaryBrush"), Find(expander), 4.5);
                }
            });
        }

        private static double ContrastRatio(Brush first, Brush second)
        {
            static double Luminance(Color color)
            {
                static double Linear(byte component)
                {
                    double value = component / 255.0;
                    return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                }

                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }

            double a = Luminance(Assert.IsType<SolidColorBrush>(first).Color);
            double b = Luminance(Assert.IsType<SolidColorBrush>(second).Color);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        // Shows WINDOW in the high contrast theme with PALETTE standing in for a contrast theme's system colors.
        private static void UseHighContrastTheme(Window window, Color[] palette)
        {
            window.Resources.Remove("TextPrimaryBrush");
            window.Resources.Remove("TextSecondaryBrush");
            window.Resources.MergedDictionaries.Add(Theme("HighContrastTheme"));
            for (int index = 0; index < PaletteKeys.Length; index++)
            {
                window.Resources[PaletteKeys[index]] = palette[index];
            }
        }

        private static void AssertContrast(string theme, Brush foreground, Brush background, double minimum)
        {
            double contrast = ContrastRatio(foreground, background);
            Assert.True(contrast >= minimum,
                $"{theme}: {((SolidColorBrush)foreground).Color} on {((SolidColorBrush)background).Color} is {contrast:F2}:1");
        }

        private static ResourceDictionary Theme(string name) => new()
        {
            Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{name}.xaml")
        };

        private static Color[] Palette(params string[] colors) =>
            colors.Select(color => (Color)ColorConverter.ConvertFromString(color)).ToArray();
    }
}
