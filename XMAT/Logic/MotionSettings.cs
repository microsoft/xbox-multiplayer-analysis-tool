// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;

namespace XMAT
{
    // Whether XMAT animates. It follows the Windows "Animation effects" setting, which WPF reads as
    // SystemParameters.ClientAreaAnimation, and raises PropertyChanged when it changes, so XAML can bind to
    // MotionSettings.Current.AnimationsEnabled. Animations started from code check it before they begin and stop, showing
    // their end state, when it turns off.
    public sealed class MotionSettings : INotifyPropertyChanged
    {
        private bool? _override;
        private bool _animationsEnabled = SystemParameters.ClientAreaAnimation;

        private MotionSettings()
        {
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                {
                    Update();
                }
            };
        }

        public static MotionSettings Current { get; } = new();

        public event PropertyChangedEventHandler PropertyChanged;

        public bool AnimationsEnabled => _animationsEnabled;

        // Takes the place of the Windows setting, for tests; null follows the setting again.
        internal bool? Override
        {
            get => _override;
            set
            {
                _override = value;
                Update();
            }
        }

        private void Update()
        {
            bool enabled = _override ?? SystemParameters.ClientAreaAnimation;
            if (enabled != _animationsEnabled)
            {
                _animationsEnabled = enabled;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnimationsEnabled)));
            }
        }
    }
}
