// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XMAT
{
    public class ColorToBrushConverter : IValueConverter
    {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var color = (System.Drawing.Color)value;
            if(value == null)
                throw new InvalidOperationException("Value must be a Color");
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
