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
    // The HTTP version that replay asks for. Auto sends HTTP/2 when an HTTPS server offers it and HTTP/1.1 otherwise; the
    // others ask for exactly that version and fail rather than send another.
    internal enum ReplayHttpVersion
    {
        Auto,
        Http11,
        Http2,
    }

    // How replay reaches the server, fixed for a whole run: the HTTP version to ask for and, optionally, another endpoint
    // to connect to instead of the address of the URL's host (Fiddler's Fiddler-Host). The URL, the Host header and the
    // TLS server name and certificate check stay those of the URL.
    internal sealed record ReplayTransportOptions(
        ReplayHttpVersion HttpVersion = ReplayHttpVersion.Auto,
        ReplayConnectTo ConnectTo = null)
    {
        internal const string DirectName = "direct";
        internal const string ConnectToName = "connect-to";
        internal const string AutoVersionName = "auto";

        internal static ReplayTransportOptions Default { get; } = new();

        internal bool IsDefault => HttpVersion == ReplayHttpVersion.Auto && ConnectTo == null;

        // How a send was configured to reach the server, as replay reports it: "direct" or "connect-to".
        internal string TransportName => ConnectTo == null ? DirectName : ConnectToName;

        internal string HttpVersionName => VersionName(HttpVersion);

        internal static string VersionName(ReplayHttpVersion version) => version switch
        {
            ReplayHttpVersion.Http11 => "1.1",
            ReplayHttpVersion.Http2 => "2",
            _ => AutoVersionName,
        };

        // "auto", "1.1" or "2", exactly.
        internal static bool TryParseVersion(string text, out ReplayHttpVersion version)
        {
            version = text switch
            {
                AutoVersionName => ReplayHttpVersion.Auto,
                "1.1" => ReplayHttpVersion.Http11,
                "2" => ReplayHttpVersion.Http2,
                _ => (ReplayHttpVersion)(-1),
            };
            return Enum.IsDefined(version);
        }

        // The version of a response as replay reports it ("1.1", "2"), or null without one.
        internal static string ResponseVersionName(Version version) =>
            version == null
                ? null
                : version.Major >= 2
                    ? version.Major.ToString(CultureInfo.InvariantCulture)
                    : version.ToString(2);
    }

    // The endpoint that replay connects to instead of the address of the URL's host: a numeric IP address and a port,
    // never a host name, so nothing is looked up. Written a.b.c.d:port, or [IPv6]:port without a zone.
    internal sealed record ReplayConnectTo
    {
        private readonly bool _ipv6;

        private ReplayConnectTo(string address, bool ipv6, int port)
        {
            Address = address;
            _ipv6 = ipv6;
            Port = port;
        }

        // The address in its canonical form, without brackets.
        internal string Address { get; }

        internal int Port { get; }

        // Addresses that no request may be sent to: unspecified (0.0.0.0, ::), multicast and the limited broadcast
        // address, also written as IPv4-mapped IPv6.
        internal bool IsDenied
        {
            get
            {
                IPAddress address = ToAddress();
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.Equals(IPAddress.Any) ||
                        address.Equals(IPAddress.Broadcast) ||
                        (address.GetAddressBytes()[0] & 0xF0) == 0xE0;
                }

                return address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast;
            }
        }

        // Only a.b.c.d:port, each part a decimal number without leading zeros (so never an abbreviated, octal or
        // hexadecimal address), or [IPv6]:port without a zone; the port is 1 to 65535. Anything else, a host name, a
        // URL or a path included, is refused.
        internal static bool TryParse(string text, out ReplayConnectTo target)
        {
            target = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string address;
            string port;
            bool ipv6 = text[0] == '[';
            if (ipv6)
            {
                int close = text.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || close + 2 >= text.Length || text[close + 1] != ':')
                {
                    return false;
                }

                string inside = text[1..close];
                if (!inside.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.') ||
                    !IPAddress.TryParse(inside, out IPAddress parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }

                address = parsed.ToString();
                port = text[(close + 2)..];
            }
            else
            {
                int colon = text.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0 || colon != text.LastIndexOf(':'))
                {
                    return false;
                }

                address = text[..colon];
                string[] parts = address.Split('.');
                if (parts.Length != 4 || !parts.All(part => IsDecimal(part, 3, 255)))
                {
                    return false;
                }

                port = text[(colon + 1)..];
            }

            if (!IsDecimal(port, 5, 65535) || port == "0")
            {
                return false;
            }

            target = new ReplayConnectTo(address, ipv6, int.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture));
            return true;
        }

        internal IPEndPoint ToEndPoint() => new(ToAddress(), Port);

        public override string ToString() =>
            (_ipv6 ? "[" + Address + "]" : Address) + ":" + Port.ToString(CultureInfo.InvariantCulture);

        private IPAddress ToAddress()
        {
            IPAddress address = IPAddress.Parse(Address);
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        // 1 to maxDigits ASCII digits, no leading zero, at most maxValue.
        private static bool IsDecimal(string text, int maxDigits, int maxValue) =>
            text.Length is >= 1 && text.Length <= maxDigits &&
            text.All(char.IsAsciiDigit) &&
            (text.Length == 1 || text[0] != '0') &&
            int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) <= maxValue;
    }
}
