// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XMAT
{
    internal static class ThemeManager
    {
        private const string LightThemeUri = "Themes/LightTheme.xaml";
        private const string DarkThemeUri = "Themes/DarkTheme.xaml";
        private const string OriginalLightThemeUri = "Themes/Original/LightTheme.xaml";
        private const string OriginalDarkThemeUri = "Themes/Original/DarkTheme.xaml";
        private const string HighContrastThemeUri = "Themes/HighContrastTheme.xaml";

        private static readonly ResourceKey[] SystemColorKeys =
        {
            SystemColors.ControlBrushKey, SystemColors.ControlTextBrushKey,
            SystemColors.WindowBrushKey, SystemColors.WindowTextBrushKey,
            SystemColors.HighlightBrushKey, SystemColors.HighlightTextBrushKey,
            SystemColors.InactiveSelectionHighlightBrushKey, SystemColors.InactiveSelectionHighlightTextBrushKey,
            SystemColors.GrayTextBrushKey,
            SystemColors.MenuBrushKey, SystemColors.MenuTextBrushKey, SystemColors.MenuHighlightBrushKey,
            SystemColors.ControlLightBrushKey, SystemColors.ControlLightLightBrushKey,
            SystemColors.ControlDarkBrushKey, SystemColors.ControlDarkDarkBrushKey,
            SystemColors.ActiveBorderBrushKey, SystemColors.InactiveBorderBrushKey,
            SystemColors.InfoBrushKey, SystemColors.InfoTextBrushKey,
            SystemColors.ScrollBarBrushKey,
        };

        public static string CurrentTheme { get; private set; } = "Light";
        public static event EventHandler ThemeChanged;

        // Applies THEME to the application's resources and announces it.
        public static void ApplyTheme(string theme)
        {
            var app = Application.Current;
            PrepareTheme(app.Resources, theme, LookState.Current.IsNewLook);
            RaiseThemeChanged();
        }

        // Puts THEME into RESOURCES, the new look's (App.xaml) or the original look's (Looks/OriginalLook.xaml), without
        // announcing it. A look's resources are prepared this way before they replace the application's resources, so
        // views never resolve a mix of two themes; raise ThemeChanged once they are shown.
        public static void PrepareTheme(ResourceDictionary resources, string theme, bool newLook) =>
            PrepareTheme(resources, theme, newLook, SystemParameters.HighContrast);

        // While Windows high contrast is on, both looks use the high contrast theme, whose colors are the system's.
        // THEME stays the saved choice and returns when high contrast is turned off.
        public static void PrepareTheme(ResourceDictionary resources, string theme, bool newLook, bool highContrast)
        {
            string uri = theme switch
            {
                "Light" => newLook ? LightThemeUri : OriginalLightThemeUri,
                "Dark" => newLook ? DarkThemeUri : OriginalDarkThemeUri,
                _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown appearance theme.")
            };
            if (highContrast)
            {
                uri = HighContrastThemeUri;
            }

            var newTheme = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/{uri}")
            };

            // Each look's resources declare a theme in XAML, so remove every theme dictionary rather than tracking
            // the one added last.
            foreach (var existing in resources.MergedDictionaries
                .Where(d => d.Source != null && d.Source.OriginalString.EndsWith("Theme.xaml", StringComparison.Ordinal))
                .ToList())
            {
                resources.MergedDictionaries.Remove(existing);
            }

            resources.MergedDictionaries.Add(newTheme);
            CurrentTheme = theme;

            ApplySystemColorOverrides(resources, newTheme);
        }

        public static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);

        // The high contrast theme defines no system colors, so it removes every override and Windows' colors apply.
        private static void ApplySystemColorOverrides(ResourceDictionary resources, ResourceDictionary theme)
        {
            foreach (var key in SystemColorKeys)
            {
                if (theme[key] is SolidColorBrush brush)
                {
                    resources[key] = brush;
                }
                else
                {
                    resources.Remove(key);
                }
            }
        }
    }
}
