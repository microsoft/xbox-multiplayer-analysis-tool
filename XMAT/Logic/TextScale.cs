// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;

namespace XMAT
{
    // Follows the Windows Text size setting (Settings > Accessibility > Text size), which WPF doesn't apply by itself:
    // a look's font size tokens are multiplied by the setting's factor, starting from the sizes the look defines.
    internal static class TextScale
    {
        // The font size tokens that the setting scales. A look defines the ones its views use.
        internal static readonly string[] FontSizeKeys =
        {
            "MetadataFontSize", "BaseFontSize", "TimelineAxisFontSize", "PaneTitleFontSize", "SectionTitleFontSize",
            "AnalysisTitleFontSize", "DialogNoteFontSize",
            "DialogTextFontSize", "DialogTitleFontSize", "EmptyStateFontSize",
            "TrafficOverviewLabelFontSize", "TrafficOverviewTotalFontSize",
        };

        private static readonly ConditionalWeakTable<ResourceDictionary, Dictionary<string, double>> DefinedSizes = new();

        // The Text size setting as a factor, from 1 (100%) to 2.25 (225%).
        internal static double ReadFactor()
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent && percent >= 100 && percent <= 225
                ? percent / 100.0
                : 1.0;
        }

        // Sets RESOURCES' font size tokens to their defined sizes times FACTOR.
        internal static void Apply(ResourceDictionary resources, double factor)
        {
            Dictionary<string, double> defined = DefinedSizes.GetValue(resources, Defined);
            foreach ((string key, double size) in defined)
            {
                resources[key] = size * factor;
            }
        }

        private static Dictionary<string, double> Defined(ResourceDictionary resources)
        {
            var defined = new Dictionary<string, double>();
            foreach (string key in FontSizeKeys)
            {
                if (resources.Contains(key) && resources[key] is double size)
                {
                    defined[key] = size;
                }
            }

            return defined;
        }
    }
}
