// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls
{
    // One row's call rate, as wide as its column: the calls in the limit's window at each sample as a line, a dashed line
    // at the limit (CallRateLimitLabel labels it beside the chart), and the stretches over the limit shaded and drawn in
    // a thicker line in the error color, so color is never the only cue. Hovering draws a hairline at the sample under
    // the pointer and raises HoverChanged. Drawn in OnRender from frozen geometry; its style sets the brushes by resource
    // reference, so themes redraw it.
    internal sealed class CallRateChart : FrameworkElement
    {
        private const double LineThickness = 1.5;
        private const double OverLimitThickness = 2.5;
        private const double ShadeOpacity = 0.15;
        private const double DotRadius = 2.5;

        private static readonly DashStyle LimitDash = Frozen(new DashStyle(new[] { 3.0, 3.0 }, 0));

        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
            nameof(Series), typeof(CallRateSeries), typeof(CallRateChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPlotChanged));

        public static readonly DependencyProperty PlotHeightProperty = DependencyProperty.Register(
            nameof(PlotHeight), typeof(double), typeof(CallRateChart),
            new FrameworkPropertyMetadata(
                32.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnPlotChanged));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(CallRateChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty OverLimitStrokeProperty = DependencyProperty.Register(
            nameof(OverLimitStroke), typeof(Brush), typeof(CallRateChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        // The limit's dashed line and the hover hairline.
        public static readonly DependencyProperty GuideBrushProperty = DependencyProperty.Register(
            nameof(GuideBrush), typeof(Brush), typeof(CallRateChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly RoutedEvent HoverChangedEvent = EventManager.RegisterRoutedEvent(
            "HoverChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CallRateChart));

        private CallRatePlot _plot;

        public CallRateSeries Series
        {
            get => (CallRateSeries)GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        public double PlotHeight
        {
            get => (double)GetValue(PlotHeightProperty);
            set => SetValue(PlotHeightProperty, value);
        }

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public Brush OverLimitStroke
        {
            get => (Brush)GetValue(OverLimitStrokeProperty);
            set => SetValue(OverLimitStrokeProperty, value);
        }

        public Brush GuideBrush
        {
            get => (Brush)GetValue(GuideBrushProperty);
            set => SetValue(GuideBrushProperty, value);
        }

        public event RoutedEventHandler HoverChanged
        {
            add => AddHandler(HoverChangedEvent, value);
            remove => RemoveHandler(HoverChangedEvent, value);
        }

        // The sample under the pointer, or -1.
        internal int HoverSample { get; private set; } = -1;

        internal CallRatePlot Plot => Series == null || ActualWidth <= 0
            ? null
            : _plot ??= CallRatePlot.Build(Series, ActualWidth, PlotHeight);

        // Where the hairline for the hovered sample is drawn, or NaN when nothing is hovered.
        internal double HoverX => HoverSample < 0 || Plot == null
            ? double.NaN
            : Math.Clamp(Math.Floor(Plot.X(HoverSample)) + 0.5, 0.5, Math.Max(0.5, ActualWidth - 0.5));

        // Shows the hairline at SAMPLE, or hides it for -1.
        internal void ShowSample(int sample)
        {
            sample = Series == null || sample < 0 ? -1 : Math.Min(sample, Series.Counts.Count - 1);
            if (sample == HoverSample)
            {
                return;
            }

            HoverSample = sample;
            InvalidateVisual();
            RaiseEvent(new RoutedEventArgs(HoverChangedEvent, this));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point position = e.GetPosition(this);
            ShowSample(Plot != null && position.X >= 0 && position.X <= ActualWidth && position.Y <= PlotHeight
                ? Plot.SampleAt(position.X)
                : -1);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            ShowSample(-1);
        }

        // The chart takes the width its column gives it.
        protected override Size MeasureOverride(Size availableSize) => new(0, PlotHeight);

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (sizeInfo.WidthChanged)
            {
                _plot = null;
                ShowSample(-1);
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            CallRatePlot plot = Plot;
            if (plot == null)
            {
                return;
            }

            double width = ActualWidth;
            // The whole plot takes the pointer, not only its lines.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, PlotHeight));
            Brush line = Stroke;
            Brush overLimit = OverLimitStroke;
            Brush guide = GuideBrush;
            if (overLimit != null)
            {
                drawingContext.PushOpacity(ShadeOpacity);
                drawingContext.DrawGeometry(overLimit, null, plot.Shade);
                drawingContext.Pop();
            }

            if (guide != null)
            {
                drawingContext.DrawLine(Pen(guide, 1, LimitDash), new Point(0, plot.LimitY), new Point(width, plot.LimitY));
                if (HoverSample >= 0)
                {
                    drawingContext.DrawLine(Pen(guide, 1), new Point(HoverX, 0), new Point(HoverX, PlotHeight));
                }
            }

            if (line != null)
            {
                drawingContext.DrawGeometry(null, Pen(line, LineThickness, round: true), plot.Line);
            }

            if (overLimit != null)
            {
                drawingContext.DrawGeometry(null, Pen(overLimit, OverLimitThickness, round: true), plot.OverLimit);
            }

            if (HoverSample >= 0 && (Series.IsOverLimit(HoverSample) ? overLimit : line) is Brush dot)
            {
                drawingContext.DrawEllipse(
                    dot, null, new Point(HoverX, plot.Y(Series.Counts[HoverSample])), DotRadius, DotRadius);
            }
        }

        private static void OnPlotChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            var chart = (CallRateChart)element;
            chart._plot = null;
            chart.ShowSample(-1);
        }

        // A theme brush can follow the system's colors (high contrast), so it can't always be frozen; then neither can
        // its pen.
        private static Pen Pen(Brush brush, double thickness, DashStyle dash = null, bool round = false)
        {
            var pen = new Pen(brush, thickness);
            if (dash != null)
            {
                pen.DashStyle = dash;
            }

            if (round)
            {
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.LineJoin = PenLineJoin.Round;
            }

            return pen.CanFreeze ? Frozen(pen) : pen;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }

    // A series' geometry in a plot WIDTH by HEIGHT: frozen, so a chart only draws it. Following the design, the limit sits
    // at two thirds of the height or lower, the peak gets a tenth of headroom, and the scale is fitted so that the
    // limit falls on a whole pixel's centre, where its dashed line is crisp.
    internal sealed class CallRatePlot
    {
        // The line keeps this far from the plot's top and bottom edges.
        internal const double Inset = 2;

        private readonly double _width;
        private readonly int _intervals;
        private readonly double _bottom;
        private readonly double _scale;

        private CallRatePlot(double width, int intervals, double bottom, double scale, double limitY)
        {
            _width = width;
            _intervals = intervals;
            _bottom = bottom;
            _scale = scale;
            LimitY = limitY;
        }

        // The dashed limit line's y.
        public double LimitY { get; }

        // The line where it is within the limit, where it is over it, and the area between the limit and the line
        // where it is over.
        public Geometry Line { get; private set; }
        public Geometry OverLimit { get; private set; }
        public Geometry Shade { get; private set; }

        public double X(int sample) => sample * _width / _intervals;

        public double Y(double calls) => _bottom - calls * _scale;

        public int SampleAt(double x) => (int)Math.Clamp(Math.Round(x / _width * _intervals), 0, _intervals);

        // The scale, in pixels a call, and the limit line's y for SERIES in a plot HEIGHT high; neither depends on the
        // plot's width.
        internal static (double Scale, double LimitY) Fit(CallRateSeries series, double height)
        {
            double top = Inset;
            double bottom = height - Inset;
            double span = bottom - top;
            uint limit = series.Limit;
            double ceiling = Math.Max(Math.Max(series.Peak * 1.1, limit * 1.5), 1);
            double limitY;
            if (limit > 0)
            {
                limitY = Math.Clamp(Math.Floor(bottom - span * limit / ceiling) + 0.5, top + 0.5, bottom - 0.5);
                double fitted = span * limit / (bottom - limitY);
                ceiling = fitted >= series.Peak ? fitted : ceiling;
            }
            else
            {
                limitY = Math.Floor(bottom) - 0.5;
            }

            return (span / ceiling, limitY);
        }

        internal static CallRatePlot Build(CallRateSeries series, double width, double height)
        {
            (double scale, double limitY) = Fit(series, height);
            var plot = new CallRatePlot(width, Math.Max(1, series.Counts.Count - 1), height - Inset, scale, limitY);
            plot.Trace(series);
            return plot;
        }

        // Splits the line where it crosses the limit, into runs within it and runs over it.
        private void Trace(CallRateSeries series)
        {
            double limit = series.Limit;
            var runs = new List<(bool Over, List<Point> Points)>();

            void Add((double X, double Calls) from, (double X, double Calls) to)
            {
                bool over = (from.Calls + to.Calls) / 2 > limit;
                if (runs.Count > 0 && runs[^1].Over == over)
                {
                    runs[^1].Points.Add(new Point(to.X, Y(to.Calls)));
                }
                else
                {
                    runs.Add((over, new List<Point> { new(from.X, Y(from.Calls)), new(to.X, Y(to.Calls)) }));
                }
            }

            List<(double X, double Calls)> points = Points(series.Counts);
            for (int index = 0; index + 1 < points.Count; index++)
            {
                (double X, double Calls) from = points[index];
                (double X, double Calls) to = points[index + 1];
                if ((from.Calls - limit) * (to.Calls - limit) < 0)
                {
                    double x = from.X + (limit - from.Calls) / (to.Calls - from.Calls) * (to.X - from.X);
                    Add(from, (x, limit));
                    Add((x, limit), to);
                }
                else
                {
                    Add(from, to);
                }
            }

            Line = Polylines(runs.Where(run => !run.Over).Select(run => run.Points));
            OverLimit = Polylines(runs.Where(run => run.Over).Select(run => run.Points));

            double limitLine = Y(limit);
            var shade = new StreamGeometry();
            using (StreamGeometryContext context = shade.Open())
            {
                foreach (List<Point> run in runs.Where(run => run.Over).Select(run => run.Points))
                {
                    var outline = new List<Point>();
                    if (run[0].Y != limitLine)
                    {
                        outline.Add(new Point(run[0].X, limitLine));
                    }

                    outline.AddRange(run);
                    if (run[^1].Y != limitLine)
                    {
                        outline.Add(new Point(run[^1].X, limitLine));
                    }

                    context.BeginFigure(outline[0], isFilled: true, isClosed: true);
                    context.PolyLineTo(outline.Skip(1).ToList(), false, true);
                }
            }

            shade.Freeze();
            Shade = shade;
        }

        // The samples as points; when there are many more than the plot has pixels, each pixel column keeps only its
        // first, lowest, highest and last sample, which draws the same line.
        private List<(double X, double Calls)> Points(IReadOnlyList<int> counts)
        {
            var points = new List<(double X, double Calls)>();
            bool decimate = counts.Count > 4 * _width;
            int next = 0;
            while (next < counts.Count)
            {
                double column = Math.Floor(X(next));
                int first = next;
                int lowest = next;
                int highest = next;
                next++;
                while (decimate && next < counts.Count && Math.Floor(X(next)) == column)
                {
                    lowest = counts[next] < counts[lowest] ? next : lowest;
                    highest = counts[next] > counts[highest] ? next : highest;
                    next++;
                }

                foreach (int sample in new[] { first, lowest, highest, next - 1 }.Distinct().Order())
                {
                    points.Add((X(sample), counts[sample]));
                }
            }

            return points;
        }

        private static StreamGeometry Polylines(IEnumerable<List<Point>> runs)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                foreach (List<Point> run in runs)
                {
                    context.BeginFigure(run[0], isFilled: false, isClosed: false);
                    context.PolyLineTo(run.Skip(1).ToList(), true, true);
                }
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
