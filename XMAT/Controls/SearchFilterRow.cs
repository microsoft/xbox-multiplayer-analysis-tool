// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Lays out a search box and, after it, a row of filters in a FilterOverflowHost. The filters keep their full width
    // while that leaves the search box at least SearchMinWidth; the search box takes the rest, up to SearchMaxWidth. When
    // the filters don't fit beside the narrowest search box, they fold into their Filters button and the search box takes
    // the room they gave up. A collapsed search box leaves the filters the whole row.
    public sealed class SearchFilterRow : Panel
    {
        public static readonly DependencyProperty SearchMinWidthProperty = DependencyProperty.Register(
            nameof(SearchMinWidth), typeof(double), typeof(SearchFilterRow),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SearchMaxWidthProperty = DependencyProperty.Register(
            nameof(SearchMaxWidth), typeof(double), typeof(SearchFilterRow),
            new FrameworkPropertyMetadata(360.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(SearchFilterRow),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private double _searchWidth;

        public double SearchMinWidth
        {
            get => (double)GetValue(SearchMinWidthProperty);
            set => SetValue(SearchMinWidthProperty, value);
        }

        public double SearchMaxWidth
        {
            get => (double)GetValue(SearchMaxWidthProperty);
            set => SetValue(SearchMaxWidthProperty, value);
        }

        // The room between the search box and the filters.
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        private UIElement Search => InternalChildren.Count > 0 ? InternalChildren[0] : null;

        private FilterOverflowHost Filters => InternalChildren.Count > 1 ? InternalChildren[1] as FilterOverflowHost : null;

        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement search = Search;
            FilterOverflowHost filters = Filters;
            if (search == null || filters == null || search.Visibility == Visibility.Collapsed)
            {
                _searchWidth = 0;
                search?.Measure(new Size(0, availableSize.Height));
                filters?.Measure(availableSize);
                return filters?.DesiredSize ?? default;
            }

            double width = availableSize.Width;
            if (double.IsInfinity(width))
            {
                // Unbounded, the filters take their full width and the search box its widest.
                filters.Measure(availableSize);
                _searchWidth = SearchMaxWidth;
            }
            else
            {
                // Measured beside the narrowest search box, the filters fold if they must. Unfolded, they need no more
                // than their natural width, so the search box can grow into the rest; folded, it grows into the room the
                // row gave up. Either way they're measured again in the room that's left, which doesn't change whether
                // they fold.
                filters.Measure(new Size(Math.Max(0, width - SearchMinWidth - Gap), availableSize.Height));
                double filtersWidth = filters.IsOverflowing ? filters.DesiredSize.Width : filters.NaturalWidth;
                _searchWidth = Math.Clamp(width - filtersWidth - Gap, Math.Min(SearchMinWidth, width), Math.Max(SearchMinWidth, SearchMaxWidth));
                filters.Measure(new Size(Math.Max(0, width - _searchWidth - Gap), availableSize.Height));
            }

            search.Measure(new Size(_searchWidth, availableSize.Height));
            return new Size(
                _searchWidth + Gap + filters.DesiredSize.Width,
                Math.Max(search.DesiredSize.Height, filters.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            UIElement search = Search;
            FilterOverflowHost filters = Filters;
            if (search == null || filters == null || search.Visibility == Visibility.Collapsed)
            {
                search?.Arrange(new Rect(0, 0, 0, 0));
                filters?.Arrange(new Rect(finalSize));
                return finalSize;
            }

            double searchWidth = Math.Min(_searchWidth, finalSize.Width);
            search.Arrange(new Rect(0, 0, searchWidth, search.DesiredSize.Height));
            double filtersLeft = Math.Min(finalSize.Width, searchWidth + Gap);
            filters.Arrange(new Rect(filtersLeft, 0, finalSize.Width - filtersLeft, finalSize.Height));
            return finalSize;
        }
    }
}
