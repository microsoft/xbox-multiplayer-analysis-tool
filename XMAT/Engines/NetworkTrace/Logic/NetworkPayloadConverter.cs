// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using XMAT.NetworkTrace.Models;

namespace XMAT.NetworkTrace
{
    internal class NetworkPayloadConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is not NetworkTracePacketDataModel model)
            {
                throw new ArgumentException("NetworkPayloadConverter received a value that wasn't a NetworkTracePacketDataModel");
            }

            var sb = new StringBuilder();
            var dataModel = value as NetworkTracePacketDataModel;

            foreach (var octet in dataModel.Payload)
            {
                sb.Append($"{octet:X02} ");
            }

            sb.Append("\n\n");
            sb.Append(Encoding.UTF8.GetString(dataModel.Payload));

            return sb.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("NetworkTracePacketDataModel.ConvertBack() not implemented");
        }
    }
}
