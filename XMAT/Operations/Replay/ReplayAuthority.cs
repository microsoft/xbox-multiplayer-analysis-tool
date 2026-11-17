// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;

namespace XMAT.Operations.Replay
{
    internal static class ReplayAuthority
    {
        // A URL's authority as the HTTP stack sends it in Host, in ASCII: an international host name as its IDN (punycode)
        // form, and the port unless it is the scheme's default; an IPv6 address in brackets without its zone, which is
        // never sent. Replay records it for the request's Host, its Replays row and a saved archive, so all three name the
        // host as it went out. The request itself keeps its URL.
        internal static string Ascii(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            if (uri.HostNameType == UriHostNameType.IPv6)
            {
                return uri.Authority;
            }

            return uri.IsDefaultPort
                ? uri.IdnHost
                : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        }
    }
}
