// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows.Data;
using XMAT.NetworkTrace.Models;

namespace XMAT.NetworkTrace
{
    internal class PacketFlagsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is not NetworkPacketFlags model)
            {
                throw new ArgumentException("PacketFlagsConverter received a value that wasn't a NetworkPacketFlags");
            }

            if (parameter is not NetworkPacketFlags)
            {
                throw new ArgumentException("Parameter was not of type NetworkPacketFlags");
            }

            if ((model & (NetworkPacketFlags) parameter) != 0)
            {
                return "\u2713"; // check-mark
            }

            return String.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("PacketFlagsConverter.ConvertBack() not implemented");
        }
    }
}
