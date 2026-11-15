// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace XMAT
{
    // A column's full name, for a header that shows an abbreviation such as "S" or "#". The column header styles show it
    // as the header's tooltip and give it to screen readers as the header's name.
    public static class DataGridColumnInfo
    {
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached(
            "Description", typeof(string), typeof(DataGridColumnInfo), new PropertyMetadata(null));

        public static string GetDescription(DataGridColumn column) => (string)column.GetValue(DescriptionProperty);

        public static void SetDescription(DataGridColumn column, string value) => column.SetValue(DescriptionProperty, value);

        // Reads a header's column description for the header styles. Its bindings are the header and the header's
        // content: a header gets its column after its style applies and reports no change, so the description is read
        // again when the content arrives, which follows the column.
        public static IMultiValueConverter HeaderDescriptionConverter { get; } = new HeaderDescription();

        private sealed class HeaderDescription : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
                values.Length > 0 && values[0] is DataGridColumnHeader { Column: { } column } ? GetDescription(column) : null;

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
