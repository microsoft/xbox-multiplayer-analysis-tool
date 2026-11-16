// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace XMAT
{
    // Lays its children out in rows of equal columns: as many columns as fit at ColumnMinWidth with ColumnSpacing
    // between them, filled from the left. A row is as tall as its tallest child, and rows are RowSpacing apart. The
    // panel takes the whole width it is offered, so in a host that is only as wide as its content it still fills its
    // pane, and two panels given the same width line their columns up. ColumnMinWidth is at the base text size, 12, and
    // grows with the text size.
    public sealed class ColumnFlowPanel : Panel
    {
        public static readonly DependencyProperty ColumnMinWidthProperty = DependencyProperty.Register(
            nameof(ColumnMinWidth), typeof(double), typeof(ColumnFlowPanel),
            new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidLength);

        public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
            nameof(ColumnSpacing), typeof(double), typeof(ColumnFlowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidLength);

        public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
            nameof(RowSpacing), typeof(double), typeof(ColumnFlowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsValidLength);

        private const double UnscaledFontSize = 12;

        static ColumnFlowPanel()
        {
            // The column width follows the text size, which the panel inherits.
            TextElement.FontSizeProperty.OverrideMetadata(typeof(ColumnFlowPanel), new FrameworkPropertyMetadata(
                SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
        }

        public double ColumnMinWidth
        {
            get => (double)GetValue(ColumnMinWidthProperty);
            set => SetValue(ColumnMinWidthProperty, value);
        }

        public double ColumnSpacing
        {
            get => (double)GetValue(ColumnSpacingProperty);
            set => SetValue(ColumnSpacingProperty, value);
        }

        public double RowSpacing
        {
            get => (double)GetValue(RowSpacingProperty);
            set => SetValue(RowSpacingProperty, value);
        }

        // The number of columns that fit WIDTH, at least one.
        internal static int ColumnsFor(double width, double minWidth, double spacing) =>
            double.IsFinite(width) ? Math.Max(1, (int)Math.Floor((width + spacing) / (minWidth + spacing))) : 1;

        protected override Size MeasureOverride(Size availableSize)
        {
            (int columns, double columnWidth) = Columns(availableSize.Width);
            List<UIElement> children = VisibleChildren();
            double height = 0;
            for (int start = 0; start < children.Count; start += columns)
            {
                double rowHeight = 0;
                for (int index = start; index < Math.Min(start + columns, children.Count); index++)
                {
                    children[index].Measure(new Size(columnWidth, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, children[index].DesiredSize.Height);
                }

                height += rowHeight + (start > 0 ? RowSpacing : 0);
            }

            double width = double.IsFinite(availableSize.Width)
                ? availableSize.Width
                : (columns * columnWidth) + ((columns - 1) * ColumnSpacing);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            (int columns, double columnWidth) = Columns(finalSize.Width);
            List<UIElement> children = VisibleChildren();
            double y = 0;
            for (int start = 0; start < children.Count; start += columns)
            {
                int end = Math.Min(start + columns, children.Count);
                double rowHeight = 0;
                for (int index = start; index < end; index++)
                {
                    rowHeight = Math.Max(rowHeight, children[index].DesiredSize.Height);
                }

                for (int index = start; index < end; index++)
                {
                    double x = (index - start) * (columnWidth + ColumnSpacing);
                    children[index].Arrange(new Rect(x, y, columnWidth, rowHeight));
                }

                y += rowHeight + RowSpacing;
            }

            return finalSize;
        }

        private static bool IsValidLength(object value) => value is double length && length >= 0 && double.IsFinite(length);

        // The columns that fit WIDTH and their width; offered unlimited width, one column at its least width.
        private (int Columns, double Width) Columns(double width)
        {
            double fontSize = TextElement.GetFontSize(this);
            double minWidth = ColumnMinWidth * (fontSize > 0 ? fontSize / UnscaledFontSize : 1);
            int columns = ColumnsFor(width, minWidth, ColumnSpacing);
            double columnWidth = double.IsFinite(width)
                ? Math.Max(0, (width - ((columns - 1) * ColumnSpacing)) / columns)
                : minWidth;
            return (columns, columnWidth);
        }

        private List<UIElement> VisibleChildren()
        {
            var children = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
            {
                if (child != null && child.Visibility != Visibility.Collapsed)
                {
                    children.Add(child);
                }
            }

            return children;
        }
    }
}
