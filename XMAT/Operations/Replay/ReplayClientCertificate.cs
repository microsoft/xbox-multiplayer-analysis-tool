// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace XMAT.Operations.Replay
{
    // The client certificate that a run may send, named by its SHA-1 thumbprint: exactly 40 hexadecimal digits, nothing
    // else, kept in upper case. It names a certificate in the current user's Personal store, which the run resolves
    // (ReplayCertificateResolver) only after every check and consent passed.
    internal sealed record ReplayClientCertificate
    {
        private const int ThumbprintLength = 40;

        private ReplayClientCertificate(string thumbprint)
        {
            Thumbprint = thumbprint;
        }

        internal string Thumbprint { get; }

        internal static bool TryParse(string text, out ReplayClientCertificate certificate)
        {
            certificate = null;
            if (text == null || text.Length != ThumbprintLength || !text.All(char.IsAsciiHexDigit))
            {
                return false;
            }

            certificate = new ReplayClientCertificate(text.ToUpperInvariant());
            return true;
        }

        public override string ToString() => Thumbprint;
    }

    // The one origin that a run's credentials may go to, written https://HOST or https://HOST:PORT, with or without a final
    // "/". Two origins are the same when their hosts, as Uri gives them in ASCII (IdnHost, so an international name in
    // its xn-- form) and in lower case, and their effective ports are: so https://bücher.example and
    // https://xn--bcher-kva.example:443 are one origin. A trailing dot is part of the host, as in URLs. Only https: a
    // credential goes only to a server that proves that it is the URL's host.
    internal sealed record ReplayCredentialOrigin
    {
        private const string Scheme = "https://";

        private readonly bool _ipv6;

        private ReplayCredentialOrigin(string host, bool ipv6, int port)
        {
            Host = host;
            _ipv6 = ipv6;
            Port = port;
        }

        // The host in ASCII and lower case: a name (punycode for an international one), a dotted IPv4 address, or an IPv6
        // address without brackets.
        internal string Host { get; }

        internal int Port { get; }

        // Accepts https://HOST[:PORT][/] only. HOST is a host name, international ones included, a dotted IPv4 address
        // or an IPv6 address in brackets without a zone; PORT is 1 to 65535, without leading zeros. Refused: a user name or
        // password, a path, query or fragment, another scheme, an empty label, and a host whose last label is a number
        // unless it is a dotted IPv4 address as written, so 127.1, 0x7f.0.0.1 and 2130706433, which a URL parser reads as
        // other addresses, are never accepted as written.
        internal static bool TryParse(string text, out ReplayCredentialOrigin origin)
        {
            origin = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string authority = text[Scheme.Length..];
            if (authority.EndsWith('/'))
            {
                authority = authority[..^1];
            }

            if (authority.Length == 0 ||
                authority.Any(character =>
                    character is '/' or '?' or '#' or '@' or '\\' or '%' ||
                    char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                return false;
            }

            string port;
            bool ipv6 = authority[0] == '[';
            if (ipv6)
            {
                int close = authority.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || (close + 1 < authority.Length && authority[close + 1] != ':'))
                {
                    return false;
                }

                string inside = authority[1..close];
                if (!inside.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.') ||
                    !IPAddress.TryParse(inside, out IPAddress address) ||
                    address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }

                port = close + 1 < authority.Length ? authority[(close + 2)..] : null;
            }
            else
            {
                int colon = authority.IndexOf(':', StringComparison.Ordinal);
                if (colon != authority.LastIndexOf(':'))
                {
                    return false;
                }

                string host = colon < 0 ? authority : authority[..colon];
                port = colon < 0 ? null : authority[(colon + 1)..];
                string name = host.EndsWith('.') ? host[..^1] : host;
                string[] labels = name.Split('.');
                if (labels.Any(label => label.Length == 0))
                {
                    return false;
                }

                if (EndsInANumber(host) && !ReplayProxy.IsIPv4(host))
                {
                    return false;
                }
            }

            if (port != null && (!ReplayConnectTo.IsDecimal(port, 5, 65535) || port == "0"))
            {
                return false;
            }

            if (!Uri.TryCreate(Scheme + authority + "/", UriKind.Absolute, out Uri uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment) ||
                ipv6 != (uri.HostNameType == UriHostNameType.IPv6))
            {
                return false;
            }

            origin = Of(uri);
            return origin != null;
        }

        // The origin of an https URL as the HTTP stack reads it, which is where the URL's requests go; null for another
        // scheme, a host that is neither a name nor an IP address, a name that ends in a number, or an IPv6 address with a
        // zone.
        internal static ReplayCredentialOrigin Of(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            bool ipv6 = uri.HostNameType == UriHostNameType.IPv6;
            if (!ipv6 && uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            {
                return null;
            }

            string host;
            try
            {
                host = uri.IdnHost;
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                return null;
            }

            if (string.IsNullOrEmpty(host) || host.Contains('%', StringComparison.Ordinal) || !host.All(char.IsAscii))
            {
                return null;
            }

            // A name that ends in a number is read as an IPv4 address by URL parsers and resolvers, or is no name at all,
            // however it was written: 127。1 and a.1 followed by a soft hyphen are 127.1 and a.1 once IDNA maps them.
            if (uri.HostNameType == UriHostNameType.Dns && EndsInANumber(host))
            {
                return null;
            }

            return new ReplayCredentialOrigin(host.ToLowerInvariant(), ipv6, uri.Port);
        }

        // Whether a host's last label, after one trailing dot, is a number as a URL parser reads one: decimal digits, or 0x
        // and hexadecimal digits.
        private static bool EndsInANumber(string host)
        {
            string name = host.EndsWith('.') ? host[..^1] : host;
            string last = name[(name.LastIndexOf('.') + 1)..];
            return last.All(char.IsAsciiDigit) ||
                (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && last[2..].All(char.IsAsciiHexDigit));
        }

        // https://HOST:PORT, always with the port; an IPv6 address in brackets.
        public override string ToString() =>
            Scheme + (_ipv6 ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);
    }
}
