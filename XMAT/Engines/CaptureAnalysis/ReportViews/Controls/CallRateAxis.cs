// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls
{
    // The time axis under the call rate charts, as wide as they are: the capture's start and end, and between them a
    // label every minute, or at whatever interval keeps the labels apart, all as m:ss.
    internal sealed class CallRateAxis : FrameworkElement
    {
        private const double LabelTop = 2;
        private const double MinSpacing = 56;
        private const double MinGap = 8;

        private static readonly int[] Intervals =
        {
            1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400,
        };

        public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(
            nameof(Seconds), typeof(long), typeof(CallRateAxis),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(CallRateAxis),
            new FrameworkPropertyMetadata(
                Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
            typeof(CallRateAxis),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.Inherits |
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(CallRateAxis),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontSize,
                FrameworkPropertyMetadataOptions.Inherits |
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

        // The axis' length: the capture's length, rounded up to a whole sample.
        public long Seconds
        {
            get => (long)GetValue(SecondsProperty);
            set => SetValue(SecondsProperty, value);
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

        // The times labelled between the start and the end of an axis SECONDS long and WIDTH wide.
        internal static IReadOnlyList<long> Ticks(long seconds, double width)
        {
            var ticks = new List<long>();
            if (seconds <= 0 || width <= 0)
            {
                return ticks;
            }

            long interval = Intervals[^1];
            foreach (int candidate in Intervals)
            {
                if (candidate / (double)seconds * width >= MinSpacing)
                {
                    interval = candidate;
                    break;
                }
            }

            for (long time = interval; time < seconds; time += interval)
            {
                ticks.Add(time);
            }

            return ticks;
        }

        // The axis takes the width its column gives it.
        protected override Size MeasureOverride(Size availableSize) =>
            new(0, LabelTop + Text("0:00").Height);

        // The labels' text is laid out for the display's DPI.
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            long seconds = Seconds;
            if (seconds <= 0 || Foreground == null)
            {
                return;
            }

            double width = ActualWidth;
            FormattedText start = Text(CallRateReport.FormatTime(0));
            FormattedText end = Text(CallRateReport.FormatTime(seconds));
            double endLeft = width - end.WidthIncludingTrailingWhitespace;
            drawingContext.DrawText(start, new Point(0, LabelTop));
            drawingContext.DrawText(end, new Point(endLeft, LabelTop));

            // Labels centred on their time, dropped where they would crowd the ends or each other.
            double right = start.WidthIncludingTrailingWhitespace;
            foreach (long time in Ticks(seconds, width))
            {
                FormattedText label = Text(CallRateReport.FormatTime(time));
                double left = Math.Round(time / (double)seconds * width - label.WidthIncludingTrailingWhitespace / 2);
                if (left < right + MinGap || left + label.WidthIncludingTrailingWhitespace > endLeft - MinGap)
                {
                    continue;
                }

                drawingContext.DrawText(label, new Point(left, LabelTop));
                right = left + label.WidthIncludingTrailingWhitespace;
            }
        }

        private FormattedText Text(string text) =>
            new(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                FontSize,
                Foreground ?? Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
