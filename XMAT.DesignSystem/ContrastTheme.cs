// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XMAT.DesignSystem
{
    // A Windows 11 contrast theme, to preview the high contrast theme in the design system without turning high
    // contrast on. XMAT's high contrast theme takes its colors from eight system colors; a preview puts this theme's
    // values for them into a window's resources. The other system colors, which only WPF's built-in templates use,
    // take the nearest of the eight.
    internal sealed class ContrastTheme
    {
        private static readonly ResourceKey[] ColorKeys =
        {
            SystemColors.WindowColorKey, SystemColors.WindowTextColorKey, SystemColors.HighlightColorKey,
            SystemColors.HighlightTextColorKey, SystemColors.GrayTextColorKey, SystemColors.HotTrackColorKey,
            SystemColors.ControlColorKey, SystemColors.ControlTextColorKey,
        };

        private static readonly ResourceKey[] BrushKeys =
        {
            SystemColors.WindowBrushKey, SystemColors.WindowTextBrushKey, SystemColors.HighlightBrushKey,
            SystemColors.HighlightTextBrushKey, SystemColors.GrayTextBrushKey, SystemColors.HotTrackBrushKey,
            SystemColors.ControlBrushKey, SystemColors.ControlTextBrushKey,
        };

        // The other system brushes, each with the index of the color in ColorKeys it takes.
        private static readonly (ResourceKey Key, int Color)[] NearestBrushes =
        {
            (SystemColors.InactiveSelectionHighlightBrushKey, 2),
            (SystemColors.InactiveSelectionHighlightTextBrushKey, 3),
            (SystemColors.MenuBrushKey, 0), (SystemColors.MenuTextBrushKey, 1), (SystemColors.MenuHighlightBrushKey, 2),
            (SystemColors.ControlLightBrushKey, 7), (SystemColors.ControlLightLightBrushKey, 7),
            (SystemColors.ControlDarkBrushKey, 7), (SystemColors.ControlDarkDarkBrushKey, 7),
            (SystemColors.ActiveBorderBrushKey, 1), (SystemColors.InactiveBorderBrushKey, 1),
            (SystemColors.InfoBrushKey, 0), (SystemColors.InfoTextBrushKey, 1), (SystemColors.ScrollBarBrushKey, 0),
        };

        private readonly Color[] _colors;

        // COLORS are the window, window text, highlight, highlight text, gray text, hot track (links), button face
        // and button text colors.
        private ContrastTheme(string name, params string[] colors)
        {
            Name = name;
            _colors = colors.Select(color => (Color)ColorConverter.ConvertFromString(color)).ToArray();
        }

        internal static IReadOnlyList<ContrastTheme> All { get; } = new[]
        {
            new ContrastTheme("Aquatic", "#202020", "#FFFFFF", "#8EE3F0", "#263B50", "#A6A6A6", "#75E9FC", "#202020", "#FFFFFF"),
            new ContrastTheme("Desert", "#FFFAEF", "#3D3D3D", "#903909", "#FFF5E3", "#676767", "#1C5E75", "#FFFAEF", "#202020"),
            new ContrastTheme("Dusk", "#2D3236", "#B6F6F0", "#A1BFDE", "#212D3B", "#A6A6A6", "#70EBDE", "#2D3236", "#B6F6F0"),
            new ContrastTheme("Night sky", "#000000", "#FFFFFF", "#D6B4FD", "#2B2B2B", "#A6A6A6", "#8080FF", "#000000", "#FFEE32"),
        };

        internal string Name { get; }

        internal IReadOnlyList<Color> Colors => _colors;

        // Gives RESOURCES this theme's system colors. ThemeManager.LoadTheme must have put the high contrast theme
        // there.
        internal void Apply(ResourceDictionary resources)
        {
            for (int index = 0; index < ColorKeys.Length; index++)
            {
                resources[ColorKeys[index]] = _colors[index];
                resources[BrushKeys[index]] = Frozen(_colors[index]);
            }

            foreach ((ResourceKey key, int color) in NearestBrushes)
            {
                resources[key] = Frozen(_colors[color]);
            }
        }

        // Takes a preview's system colors out of RESOURCES, so the theme loaded next sets them or leaves them to
        // Windows.
        internal static void Remove(ResourceDictionary resources)
        {
            foreach (ResourceKey key in ColorKeys.Concat(BrushKeys).Concat(NearestBrushes.Select(brush => brush.Key)))
            {
                resources.Remove(key);
            }
        }

        public override string ToString() => Name;

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
