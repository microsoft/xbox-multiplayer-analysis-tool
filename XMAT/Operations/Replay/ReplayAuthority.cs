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

        // Whether Uri gives an absolute URL's host the ASCII form that the HTTP stack connects to and sends (Ascii): an IP
        // address, a host name in ASCII, which Uri gives in lower case without checking it against DNS's rules, or an
        // international host name in its IDN (punycode) form, once Uri has converted it (TryAsciiHost). Uri does not convert
        // every name that IDNA allows, so this is XMAT's requirement for a request's host, not a claim about DNS or IDNA. An
        // IPv6 address's zone, which IdnHost keeps as written, is not judged here: it is data, which ReplayDestination refuses
        // for sending, whatever its characters.
        internal static bool HasIdnHost(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            return TryAsciiHost(uri, out _);
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
        // (TryAsciiHost: IdnHost, punycode for an international name that Uri converts), which Uri gives a name and an IPv6
        // address in lower case; an IPv6 address in brackets with its zone exactly as Uri keeps it, case, escapes and
        // characters included, since XMAT does not decide whether two spellings of a zone name one interface, and a zone is
        // data, which ReplayDestination refuses for sending; and the effective port. False when Uri gives the host no such
        // form, such as for an international host name that IDNA refuses, or one that IDNA allows but Uri does not convert.
        internal static bool TryOriginKey(Uri uri, out string key)
        {
            key = null;
            if (!TryAsciiHost(uri, out string host))
            {
                return false;
            }

            key = uri.Scheme + "://" + (uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + ":" +
                uri.Port.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        // The host of an absolute URL as the HTTP stack connects to it and sends it (IdnHost), when Uri gives it one: for any
        // host but an IPv6 address, non-empty ASCII text. A host name in ASCII is its own form, whatever DNS's rules say of it,
        // such as a label longer than 63 characters. An international host name has none when Uri does not convert it: IdnHost
        // throws for some names that IDNA refuses (a label longer than 63 characters once converted, a character that IDNA
        // disallows), and a name that fails Uri's preliminary DNS checks can fall back to a Basic host, whose IdnHost is the
        // name as written, Unicode included. Those checks also refuse some names that IDNA converts, such as a label of 30
        // Chinese characters, whose punycode label is 36 characters, so a host without this form may still have an IDNA form.
        // An IPv6 address's zone is data and is not judged, whatever its characters. HasIdnHost and TryOriginKey both read the
        // host here, so they cannot disagree about it.
        private static bool TryAsciiHost(Uri uri, out string host)
        {
            host = null;
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return false;
            }

            string idnHost;
            try
            {
                idnHost = uri.IdnHost;
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(idnHost) ||
                (uri.HostNameType != UriHostNameType.IPv6 && !idnHost.All(char.IsAscii)))
            {
                return false;
            }

            host = idnHost;
            return true;
        }
    }
}
