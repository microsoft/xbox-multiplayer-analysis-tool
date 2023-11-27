// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows.Data;
using XMAT.NetworkTrace.Models;

namespace XMAT.NetworkTrace
{
    internal class NetworkProtocolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (Enum.IsDefined(typeof(NetworkProtocol), value))
            {
                return ((NetworkProtocol)value).ToString();
            }

            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("PacketFlagsConverter.ConvertBack() not implemented");
        }
    }
}
