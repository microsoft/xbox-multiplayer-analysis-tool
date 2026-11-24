// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace XMAT
{
    // Shows text written in capitals, such as "STATUS", in title case ("Status"). Text that already has lower-case
    // letters is shown as it is.
    public sealed class TitleCaseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string text || text.Any(char.IsLower))
            {
                return value;
            }

            return culture.TextInfo.ToTitleCase(text.ToLower(culture));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
