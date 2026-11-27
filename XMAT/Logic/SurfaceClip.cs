// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Media;

namespace XMAT
{
    // Clips an element to a rounded rectangle that follows its size, for surfaces such as data tables whose own parts
    // (headers, rows) would otherwise draw square corners over a rounded background. Zero removes the clip.
    public static class SurfaceClip
    {
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(double), typeof(SurfaceClip), new PropertyMetadata(0.0, OnCornerRadiusChanged));

        public static double GetCornerRadius(DependencyObject element) => (double)element.GetValue(CornerRadiusProperty);

        public static void SetCornerRadius(DependencyObject element, double value) =>
            element.SetValue(CornerRadiusProperty, value);

        private static void OnCornerRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            element.SizeChanged -= Element_SizeChanged;
            if ((double)e.NewValue > 0)
            {
                element.SizeChanged += Element_SizeChanged;
                Update(element);
            }
            else
            {
                element.ClearValue(UIElement.ClipProperty);
            }
        }

        private static void Element_SizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

        private static void Update(FrameworkElement element)
        {
            double radius = GetCornerRadius(element);
            if (radius <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
            clip.Freeze();
            element.Clip = clip;
        }
    }
}
