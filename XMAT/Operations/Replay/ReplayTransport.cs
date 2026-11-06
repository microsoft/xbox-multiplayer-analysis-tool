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

    // How replay reaches the server, fixed for a whole run: the HTTP version to ask for and, optionally, either another
    // endpoint to connect to instead of the address of the URL's host (Fiddler's Fiddler-Host) or an upstream HTTP proxy
    // to send through (Fiddler's gateway). The URL, the Host header and the TLS server name and certificate check stay
    // those of the URL. The operation refuses a run with both an endpoint and a proxy before anything is sent.
    internal sealed record ReplayTransportOptions(
        ReplayHttpVersion HttpVersion = ReplayHttpVersion.Auto,
        ReplayConnectTo ConnectTo = null,
        ReplayProxy Proxy = null)
    {
        internal const string DirectName = "direct";
        internal const string ConnectToName = "connect-to";
        internal const string ProxyName = "proxy";
        internal const string AutoVersionName = "auto";

        internal static ReplayTransportOptions Default { get; } = new();

        internal bool IsDefault => HttpVersion == ReplayHttpVersion.Auto && ConnectTo == null && Proxy == null;

        // Whether the run needs a sender of its own, made for it and disposed when it ends: a connect-to endpoint or a proxy
        // is a setting of the handler, so no pooled connection may carry the run's requests. The HTTP version is asked for
        // per request, so it alone needs none.
        internal bool NeedsOwnSender => ConnectTo != null || Proxy != null;

        // How a send was configured to reach the server, as replay reports it: "direct", "connect-to" or "proxy".
        internal string TransportName => ConnectTo != null ? ConnectToName : Proxy != null ? ProxyName : DirectName;

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
        internal static bool IsDecimal(string text, int maxDigits, int maxValue) =>
            text.Length is >= 1 && text.Length <= maxDigits &&
            text.All(char.IsAsciiDigit) &&
            (text.Length == 1 || text[0] != '0') &&
            int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) <= maxValue;
    }

    // An upstream HTTP proxy that every request of a run goes through when the caller allows it, written http://HOST or
    // http://HOST:PORT, with or without a final "/". HOST is an ASCII host name or an IP address, an IPv6 one in brackets
    // without a zone; PORT is 1 to 65535, 80 by default. Nothing else is accepted: no user name or password, path, query or
    // fragment, no other scheme (HTTPS and SOCKS proxies included), no Unicode host name or trailing dot, and no address
    // that Uri would read as another host, such as 127.1 or 0x7f.0.0.1, so the proxy that the user allows is exactly the
    // one that the HTTP stack connects to.
    internal sealed record ReplayProxy
    {
        private const string Scheme = "http://";
        private const int DefaultPort = 80;
        private const int MaxNameLength = 253;
        private const int MaxLabelLength = 63;

        private readonly bool _ipv6;

        private ReplayProxy(string host, bool ipv6, int port)
        {
            Host = host;
            _ipv6 = ipv6;
            Port = port;
        }

        // The host in its canonical form: a lower-case name, a dotted IPv4 address or an IPv6 address without brackets.
        internal string Host { get; }

        internal int Port { get; }

        internal static bool TryParse(string text, out ReplayProxy proxy)
        {
            proxy = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string authority = text[Scheme.Length..];
            if (authority.EndsWith('/'))
            {
                authority = authority[..^1];
            }

            // What remains may hold only a host, an IPv6 literal and a port: '/', '?', '#', '@', '%', '\', whitespace and
            // anything outside ASCII refuse the text.
            if (authority.Length == 0 ||
                !authority.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ':' or '[' or ']'))
            {
                return false;
            }

            string host;
            string port = null;
            bool ipv6 = authority[0] == '[';
            if (ipv6)
            {
                int close = authority.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || (close + 1 < authority.Length && authority[close + 1] != ':'))
                {
                    return false;
                }

                if (close + 1 < authority.Length)
                {
                    port = authority[(close + 2)..];
                }

                string inside = authority[1..close];
                if (!inside.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.') ||
                    !IPAddress.TryParse(inside, out IPAddress parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }

                host = parsed.ToString();
            }
            else
            {
                int colon = authority.IndexOf(':', StringComparison.Ordinal);
                if (colon != authority.LastIndexOf(':'))
                {
                    return false;
                }

                host = colon < 0 ? authority : authority[..colon];
                port = colon < 0 ? null : authority[(colon + 1)..];
                if (!IsIPv4(host) && !IsName(host))
                {
                    return false;
                }

                host = host.ToLowerInvariant();
            }

            if (port != null && (!ReplayConnectTo.IsDecimal(port, 5, 65535) || port == "0"))
            {
                return false;
            }

            var candidate = new ReplayProxy(
                host, ipv6, port == null ? DefaultPort : int.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture));

            // The HTTP stack connects to the proxy URI's IdnHost and Port: they must be what was written.
            if (!Uri.TryCreate(candidate.ToString() + "/", UriKind.Absolute, out Uri uri) ||
                uri.HostNameType != (ipv6 ? UriHostNameType.IPv6 : IsIPv4(host) ? UriHostNameType.IPv4 : UriHostNameType.Dns) ||
                !string.Equals(uri.IdnHost, host, StringComparison.Ordinal) ||
                uri.Port != candidate.Port)
            {
                return false;
            }

            proxy = candidate;
            return true;
        }

        // http://HOST:PORT/, the URI that FixedProxy gives the HTTP stack.
        internal Uri ToUri() => new(ToString() + "/", UriKind.Absolute);

        // http://HOST:PORT, always with the port.
        public override string ToString() =>
            Scheme + (_ipv6 ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

        // Four decimal numbers of 0 to 255 without leading zeros. A host of digits and dots is an IPv4 address or nothing.
        private static bool IsIPv4(string host)
        {
            string[] parts = host.Split('.');
            return parts.Length == 4 && parts.All(part => ReplayConnectTo.IsDecimal(part, 3, 255));
        }

        // Labels of 1 to 63 letters, digits and hyphens, neither starting nor ending with a hyphen, at most 253 characters
        // in all. As URL parsers do, a host whose last label is a number (all digits, or 0x and hexadecimal digits) is read
        // as an IPv4 address, so it is no name: 127.1, 0x7f.0.0.1 and 2130706433 are refused, not looked up.
        private static bool IsName(string host)
        {
            if (host.Length is < 1 or > MaxNameLength)
            {
                return false;
            }

            string[] labels = host.Split('.');
            string last = labels[^1];
            if (last.All(char.IsAsciiDigit) ||
                (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && last[2..].All(char.IsAsciiHexDigit)))
            {
                return false;
            }

            return labels.All(label =>
                label.Length is >= 1 and <= MaxLabelLength &&
                label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
                label[0] != '-' && label[^1] != '-');
        }
    }

    // The proxy of a replay handler that sends through one (ReplaySender.CreateHandler): every request goes to the one
    // proxy, none is bypassed, loopback included, and no proxy credentials are ever given. No member throws, and GetProxy
    // never returns null, because the HTTP stack sends a request directly when its proxy throws or names none
    // (HttpConnectionPoolManager.SendAsync).
    internal sealed class FixedProxy : IWebProxy
    {
        private readonly Uri _uri;

        internal FixedProxy(ReplayProxy proxy)
        {
            ArgumentNullException.ThrowIfNull(proxy);
            _uri = proxy.ToUri();
        }

        // Always null; a value that anyone sets is ignored.
        public ICredentials Credentials
        {
            get => null;
            set
            {
                // Ignored: the proxy is never answered with credentials.
            }
        }

        public Uri GetProxy(Uri destination) => _uri;

        public bool IsBypassed(Uri host) => false;
    }
}
