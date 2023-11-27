// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Data;

namespace XMAT.WebServiceCapture
{
    internal class BodyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if(values == null || values[0] == null || values[1] == null)
                return string.Empty;

            if(values[0] is not Dictionary<string,string> headers)
            {
                throw new ArgumentException("BodyConverter first value is not a Dictionary<string,string>");
            }

            if (values[1] is not byte[] body)
            {
                throw new ArgumentException("BodyConverter second value is not a byte[]");
            }

            if(headers.TryGetValue("Content-Encoding", out string encoding))
            {
                switch(encoding.ToLower())
                {
                    case "gzip":
                        try
                        {
                            body = PublicUtilities.DecodeGzippedData(body);
                        }
                        catch
                        {
                            // header isn't correct, treat the body as something not encoded
                        }
                        break;
                    case "deflate":
                        try
                        {
                            body = PublicUtilities.DecodeDeflatedData(body);
                        }
                        catch
                        {
                            // header isn't correct, treat the body as something not encoded
                        }
                        break;
                }
            }

            if(headers.TryGetValue("Content-Type", out string type))
            {
                if(type.Contains("json"))
                {
                    var options = new JsonSerializerOptions()
                    {
                        AllowTrailingCommas = true,
                        WriteIndented = true
                    };

                    try
                    {
                        string rawJson = Encoding.UTF8.GetString(body);
                        var tempJson = JsonSerializer.Deserialize<JsonElement>(rawJson);
                        string formattedJson = JsonSerializer.Serialize(tempJson, options);
                        return formattedJson;
                    }
                    catch
                    {
                    }
                }
            }

            return Encoding.ASCII.GetString(body);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("BodyConverter.ConvertBack() not implemented");
        }
    }
}
