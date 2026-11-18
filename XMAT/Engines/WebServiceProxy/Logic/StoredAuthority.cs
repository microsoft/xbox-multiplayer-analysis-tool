// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace XMAT.WebServiceCapture
{
    // An authority as capture data. Unlike HostAuthority's host-identity normalization, this keeps an IPv6 zone's exact
    // lexical spelling: a zero or an interface name is data, never resolved to an index or erased by IPAddress.ToString.
    internal static class StoredAuthority
    {
        private const int MaxHostChars = 255;

        internal static bool TryParse(string authority, out string host, out int? port)
        {
            host = null;
            port = null;
            if (string.IsNullOrWhiteSpace(authority))
            {
                return false;
            }

            string value = authority.Trim();
            if (value[0] != '[')
            {
                if (!HostAuthority.TryParse(value, out string parsedHost, out int? parsedPort) ||
                    !HostFits(parsedHost))
                {
                    return false;
                }

                host = parsedHost;
                port = parsedPort;
                return true;
            }

            int close = value.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            string literal = value[1..close];
            string rest = value[(close + 1)..];
            int? storedPort = null;
            if (rest.Length != 0)
            {
                if (rest[0] != ':' ||
                    !TryParsePort(rest[1..], out int parsedPort))
                {
                    return false;
                }

                storedPort = parsedPort;
            }

            int zone = literal.IndexOf('%');
            string addressText = zone < 0 ? literal : literal[..zone];
            if (!IPAddress.TryParse(addressText, out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6 ||
                (zone >= 0 && !IsValidZone(literal.AsSpan(zone + 1))))
            {
                return false;
            }

            host = address.ToString();
            if (zone >= 0)
            {
                host += literal[zone..];
            }

            if (!HostFits(host))
            {
                host = null;
                return false;
            }

            port = storedPort;
            return true;
        }

        internal static string Format(string host, int? port)
        {
            ArgumentException.ThrowIfNullOrEmpty(host);
            string written = host.Contains(':') ? "[" + host + "]" : host;
            return port.HasValue
                ? written + ":" + port.Value.ToString(CultureInfo.InvariantCulture)
                : written;
        }

        internal static bool TryParseAbsoluteTarget(string target, out string host, out int? port)
        {
            host = null;
            port = null;
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            int schemeEnd = target.IndexOf(':');
            int authorityStart = schemeEnd + 1;
            if (schemeEnd <= 0 ||
                authorityStart + 2 > target.Length ||
                !IsSlash(target[authorityStart]) ||
                !IsSlash(target[authorityStart + 1]))
            {
                return false;
            }

            authorityStart += 2;
            int tail = target.AsSpan(authorityStart).IndexOfAny("/?#".AsSpan());
            string authority = tail < 0
                ? target[authorityStart..]
                : target.Substring(authorityStart, tail);
            int userInfo = authority.LastIndexOf('@');
            if (authority.StartsWith('[') && userInfo >= 0)
            {
                return false;
            }

            string hostAndPort = userInfo < 0 ? authority : authority[(userInfo + 1)..];
            if (hostAndPort.EndsWith(':'))
            {
                hostAndPort = hostAndPort[..^1];
            }

            return TryParse(hostAndPort, out host, out port);
        }

        private static bool TryParsePort(string value, out int port) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
            port > 0 &&
            port <= IPEndPoint.MaxPort;

        // RFC 6874 ZoneID: one or more unreserved characters or percent-encoded bytes.
        private static bool IsValidZone(ReadOnlySpan<char> zone)
        {
            if (zone.IsEmpty)
            {
                return false;
            }

            for (int index = 0; index < zone.Length;)
            {
                char value = zone[index];
                if (char.IsAsciiLetterOrDigit(value) ||
                    value is '-' or '.' or '_' or '~')
                {
                    index++;
                    continue;
                }

                if (value != '%' ||
                    index + 2 >= zone.Length ||
                    !IsHex(zone[index + 1]) ||
                    !IsHex(zone[index + 2]))
                {
                    return false;
                }

                index += 3;
            }

            return true;
        }

        private static bool IsHex(char value) =>
            char.IsAsciiHexDigit(value);

        private static bool HostFits(string host) =>
            host.Length <= MaxHostChars;

        private static bool IsSlash(char value) =>
            value is '/' or '\\';
    }
}
