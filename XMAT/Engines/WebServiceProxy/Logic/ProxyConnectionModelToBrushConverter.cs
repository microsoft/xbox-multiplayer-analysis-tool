// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.WebServiceCapture.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XMAT.WebServiceCapture
{
    internal class ProxyConnectionModelToBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool IsErrorStatus(string statusText) => int.TryParse(statusText, out int status) && status >= 400;
            bool IsConnectMethod(string method) => (method == "CONNECT");

            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
                return new SolidColorBrush(Colors.Black);

            // Expected order of parameters for this is [StatusCode, Method]
            if (values?.Length == 0)
                return null;

            if (values.Length != 2)
            {
                throw new ArgumentException("Changed the number of parameters to this converter without updating this code.");
            }

            string status = values[0] as string ?? throw new ArgumentException("Status value wasn't a string.");
            string method = values[1] as string ?? throw new ArgumentException("Method value wasn't a string.");

            if (IsErrorStatus(status))
            {
                return new SolidColorBrush(Colors.Red);
            }
            else if (IsConnectMethod(method))
            {
                return new SolidColorBrush(Colors.DimGray);
            }

            return new SolidColorBrush(Colors.Black);

        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
