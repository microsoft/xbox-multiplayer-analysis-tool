// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace XMAT
{
    // The traffic overview's requests-per-second line: an area under a line, with the newest point marked. It is drawn as
    // two geometries, not as an element per point.
    public sealed class TrafficSparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            nameof(Values), typeof(IReadOnlyList<double>), typeof(TrafficSparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            nameof(LineBrush), typeof(Brush), typeof(TrafficSparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        // How strongly the area under the line shows the line's color.
        public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
            nameof(FillOpacity), typeof(double), typeof(TrafficSparkline),
            new FrameworkPropertyMetadata(0.12, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double LineThickness = 1.5;
        private const double MarkerRadius = 2;

        public IReadOnlyList<double> Values
        {
            get => (IReadOnlyList<double>)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Brush LineBrush
        {
            get => (Brush)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        // Where each value is drawn: oldest at the left, newest at the right; zero at the bottom, the largest value at the
        // top. The edges leave room for the line and the marker.
        internal static Point[] Points(IReadOnlyList<double> values, Size size)
        {
            if (values == null || values.Count == 0)
            {
                return Array.Empty<Point>();
            }

            double left = 1;
            double right = Math.Max(left, size.Width - MarkerRadius);
            double top = MarkerRadius + 1;
            double bottom = Math.Max(top, size.Height - MarkerRadius - 1);
            double max = values.Max();
            var points = new Point[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                double x = values.Count == 1 ? right : left + (right - left) * index / (values.Count - 1);
                double y = max > 0 ? bottom - (bottom - top) * Math.Max(0, values[index]) / max : bottom;
                points[index] = new Point(x, y);
            }

            return points;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Brush line = LineBrush;
            Point[] points = Points(Values, RenderSize);
            if (line == null || points.Length == 0)
            {
                return;
            }

            if (points.Length > 1)
            {
                var area = new StreamGeometry();
                using (StreamGeometryContext context = area.Open())
                {
                    context.BeginFigure(new Point(points[0].X, RenderSize.Height), true, true);
                    context.PolyLineTo(points, true, true);
                    context.LineTo(new Point(points[^1].X, RenderSize.Height), true, true);
                }
                area.Freeze();

                var path = new StreamGeometry();
                using (StreamGeometryContext context = path.Open())
                {
                    context.BeginFigure(points[0], false, false);
                    context.PolyLineTo(points.Skip(1).ToArray(), true, true);
                }
                path.Freeze();

                Brush fill = line.CloneCurrentValue();
                fill.Opacity = FillOpacity;
                fill.Freeze();
                drawingContext.DrawGeometry(fill, null, area);
                drawingContext.DrawGeometry(null, new Pen(line, LineThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                }, path);
            }

            drawingContext.DrawEllipse(line, null, points[^1], MarkerRadius, MarkerRadius);
        }
    }

    // The traffic overview's status bar: a segment for each status class in proportion to its count, drawn in one pass.
    // The segment under the mouse, or the one HighlightedClass names, is outlined. Hovering a segment shows
    // DescribeSegment's text below the status legend, and selecting a segment that CanSelectSegment allows raises
    // SegmentSelected.
    public sealed class TrafficStatusBar : FrameworkElement
    {
        public static readonly DependencyProperty CountsProperty = DependencyProperty.Register(
            nameof(Counts), typeof(IReadOnlyList<int>), typeof(TrafficStatusBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SuccessBrushProperty = RegisterBrush(nameof(SuccessBrush));
        public static readonly DependencyProperty RedirectBrushProperty = RegisterBrush(nameof(RedirectBrush));
        public static readonly DependencyProperty ClientErrorBrushProperty = RegisterBrush(nameof(ClientErrorBrush));
        public static readonly DependencyProperty ServerErrorBrushProperty = RegisterBrush(nameof(ServerErrorBrush));
        public static readonly DependencyProperty NoStatusBrushProperty = RegisterBrush(nameof(NoStatusBrush));

        // The empty bar, before there are requests.
        public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
        public static readonly DependencyProperty OutlineBrushProperty = RegisterBrush(nameof(OutlineBrush));

        // The class whose legend item is hovered or focused, or -1.
        public static readonly DependencyProperty HighlightedClassProperty = DependencyProperty.Register(
            nameof(HighlightedClass), typeof(int), typeof(TrafficStatusBar),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        // How far below the bar its tooltip opens, so that it leaves the legend under the bar readable.
        public static readonly DependencyProperty ToolTipOffsetProperty = DependencyProperty.Register(
            nameof(ToolTipOffset), typeof(double), typeof(TrafficStatusBar), new PropertyMetadata(4.0));

        internal const double Gap = 1;
        internal const double MinSegmentWidth = 3;
        private const double CornerRadius = 2;
        private const double OutlineThickness = 1.5;

        private readonly ToolTip _toolTip;
        private int _hovered = -1;

        public TrafficStatusBar()
        {
            _toolTip = new ToolTip { Placement = PlacementMode.Custom, CustomPopupPlacementCallback = PlaceToolTip };
            ToolTip = _toolTip;
            ToolTipOpening += (_, e) => e.Handled = _hovered < 0;
        }

        public event EventHandler<TrafficStatusClass> SegmentSelected;

        public IReadOnlyList<int> Counts
        {
            get => (IReadOnlyList<int>)GetValue(CountsProperty);
            set => SetValue(CountsProperty, value);
        }

        public Brush SuccessBrush
        {
            get => (Brush)GetValue(SuccessBrushProperty);
            set => SetValue(SuccessBrushProperty, value);
        }

        public Brush RedirectBrush
        {
            get => (Brush)GetValue(RedirectBrushProperty);
            set => SetValue(RedirectBrushProperty, value);
        }

        public Brush ClientErrorBrush
        {
            get => (Brush)GetValue(ClientErrorBrushProperty);
            set => SetValue(ClientErrorBrushProperty, value);
        }

        public Brush ServerErrorBrush
        {
            get => (Brush)GetValue(ServerErrorBrushProperty);
            set => SetValue(ServerErrorBrushProperty, value);
        }

        public Brush NoStatusBrush
        {
            get => (Brush)GetValue(NoStatusBrushProperty);
            set => SetValue(NoStatusBrushProperty, value);
        }

        public Brush TrackBrush
        {
            get => (Brush)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public Brush OutlineBrush
        {
            get => (Brush)GetValue(OutlineBrushProperty);
            set => SetValue(OutlineBrushProperty, value);
        }

        public int HighlightedClass
        {
            get => (int)GetValue(HighlightedClassProperty);
            set => SetValue(HighlightedClassProperty, value);
        }

        public double ToolTipOffset
        {
            get => (double)GetValue(ToolTipOffsetProperty);
            set => SetValue(ToolTipOffsetProperty, value);
        }

        internal Func<TrafficStatusClass, string> DescribeSegment { get; set; }

        internal Func<TrafficStatusClass, bool> CanSelectSegment { get; set; }

        // The class under the mouse, or -1.
        internal int HoveredClass => _hovered;

        // The segments of COUNTS (indexed by TrafficStatusClass) across WIDTH: each class with requests gets a share of the
        // width in proportion to its count, at least MinSegmentWidth, in whole pixels, with a Gap between neighbors.
        internal static IReadOnlyList<(TrafficStatusClass Class, Rect Bounds)> Layout(
            IReadOnlyList<int> counts, double width, double height)
        {
            var shown = Enumerable.Range(0, counts?.Count ?? 0).Where(index => counts[index] > 0).ToList();
            if (shown.Count == 0 || width <= 0)
            {
                return Array.Empty<(TrafficStatusClass, Rect)>();
            }

            double available = Math.Max(0, width - Gap * (shown.Count - 1));
            double total = shown.Sum(index => (double)counts[index]);
            double[] widths = shown.Select(index => Math.Max(MinSegmentWidth, Math.Round(available * counts[index] / total)))
                .ToArray();
            // The widest segment takes up the rounding, so the segments always fill the bar.
            int widest = Array.IndexOf(widths, widths.Max());
            widths[widest] = Math.Max(0, widths[widest] - (widths.Sum() - available));

            var segments = new List<(TrafficStatusClass, Rect)>(shown.Count);
            double x = 0;
            for (int index = 0; index < shown.Count; index++)
            {
                segments.Add(((TrafficStatusClass)shown[index], new Rect(x, 0, widths[index], height)));
                x += widths[index] + Gap;
            }

            return segments;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var bounds = new Rect(RenderSize);
            // Transparent, so the gaps between segments still take the mouse.
            drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);
            var segments = Layout(Counts, bounds.Width, bounds.Height);
            if (segments.Count == 0)
            {
                drawingContext.DrawRoundedRectangle(TrackBrush, null, bounds, CornerRadius, CornerRadius);
                return;
            }

            drawingContext.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));
            foreach ((TrafficStatusClass statusClass, Rect segment) in segments)
            {
                drawingContext.DrawRectangle(BrushOf(statusClass), null, segment);
            }
            drawingContext.Pop();

            int highlighted = _hovered >= 0 ? _hovered : HighlightedClass;
            foreach ((TrafficStatusClass statusClass, Rect segment) in segments)
            {
                if ((int)statusClass == highlighted && OutlineBrush != null)
                {
                    Rect outline = segment;
                    outline.Inflate(OutlineThickness / 2, OutlineThickness / 2);
                    drawingContext.DrawRectangle(null, new Pen(OutlineBrush, OutlineThickness), outline);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hover(SegmentAt(e.GetPosition(this).X));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Hover(null);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (SegmentAt(e.GetPosition(this).X) is { } segment && CanSelect(segment.Class))
            {
                e.Handled = true;
                SegmentSelected?.Invoke(this, segment.Class);
            }
        }

        // The segment at X; a gap belongs to its nearer neighbor.
        internal (TrafficStatusClass Class, Rect Bounds)? SegmentAt(double x)
        {
            foreach (var segment in Layout(Counts, ActualWidth, ActualHeight))
            {
                if (x < segment.Bounds.Right + Gap / 2)
                {
                    return segment;
                }
            }

            return null;
        }

        private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(TrafficStatusBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private bool CanSelect(TrafficStatusClass statusClass) => CanSelectSegment?.Invoke(statusClass) ?? false;

        private void Hover((TrafficStatusClass Class, Rect Bounds)? segment)
        {
            int hovered = segment is { } value ? (int)value.Class : -1;
            if (hovered == _hovered)
            {
                return;
            }

            _hovered = hovered;
            Cursor = segment is { } selectable && CanSelect(selectable.Class) ? Cursors.Hand : null;
            if (segment is { } shown)
            {
                _toolTip.Content = DescribeSegment?.Invoke(shown.Class);
                // Moving to another segment moves the open tooltip under it.
                _toolTip.PlacementRectangle = shown.Bounds;
            }
            else
            {
                _toolTip.IsOpen = false;
            }

            InvalidateVisual();
        }

        // Centers the tooltip under the hovered segment, ToolTipOffset below the bar. The sizes can be in device pixels.
        private CustomPopupPlacement[] PlaceToolTip(Size popupSize, Size targetSize, Point offset)
        {
            double scale = _toolTip.PlacementRectangle.Width > 0 ? targetSize.Width / _toolTip.PlacementRectangle.Width : 1;
            return new[]
            {
                new CustomPopupPlacement(
                    new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + ToolTipOffset * scale),
                    PopupPrimaryAxis.Horizontal),
            };
        }

        private Brush BrushOf(TrafficStatusClass statusClass) => statusClass switch
        {
            TrafficStatusClass.Success => SuccessBrush,
            TrafficStatusClass.Redirect => RedirectBrush,
            TrafficStatusClass.ClientError => ClientErrorBrush,
            TrafficStatusClass.ServerError => ServerErrorBrush,
            _ => NoStatusBrush,
        };
    }

    // Which cell of the traffic overview a TrafficOverviewPanel child belongs to.
    public enum TrafficOverviewCell
    {
        // The requests count and the divider after the requests.
        Always,
        // The status bar with its legend.
        Chart,
        // The status bar and legend without No status.
        CompactChart,
        // The status classes as one line of text.
        Text,
        // The busiest hosts and the divider before them.
        Hosts,
        // The requests per second line beside the count, with the rate above its end while capturing.
        RequestsLine,
    }

    // How much of the traffic overview fits its width, from all of it to the least.
    public enum TrafficOverviewReduction
    {
        None,
        WithoutNoStatus,
        WithoutHosts,
        WithoutHostsOrNoStatus,
        StatusAsText,
        StatusAsTextWithoutRequestsLine,
    }

    // Lays the traffic overview's cells side by side and drops what doesn't fit: first No status leaves the status chart,
    // then the busiest hosts go, then the status chart gives way to the status as text, and last the requests line goes
    // before the status is cut off. A dropped cell is hidden rather than collapsed, so it is still measured and returns as
    // soon as it fits; hidden cells take no room, focus or clicks. What's shown fills the width: the columns (Column) share
    // it as equally as their cells allow, and each column's last shown cell, whose chart stretches, takes its extra width.
    public sealed class TrafficOverviewPanel : Panel
    {
        public static readonly DependencyProperty CellProperty = DependencyProperty.RegisterAttached(
            "Cell", typeof(TrafficOverviewCell), typeof(TrafficOverviewPanel),
            new FrameworkPropertyMetadata(TrafficOverviewCell.Always, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        // The column a cell is in, counted from 0 at the left: the requests, the status and the busiest hosts. The
        // dividers between them are in none (-1) and keep their width.
        public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
            "Column", typeof(int), typeof(TrafficOverviewPanel),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        // Room kept free at the right while the busiest hosts are shown, whose first line would otherwise run under a
        // button in the corner.
        public static readonly DependencyProperty HostsReserveProperty = DependencyProperty.Register(
            nameof(HostsReserve), typeof(double), typeof(TrafficOverviewPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private static readonly DependencyPropertyKey ReductionPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(Reduction), typeof(TrafficOverviewReduction), typeof(TrafficOverviewPanel),
            new PropertyMetadata(TrafficOverviewReduction.None));

        public static readonly DependencyProperty ReductionProperty = ReductionPropertyKey.DependencyProperty;

        public static TrafficOverviewCell GetCell(UIElement element) => (TrafficOverviewCell)element.GetValue(CellProperty);

        public static void SetCell(UIElement element, TrafficOverviewCell value) => element.SetValue(CellProperty, value);

        public static int GetColumn(UIElement element) => (int)element.GetValue(ColumnProperty);

        public static void SetColumn(UIElement element, int value) => element.SetValue(ColumnProperty, value);

        public double HostsReserve
        {
            get => (double)GetValue(HostsReserveProperty);
            set => SetValue(HostsReserveProperty, value);
        }

        public TrafficOverviewReduction Reduction => (TrafficOverviewReduction)GetValue(ReductionProperty);

        // Raised during layout, once the cells that no longer fit are hidden.
        internal event EventHandler ReductionChanged;

        // The fullest layout that fits WIDTH, from the widths of the cells: the requests count (ALWAYS) and its line, the
        // status chart with and without No status, the status as text, and the busiest hosts. Without No status, the
        // chart is never wider.
        internal static TrafficOverviewReduction Reduce(
            double width, double always, double requestsLine, double chart, double compactChart, double text,
            double hosts, double hostsReserve)
        {
            double requests = always + requestsLine;
            return requests + chart + hosts + hostsReserve <= width ? TrafficOverviewReduction.None
                : requests + compactChart + hosts + hostsReserve <= width ? TrafficOverviewReduction.WithoutNoStatus
                : requests + chart <= width ? TrafficOverviewReduction.WithoutHosts
                : requests + compactChart <= width ? TrafficOverviewReduction.WithoutHostsOrNoStatus
                : requests + text <= width ? TrafficOverviewReduction.StatusAsText
                : TrafficOverviewReduction.StatusAsTextWithoutRequestsLine;
        }

        internal static bool IsShown(TrafficOverviewCell cell, TrafficOverviewReduction reduction) => cell switch
        {
            TrafficOverviewCell.Chart =>
                reduction is TrafficOverviewReduction.None or TrafficOverviewReduction.WithoutHosts,
            TrafficOverviewCell.CompactChart =>
                reduction is TrafficOverviewReduction.WithoutNoStatus or TrafficOverviewReduction.WithoutHostsOrNoStatus,
            TrafficOverviewCell.Text =>
                reduction is TrafficOverviewReduction.StatusAsText or TrafficOverviewReduction.StatusAsTextWithoutRequestsLine,
            TrafficOverviewCell.Hosts =>
                reduction is TrafficOverviewReduction.None or TrafficOverviewReduction.WithoutNoStatus,
            TrafficOverviewCell.RequestsLine => reduction != TrafficOverviewReduction.StatusAsTextWithoutRequestsLine,
            _ => true,
        };

        protected override Size MeasureOverride(Size availableSize)
        {
            var widths = new double[Enum.GetValues<TrafficOverviewCell>().Length];
            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                widths[(int)GetCell(child)] += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }

            TrafficOverviewReduction reduction = Reduce(
                availableSize.Width, widths[(int)TrafficOverviewCell.Always], widths[(int)TrafficOverviewCell.RequestsLine],
                widths[(int)TrafficOverviewCell.Chart], widths[(int)TrafficOverviewCell.CompactChart],
                widths[(int)TrafficOverviewCell.Text], widths[(int)TrafficOverviewCell.Hosts], HostsReserve);
            bool changed = reduction != Reduction;
            SetValue(ReductionPropertyKey, reduction);
            double used = 0;
            foreach (UIElement child in InternalChildren)
            {
                bool shown = IsShown(GetCell(child), reduction);
                // Hidden keeps the child's measured size, which tells when it fits again.
                child.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
                used += shown ? child.DesiredSize.Width : 0;
            }

            if (changed)
            {
                ReductionChanged?.Invoke(this, EventArgs.Empty);
            }

            return new Size(Math.Min(used, availableSize.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double[] widths = Widths(finalSize.Width);
            double x = 0;
            for (int index = 0; index < InternalChildren.Count; index++)
            {
                UIElement child = InternalChildren[index];
                if (child.Visibility == Visibility.Visible)
                {
                    child.Arrange(new Rect(x, 0, widths[index], finalSize.Height));
                    x += widths[index];
                }
                else
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
            }

            return finalSize;
        }

        // The width of each shown child in WIDTH. The dividers keep their widths, and the columns share the rest, less the
        // room kept for the hide button while the hosts show, as equally as their cells allow: a column wider than an equal
        // share keeps its width and the others share what's left. A column's extra width goes to its last shown cell.
        private double[] Widths(double width)
        {
            UIElementCollection children = InternalChildren;
            var widths = new double[children.Count];
            var natural = new double[children.Count];
            var last = new int[children.Count];
            Array.Fill(last, -1);
            double room = width;
            bool hostsShown = false;
            for (int index = 0; index < children.Count; index++)
            {
                UIElement child = children[index];
                if (child.Visibility != Visibility.Visible)
                {
                    continue;
                }

                widths[index] = child.DesiredSize.Width;
                hostsShown |= GetCell(child) == TrafficOverviewCell.Hosts;
                int column = GetColumn(child);
                if (column >= 0 && column < children.Count)
                {
                    natural[column] += widths[index];
                    last[column] = index;
                }
                else
                {
                    room -= widths[index];
                }
            }

            room -= hostsShown ? HostsReserve : 0;
            var open = new List<int>();
            for (int column = 0; column < children.Count; column++)
            {
                if (last[column] >= 0)
                {
                    open.Add(column);
                }
            }

            while (open.Count > 0)
            {
                double share = room / open.Count;
                int[] wide = open.Where(column => natural[column] > share).ToArray();
                if (wide.Length == 0)
                {
                    break;
                }

                foreach (int column in wide)
                {
                    room -= natural[column];
                    open.Remove(column);
                }
            }

            foreach (int column in open)
            {
                widths[last[column]] += room / open.Count - natural[column];
            }

            return widths;
        }
    }

    // Asks for no width while Overhangs, so its child, laid out at full width, runs over the cell after it, as the traffic
    // overview's Requests label runs over the requests line. Otherwise it asks for its child's width like any decorator.
    public sealed class OverhangDecorator : Decorator
    {
        public static readonly DependencyProperty OverhangsProperty = DependencyProperty.Register(
            nameof(Overhangs), typeof(bool), typeof(OverhangDecorator),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public bool Overhangs
        {
            get => (bool)GetValue(OverhangsProperty);
            set => SetValue(OverhangsProperty, value);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child == null)
            {
                return default;
            }

            Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
            return new Size(Overhangs ? 0 : Child.DesiredSize.Width, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(0, 0, Math.Max(arrangeSize.Width, Child.DesiredSize.Width), arrangeSize.Height));
            return arrangeSize;
        }
    }
}
