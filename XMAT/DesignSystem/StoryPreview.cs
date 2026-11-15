// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;

namespace XMAT.DesignSystem
{
    // The resources design system pages are shown with: a copy of the new look with the design system's own styles,
    // set to a theme or a Windows contrast theme, with Comfort view on or off. The window shows its pages this way,
    // and the reference image tests render them this way.
    internal static class StoryPreview
    {
        private const string StylesUri =
            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/DesignSystem/DesignSystem.xaml";

        internal static ResourceDictionary CreateLook()
        {
            ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
            look.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(StylesUri) });
            return look;
        }

        // Sets LOOK to THEME, or to CONTRAST (a Windows contrast theme) when it isn't null, with Comfort view on or
        // off. While WINDOWS HIGH CONTRAST is on, Windows sets the colors and CONTRAST is ignored. TEXT SCALE is the
        // Windows Text size setting's factor.
        internal static void Apply(
            ResourceDictionary look,
            string theme,
            ContrastTheme contrast,
            bool comfortView,
            bool windowsHighContrast,
            double textScale)
        {
            // The theme first: it sets or clears the system colors that a contrast theme then replaces.
            ContrastTheme.Remove(look);
            ThemeManager.LoadTheme(look, theme, newLook: true, highContrast: windowsHighContrast || contrast != null);
            if (!windowsHighContrast)
            {
                contrast?.Apply(look);
            }

            ComfortView.Apply(look, comfortView);
            TextScale.Apply(look, textScale);
        }
    }
}
