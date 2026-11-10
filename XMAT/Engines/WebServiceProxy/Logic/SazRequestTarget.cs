// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Threading;

namespace XMAT.WebServiceCapture
{
    internal readonly record struct SazAbsoluteTarget(
        Uri Uri,
        string Authority);

    internal static class SazRequestTarget
    {
        internal static readonly AsyncLocal<
            Func<string, string, bool>> RefuseAbsoluteTargetSeam =
            new();

        internal static bool TryReadAbsoluteTarget(
            string method,
            string target,
            out SazAbsoluteTarget absolute)
        {
            absolute = default;
            if (RefuseAbsoluteTargetSeam.Value?.Invoke(
                    method,
                    target) == true)
            {
                return false;
            }

            if (method != "CONNECT" &&
                StoredAuthority.TryParseAbsoluteTarget(
                    target,
                    out _,
                    out _) &&
                Uri.TryCreate(
                    target,
                    UriKind.Absolute,
                    out Uri uri) &&
                UriUtils.IsUrlScheme(uri.Scheme) &&
                TryGetHostText(uri, out string hostText))
            {
                string written;
                if (uri.HostNameType == UriHostNameType.IPv6)
                {
                    written = "[" + hostText + "]";
                }
                else
                {
                    if (!HostAuthority.TryNormalizeHost(
                            hostText,
                            out string normalizedHost))
                    {
                        return false;
                    }

                    written = normalizedHost;
                }

                if (!uri.IsDefaultPort)
                {
                    written +=
                        ":" +
                        uri.Port.ToString(
                            CultureInfo.InvariantCulture);
                }

                if (StoredAuthority.TryParse(
                        written,
                        out string storedHost,
                        out int? storedPort))
                {
                    absolute = new(
                        uri,
                        StoredAuthority.Format(
                            storedHost,
                            storedPort));
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetHostText(
            Uri uri,
            out string host)
        {
            try
            {
                host =
                    uri.HostNameType == UriHostNameType.IPv6
                        ? uri.DnsSafeHost
                        : uri.IdnHost;
                return true;
            }
            catch (UriFormatException)
            {
                host = null;
                return false;
            }
        }
    }
}
