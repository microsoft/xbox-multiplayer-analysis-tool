// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace XMAT
{
    // Sets a tab header's type size and weight for whether its tab is selected, and always takes the room that the
    // larger of the two needs, so selecting a tab never resizes it or moves the tabs beside it. A size of NaN keeps the
    // size that the header would otherwise inherit.
    public sealed class TabHeaderSizer : Decorator
    {
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(TabHeaderSizer),
            new FrameworkPropertyMetadata(false, OnTypographyChanged));

        public static readonly DependencyProperty NormalFontSizeProperty = DependencyProperty.Register(
            nameof(NormalFontSize), typeof(double), typeof(TabHeaderSizer),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTypographyChanged));

        public static readonly DependencyProperty NormalFontWeightProperty = DependencyProperty.Register(
            nameof(NormalFontWeight), typeof(FontWeight), typeof(TabHeaderSizer),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTypographyChanged));

        public static readonly DependencyProperty SelectedFontSizeProperty = DependencyProperty.Register(
            nameof(SelectedFontSize), typeof(double), typeof(TabHeaderSizer),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTypographyChanged));

        public static readonly DependencyProperty SelectedFontWeightProperty = DependencyProperty.Register(
            nameof(SelectedFontWeight), typeof(FontWeight), typeof(TabHeaderSizer),
            new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTypographyChanged));

        public TabHeaderSizer() => ApplyTypography(false);

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public double NormalFontSize
        {
            get => (double)GetValue(NormalFontSizeProperty);
            set => SetValue(NormalFontSizeProperty, value);
        }

        public FontWeight NormalFontWeight
        {
            get => (FontWeight)GetValue(NormalFontWeightProperty);
            set => SetValue(NormalFontWeightProperty, value);
        }

        public double SelectedFontSize
        {
            get => (double)GetValue(SelectedFontSizeProperty);
            set => SetValue(SelectedFontSizeProperty, value);
        }

        public FontWeight SelectedFontWeight
        {
            get => (FontWeight)GetValue(SelectedFontWeightProperty);
            set => SetValue(SelectedFontWeightProperty, value);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            UIElement child = Child;
            if (child == null)
            {
                return new Size();
            }

            // Measure the header as it would look in the other state, then as it looks now, so it ends up laid out for
            // what it shows. Changes made while this element measures don't ask it to measure again.
            var other = new Size();
            if (!HasSameTypography())
            {
                ApplyTypography(!IsSelected);
                child.InvalidateMeasure();
                child.Measure(constraint);
                other = child.DesiredSize;
                ApplyTypography(IsSelected);
                child.InvalidateMeasure();
            }

            child.Measure(constraint);
            Size current = child.DesiredSize;
            return new Size(Math.Max(current.Width, other.Width), Math.Max(current.Height, other.Height));
        }

        private static void OnTypographyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var sizer = (TabHeaderSizer)sender;
            sizer.ApplyTypography(sizer.IsSelected);
        }

        private bool HasSameTypography() =>
            NormalFontWeight == SelectedFontWeight && NormalFontSize.Equals(SelectedFontSize);

        private void ApplyTypography(bool selected)
        {
            double size = selected ? SelectedFontSize : NormalFontSize;
            if (double.IsNaN(size))
            {
                ClearValue(TextElement.FontSizeProperty);
            }
            else
            {
                SetValue(TextElement.FontSizeProperty, size);
            }

            SetValue(TextElement.FontWeightProperty, selected ? SelectedFontWeight : NormalFontWeight);
        }
    }
}
