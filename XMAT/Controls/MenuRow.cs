// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;

namespace XMAT
{
    // What a menu row shows besides its label in the new look, where the new look's MenuItem template draws them: a Segoe
    // MDL2 Assets glyph in the icon lane, and a description on a second line, which assistive technology reads as the
    // row's help text.
    public static class MenuRow
    {
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
            "Glyph", typeof(string), typeof(MenuRow), new PropertyMetadata(null));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached(
            "Description", typeof(string), typeof(MenuRow), new PropertyMetadata(null, OnDescriptionChanged));

        public static string GetGlyph(DependencyObject item) => (string)item.GetValue(GlyphProperty);

        public static void SetGlyph(DependencyObject item, string value) => item.SetValue(GlyphProperty, value);

        public static string GetDescription(DependencyObject item) => (string)item.GetValue(DescriptionProperty);

        public static void SetDescription(DependencyObject item, string value) => item.SetValue(DescriptionProperty, value);

        private static void OnDescriptionChanged(DependencyObject item, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is string description)
            {
                AutomationProperties.SetHelpText(item, description);
            }
            else
            {
                item.ClearValue(AutomationProperties.HelpTextProperty);
            }
        }
    }

    // Segoe MDL2 Assets glyphs for menu rows.
    internal static class MenuGlyphs
    {
        internal const string Block = "\uF140";
        internal const string Bypass = "\uE7A6";
        internal const string Settings = "\uE713";
        internal const string Filter = "\uE71C";
        internal const string Hide = "\uED1A";
        internal const string ShowAll = "\uE7B3";
        internal const string Export = "\uE74E";
        internal const string Delete = "\uE74D";
        internal const string Sort = "\uE8CB";
    }
}
