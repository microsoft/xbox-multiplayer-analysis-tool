// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows.Media;
using XMAT.DesignSystem;

namespace XMAT.Tests.DesignSystem
{
    // The Color page shows what the theme files say, so it can't fall behind them.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ColorStoryTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public ColorStoryTests(CaptureUiApplicationFixture application) => _application = application;

        // Gray, then a ramp for each base color in Palette.json, which is itself one of its ramp's steps.
        [Fact]
        public void Palette_HasAGrayRampAndARampForEachBaseColor()
        {
            _application.Run(_ =>
            {
                IReadOnlyList<PaletteRamp> ramps = ThemeSources.Ramps;

                Assert.Equal(new[] { "Gray", "Blue", "Green", "Amber", "Red" }, ramps.Select(ramp => ramp.Name));
                Assert.Equal(16, ramps[0].Steps.Count);
                Assert.Null(ramps[0].BaseKey);
                Assert.Equal(Colors.White, ramps[0].Steps[0].Color);
                PaletteRamp blue = ramps[1];
                Assert.Equal("Blue8", blue.BaseKey);
                Assert.Equal("#0078D4", blue.BaseHex);
                Assert.Equal(blue.BaseHex, blue.Steps.Single(step => step.Key == blue.BaseKey).Color.ToString()[3..].Insert(0, "#"));
            });
        }

        // Every role and component color, in the catalog's order, with the step, opacity, gradient or contrast theme
        // color each theme gives it; and a contrast grid cell for every pair, in Light and Dark.
        [Fact]
        public void ColorPage_ShowsEveryColorWithEachThemesValue_AndTheContrastOfEveryPair()
        {
            _application.Run(window =>
            {
                CultureInfo culture = CultureInfo.CurrentCulture;
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                try
                {
                    window.Resources = StoryPreview.CreateLook();
                    StoryPreview.Apply(window.Resources, "Light", null, comfortView: true, windowsHighContrast: false, textScale: 1);
                    var story = new ColorStory();
                    WpfTestHost.Show(window, story);

                    List<ColorRowView> rows = story.ColorRows.ToList();
                    Assert.Equal(
                        ColorCatalog.Roles.Concat(ColorCatalog.ComponentColors).Select(color => color.Key),
                        rows.Select(row => row.Key));
                    Assert.Equal("Light Gray3 · Dark Gray15 · High contrast Background", Values(rows, "SurfaceCanvasBrush"));
                    Assert.Equal("Light Gray5 at 30% · Dark Gray12 · High contrast Text", Values(rows, "ColumnDividerBrush"));
                    Assert.Equal("Light Gray13 · Dark Gray4 · High contrast Button text", Values(rows, "ButtonTextBrush"));
                    Assert.Equal(
                        "Light Green8 to Green10 · Dark Green8 to Green10 · High contrast Selected text background",
                        Values(rows, "CaptureActionBackgroundBrush"));
                    Assert.Equal(
                        "Light Clear to Black at 4% · Dark Clear to Black at 25% · High contrast Clear",
                        Values(rows, "TabItemInsetShadowBrush"));
                    Assert.Equal(
                        "Light White at 22% · Dark White at 22% · High contrast Clear", Values(rows, "CaptureActionSheenBrush"));
                    ColorRowView panel = rows.Single(row => row.Key == "SurfacePanelBrush");
                    Assert.Equal(Colors.White, ((SolidColorBrush)panel.Light.Fill).Color);
                    Assert.Equal(PaletteColor("Gray13"), ((SolidColorBrush)panel.Dark.Fill).Color);

                    Assert.Equal(
                        ColorCatalog.ContrastGroups.SelectMany(group => group.Pairs).Select(pair => (pair.Foreground, pair.Surface)),
                        story.ContrastCells.Select(cell => (cell.Foreground, cell.Surface)));
                    ContrastCell body = story.ContrastCells.Single(cell => cell.Foreground == "TextPrimaryBrush" && cell.Surface == "SurfacePanelBrush");
                    Assert.Equal(
                        ColorStory.Ratio(SelectionForegroundConverter.Contrast(PaletteColor("Gray13"), PaletteColor("Gray1"))),
                        body.Light.Ratio);
                    Assert.Equal(
                        ColorStory.Ratio(SelectionForegroundConverter.Contrast(PaletteColor("Gray4"), PaletteColor("Gray13"))),
                        body.Dark.Ratio);
                    Assert.Same(Brushes.Transparent, body.Light.Outline);
                    // An outline frames the theme's text in the outline's color.
                    ContrastCell outline = story.ContrastCells.Single(cell => cell.Foreground == "BorderStrongBrush" && cell.Surface == "SurfacePanelBrush");
                    Assert.Equal(PaletteColor("Gray8"), ((SolidColorBrush)outline.Light.Outline).Color);
                    Assert.Equal(PaletteColor("Gray13"), ((SolidColorBrush)outline.Light.Text).Color);
                }
                finally
                {
                    CultureInfo.CurrentCulture = culture;
                }
            });
        }

        // A ratio never reads higher than it is, so one just under 4.5:1 doesn't look like a pass.
        [Theory]
        [InlineData(4.4999, "4.4:1")]
        [InlineData(4.5, "4.5:1")]
        [InlineData(12.96, "12.9:1")]
        public void Ratio_RoundsDown(double contrast, string shown)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                Assert.Equal(shown, ColorStory.Ratio(contrast));
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        private static string Values(IEnumerable<ColorRowView> rows, string key) => rows.Single(row => row.Key == key).Values;

        private static Color PaletteColor(string key) =>
            ThemeSources.Ramps.SelectMany(ramp => ramp.Steps).Single(step => step.Key == key).Color;
    }
}
