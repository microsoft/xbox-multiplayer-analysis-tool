// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XMAT
{
    // A titled histogram in a report, such as the network analysis's packets by source: the HistogramModel in its
    // DataContext, drawn by the HistogramView style in Styles/DataViz.xaml with a column summary's lanes and bars. It
    // fills the width it is given. The label lane fits the longest label, between 64 and 200 wide, and the amount and
    // share lanes fit their widest text; the lanes grow with the text size, and keep their widths when Show all
    // lists every value. Histograms in a shared size scope take the widest label and amount lanes among them.
    public sealed class HistogramView : Control
    {
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
            nameof(IsExpanded), typeof(bool), typeof(HistogramView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsExpandedChanged));

        private static readonly DependencyPropertyKey ShownRowsPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(ShownRows), typeof(IReadOnlyList<HistogramRow>), typeof(HistogramView),
            new PropertyMetadata(Array.Empty<HistogramRow>()));

        public static readonly DependencyProperty ShownRowsProperty = ShownRowsPropertyKey.DependencyProperty;

        public static readonly DependencyProperty LabelLaneWidthProperty = DependencyProperty.Register(
            nameof(LabelLaneWidth), typeof(double), typeof(HistogramView), new PropertyMetadata(MinLabelLane));

        public static readonly DependencyProperty AmountLaneWidthProperty = DependencyProperty.Register(
            nameof(AmountLaneWidth), typeof(double), typeof(HistogramView), new PropertyMetadata(MinAmountLane));

        public static readonly DependencyProperty ShareLaneWidthProperty = DependencyProperty.Register(
            nameof(ShareLaneWidth), typeof(double), typeof(HistogramView), new PropertyMetadata(MinShareLane));

        // The sizes at the base text size, 12. A lane's gap is the room before its text.
        private const double UnscaledFontSize = 12;
        private const double MinLabelLane = 64;
        private const double MaxLabelLane = 200;
        private const double LabelGap = 12;
        private const double MinAmountLane = 48;
        private const double AmountGap = 8;
        private const double MinShareLane = 40;
        private const double ShareGap = 4;

        public HistogramView()
        {
            Focusable = false;
            DataContextChanged += (sender, e) => Update();
        }

        // Show all: a row for every value, instead of the largest and the rest.
        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        public IReadOnlyList<HistogramRow> ShownRows => (IReadOnlyList<HistogramRow>)GetValue(ShownRowsProperty);

        public double LabelLaneWidth
        {
            get => (double)GetValue(LabelLaneWidthProperty);
            private set => SetValue(LabelLaneWidthProperty, value);
        }

        public double AmountLaneWidth
        {
            get => (double)GetValue(AmountLaneWidthProperty);
            private set => SetValue(AmountLaneWidthProperty, value);
        }

        // No lane when the amounts don't add up.
        public double ShareLaneWidth
        {
            get => (double)GetValue(ShareLaneWidthProperty);
            private set => SetValue(ShareLaneWidthProperty, value);
        }

        internal HistogramModel Model => DataContext as HistogramModel;

        // The list of rows, once the template is applied.
        internal ItemsControl Rows => GetTemplateChild("PART_Rows") as ItemsControl;

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty)
            {
                UpdateLanes();
            }
        }

        private static void OnIsExpandedChanged(DependencyObject view, DependencyPropertyChangedEventArgs e) =>
            ((HistogramView)view).ShowRows();

        private void Update()
        {
            ShowRows();
            UpdateLanes();
        }

        private void ShowRows() =>
            SetValue(ShownRowsPropertyKey, (IsExpanded ? Model?.AllRows : Model?.Rows) ?? Array.Empty<HistogramRow>());

        private void UpdateLanes()
        {
            double scale = FontSize > 0 ? FontSize / UnscaledFontSize : 1;
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double Widest(IEnumerable<string> texts) => texts
                .Select(text => new FormattedText(
                    text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize, Brushes.Black,
                    pixelsPerDip).WidthIncludingTrailingWhitespace)
                .DefaultIfEmpty(0)
                .Max();

            // Every row, collapsed and expanded, so the lanes stay put when Show all is chosen.
            List<HistogramRow> rows = Model == null ? new List<HistogramRow>() : Model.Rows.Concat(Model.AllRows).ToList();
            LabelLaneWidth = Math.Clamp(
                Widest(rows.Select(row => row.Label)) + (LabelGap * scale), MinLabelLane * scale, MaxLabelLane * scale);
            AmountLaneWidth = Math.Max(MinAmountLane * scale, Widest(rows.Select(row => row.AmountText)) + (AmountGap * scale));
            ShareLaneWidth = rows.Any(row => row.ShareText != null)
                ? Math.Max(MinShareLane * scale, Widest(rows.Select(row => row.ShareText)) + (ShareGap * scale))
                : 0;
        }
    }
}
