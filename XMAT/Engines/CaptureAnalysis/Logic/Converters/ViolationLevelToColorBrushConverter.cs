// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XMAT.XboxLiveCaptureAnalysis
{
    internal class ViolationLevelToColorBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is not ViolationLevel model)
            {
                throw new ArgumentException("ViolationLevelToColorBrushConverter received a value that wasn't a ViolationLevel");
            }

            switch(model)
            {
                case ViolationLevel.Error: return new SolidColorBrush(Colors.Red);
                case ViolationLevel.Warning: return new SolidColorBrush(Colors.Orange);
                case ViolationLevel.Info: return new SolidColorBrush(Colors.Green);
            }

            throw new ArgumentException("Unhandled ViolationLevel value");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("IssueCountToValueConverter.ConvertBack() not implemented");
        }
    }
}
