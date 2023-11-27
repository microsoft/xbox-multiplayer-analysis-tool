// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    internal class BooleanValueToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            bool result;

            if (value is int)
            {
                result = (int)value != 0;
            }
            else if (value is bool)
            {
                result = (bool)value;
            }
            else
            {
                throw new ArgumentException("BooleanValueToColorConverter can only convert from int or bool");
            }

            return result ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("BooleanValueToColorConverter.ConvertBack() not implemented");
        }
    }
}
