// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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

        private static ResourceDictionary _currentThemeDictionary;

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

        public static void ApplyTheme(string theme)
        {
            string uri = theme == "Dark" ? DarkThemeUri : LightThemeUri;

            var newTheme = new ResourceDictionary
            {
                Source = new Uri(uri, UriKind.Relative)
            };

            var app = Application.Current;

            if (_currentThemeDictionary != null)
            {
                app.Resources.MergedDictionaries.Remove(_currentThemeDictionary);
            }
            else
            {
                // First call: remove the XAML-declared theme dictionary
                var existing = app.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null &&
                        (d.Source.OriginalString.Contains("LightTheme") ||
                         d.Source.OriginalString.Contains("DarkTheme")));
                if (existing != null)
                {
                    app.Resources.MergedDictionaries.Remove(existing);
                }
            }

            app.Resources.MergedDictionaries.Add(newTheme);
            _currentThemeDictionary = newTheme;
            CurrentTheme = theme;

            ApplySystemColorOverrides(theme == "Dark");
        }

        private static void ApplySystemColorOverrides(bool dark)
        {
            var res = Application.Current.Resources;

            if (dark)
            {
                res[SystemColors.ControlBrushKey] = Frozen(0x2D, 0x2D, 0x30);
                res[SystemColors.ControlTextBrushKey] = Frozen(0xD4, 0xD4, 0xD4);
                res[SystemColors.WindowBrushKey] = Frozen(0x1E, 0x1E, 0x1E);
                res[SystemColors.WindowTextBrushKey] = Frozen(0xD4, 0xD4, 0xD4);
                res[SystemColors.HighlightBrushKey] = Frozen(0x26, 0x4F, 0x78);
                res[SystemColors.HighlightTextBrushKey] = Frozen(0xFF, 0xFF, 0xFF);
                res[SystemColors.InactiveSelectionHighlightBrushKey] = Frozen(0x3E, 0x3E, 0x42);
                res[SystemColors.InactiveSelectionHighlightTextBrushKey] = Frozen(0xD4, 0xD4, 0xD4);
                res[SystemColors.GrayTextBrushKey] = Frozen(0x65, 0x65, 0x65);
                res[SystemColors.MenuBrushKey] = Frozen(0x2D, 0x2D, 0x30);
                res[SystemColors.MenuTextBrushKey] = Frozen(0xD4, 0xD4, 0xD4);
                res[SystemColors.MenuHighlightBrushKey] = Frozen(0x3E, 0x3E, 0x42);
                res[SystemColors.ControlLightBrushKey] = Frozen(0x33, 0x33, 0x37);
                res[SystemColors.ControlLightLightBrushKey] = Frozen(0x25, 0x25, 0x26);
                res[SystemColors.ControlDarkBrushKey] = Frozen(0x3F, 0x3F, 0x46);
                res[SystemColors.ControlDarkDarkBrushKey] = Frozen(0x1E, 0x1E, 0x1E);
                res[SystemColors.ActiveBorderBrushKey] = Frozen(0x3F, 0x3F, 0x46);
                res[SystemColors.InactiveBorderBrushKey] = Frozen(0x3F, 0x3F, 0x46);
                res[SystemColors.InfoBrushKey] = Frozen(0x2D, 0x2D, 0x30);
                res[SystemColors.InfoTextBrushKey] = Frozen(0xD4, 0xD4, 0xD4);
                res[SystemColors.ScrollBarBrushKey] = Frozen(0x1E, 0x1E, 0x1E);
            }
            else
            {
                foreach (var key in SystemColorKeys)
                {
                    res.Remove(key);
                }
            }
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
