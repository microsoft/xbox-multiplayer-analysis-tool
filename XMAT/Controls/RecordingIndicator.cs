// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Shows that a capture is running, beside its Stop button: a red dot and the word Capturing, which screen readers
    // announce when the capture starts. The dot pulses unless animations are off (MotionSettings).
    public sealed class RecordingIndicator : Control
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(RecordingIndicator),
            new PropertyMetadata(false, (indicator, _) => ((RecordingIndicator)indicator).UpdatePulse()));

        private static readonly DependencyPropertyKey IsPulsingPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsPulsing), typeof(bool), typeof(RecordingIndicator), new PropertyMetadata(false));

        public static readonly DependencyProperty IsPulsingProperty = IsPulsingPropertyKey.DependencyProperty;

        public RecordingIndicator() =>
            // A weak handler, so the shared settings don't keep indicators alive.
            PropertyChangedEventManager.AddHandler(
                MotionSettings.Current, MotionSettings_PropertyChanged, nameof(MotionSettings.AnimationsEnabled));

        public static string CapturingText => Localization.GetLocalizedString("CAPTURE_CAPTURING");

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        // Whether the dot pulses: while the capture runs, unless animations are off.
        public bool IsPulsing => (bool)GetValue(IsPulsingProperty);

        private void MotionSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) => UpdatePulse();

        private void UpdatePulse() =>
            SetValue(IsPulsingPropertyKey, IsActive && MotionSettings.Current.AnimationsEnabled);
    }
}
