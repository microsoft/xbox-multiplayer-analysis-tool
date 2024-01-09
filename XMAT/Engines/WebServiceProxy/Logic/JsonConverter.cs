// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Text.Json;

namespace XMAT.WebServiceCapture
{
    internal class JsonConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var jsonTreeViewItems = new ObservableCollection<JsonTreeViewItem>();

            if (value != null)
            {
                var bodyText = value as string;
                try
                {
                    var rootJsonElement = JsonSerializer.Deserialize<JsonElement>(bodyText);
                    var rootTreeViewItem = RecursiveEnumerateJsonElement("[root]", rootJsonElement);
                    if (rootTreeViewItem != null)
                    {
                        rootTreeViewItem.AutoExpandJson = true;
                        jsonTreeViewItems.Add(rootTreeViewItem);
                    }
                }
                catch (Exception)
                {
                    // if it fails to parse, it is possible that the body contains multiple JSON
                    // documents, so we will give that a try.
                    var bodyTextLines = bodyText.Split("\r\n");
                    if (bodyTextLines.Length > 1)
                    {
                        try
                        {
                            foreach (var bodyTextLine in bodyTextLines)
                            {
                                var rootJsonElement = JsonSerializer.Deserialize<JsonElement>(bodyTextLine);
                                var rootTreeViewItem = RecursiveEnumerateJsonElement("[root]", rootJsonElement);
                                if (rootTreeViewItem != null)
                                {
                                    rootTreeViewItem.AutoExpandJson = false;
                                    jsonTreeViewItems.Add(rootTreeViewItem);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // once we hit non-parseable JSON, we will just break out of the
                            // loop for performance reasons
                        }
                    }
                }
            }

            return jsonTreeViewItems;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("JsonConverter.ConvertBack() not implemented");
        }

        private JsonTreeViewItem RecursiveEnumerateJsonElement(string propertyName, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    var arrayTreeViewItem = new JsonTreeViewItem() { PropertyDescriptor = propertyName };
                    var arrayEnumerator = element.EnumerateArray();
                    var arrayIndex = 0;
                    while (arrayEnumerator.MoveNext())
                    {
                        arrayTreeViewItem.JsonTreeViewItems.Add(
                            RecursiveEnumerateJsonElement(
                                $"{arrayIndex}",
                                arrayEnumerator.Current));
                        arrayIndex++;
                    }
                    return arrayTreeViewItem;

                case JsonValueKind.Object:
                    var objectTreeViewItem = new JsonTreeViewItem() { PropertyDescriptor = propertyName };
                    var objectEnumerator = element.EnumerateObject();
                    while (objectEnumerator.MoveNext())
                    {
                        objectTreeViewItem.JsonTreeViewItems.Add(
                            RecursiveEnumerateJsonElement(
                                $"{objectEnumerator.Current.Name}",
                                objectEnumerator.Current.Value));
                    }
                    return objectTreeViewItem;

                default:
                    return new JsonTreeViewItem() { PropertyDescriptor = $"{propertyName}={element.ToString()}" };
            }
        }
    }
}
