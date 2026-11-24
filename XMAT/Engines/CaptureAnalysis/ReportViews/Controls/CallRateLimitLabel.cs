// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls
{
    // A chart's limit, such as "10 / 15 s", beside it at the height of its dashed line. The labels share a column, so
    // every chart, and the time axis under them, is as wide as the others.
    internal sealed class CallRateLimitLabel : FrameworkElement
    {
        private const double Gap = 6;
        private const double TrailingSpace = 12;

        public static readonly DependencyProperty SeriesProperty = CallRateChart.SeriesProperty.AddOwner(
            typeof(CallRateLimitLabel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(CallRateLimitLabel),
            new FrameworkPropertyMetadata(
                null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlotHeightProperty = CallRateChart.PlotHeightProperty.AddOwner(
            typeof(CallRateLimitLabel),
            new FrameworkPropertyMetadata(
                32.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(CallRateLimitLabel),
            new FrameworkPropertyMetadata(
                Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
            typeof(CallRateLimitLabel),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.Inherits |
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(CallRateLimitLabel),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontSize,
                FrameworkPropertyMetadataOptions.Inherits |
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

        // The chart's series, which places its limit line.
        public CallRateSeries Series
        {
            get => (CallRateSeries)GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        // The chart's height.
        public double PlotHeight
        {
            get => (double)GetValue(PlotHeightProperty);
            set => SetValue(PlotHeightProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText text = Label();
            return new Size(text == null ? 0 : Gap + Math.Ceiling(text.WidthIncludingTrailingWhitespace) + TrailingSpace, PlotHeight);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            FormattedText text = Label();
            if (text == null || Series == null || Foreground == null)
            {
                return;
            }

            // Centred on the limit's line, a pixel up as in the design, and kept beside the chart.
            double top = Math.Round(CallRatePlot.Fit(Series, PlotHeight).LimitY - 1 - text.Height / 2);
            top = Math.Max(-3, Math.Min(PlotHeight + 3 - text.Height, top));
            drawingContext.DrawText(text, new Point(Gap, top));
        }

        // The text is laid out for the display's DPI.
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateMeasure();
            InvalidateVisual();
        }

        private FormattedText Label() =>
            string.IsNullOrEmpty(Text)
                ? null
                : new FormattedText(
                    Text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    FontSize,
                    Foreground ?? Brushes.Transparent,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
