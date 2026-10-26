// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Host authority parsing and host identity shared by proxy ingress, capture rows,
    /// host-list entries, context menus and analysis provenance. <see cref="TryParse"/> checks
    /// only an authority's structure and returns any non-IP host trimmed as sent, which need not
    /// be a host identity. Only <see cref="TryNormalizeHost"/> yields host identities, which
    /// host-list actions require: an identity never includes a port, DNS names keep their
    /// trimmed spelling, including case and a final dot, and are compared ordinally ignoring
    /// case (a final dot is significant), and IP literals use their canonical text without
    /// brackets.
    /// </summary>
    internal static class HostAuthority
    {
        private const int MaxDnsNameLength = 253;
        private const int MaxDnsLabelLength = 63;
        private static readonly char[] Brackets = { '[', ']' };

        /// <summary>
        /// Parses the structure of an HTTP authority: "host", "host:port", "[IPv6]" or
        /// "[IPv6]:port". Only the structure is checked: a bracketed host must be an IPv6
        /// literal (returned canonical), an unbracketed IPv6 address is rejected rather than
        /// split at a colon, a port must be 1-65535, and the host must not be empty or contain a
        /// stray bracket. Any other host text is returned trimmed as sent, without host-name
        /// validation, and is not necessarily a host identity. The port is optional so callers
        /// keep their protocol-specific defaults.
        /// </summary>
        internal static bool TryParse(string authority, out string host, out int? port)
        {
            host = null;
            port = null;
            if (string.IsNullOrWhiteSpace(authority))
            {
                return false;
            }

            string value = authority.Trim();
            string hostPart;
            string portPart = null;
            if (value[0] == '[')
            {
                int close = value.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }
                hostPart = value[..(close + 1)];
                string rest = value[(close + 1)..];
                if (rest.Length > 0)
                {
                    if (rest[0] != ':')
                    {
                        return false;
                    }
                    portPart = rest[1..];
                }
            }
            else
            {
                int colon = value.IndexOf(':');
                if (colon >= 0 && value.IndexOf(':', colon + 1) >= 0)
                {
                    return false;
                }
                hostPart = colon >= 0 ? value[..colon] : value;
                portPart = colon >= 0 ? value[(colon + 1)..] : null;
            }

            int parsedPort = 0;
            if ((portPart != null && !TryParsePort(portPart, out parsedPort)) ||
                !TryParseAuthorityHost(hostPart, out string parsedHost))
            {
                return false;
            }

            host = parsedHost;
            port = portPart != null ? parsedPort : null;
            return true;
        }

        /// <summary>
        /// Returns the identity of one host literal: a DNS name with its trimmed spelling, an
        /// IPv4 address, or an IPv6 address (with or without brackets) in canonical form.
        /// Ports, wildcards and paths are rejected.
        /// </summary>
        internal static bool TryNormalizeHost(string host, out string canonicalHost)
        {
            canonicalHost = null;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            string value = host.Trim();
            if (value[0] == '[' || value[^1] == ']')
            {
                if (value.Length < 3 || value[0] != '[' || value[^1] != ']')
                {
                    return false;
                }
                return TryNormalizeIPv6(value[1..^1], out canonicalHost);
            }

            if (value.Contains(':'))
            {
                return TryNormalizeIPv6(value, out canonicalHost);
            }

            if (TryNormalizeIPv4(value, out canonicalHost))
            {
                return true;
            }

            return TryNormalizeDnsName(value, out canonicalHost);
        }

        // The host of an authority: a bracketed host must be an IPv6 literal, made canonical.
        // Any other host is returned trimmed as sent if it is not empty and has no bracket;
        // dotted-quad IPv4 text is already canonical.
        private static bool TryParseAuthorityHost(string hostPart, out string host)
        {
            string value = hostPart.Trim();
            if (value.StartsWith('['))
            {
                return TryNormalizeHost(value, out host);
            }

            host = value.Length > 0 && value.IndexOfAny(Brackets) < 0 ? value : null;
            return host != null;
        }

        private static bool TryParsePort(string value, out int port)
        {
            port = 0;
            if (value.Length == 0)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }
            // Any run of digits is a port, zero padding included; a value that overflows fails
            // like any other value outside 1-65535.
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port > 0 && port <= IPEndPoint.MaxPort;
        }

        private static bool TryNormalizeIPv6(string value, out string canonicalHost)
        {
            canonicalHost = null;
            if (!IPAddress.TryParse(value, out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            canonicalHost = address.ToString();
            return true;
        }

        // Only dotted-quad decimal text is an IPv4 literal; other numeric spellings (including
        // octets with leading zeros, which some resolvers read as octal) stay DNS-style names.
        private static bool TryNormalizeIPv4(string value, out string canonicalHost)
        {
            canonicalHost = null;
            string[] octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }

            foreach (string octet in octets)
            {
                if (octet.Length == 0 || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0'))
                {
                    return false;
                }
                foreach (char character in octet)
                {
                    if (!char.IsAsciiDigit(character))
                    {
                        return false;
                    }
                }
                if (int.Parse(octet, NumberStyles.None, CultureInfo.InvariantCulture) > 255)
                {
                    return false;
                }
            }

            canonicalHost = value;
            return true;
        }

        // Labels are validated without the optional final dot, but the name keeps its spelling:
        // "Example.test." and "example.test" are different textual hosts.
        private static bool TryNormalizeDnsName(string value, out string canonicalHost)
        {
            canonicalHost = null;
            string name = value.EndsWith('.') ? value[..^1] : value;
            if (name.Length == 0 || name.Length > MaxDnsNameLength)
            {
                return false;
            }

            foreach (string label in name.Split('.'))
            {
                if (label.Length == 0 || label.Length > MaxDnsLabelLength)
                {
                    return false;
                }
                foreach (char character in label)
                {
                    if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
                    {
                        return false;
                    }
                }
            }

            canonicalHost = value;
            return true;
        }
    }
}
