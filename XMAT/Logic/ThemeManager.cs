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

        private const string NewLookUri =
            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/NewLook.xaml";
        private const string OriginalLookUri =
            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/OriginalLook.xaml";

        // Applies THEME to the application's resources and announces it.
        public static void ApplyTheme(string theme)
        {
            var app = Application.Current;
            PrepareTheme(app.Resources, theme, LookState.Current.IsNewLook);
            RaiseThemeChanged();
        }

        // A look's resources for Application.Resources or a window's Resources: a dictionary that merges the look's
        // file (Looks/NewLook.xaml or Looks/OriginalLook.xaml), ready for a theme. A dictionary loaded from a file
        // doesn't tell the views that use it when its merged dictionaries change, so a theme put into it later wouldn't
        // show; a theme put into this one does. App.xaml declares the new look's the same way.
        internal static ResourceDictionary CreateLook(bool newLook) => new()
        {
            MergedDictionaries = { new ResourceDictionary { Source = new Uri(newLook ? NewLookUri : OriginalLookUri) } }
        };

        // Puts THEME into RESOURCES, a look's resources from CreateLook or App.xaml, without announcing it. A look's
        // resources are prepared this way before they replace the application's resources, so views never resolve a mix
        // of two themes; raise ThemeChanged once they are shown.
        public static void PrepareTheme(ResourceDictionary resources, string theme, bool newLook) =>
            PrepareTheme(resources, theme, newLook, SystemParameters.HighContrast);

        // While Windows high contrast is on, both looks use the high contrast theme, whose colors are the system's.
        // THEME stays the saved choice and returns when high contrast is turned off.
        public static void PrepareTheme(ResourceDictionary resources, string theme, bool newLook, bool highContrast)
        {
            LoadTheme(resources, theme, newLook, highContrast);
            CurrentTheme = theme;
        }

        // Puts THEME into RESOURCES like PrepareTheme, without making it the application's theme: the design system app
        // shows any theme in its window's resources, and its tests share the application with XMAT's.
        internal static void LoadTheme(ResourceDictionary resources, string theme, bool newLook, bool highContrast)
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

            // Each look's file declares a theme of its own, so remove every theme dictionary, in RESOURCES and in the
            // dictionaries it merges, rather than tracking the one added last: a theme left behind would still supply
            // system colors that high contrast must leave to Windows.
            RemoveThemes(resources);
            foreach (var merged in resources.MergedDictionaries)
            {
                RemoveThemes(merged);
            }

            resources.MergedDictionaries.Add(newTheme);
            ApplySystemColorOverrides(resources, newTheme);
        }

        private static void RemoveThemes(ResourceDictionary resources)
        {
            foreach (var existing in resources.MergedDictionaries
                .Where(d => d.Source != null && d.Source.OriginalString.EndsWith("Theme.xaml", StringComparison.Ordinal))
                .ToList())
            {
                resources.MergedDictionaries.Remove(existing);
            }
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
