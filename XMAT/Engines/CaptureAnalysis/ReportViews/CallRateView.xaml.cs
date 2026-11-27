// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls;

namespace XMAT.XboxLiveCaptureAnalysis
{
    // The Per Endpoint report's call frequency section (CallRateReport): each endpoint's call rate against its rule's
    // limits, in the burst or the sustained window.
    public partial class CallRateView : UserControl
    {
        // The table fills the view's width. Calls and Over limit are fixed; the Call rate column takes the rest, but
        // no less than MinCallRateWidth. The Endpoint column keeps EndpointWidth until the Call rate column is down to
        // that minimum, then gives way to MinEndpointWidth, its names trimmed with an ellipsis.
        internal const double EndpointWidth = 190;
        internal const double MinEndpointWidth = 160;

        public static readonly DependencyProperty EndpointColumnWidthProperty = DependencyProperty.Register(
            nameof(EndpointColumnWidth), typeof(GridLength), typeof(CallRateView),
            new PropertyMetadata(new GridLength(EndpointWidth)));

        public static string Title => Localization.GetLocalizedString("CALL_RATE_TITLE");
        public static string TableName => Localization.GetLocalizedString("CALL_RATE_TABLE");
        public static string WindowName => Localization.GetLocalizedString("CALL_RATE_WINDOW");
        public static string EndpointHeader => Localization.GetLocalizedString("CALL_RATE_HEADER_ENDPOINT");
        public static string CallsHeader => Localization.GetLocalizedString("CALL_RATE_HEADER_CALLS");
        public static string OverLimitHeader => Localization.GetLocalizedString("CALL_RATE_HEADER_OVER_LIMIT");
        public static string CallRateHeader => Localization.GetLocalizedString("CALL_RATE_HEADER_CALL_RATE");

        public static GridLength CallsColumnWidth { get; } = new(56);
        public static GridLength OverLimitColumnWidth { get; } = new(90);
        public static double MinCallRateWidth => 200;

        public CallRateView()
        {
            InitializeComponent();
            AddHandler(CallRateChart.HoverChangedEvent, new RoutedEventHandler(OnChartHoverChanged));
        }

        public GridLength EndpointColumnWidth
        {
            get => (GridLength)GetValue(EndpointColumnWidthProperty);
            private set => SetValue(EndpointColumnWidthProperty, value);
        }

        // The Endpoint column's width in a table WIDTH wide.
        internal static double EndpointWidthFor(double width) =>
            double.IsFinite(width)
                ? Math.Clamp(
                    width - CallsColumnWidth.Value - OverLimitColumnWidth.Value - MinCallRateWidth,
                    MinEndpointWidth,
                    EndpointWidth)
                : EndpointWidth;

        // The section has no horizontal margin, so the table is offered the view's whole width; its columns are set
        // before the table measures them.
        protected override Size MeasureOverride(Size constraint)
        {
            EndpointColumnWidth = new GridLength(EndpointWidthFor(constraint.Width));
            return base.MeasureOverride(constraint);
        }

        // Shows the hovered sample's readout below its chart, centred on the hairline and kept within the table.
        private void OnChartHoverChanged(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not CallRateChart { HoverSample: >= 0 } chart || chart.DataContext is not CallRateRow row)
            {
                Tip.Visibility = Visibility.Collapsed;
                return;
            }

            TipText.Text = row.Tip(chart.HoverSample);
            Tip.Visibility = Visibility.Visible;
            Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = Tip.DesiredSize.Width;
            Point below = chart.TranslatePoint(new Point(chart.HoverX, chart.PlotHeight + 2), TipLayer);
            Canvas.SetLeft(Tip, Math.Round(Math.Max(0, Math.Min(below.X - width / 2, TipLayer.ActualWidth - width))));
            Canvas.SetTop(Tip, Math.Round(below.Y));
        }
    }
}
