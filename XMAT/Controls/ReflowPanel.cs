// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Lays its children out side by side, or one below another when the panel is too narrow: narrower than StackBelow,
    // or, when StackBelow is NaN, too narrow for the children's natural widths. Side by side, a child with a Weight
    // shares the width that the other children leave, in proportion to its weight. Stacked, StackedHeight sizes a child
    // like a grid row (Auto, pixels or star), and StackedAlignment can replace its horizontal alignment, for example to
    // move details that sit at the right of a heading to the left once they wrap below it.
    public sealed class ReflowPanel : Panel
    {
        private const double Epsilon = 0.01;

        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing), typeof(double), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty StackedSpacingProperty = DependencyProperty.Register(
            nameof(StackedSpacing), typeof(double), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty StackBelowProperty = DependencyProperty.Register(
            nameof(StackBelow), typeof(double), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private static readonly DependencyPropertyKey IsStackedPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsStacked), typeof(bool), typeof(ReflowPanel), new FrameworkPropertyMetadata(false));

        public static readonly DependencyProperty IsStackedProperty = IsStackedPropertyKey.DependencyProperty;

        public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
            "Weight", typeof(double), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure), IsValidWeight);

        public static readonly DependencyProperty StackedHeightProperty = DependencyProperty.RegisterAttached(
            "StackedHeight", typeof(GridLength), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(GridLength.Auto, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty StackedAlignmentProperty = DependencyProperty.RegisterAttached(
            "StackedAlignment", typeof(HorizontalAlignment?), typeof(ReflowPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        // The gap between children side by side.
        public double Spacing
        {
            get => (double)GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        // The gap between stacked children.
        public double StackedSpacing
        {
            get => (double)GetValue(StackedSpacingProperty);
            set => SetValue(StackedSpacingProperty, value);
        }

        public double StackBelow
        {
            get => (double)GetValue(StackBelowProperty);
            set => SetValue(StackBelowProperty, value);
        }

        public bool IsStacked => (bool)GetValue(IsStackedProperty);

        public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);

        public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

        public static GridLength GetStackedHeight(DependencyObject element) =>
            (GridLength)element.GetValue(StackedHeightProperty);

        public static void SetStackedHeight(DependencyObject element, GridLength value) =>
            element.SetValue(StackedHeightProperty, value);

        public static HorizontalAlignment? GetStackedAlignment(DependencyObject element) =>
            (HorizontalAlignment?)element.GetValue(StackedAlignmentProperty);

        public static void SetStackedAlignment(DependencyObject element, HorizontalAlignment? value) =>
            element.SetValue(StackedAlignmentProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            List<UIElement> children = VisibleChildren();
            if (children.Count == 0)
            {
                SetValue(IsStackedPropertyKey, false);
                return new Size();
            }

            bool stacked;
            if (double.IsNaN(StackBelow))
            {
                double natural = Spacing * (children.Count - 1);
                foreach (UIElement child in children)
                {
                    child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    natural += child.DesiredSize.Width;
                }

                stacked = natural > availableSize.Width + Epsilon;
            }
            else
            {
                stacked = availableSize.Width < StackBelow;
            }

            SetValue(IsStackedPropertyKey, stacked);
            return stacked ? MeasureStacked(children, availableSize) : MeasureSideBySide(children, availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            List<UIElement> children = VisibleChildren();
            if (IsStacked)
            {
                ArrangeStacked(children, finalSize);
            }
            else
            {
                ArrangeSideBySide(children, finalSize);
            }

            return finalSize;
        }

        private static bool IsValidWeight(object value) => value is double weight && weight >= 0 && !double.IsInfinity(weight);

        private Size MeasureSideBySide(List<UIElement> children, Size availableSize)
        {
            double fixedWidth = Spacing * (children.Count - 1);
            double totalWeight = 0;
            foreach (UIElement child in children)
            {
                double weight = GetWeight(child);
                if (weight > 0)
                {
                    totalWeight += weight;
                }
                else
                {
                    child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    fixedWidth += child.DesiredSize.Width;
                }
            }

            double remaining = Math.Max(0, availableSize.Width - fixedWidth);
            double width = fixedWidth;
            double height = 0;
            foreach (UIElement child in children)
            {
                double weight = GetWeight(child);
                if (weight > 0)
                {
                    double share = double.IsInfinity(remaining) ? double.PositiveInfinity : remaining * weight / totalWeight;
                    child.Measure(new Size(share, availableSize.Height));
                    width += child.DesiredSize.Width;
                }

                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(width, height);
        }

        private Size MeasureStacked(List<UIElement> children, Size availableSize)
        {
            double fixedHeight = StackedSpacing * (children.Count - 1);
            double totalStars = 0;
            double width = 0;
            foreach (UIElement child in children)
            {
                GridLength height = GetStackedHeight(child);
                if (height.IsStar)
                {
                    totalStars += height.Value;
                    continue;
                }

                child.Measure(new Size(availableSize.Width, height.IsAbsolute ? height.Value : double.PositiveInfinity));
                fixedHeight += height.IsAbsolute ? height.Value : child.DesiredSize.Height;
                width = Math.Max(width, child.DesiredSize.Width);
            }

            double remaining = Math.Max(0, availableSize.Height - fixedHeight);
            double desiredHeight = fixedHeight;
            foreach (UIElement child in children)
            {
                GridLength height = GetStackedHeight(child);
                if (!height.IsStar)
                {
                    continue;
                }

                double share = double.IsInfinity(remaining) ? double.PositiveInfinity : remaining * height.Value / totalStars;
                child.Measure(new Size(availableSize.Width, share));
                desiredHeight += child.DesiredSize.Height;
                width = Math.Max(width, child.DesiredSize.Width);
            }

            return new Size(width, desiredHeight);
        }

        private void ArrangeSideBySide(List<UIElement> children, Size finalSize)
        {
            double fixedWidth = Spacing * (children.Count - 1);
            double totalWeight = 0;
            foreach (UIElement child in children)
            {
                double weight = GetWeight(child);
                if (weight > 0)
                {
                    totalWeight += weight;
                }
                else
                {
                    fixedWidth += child.DesiredSize.Width;
                }
            }

            double remaining = Math.Max(0, finalSize.Width - fixedWidth);
            double x = 0;
            foreach (UIElement child in children)
            {
                double weight = GetWeight(child);
                double width = weight > 0 ? remaining * weight / totalWeight : child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width + Spacing;
            }
        }

        private void ArrangeStacked(List<UIElement> children, Size finalSize)
        {
            double fixedHeight = StackedSpacing * (children.Count - 1);
            double totalStars = 0;
            foreach (UIElement child in children)
            {
                GridLength height = GetStackedHeight(child);
                if (height.IsStar)
                {
                    totalStars += height.Value;
                }
                else
                {
                    fixedHeight += height.IsAbsolute ? height.Value : child.DesiredSize.Height;
                }
            }

            double remaining = Math.Max(0, finalSize.Height - fixedHeight);
            double y = 0;
            foreach (UIElement child in children)
            {
                GridLength length = GetStackedHeight(child);
                double height = length.IsStar ? remaining * length.Value / totalStars
                    : length.IsAbsolute ? length.Value
                    : child.DesiredSize.Height;
                child.Arrange(StackedSlot(child, y, height, finalSize.Width));
                y += height + StackedSpacing;
            }
        }

        private static Rect StackedSlot(UIElement child, double y, double height, double panelWidth)
        {
            HorizontalAlignment? alignment = GetStackedAlignment(child);
            if (alignment is null or HorizontalAlignment.Stretch)
            {
                return new Rect(0, y, panelWidth, height);
            }

            double width = Math.Min(child.DesiredSize.Width, panelWidth);
            double x = alignment switch
            {
                HorizontalAlignment.Right => panelWidth - width,
                HorizontalAlignment.Center => (panelWidth - width) / 2,
                _ => 0,
            };
            return new Rect(x, y, width, height);
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
