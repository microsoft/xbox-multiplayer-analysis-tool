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

    // A color on a surface in one theme, and their contrast, such as "12.6:1": text in the color, or, for an outline, the
    // theme's text framed by an outline in the color.
    public sealed record ContrastSample(string Name, Brush Surface, Brush Edge, Brush Text, Brush Outline, string Ratio);

    // A cell of a contrast grid: a color on a surface in Light and in Dark.
    public sealed record ContrastCell(string Foreground, string Surface, ContrastSample Light, ContrastSample Dark);

    // The Color page: the palette, the roles, the colors that must stand out from each other and the component colors,
    // all read from the theme files and ColorCatalog.
    public partial class ColorStory : Story
    {
        private readonly List<ContrastCell> _contrastCells = new();

        public ColorStory()
        {
            InitializeComponent();
            ShowPalette();
            foreach (ColorRoleGroup group in ColorCatalog.RoleGroups)
            {
                RolesSection.Items.Add(GroupExample(group, "role", "roles"));
            }

            foreach (ContrastGroup group in ColorCatalog.ContrastGroups)
            {
                ContrastSection.Items.Add(ContrastExample(group));
            }

            foreach (ColorRoleGroup group in ColorCatalog.ComponentColorGroups)
            {
                ComponentSection.Items.Add(GroupExample(group, "color", "colors"));
            }
        }

        // The rows of the Roles and Component colors sections, for the tests.
        internal IEnumerable<ColorRowView> ColorRows =>
            RolesSection.Items.Cast<StoryExample>().Concat(ComponentSection.Items.Cast<StoryExample>())
                .SelectMany(example => ((ItemsControl)((StackPanel)example.Content).Children[1]).Items.Cast<ColorRowView>());

        // The cells of the contrast grids, for the tests.
        internal IReadOnlyList<ContrastCell> ContrastCells => _contrastCells;

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
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return new StoryExample
            {
                Caption = $"{group.Roles.Count} {(group.Roles.Count == 1 ? singular : plural)}",
                Content = new StackPanel { Width = 360, Children = { title, rows } },
            };
        }

        // A grid of GROUP's pairs: the colors down the side and the surfaces across the top, each cell in Light, then
        // Dark.
        private StoryExample ContrastExample(ContrastGroup group)
        {
            List<string> foregrounds = group.Pairs.Select(pair => pair.Foreground).Distinct().ToList();
            List<string> columns = group.Pairs.Select(ColumnOf).Distinct().ToList();
            var grid = new Grid();
            AutomationProperties.SetName(grid, group.Name);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int column = 0; column < columns.Count; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(Label(columns[column], 0, column + 1, secondary: true));
            }

            for (int row = 0; row < foregrounds.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Children.Add(Label(foregrounds[row].Replace("Brush", string.Empty, StringComparison.Ordinal), row + 1, 0, secondary: false));
                foreach (ContrastPair pair in group.Pairs.Where(pair => pair.Foreground == foregrounds[row]))
                {
                    var cell = new ContrastCell(
                        pair.Foreground,
                        pair.Surface,
                        Sample(ThemeSources.Light, pair, group.Outlines),
                        Sample(ThemeSources.Dark, pair, group.Outlines));
                    _contrastCells.Add(cell);
                    var presenter = new ContentPresenter { Content = cell, ContentTemplate = (DataTemplate)Resources["ContrastCellTemplate"] };
                    Grid.SetRow(presenter, row + 1);
                    Grid.SetColumn(presenter, columns.IndexOf(ColumnOf(pair)) + 1);
                    grid.Children.Add(presenter);
                }
            }

            return new StoryExample
            {
                Caption = string.Format(CultureInfo.CurrentCulture, "{0}: {1}:1 or more", group.Name, group.Needs),
                Content = grid,
            };
        }

        // A surface's column: its name without "Surface" and "Brush", or, for status text on its own soft background,
        // "Soft background".
        private static string ColumnOf(ContrastPair pair)
        {
            string surface = pair.Surface.Replace("Brush", string.Empty, StringComparison.Ordinal);
            string text = pair.Foreground.Replace("TextBrush", string.Empty, StringComparison.Ordinal);
            return surface == $"{text}Soft" ? "Soft background"
                : surface.StartsWith("Surface", StringComparison.Ordinal) ? surface["Surface".Length..]
                : surface;
        }

        private static TextBlock Label(string text, int row, int column, bool secondary)
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 0, 12, secondary ? 6 : 8),
                VerticalAlignment = secondary ? VerticalAlignment.Bottom : VerticalAlignment.Center,
            };
            if (secondary)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }

            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            return label;
        }

        private static ThemeSwatch Swatch(string theme, string key) => new(
            $"{(theme == ThemeSources.Light ? "Light" : "Dark")}: {ValueOf(theme, key)}",
            ThemeSources.BrushOf(theme, key),
            ThemeSources.BrushOf(theme, "SurfacePanelBrush"),
            ThemeSources.BrushOf(theme, "BorderSubtleBrush"));

        // PAIR in THEME: its color as text on the surface, or, for an OUTLINE, as a frame around the theme's text.
        private static ContrastSample Sample(string theme, ContrastPair pair, bool outline)
        {
            Brush color = ThemeSources.BrushOf(theme, pair.Foreground);
            string ratio = Ratio(ThemeSources.Contrast(theme, pair.Foreground, pair.Surface));
            return new ContrastSample(
                $"{(theme == ThemeSources.Light ? "Light" : "Dark")}: {pair.Foreground} on {pair.Surface}, {ratio}",
                ThemeSources.BrushOf(theme, pair.Surface),
                ThemeSources.BrushOf(theme, "BorderSubtleBrush"),
                outline ? ThemeSources.BrushOf(theme, "TextPrimaryBrush") : color,
                outline ? color : Brushes.Transparent,
                ratio);
        }

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
