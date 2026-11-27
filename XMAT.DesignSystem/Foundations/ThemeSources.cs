// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace XMAT.DesignSystem
{
    // A step of the palette, such as Blue8, and its color.
    public sealed record PaletteStep(string Key, Color Color);

    // A ramp of the palette, lightest first, with the base color it was generated from, if it has one.
    public sealed record PaletteRamp(string Name, IReadOnlyList<PaletteStep> Steps, string BaseKey, string BaseHex);

    // XMAT's theme files as written, for the foundation pages: the palette that Palette.json generates, and the value
    // each theme gives a color, such as "Gray3" or, in the High Contrast theme, the Windows contrast theme color it takes.
    // The files are embedded in the design system app (XMAT.DesignSystem.csproj), so the pages read the files XMAT is
    // built from; brushes come from the compiled themes.
    public static class ThemeSources
    {
        public const string Light = "LightTheme";
        public const string Dark = "DarkTheme";
        public const string HighContrast = "HighContrastTheme";

        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // The Windows system colors the High Contrast theme uses, by the names Settings gives them in a contrast theme.
        private static readonly Dictionary<string, string> ContrastNames = new(StringComparer.Ordinal)
        {
            ["WindowColorKey"] = "Background",
            ["WindowTextColorKey"] = "Text",
            ["HighlightColorKey"] = "Selected text background",
            ["HighlightTextColorKey"] = "Selected text",
            ["GrayTextColorKey"] = "Inactive text",
            ["HotTrackColorKey"] = "Hyperlink",
            ["ControlColorKey"] = "Button background",
            ["ControlTextColorKey"] = "Button text",
        };

        private static readonly Lazy<IReadOnlyList<PaletteRamp>> LazyRamps = new(LoadRamps);
        private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Values = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ResourceDictionary> Compiled = new(StringComparer.Ordinal);

        // Gray, then the colors, in the order Palette.json lists them.
        public static IReadOnlyList<PaletteRamp> Ramps => LazyRamps.Value;

        // The value THEME gives each of its colors, as written: a palette step such as "Gray3", "Gray5 at 30%", "Clear",
        // "White at 22%", the steps of a gradient, or a contrast theme color such as "Background". Windows system colors
        // that a theme sets for WPF's own controls aren't included.
        public static IReadOnlyDictionary<string, string> ColorsOf(string theme)
        {
            if (!Values.TryGetValue(theme, out IReadOnlyDictionary<string, string> values))
            {
                values = Document($"{theme}.xaml").Root.Elements()
                    .Where(element => element.Attribute(Xaml + "Key") is { } key && !key.Value.StartsWith('{'))
                    .ToDictionary(element => element.Attribute(Xaml + "Key").Value, Describe, StringComparer.Ordinal);
                Values[theme] = values;
            }

            return values;
        }

        // THEME's brush for KEY, from the compiled theme, or null when the theme has none.
        public static Brush BrushOf(string theme, string key)
        {
            if (!Compiled.TryGetValue(theme, out ResourceDictionary dictionary))
            {
                dictionary = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{theme}.xaml"),
                };
                Compiled[theme] = dictionary;
            }

            return dictionary.Contains(key) ? dictionary[key] as Brush : null;
        }

        // The contrast of two solid colors of THEME, such as text on a surface.
        public static double Contrast(string theme, string foreground, string background) =>
            SelectionForegroundConverter.Contrast(SolidColor(theme, foreground), SolidColor(theme, background));

        private static Color SolidColor(string theme, string key) =>
            BrushOf(theme, key) is SolidColorBrush brush
                ? brush.Color
                : throw new InvalidOperationException($"{theme} has no solid color {key}.");

        private static string Describe(XElement brush)
        {
            if (brush.Name.LocalName == "LinearGradientBrush")
            {
                List<string> stops = brush.Descendants()
                    .Where(element => element.Name.LocalName == "GradientStop")
                    .Select(stop => ColorName(stop.Attribute("Color")?.Value))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return stops.Count == 1 ? stops[0] : $"{stops[0]} to {stops[^1]}";
            }

            string color = ColorName(brush.Attribute("Color")?.Value);
            return brush.Attribute("Opacity")?.Value is string opacity
                ? $"{color} at {Percent(double.Parse(opacity, CultureInfo.InvariantCulture))}"
                : color;
        }

        // A color as a theme writes it, by name: a palette step, a contrast theme color, Clear, or white or black at an
        // opacity.
        private static string ColorName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "?";
            }

            Match step = Regex.Match(value, @"^\{StaticResource (\w+)\}$");
            if (step.Success)
            {
                return step.Groups[1].Value;
            }

            Match system = Regex.Match(value, @"SystemColors\.(\w+)\}");
            if (system.Success)
            {
                return ContrastNames.TryGetValue(system.Groups[1].Value, out string name) ? name : system.Groups[1].Value;
            }

            if (value == "Transparent")
            {
                return "Clear";
            }

            Match overlay = Regex.Match(value, "^#([0-9A-F]{2})(000000|FFFFFF)$", RegexOptions.IgnoreCase);
            if (overlay.Success)
            {
                double alpha = int.Parse(overlay.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                string shade = overlay.Groups[2].Value == "000000" ? "Black" : "White";
                return alpha == 0 ? "Clear" : $"{shade} at {Percent(alpha)}";
            }

            return value;
        }

        private static string Percent(double fraction) => $"{Math.Round(fraction * 100)}%";

        private static IReadOnlyList<PaletteRamp> LoadRamps()
        {
            List<PaletteStep> steps = Document("Palette.xaml").Root.Elements()
                .Where(element => element.Name.LocalName == "Color")
                .Select(element => new PaletteStep(
                    element.Attribute(Xaml + "Key").Value, (Color)ColorConverter.ConvertFromString(element.Value.Trim())))
                .ToList();
            using JsonDocument json = JsonDocument.Parse(Source("Palette.json"));
            Dictionary<string, (string Key, string Hex)> bases = json.RootElement.GetProperty("colors").EnumerateArray()
                .ToDictionary(
                    color => color.GetProperty("name").GetString(),
                    color => (
                        color.GetProperty("name").GetString() + color.GetProperty("baseStep").GetInt32().ToString(CultureInfo.InvariantCulture),
                        color.GetProperty("base").GetString()),
                    StringComparer.Ordinal);

            return steps
                .GroupBy(step => Regex.Match(step.Key, @"^[A-Za-z]+").Value)
                .Select(ramp => bases.TryGetValue(ramp.Key, out var baseColor)
                    ? new PaletteRamp(ramp.Key, ramp.ToList(), baseColor.Key, baseColor.Hex)
                    : new PaletteRamp(ramp.Key, ramp.ToList(), null, null))
                .ToList();
        }

        private static XDocument Document(string file) => XDocument.Parse(Source(file));

        private static string Source(string file)
        {
            using Stream stream = typeof(ThemeSources).Assembly.GetManifestResourceStream($"XMAT.DesignSystem.Themes.{file}")
                ?? throw new InvalidOperationException($"The design system app doesn't embed XMAT/Themes/{file}.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
