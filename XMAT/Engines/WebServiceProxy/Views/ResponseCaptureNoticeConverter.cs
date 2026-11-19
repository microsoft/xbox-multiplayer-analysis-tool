// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace XMAT.WebServiceCapture
{
    // The notice above a captured response: still streaming, and where the capture truncated its body. It is built here,
    // not on the row, because the row is also read where no localization is loaded.
    internal sealed class ResponseCaptureNoticeConverter : IMultiValueConverter
    {
        // IsResponseStreaming and ResponseBodyTruncatedAt of the selected row.
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            Format(
                values != null && values.Length > 0 && values[0] is bool streaming && streaming,
                values != null && values.Length > 1 && values[1] is long truncatedAt ? truncatedAt : 0);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        internal static string Format(bool streaming, long truncatedAt)
        {
            string streamingNotice = streaming ? Localization.GetLocalizedString("WEBCAP_RESPONSE_STREAMING") : null;
            string truncatedNotice = truncatedAt > 0
                ? Localization.GetLocalizedString("WEBCAP_RESPONSE_TRUNCATED", FormatSize(truncatedAt))
                : null;
            return string.Join(" ", new[] { streamingNotice, truncatedNotice }.Where(notice => notice != null));
        }

        // Megabytes of 1,048,576 bytes, as the capture limit counts them.
        private static string FormatSize(long bytes)
        {
            const long Kilobyte = 1024;
            const long Megabyte = 1024 * Kilobyte;
            return bytes >= Megabyte
                ? Localization.GetLocalizedString("WEBCAP_SIZE_MB", Math.Round(bytes / (double)Megabyte, 1))
                : bytes >= Kilobyte
                    ? Localization.GetLocalizedString("WEBCAP_SIZE_KB", Math.Round(bytes / (double)Kilobyte, 1))
                    : Localization.GetLocalizedString("WEBCAP_SIZE_BYTES", bytes);
        }
    }
}
