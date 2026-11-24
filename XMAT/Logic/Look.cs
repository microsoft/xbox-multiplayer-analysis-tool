// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace XMAT
{
    // Whether the application shows the new visual design or the original one. Shared XAML reads it through
    // LookExtension, so switching updates open views without reloading them.
    public sealed class LookState : INotifyPropertyChanged
    {
        private bool _isNewLook = true;

        public static LookState Current { get; } = new();

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsNewLook
        {
            get => _isNewLook;
            set
            {
                if (_isNewLook != value)
                {
                    _isNewLook = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNewLook)));
                }
            }
        }
    }

    // Chooses a property value for the new or the original look, for example
    // Margin="{app:Look New='0,0,12,0', Original='20,0'}". Text values are converted to the property's type, and the
    // value follows LookState when the look changes. Use it on element properties, not on Setter values; resources
    // that differ by look are defined under the same key in Looks/NewLook.xaml and Looks/OriginalLook.xaml instead.
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LookExtension : MarkupExtension
    {
        public object New { get; set; }

        public object Original { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            Type type = target?.TargetProperty switch
            {
                DependencyProperty property => property.PropertyType,
                PropertyInfo property => property.PropertyType,
                _ => typeof(object)
            };

            var binding = new Binding(nameof(LookState.IsNewLook))
            {
                Source = LookState.Current,
                Mode = BindingMode.OneWay,
                Converter = new LookValueConverter(ToType(New, type), ToType(Original, type))
            };
            return binding.ProvideValue(serviceProvider);
        }

        internal static object ToType(object value, Type type)
        {
            if (value is not string text || type == typeof(string) || type == typeof(object))
            {
                return value;
            }

            if (type == typeof(double) && string.Equals(text.Trim(), "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return double.NaN;
            }

            return TypeDescriptor.GetConverter(type).ConvertFromInvariantString(text);
        }

        private sealed class LookValueConverter : IValueConverter
        {
            private readonly object _newValue;
            private readonly object _originalValue;

            internal LookValueConverter(object newValue, object originalValue)
            {
                _newValue = newValue;
                _originalValue = originalValue;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                value is false ? _originalValue : _newValue;

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
