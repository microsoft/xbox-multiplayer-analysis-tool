// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows.Data;
using CaptureAnalysisEngine;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    // A check's result as the issue level that IssueBadge colors: a nonzero count, or true, is an error.
    internal class BooleanValueToIssueLevelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return null;
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
                throw new ArgumentException("BooleanValueToIssueLevelConverter can only convert from int or bool");
            }

            return result ? ViolationLevel.Error : ViolationLevel.Info;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("BooleanValueToIssueLevelConverter.ConvertBack() not implemented");
        }
    }
}
