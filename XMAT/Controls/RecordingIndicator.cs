// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // Shows that a capture is running, beside its Stop button: a red dot and the word Capturing, which screen readers
    // announce when the capture starts. The dot pulses unless Windows animation effects are off.
    public sealed class RecordingIndicator : Control
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(RecordingIndicator),
            new PropertyMetadata(false, (indicator, _) => ((RecordingIndicator)indicator).UpdatePulse()));

        private static readonly DependencyPropertyKey IsPulsingPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsPulsing), typeof(bool), typeof(RecordingIndicator), new PropertyMetadata(false));

        public static readonly DependencyProperty IsPulsingProperty = IsPulsingPropertyKey.DependencyProperty;

        public RecordingIndicator()
        {
            Loaded += (_, _) =>
            {
                SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
                UpdatePulse();
            };
            Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        }

        public static string CapturingText => Localization.GetLocalizedString("CAPTURE_CAPTURING");

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        // Whether the dot pulses: while the capture runs, unless Windows animation effects are off.
        public bool IsPulsing => (bool)GetValue(IsPulsingProperty);

        private void SystemParameters_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            {
                UpdatePulse();
            }
        }

        private void UpdatePulse() => SetValue(IsPulsingPropertyKey, IsActive && SystemParameters.ClientAreaAnimation);
    }
}
