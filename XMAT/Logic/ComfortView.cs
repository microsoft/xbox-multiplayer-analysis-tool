// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace XMAT
{
    // Comfort view (Edit > Appearance) gives the new look the spacing App.xaml defines. Turned off, the tighter values
    // in Looks/DenseSpacing.xaml replace it, for the data-dense view. The original look keeps its own spacing.
    internal static class ComfortView
    {
        private const string DenseSpacingUri =
            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/DenseSpacing.xaml";

        // The values a look's resources had for DenseSpacing's keys, put back when Comfort view is turned on again.
        private static readonly ConditionalWeakTable<ResourceDictionary, Dictionary<object, object>> ComfortValues =
            new();

        // Gives RESOURCES, the new look's, Comfort view's spacing when ON is true and the dense spacing otherwise.
        // Views take the spacing as dynamic resources, so open views follow at once. A dictionary's own resources take
        // precedence over its merged dictionaries, so DenseSpacing's values replace App.xaml's rather than merge in.
        internal static void Apply(ResourceDictionary resources, bool on)
        {
            var dense = new ResourceDictionary { Source = new Uri(DenseSpacingUri) };
            Dictionary<object, object> comfort = ComfortValues.GetValue(resources, _ => dense.Keys.Cast<object>()
                .Where(resources.Contains)
                .ToDictionary(key => key, key => resources[key]));
            foreach (object key in dense.Keys)
            {
                object value = on ? comfort.GetValueOrDefault(key) : dense[key];
                if (value == null)
                {
                    resources.Remove(key);
                }
                else if (!Equals(resources[key], value))
                {
                    resources[key] = value;
                }
            }
        }
    }
}
