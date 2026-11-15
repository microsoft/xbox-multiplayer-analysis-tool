// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XMAT
{
    // The text color of a selected table row: the row's own color, such as a pending or error status color, so a
    // selected row keeps that meaning. The color shifts lighter or darker only as far as it must to stay readable on
    // the selection fill. High Contrast uses the system's selected text color.
    public sealed class SelectionForegroundConverter : IMultiValueConverter
    {
        internal const double MinimumContrast = 4.5;

        // values: the row's text brush, then the selection fill.
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (SystemParameters.HighContrast)
            {
                return SystemColors.HighlightTextBrush;
            }

            if (values is not { Length: >= 2 } || values[0] is not SolidColorBrush text)
            {
                return values is { Length: > 0 } && values[0] is Brush brush ? brush : DependencyProperty.UnsetValue;
            }

            if (values[1] is not SolidColorBrush fill)
            {
                return text;
            }

            Color readable = Readable(text.Color, fill.Color);
            if (readable == text.Color)
            {
                return text;
            }

            var shifted = new SolidColorBrush(readable);
            shifted.Freeze();
            return shifted;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        // TEXT, or the nearest color in its direction (toward white on a dark fill, black on a light one) that reaches
        // MinimumContrast against FILL.
        internal static Color Readable(Color text, Color fill)
        {
            if (Contrast(text, fill) >= MinimumContrast)
            {
                return text;
            }

            Color target = Luminance(fill) < 0.18 ? Colors.White : Colors.Black;
            for (int step = 1; step <= 20; step++)
            {
                Color shifted = Blend(text, target, step / 20.0);
                if (Contrast(shifted, fill) >= MinimumContrast)
                {
                    return shifted;
                }
            }

            return target;
        }

        internal static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

        private static double Luminance(Color color) =>
            0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

        private static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
