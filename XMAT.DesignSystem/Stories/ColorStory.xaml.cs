// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace XMAT.DesignSystem
{
    // A step of a palette ramp as the Color page shows it.
    public sealed record PaletteStepView(string Number, string Hex, Brush Fill, bool IsBase);

    // A color in one theme, on that theme's panel color, and what the theme makes it of.
    public sealed record ThemeSwatch(string Name, Brush Fill, Brush Panel, Brush Edge);

    // A role or component color: what it is for and its value in each theme.
    public sealed record ColorRowView(string Key, string Use, string Values, ThemeSwatch Light, ThemeSwatch Dark);

    // Text on a surface in one theme, and their contrast, such as "12.6:1".
    public sealed record ContrastSample(Brush Text, Brush Surface, Brush Edge, string Ratio);

    public sealed record ReadablePairView(string Pair, ContrastSample Light, ContrastSample Dark);

    // The Color page: the palette, the roles, the text that must stay readable and the component colors, all read from
    // the theme files and ColorCatalog.
    public partial class ColorStory : Story
    {
        public ColorStory()
        {
            InitializeComponent();
            ShowPalette();
            foreach (ColorRoleGroup group in ColorCatalog.RoleGroups)
            {
                RolesSection.Items.Add(GroupExample(group, "role", "roles"));
            }

            ShowReadablePairs();
            foreach (ColorRoleGroup group in ColorCatalog.ComponentColorGroups)
            {
                ComponentSection.Items.Add(GroupExample(group, "color", "colors"));
            }
        }

        // The rows of the Roles and Component colors sections, for the tests.
        internal IEnumerable<ColorRowView> ColorRows =>
            RolesSection.Items.Cast<StoryExample>().Concat(ComponentSection.Items.Cast<StoryExample>())
                .SelectMany(example => ((ItemsControl)((StackPanel)example.Content).Children[1]).Items.Cast<ColorRowView>());

        internal IEnumerable<ReadablePairView> ReadablePairs =>
            ((ItemsControl)((StackPanel)ReadableSection.Items.Cast<StoryExample>().Single().Content).Children[1])
                .Items.Cast<ReadablePairView>();

        // The value each theme gives KEY: "Light Gray3 · Dark Gray14 · High contrast Background".
        internal static string ValuesOf(string key) =>
            $"Light {ValueOf(ThemeSources.Light, key)} · Dark {ValueOf(ThemeSources.Dark, key)} · " +
            $"High contrast {ValueOf(ThemeSources.HighContrast, key)}";

        // CONTRAST as the page shows it, rounded down so that it never reads higher than it is.
        internal static string Ratio(double contrast) =>
            string.Format(CultureInfo.CurrentCulture, "{0:0.0}:1", Math.Floor(contrast * 10) / 10);

        private static string ValueOf(string theme, string key) =>
            ThemeSources.ColorsOf(theme).TryGetValue(key, out string value) ? value : "none";

        private void ShowPalette()
        {
            IReadOnlyList<PaletteRamp> ramps = ThemeSources.Ramps;
            PaletteSection.StyleName = string.Join(", ", ramps.Select(ramp => $"{ramp.Steps[0].Key} to {ramp.Steps[^1].Key}"));
            foreach (PaletteRamp ramp in ramps)
            {
                var steps = new ItemsControl
                {
                    Focusable = false,
                    ItemsPanel = (ItemsPanelTemplate)Resources["WrapItemsPanel"],
                    ItemTemplate = (DataTemplate)Resources["PaletteStepTemplate"],
                    ItemsSource = ramp.Steps
                        .Select(step => new PaletteStepView(Number(step.Key), Hex(step.Color), Fill(step.Color), step.Key == ramp.BaseKey))
                        .ToList(),
                };
                AutomationProperties.SetName(steps, $"{ramp.Name} ramp");
                PaletteSection.Items.Add(new StoryExample
                {
                    Caption = ramp.BaseKey == null
                        ? $"{ramp.Name}: {ramp.Steps.Count} steps"
                        : $"{ramp.Name}: {ramp.Steps.Count} steps from {ramp.BaseHex}, which is step {Number(ramp.BaseKey)}",
                    Content = steps,
                });
            }
        }

        // A group of roles or component colors, two to a row: its name, then a row per color.
        private StoryExample GroupExample(ColorRoleGroup group, string singular, string plural)
        {
            var rows = new ItemsControl
            {
                Focusable = false,
                ItemTemplate = (DataTemplate)Resources["ColorRowTemplate"],
                ItemsSource = group.Roles.Select(role => new ColorRowView(
                    role.Key, role.Use, ValuesOf(role.Key), Swatch(ThemeSources.Light, role.Key), Swatch(ThemeSources.Dark, role.Key)))
                    .ToList(),
            };
            AutomationProperties.SetName(rows, group.Name);
            var title = new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextHeadingBrush");
            return new StoryExample
            {
                Caption = $"{group.Roles.Count} {(group.Roles.Count == 1 ? singular : plural)}",
                Content = new StackPanel { Width = 360, Children = { title, rows } },
            };
        }

        private void ShowReadablePairs()
        {
            var pairs = new ItemsControl
            {
                Focusable = false,
                ItemTemplate = (DataTemplate)Resources["ReadablePairTemplate"],
                ItemsSource = ColorCatalog.ReadablePairs
                    .Select(pair => new ReadablePairView(
                        $"{pair.Text} on {pair.Surface}",
                        Sample(ThemeSources.Light, pair.Text, pair.Surface),
                        Sample(ThemeSources.Dark, pair.Text, pair.Surface)))
                    .ToList(),
            };
            AutomationProperties.SetName(pairs, ReadableSection.Title);
            var table = new StackPanel
            {
                MaxWidth = 720,
                Children = { new ContentPresenter { ContentTemplate = (DataTemplate)Resources["ReadablePairHeaderTemplate"] }, pairs },
            };
            Grid.SetIsSharedSizeScope(table, true);
            ReadableSection.Items.Add(new StoryExample
            {
                Caption = $"{ColorCatalog.ReadablePairs.Count} pairs of text and surface",
                Content = table,
            });
        }

        private static ThemeSwatch Swatch(string theme, string key) => new(
            $"{(theme == ThemeSources.Light ? "Light" : "Dark")}: {ValueOf(theme, key)}",
            ThemeSources.BrushOf(theme, key),
            ThemeSources.BrushOf(theme, "SurfacePanelBrush"),
            ThemeSources.BrushOf(theme, "BorderDefaultBrush"));

        private static ContrastSample Sample(string theme, string text, string surface) => new(
            ThemeSources.BrushOf(theme, text),
            ThemeSources.BrushOf(theme, surface),
            ThemeSources.BrushOf(theme, "BorderDefaultBrush"),
            Ratio(ThemeSources.Contrast(theme, text, surface)));

        private static string Number(string key) => key.TrimStart(key.TakeWhile(char.IsLetter).ToArray());

        private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Brush Fill(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
