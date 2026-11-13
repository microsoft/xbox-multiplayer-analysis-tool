// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;

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

        // Whether two absolute URLs are of one origin, as replay decides it wherever an origin matters (a redirect, an
        // edit that changes the origin, the requests of a connect-to run): both their authorities as Uri writes them,
        // compared ignoring case, and their schemes, canonical hosts and effective ports (TryOriginKey) are equal. Comparing
        // the authorities alone equated hosts that differ only in letters whose upper cases match, such as Greek final
        // sigma (ς) and sigma (σ), which IDNA keeps apart: two names that DNS resolves separately. It also equated an IPv6
        // address with any of its zones, which Uri's authority omits and the canonical host keeps: the same address with
        // two zones, or with one zone spelled two ways, is two origins. A host without a canonical form is no origin, so it
        // is never the same as any other, or as itself.
        internal static bool SameOrigin(Uri first, Uri second)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            return string.Equals(
                    first.GetLeftPart(UriPartial.Authority),
                    second.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase) &&
                TryOriginKey(first, out string firstKey) &&
                TryOriginKey(second, out string secondKey) &&
                string.Equals(firstKey, secondKey, StringComparison.Ordinal);
        }

        // An absolute URL's origin in canonical form, scheme://host:port, as Uri gives its parts: the host in ASCII
        // (IdnHost, punycode for an international name), which Uri gives a name and an IPv6 address in lower case; an IPv6
        // address in brackets with its zone exactly as Uri keeps it, case and escapes included, since XMAT does not decide
        // whether two spellings of a zone name one interface; and the effective port. False when the host has no such
        // form, such as a name that IDNA refuses.
        internal static bool TryOriginKey(Uri uri, out string key)
        {
            key = null;
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return false;
            }

            string host;
            try
            {
                host = uri.IdnHost;
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(host) || !host.All(char.IsAscii))
            {
                return false;
            }

            key = uri.Scheme + "://" + (uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + ":" +
                uri.Port.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }
}
