// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace XMAT
{
    // The soft shadow under a menu in the new look. It is drawn behind the menu's surface, so the menu's text is drawn
    // without an effect. It is deeper under a dark surface, and High Contrast has none: there the system colors set the
    // menu apart.
    public sealed class MenuShadow : Border
    {
        public static readonly DependencyProperty IsHighContrastProperty = DependencyProperty.Register(
            nameof(IsHighContrast), typeof(bool), typeof(MenuShadow), new PropertyMetadata(false));

        // 0 8 24 in black: 14% under a light surface and 50% under a dark one.
        private static readonly DropShadowEffect LightShadow = Shadow(0.14);
        private static readonly DropShadowEffect DarkShadow = Shadow(0.5);

        public MenuShadow()
        {
            IsHitTestVisible = false;
            SetResourceReference(IsHighContrastProperty, SystemParameters.HighContrastKey);
            UpdateEffect();
        }

        // Follows Windows high contrast.
        public bool IsHighContrast
        {
            get => (bool)GetValue(IsHighContrastProperty);
            set => SetValue(IsHighContrastProperty, value);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsHighContrastProperty || e.Property == BackgroundProperty)
            {
                UpdateEffect();
            }
        }

        internal static Effect ShadowFor(Brush surface, bool highContrast) =>
            highContrast ? null : IsDark(surface) ? DarkShadow : LightShadow;

        private void UpdateEffect() => Effect = ShadowFor(Background, IsHighContrast);

        private static bool IsDark(Brush surface) =>
            surface is SolidColorBrush { Color: var color } && ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) < 128;

        private static DropShadowEffect Shadow(double opacity)
        {
            var effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = 8,
                BlurRadius = 24,
                Opacity = opacity,
            };
            effect.Freeze();
            return effect;
        }
    }
}
