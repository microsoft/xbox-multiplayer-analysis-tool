// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace XMAT.WebServiceCapture
{
    // The timeline's Volume view: requests per second as a line over a light fill. While a capture runs it shows the
    // last minute, the newest completed second at the right edge; otherwise the whole capture, in wider steps when its
    // seconds don't fit. It draws everything itself, with no element per point. The mouse shows a point's time and
    // count and selects its first request; the connections list offers the same requests to the keyboard, so the chart
    // takes no focus.
    internal sealed class CaptureVolumeChart : FrameworkElement
    {
        private const double LineThickness = 1.5;
        private const double FillOpacity = 0.12;
        private const double DotRadius = 2.5;
        // Room for the newest point's dot at the right edge.
        private const double RightInset = 3;
        // Between the value labels and the plot, below the readout, and between a time tick and its label.
        private const double LabelGap = 6;
        private const double ReadoutGap = 4;
        private const double TickLength = 4;
        private const double TickLabelOffset = 3;
        private const double TimeLabelTop = 2;
        private const double MinimumLabelSpacing = 80;
        private const double MinimumPlotHeight = 8;

        public static readonly DependencyProperty LineBrushProperty =
            Register(nameof(LineBrush), typeof(Brush), null);

        public static readonly DependencyProperty GridLineBrushProperty =
            Register(nameof(GridLineBrush), typeof(Brush), null);

        public static readonly DependencyProperty BaselineBrushProperty =
            Register(nameof(BaselineBrush), typeof(Brush), null);

        public static readonly DependencyProperty LabelBrushProperty =
            Register(nameof(LabelBrush), typeof(Brush), null);

        public static readonly DependencyProperty AxisFontSizeProperty =
            Register(nameof(AxisFontSize), typeof(double), 12.0);

        public static readonly DependencyProperty ReadoutFontSizeProperty =
            Register(nameof(ReadoutFontSize), typeof(double), 12.0);

        public static readonly DependencyProperty FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner(typeof(CaptureVolumeChart), new FrameworkPropertyMetadata(
                SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
                AppearanceChanged));

        private readonly List<int> _totals = new();
        private readonly List<TimeLabel> _labels = new();
        private readonly ToolTip _toolTip = new() { Placement = PlacementMode.Relative };
        private CaptureVolumeSeries _series;
        private bool _live;
        private DateTime _now;
        private CaptureVolumeSeries.PlotSpan _span = CaptureVolumeSeries.PlotSpan.Empty;
        private Pen _linePen;
        private Pen _gridPen;
        private Pen _baselinePen;
        private Pen _tickPen;
        private StreamGeometry _line;
        private StreamGeometry _area;
        private FormattedText _topLabel;
        private FormattedText _zeroLabel;
        private FormattedText _readoutText;
        private double _plotLeft;
        private double _plotRight;
        private double _plotTop;
        private double _plotBottom;
        private double _spacing;
        private Point? _pointer;

        public CaptureVolumeChart()
        {
            _toolTip.PlacementTarget = this;
            ToolTip = _toolTip;
            Readout = Localization.GetLocalizedString("TIMELINE_VOLUME_EMPTY");
            AutomationProperties.SetName(this, Localization.GetLocalizedString("TIMELINE_VOLUME_AUTOMATION_NAME"));
            UpdateHelpText();
        }

        public Brush LineBrush
        {
            get => (Brush)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        // The top gridline.
        public Brush GridLineBrush
        {
            get => (Brush)GetValue(GridLineBrushProperty);
            set => SetValue(GridLineBrushProperty, value);
        }

        public Brush BaselineBrush
        {
            get => (Brush)GetValue(BaselineBrushProperty);
            set => SetValue(BaselineBrushProperty, value);
        }

        // Value and time labels, time ticks, the readout and the hover line.
        public Brush LabelBrush
        {
            get => (Brush)GetValue(LabelBrushProperty);
            set => SetValue(LabelBrushProperty, value);
        }

        public double AxisFontSize
        {
            get => (double)GetValue(AxisFontSizeProperty);
            set => SetValue(AxisFontSizeProperty, value);
        }

        public double ReadoutFontSize
        {
            get => (double)GetValue(ReadoutFontSizeProperty);
            set => SetValue(ReadoutFontSizeProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        // Called with the first second of a clicked point and the second after it, if the point has requests.
        internal Action<long, long> StepClicked { get; set; }

        // While live, the last completed second's rate; otherwise the capture's total and its busiest second.
        internal string Readout { get; private set; }

        internal bool IsLive => _live;
        internal CaptureVolumeSeries.PlotSpan Span => _span;
        internal IReadOnlyList<int> Totals => _totals;
        internal double TopValue { get; private set; } = CaptureVolumeSeries.MinimumTop;
        internal int HoveredIndex { get; private set; } = -1;
        internal string HoverText => HoveredIndex < 0 ? null : (string)_toolTip.Content;
        internal IEnumerable<string> TimeLabels
        {
            get
            {
                foreach (var label in _labels)
                {
                    yield return label.Text.Text;
                }
            }
        }

        // Shows SERIES: while LIVE, the last minute before NOW; otherwise the whole capture.
        internal void Show(CaptureVolumeSeries series, bool live, DateTime now)
        {
            _series = series;
            _live = live;
            _now = now;
            Readout = live
                ? Localization.GetLocalizedString(
                    "TIMELINE_VOLUME_RATE", series.CountAt(CaptureVolumeSeries.SecondOf(now) - 1))
                : series.Total switch
                {
                    0 => Localization.GetLocalizedString("TIMELINE_VOLUME_EMPTY"),
                    1 => Localization.GetLocalizedString("TIMELINE_VOLUME_TOTAL_ONE"),
                    _ => Localization.GetLocalizedString("TIMELINE_VOLUME_TOTAL", series.Total, series.Peak)
                };
            UpdateHelpText();
            Recompute();
            InvalidateVisual();
        }

        internal void Clear()
        {
            _series = null;
            _pointer = null;
            Readout = Localization.GetLocalizedString("TIMELINE_VOLUME_EMPTY");
            UpdateHelpText();
            Recompute();
            InvalidateVisual();
        }

        // Follows the mouse at POSITION, or no longer when null.
        internal void SetPointer(Point? position)
        {
            _pointer = position;
            UpdateHover();
        }

        // Clicks the point under POSITION; returns whether it had requests to select.
        internal bool SelectAt(Point position)
        {
            var index = IndexAt(position);
            if (index < 0 || _totals[index] == 0)
            {
                return false;
            }

            StepClicked?.Invoke(_span.StartOf(index), _span.EndOf(index));
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetPointer(e.GetPosition(this));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            SetPointer(null);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            SelectAt(e.GetPosition(this));
        }

        protected override void OnToolTipOpening(ToolTipEventArgs e)
        {
            base.OnToolTipOpening(e);
            e.Handled |= HoveredIndex < 0;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Recompute();
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

        protected override void OnRender(DrawingContext drawingContext)
        {
            // A transparent fill, so that the whole chart takes the mouse.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (_series == null || _readoutText == null)
            {
                return;
            }

            if (HasPlot)
            {
                DrawLine(drawingContext, _gridPen, new Point(_plotLeft, _plotTop), new Point(_plotRight, _plotTop));
                DrawLine(drawingContext, _baselinePen, new Point(_plotLeft, _plotBottom), new Point(_plotRight, _plotBottom));
                drawingContext.DrawText(_topLabel,
                    new Point(_plotLeft - LabelGap - _topLabel.Width, _plotTop - (_topLabel.Height / 2)));
                drawingContext.DrawText(_zeroLabel,
                    new Point(_plotLeft - LabelGap - _zeroLabel.Width, _plotBottom - (_zeroLabel.Height / 2)));
                foreach (var label in _labels)
                {
                    DrawLine(drawingContext, _tickPen,
                        new Point(label.X, _plotBottom), new Point(label.X, _plotBottom + TickLength));
                    drawingContext.DrawText(label.Text,
                        new Point(label.X + TickLabelOffset, _plotBottom + TimeLabelTop));
                }
                if (_area != null)
                {
                    drawingContext.PushOpacity(FillOpacity);
                    drawingContext.DrawGeometry(LineBrush, null, _area);
                    drawingContext.Pop();
                }
                if (HoveredIndex >= 0)
                {
                    var x = HoverX(HoveredIndex);
                    DrawLine(drawingContext, _tickPen, new Point(x, _plotTop), new Point(x, _plotBottom));
                }
                if (_line != null)
                {
                    drawingContext.DrawGeometry(null, _linePen, _line);
                    if (_live)
                    {
                        drawingContext.DrawEllipse(LineBrush, null, PointAt(_span.Count - 1), DotRadius, DotRadius);
                    }
                }
            }
            drawingContext.DrawText(_readoutText, new Point(Math.Max(RenderSize.Width - _readoutText.Width, 0), 0));
        }

        private bool HasPlot => _plotBottom - _plotTop >= MinimumPlotHeight && _plotRight > _plotLeft;

        private static DependencyProperty Register(string name, Type type, object defaultValue) =>
            DependencyProperty.Register(name, type, typeof(CaptureVolumeChart), new FrameworkPropertyMetadata(
                defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, AppearanceChanged));

        // Colors and sizes come from the theme by resource reference, so a theme change lays the chart out again.
        private static void AppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((CaptureVolumeChart)d).Recompute();

        private void UpdateHelpText() => AutomationProperties.SetHelpText(
            this, Localization.GetLocalizedString("TIMELINE_VOLUME_AUTOMATION_HELP", Readout));

        // Lays out the plot for the current size and data: the readout above it, the top value and zero to its left,
        // time labels below, and the line through one point per step.
        private void Recompute()
        {
            _totals.Clear();
            _labels.Clear();
            _line = null;
            _area = null;
            _span = CaptureVolumeSeries.PlotSpan.Empty;
            _linePen = Freeze(new Pen(LineBrush, LineThickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            });
            _gridPen = Freeze(new Pen(GridLineBrush, 1));
            _baselinePen = Freeze(new Pen(BaselineBrush, 1));
            _tickPen = Freeze(new Pen(LabelBrush, 1));
            if (_series == null)
            {
                _readoutText = null;
                UpdateHover();
                return;
            }

            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _readoutText = Text(Readout, ReadoutFontSize, pixelsPerDip);
            _zeroLabel = Text("0", AxisFontSize, pixelsPerDip);
            _plotTop = Math.Round(_readoutText.Height + ReadoutGap);
            _plotBottom = Math.Floor(ActualHeight - _zeroLabel.Height - TimeLabelTop);
            _plotRight = ActualWidth - RightInset;
            double top;
            if (_live)
            {
                _span = _series.GetLive(_now, _totals);
                top = CaptureVolumeSeries.NiceTop(Max(_totals, (total, _) => total));
                _topLabel = Text(FormatValue(top), AxisFontSize, pixelsPerDip);
                _plotLeft = Gutter();
            }
            else
            {
                // An average is never above the busiest second, so the label for that bounds the room the top value
                // needs, and the steps can be chosen for the plot's final width.
                _topLabel = Text(FormatValue(CaptureVolumeSeries.NiceTop(_series.Peak)), AxisFontSize, pixelsPerDip);
                _plotLeft = Gutter();
                _span = _series.GetWhole(_plotRight - _plotLeft, _totals);
                top = CaptureVolumeSeries.NiceTop(Max(_totals, Average));
                _topLabel = Text(FormatValue(top), AxisFontSize, pixelsPerDip);
            }
            TopValue = top;

            var count = _span.Count;
            _spacing = count > 1 ? (_plotRight - _plotLeft) / (count - 1) : _plotRight - _plotLeft;
            if (count > 0 && HasPlot)
            {
                BuildGeometry(count);
                AddTimeLabels(count, pixelsPerDip);
            }
            UpdateHover();
        }

        private void BuildGeometry(int count)
        {
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lineContext = line.Open())
            using (var areaContext = area.Open())
            {
                var first = PointAt(0);
                lineContext.BeginFigure(first, false, false);
                areaContext.BeginFigure(new Point(first.X, _plotBottom), true, true);
                areaContext.LineTo(first, false, false);
                var last = first;
                for (var index = 1; index < count; index++)
                {
                    last = PointAt(index);
                    lineContext.LineTo(last, true, true);
                    areaContext.LineTo(last, false, false);
                }
                if (count == 1)
                {
                    // One step covers the whole width.
                    last = new Point(_plotRight, first.Y);
                    lineContext.LineTo(last, true, true);
                    areaContext.LineTo(last, false, false);
                }
                areaContext.LineTo(new Point(last.X, _plotBottom), false, false);
            }
            line.Freeze();
            area.Freeze();
            _line = line;
            _area = area;
        }

        // Elapsed capture time from the first request, at whole intervals far enough apart for their labels. A live
        // chart has no labels before the first request.
        private void AddTimeLabels(int count, double pixelsPerDip)
        {
            if (_series.Total == 0)
            {
                return;
            }

            var origin = _series.FirstSecond;
            var pixelsPerSecond = count > 1
                ? _spacing / _span.Step
                : (_plotRight - _plotLeft) / (_span.End - _span.First);
            var widest = Text(CaptureVolumeSeries.FormatElapsed(Math.Max(_span.End - origin, 0)), AxisFontSize, pixelsPerDip);
            var interval = CaptureVolumeSeries.ChooseLabelInterval(
                pixelsPerSecond, Math.Max(MinimumLabelSpacing, widest.Width + (2 * LabelGap)));
            var from = Math.Max(_span.First, origin);
            var second = origin + ((from - origin + interval - 1) / interval * interval);
            for (; second <= _span.StartOf(count - 1); second += interval)
            {
                var x = _plotLeft + ((second - _span.First) * pixelsPerSecond);
                var text = Text(CaptureVolumeSeries.FormatElapsed(second - origin), AxisFontSize, pixelsPerDip);
                if (x + TickLabelOffset + text.Width > ActualWidth)
                {
                    break;
                }
                _labels.Add(new TimeLabel(x, text));
            }
        }

        private void UpdateHover()
        {
            var index = _pointer is Point position ? IndexAt(position) : -1;
            if (index != HoveredIndex)
            {
                HoveredIndex = index;
                InvalidateVisual();
            }
            if (index < 0)
            {
                _toolTip.IsOpen = false;
                ClearValue(CursorProperty);
                return;
            }

            _toolTip.Content = PointText(index);
            _toolTip.HorizontalOffset = HoverX(index) + 12;
            _toolTip.VerticalOffset = _pointer.Value.Y + 16;
            if (_totals[index] > 0)
            {
                Cursor = Cursors.Hand;
            }
            else
            {
                ClearValue(CursorProperty);
            }
        }

        // The point nearest POSITION, or -1 off the plot. A live chart has no capture time before the first request,
        // so its seconds before then have no point to show.
        private int IndexAt(Point position)
        {
            var count = _span.Count;
            var reach = Math.Max(count > 1 ? _spacing / 2 : 0, 2);
            if (_series == null || count == 0 || !HasPlot || position.X < _plotLeft - reach)
            {
                return -1;
            }

            var index = count == 1 ? 0 : (int)Math.Clamp(Math.Round((position.X - _plotLeft) / _spacing), 0, count - 1);
            return _live && (_series.Total == 0 || _span.StartOf(index) < _series.FirstSecond) ? -1 : index;
        }

        private string PointText(int index)
        {
            var origin = _series.FirstSecond;
            var start = CaptureVolumeSeries.FormatElapsed(_span.StartOf(index) - origin);
            var time = _span.Step == 1
                ? start
                : Localization.GetLocalizedString("TIMELINE_VOLUME_RANGE",
                    start, CaptureVolumeSeries.FormatElapsed(_span.EndOf(index) - origin));
            var total = _totals[index];
            return total == 1
                ? Localization.GetLocalizedString("TIMELINE_VOLUME_POINT_ONE", time)
                : Localization.GetLocalizedString("TIMELINE_VOLUME_POINT", time, total);
        }

        // Where point INDEX is drawn.
        internal Point PointAt(int index)
        {
            var value = _live ? _totals[index] : Average(_totals[index], index);
            return new Point(_plotLeft + (index * _spacing),
                _plotBottom - (value / TopValue * (_plotBottom - _plotTop)));
        }

        private double HoverX(int index) => _span.Count == 1 && _pointer is Point position
            ? Math.Clamp(position.X, _plotLeft, _plotRight)
            : PointAt(index).X;

        private double Average(int total, int index) => _span.Average(index, total);

        private double Gutter() => Math.Ceiling(Math.Max(_topLabel.Width, _zeroLabel.Width) + LabelGap);

        private FormattedText Text(string text, double size, double pixelsPerDip) => new(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, LabelBrush ?? Brushes.Transparent, pixelsPerDip);

        private static string FormatValue(double value) => value.ToString("N0", CultureInfo.CurrentCulture);

        private static double Max(List<int> totals, Func<int, int, double> value)
        {
            double max = 0;
            for (var index = 0; index < totals.Count; index++)
            {
                max = Math.Max(max, value(totals[index], index));
            }
            return max;
        }

        private static Pen Freeze(Pen pen)
        {
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }
            return pen;
        }

        // Lines snap to device pixels, so 1px lines stay sharp.
        private static void DrawLine(DrawingContext drawingContext, Pen pen, Point from, Point to)
        {
            if (pen.Brush == null)
            {
                return;
            }

            var half = pen.Thickness / 2;
            drawingContext.PushGuidelineSet(new GuidelineSet(
                new[] { from.X + half, to.X + half }, new[] { from.Y + half, to.Y + half }));
            drawingContext.DrawLine(pen, from, to);
            drawingContext.Pop();
        }

        private readonly record struct TimeLabel(double X, FormattedText Text);

        // A chart with a name and help text that reads out its current rate or total; it is not interactive.
        private sealed class ChartAutomationPeer(CaptureVolumeChart owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

            protected override string GetClassNameCore() => nameof(CaptureVolumeChart);
        }
    }
}
