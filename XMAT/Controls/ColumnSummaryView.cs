// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace XMAT
{
    // The content of a column summary popup: a ColumnSummaryModel, drawn by the ColumnSummaryView style in
    // Styles/DataViz.xaml. The popup is 300 wide when every value fits a 64 wide lane and 380 wide otherwise, and its
    // lanes grow with the text size.
    public sealed class ColumnSummaryView : Control
    {
        public static readonly DependencyProperty SurfaceWidthProperty = DependencyProperty.Register(
            nameof(SurfaceWidth), typeof(double), typeof(ColumnSummaryView), new PropertyMetadata(NarrowWidth));

        public static readonly DependencyProperty ValueLaneWidthProperty = DependencyProperty.Register(
            nameof(ValueLaneWidth), typeof(double), typeof(ColumnSummaryView), new PropertyMetadata(NarrowValueLane));

        public static readonly DependencyProperty CountLaneWidthProperty = DependencyProperty.Register(
            nameof(CountLaneWidth), typeof(double), typeof(ColumnSummaryView), new PropertyMetadata(CountLane));

        public static readonly DependencyProperty PercentLaneWidthProperty = DependencyProperty.Register(
            nameof(PercentLaneWidth), typeof(double), typeof(ColumnSummaryView), new PropertyMetadata(PercentLane));

        // The sizes at the base text size, 12.
        private const double UnscaledFontSize = 12;
        private const double NarrowWidth = 300;
        private const double WideWidth = 380;
        private const double NarrowValueLane = 64;
        private const double WideValueLane = 190;
        private const double CountLane = 48;
        private const double PercentLane = 40;

        public ColumnSummaryView()
        {
            Focusable = false;
        }

        // A light shadow under the surface in light themes and a stronger one in dark themes, where a light one would
        // not show. It reads the surface's background brush.
        public static IValueConverter ShadowOpacityConverter { get; } = new ShadowOpacity();

        // The surface, inside a margin that leaves room for its shadow.
        public double SurfaceWidth
        {
            get => (double)GetValue(SurfaceWidthProperty);
            private set => SetValue(SurfaceWidthProperty, value);
        }

        public double ValueLaneWidth
        {
            get => (double)GetValue(ValueLaneWidthProperty);
            private set => SetValue(ValueLaneWidthProperty, value);
        }

        public double CountLaneWidth
        {
            get => (double)GetValue(CountLaneWidthProperty);
            private set => SetValue(CountLaneWidthProperty, value);
        }

        public double PercentLaneWidth
        {
            get => (double)GetValue(PercentLaneWidthProperty);
            private set => SetValue(PercentLaneWidthProperty, value);
        }

        internal ColumnSummaryModel Model
        {
            get => DataContext as ColumnSummaryModel;
            set
            {
                DataContext = value;
                UpdateLanes();
            }
        }

        // The list of values, once the template is applied.
        internal ListBox Values => GetTemplateChild("PART_Values") as ListBox;

        internal FrameworkElement Surface => GetTemplateChild("PART_Surface") as FrameworkElement;

        // Moves focus to the first value. Returns false while the values are not laid out yet.
        internal bool FocusFirstValue()
        {
            UpdateLayout();
            return Values?.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item && item.Focus();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty)
            {
                UpdateLanes();
            }
        }

        private void UpdateLanes()
        {
            double scale = FontSize > 0 ? FontSize / UnscaledFontSize : 1;
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double Measure(string text) => new FormattedText(
                text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize, Brushes.Black,
                pixelsPerDip).WidthIncludingTrailingWhitespace;

            var rows = Model?.Rows ?? Array.Empty<ColumnSummaryRow>();
            bool narrow = rows.All(row => Measure(row.Label) <= NarrowValueLane * scale);
            SurfaceWidth = (narrow ? NarrowWidth : WideWidth) * scale;
            ValueLaneWidth = (narrow ? NarrowValueLane : WideValueLane) * scale;
            // Counts and percentages never cut off: a lane grows to fit its widest text, plus a gap before it.
            double gap = 4 * scale;
            CountLaneWidth = Math.Max(CountLane * scale, rows.Select(row => Measure(row.CountText) + gap).DefaultIfEmpty(0).Max());
            PercentLaneWidth = Math.Max(PercentLane * scale, rows.Select(row => Measure(row.PercentText) + gap).DefaultIfEmpty(0).Max());
        }

        private sealed class ShadowOpacity : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is not SolidColorBrush { Color: var color })
                {
                    return 0.12;
                }

                double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255;
                return luminance < 0.5 ? 0.45 : 0.12;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
