// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XMAT
{
    // A rounded placeholder bar for content that is on its way; its style is in Styles/Motion.xaml. Each time the block
    // is shown, a soft band a third of its width sweeps across it twice and the bar then rests in its plain color, since
    // motion that starts by itself must stop within 5 seconds (WCAG 2.2.2). The sweep runs only while the block is loaded
    // and visible, Windows animation effects are on and high contrast is off. It starts from code because a template
    // storyboard can't take the theme's brushes or the block's width. Screen readers skip the block; whatever contains
    // it says what is loading.
    [TemplatePart(Name = SheenPartName, Type = typeof(FrameworkElement))]
    public sealed class SkeletonBlock : Control
    {
        private const string SheenPartName = "PART_Sheen";
        private const int SweepCount = 2;
        private static readonly Duration SweepDuration = new(TimeSpan.FromSeconds(2));

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(SkeletonBlock), new PropertyMetadata(4.0));

        private static readonly DependencyPropertyKey IsSweepingPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsSweeping), typeof(bool), typeof(SkeletonBlock), new PropertyMetadata(false));

        public static readonly DependencyProperty IsSweepingProperty = IsSweepingPropertyKey.DependencyProperty;

        private readonly TranslateTransform _sheenOffset = new();
        private FrameworkElement _sheen;
        private AnimationClock _sweep;
        private bool _isLoaded;
        // Set once the block has swept since it was last shown, so that it sweeps once each time it is shown.
        private bool _hasSwept;

        static SkeletonBlock()
        {
            FocusableProperty.OverrideMetadata(typeof(SkeletonBlock), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(SkeletonBlock), new FrameworkPropertyMetadata(false));
        }

        public SkeletonBlock()
        {
            Loaded += (_, _) =>
            {
                _isLoaded = true;
                UpdateSweep();
            };
            Unloaded += (_, _) =>
            {
                _isLoaded = false;
                UpdateSweep();
            };
            IsVisibleChanged += (_, _) => UpdateSweep();
            // A weak handler, so the shared settings don't keep blocks alive.
            PropertyChangedEventManager.AddHandler(
                MotionSettings.Current, MotionSettings_PropertyChanged, nameof(MotionSettings.AnimationsEnabled));
        }

        // In pixels; half the height makes a pill.
        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        // Whether the band is sweeping across the block now.
        public bool IsSweeping => (bool)GetValue(IsSweepingProperty);

        public override void OnApplyTemplate()
        {
            StopSweep();
            base.OnApplyTemplate();
            _sheen = GetTemplateChild(SheenPartName) as FrameworkElement;
            if (_sheen != null)
            {
                _sheen.RenderTransform = _sheenOffset;
            }

            UpdateSweep();
        }

        // A block that was shown before its first layout starts its sweep once it has a width.
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateSweep();
        }

        private void MotionSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) => UpdateSweep();

        private void UpdateSweep()
        {
            if (!_isLoaded || !IsVisible)
            {
                // Collapsed and removed elements keep animating unless their animations are stopped.
                StopSweep();
                _hasSwept = false;
            }
            else if (!MotionSettings.Current.AnimationsEnabled || SystemParameters.HighContrast)
            {
                // Also when animation effects are turned off mid-sweep: the bar rests in its plain color.
                StopSweep();
            }
            else if (_sweep == null && !_hasSwept && _sheen != null && ActualWidth > 0 &&
                     TryFindResource("MotionCurveEasyEase") is KeySpline curve)
            {
                StartSweep(curve);
            }
        }

        private void StartSweep(KeySpline curve)
        {
            double width = ActualWidth;
            double band = width / 3;
            _sheen.Width = band;
            _sheenOffset.X = -band;

            // From just off the left edge to just off the right edge, easing in and out.
            var sweep = new DoubleAnimationUsingKeyFrames
            {
                Duration = SweepDuration,
                RepeatBehavior = new RepeatBehavior(SweepCount),
                FillBehavior = FillBehavior.Stop,
            };
            sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-band, KeyTime.FromPercent(0)));
            sweep.KeyFrames.Add(new SplineDoubleKeyFrame(width, KeyTime.FromPercent(1), curve));
            // A soft band needs no more than 30 frames a second.
            Timeline.SetDesiredFrameRate(sweep, 30);

            _sweep = sweep.CreateClock();
            _sweep.Completed += Sweep_Completed;
            _hasSwept = true;
            _sheenOffset.ApplyAnimationClock(TranslateTransform.XProperty, _sweep);
            SetValue(IsSweepingPropertyKey, true);
        }

        private void Sweep_Completed(object sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _sweep))
            {
                StopSweep();
            }
        }

        private void StopSweep()
        {
            if (_sweep != null)
            {
                AnimationClock sweep = _sweep;
                _sweep = null;
                // Stops the clock, which would otherwise tick on until it is collected, and lets go of the sheen now.
                sweep.Controller.Stop();
                _sheenOffset.BeginAnimation(TranslateTransform.XProperty, null);
            }

            SetValue(IsSweepingPropertyKey, false);
        }
    }
}
