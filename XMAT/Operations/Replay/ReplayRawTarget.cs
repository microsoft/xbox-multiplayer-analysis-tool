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
    // The origin that a raw request is sent to (Raw mode): http:// or https:// in any case, a host and a port, and at
    // most a final '/'. The host is an IPv4 address of four decimal numbers without leading zeros, an IPv6 address in
    // brackets without a zone, or a DNS name of ASCII labels; a Unicode name is converted with IDNA and must then pass the
    // same rule. A name whose last label is a number is refused, as ReplayProxy refuses it, because URL parsers read it as
    // an IPv4 address. User information, a path other than '/', a query, a fragment, '%', '\', whitespace and control
    // characters refuse the text, and the Uri that the HTTP and TLS stacks would build from it must agree with it.
    //
    // Its identity is the scheme, the host in canonical ASCII form (lower case, an IPv6 address without brackets) and the
    // port. ToString() is scheme://host:port, the form that every confirmation and status shows first.
    internal sealed class ReplayRawTarget : IEquatable<ReplayRawTarget>
    {
        private const string HttpScheme = "http";
        private const string HttpsScheme = "https";

        // IDNA for a Unicode host name: the STD3 ASCII rules on and unassigned code points refused.
        private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

        private readonly UriHostNameType _kind;

        private ReplayRawTarget(string scheme, string host, UriHostNameType kind, int port)
        {
            Scheme = scheme;
            Host = host;
            _kind = kind;
            Port = port;
        }

        // "http" or "https".
        internal string Scheme { get; }

        // The host in canonical ASCII form: a lower-case name, a dotted IPv4 address, or an IPv6 address without brackets.
        internal string Host { get; }

        internal int Port { get; }

        internal bool IsHttps => Scheme == HttpsScheme;

        internal bool IsIPLiteral => _kind is UriHostNameType.IPv4 or UriHostNameType.IPv6;

        internal bool IsDefaultPort => Port == DefaultPort(Scheme);

        // The authority as a request writes it, the port only when it is not the scheme's default.
        internal string Authority => IsDefaultPort ? HostForAuthority : AuthorityWithPort;

        // The authority with its port, always.
        internal string AuthorityWithPort => HostForAuthority + ":" + Port.ToString(CultureInfo.InvariantCulture);

        // The name that TLS checks the server's certificate against.
        internal string TlsName => Host;

        // The origin as an absolute URL, scheme://authority/, for comparisons with a request's origin.
        internal Uri Origin => new(Scheme + "://" + Authority + "/", UriKind.Absolute);

        // The address of an IP literal; null for a name, which is looked up when a send begins.
        internal IPAddress Address => IsIPLiteral ? IPAddress.Parse(Host) : null;

        // An IP literal that no request may be sent to (ReplayConnectTo.IsDeniedAddress): replay.raw.target-denied. A name
        // is checked when it is looked up.
        internal bool IsDenied => IsIPLiteral && ReplayConnectTo.IsDeniedAddress(Address);

        private string HostForAuthority => _kind == UriHostNameType.IPv6 ? "[" + Host + "]" : Host;

        // Reads a target as the command line's --target and the composer's target box write it.
        internal static bool TryParse(string text, out ReplayRawTarget target)
        {
            target = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string scheme;
            if (text.StartsWith(HttpScheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = HttpScheme;
            }
            else if (text.StartsWith(HttpsScheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                scheme = HttpsScheme;
            }
            else
            {
                return false;
            }

            string authority = text[(scheme.Length + 3)..];
            if (authority.EndsWith('/'))
            {
                authority = authority[..^1];
            }

            if (authority.Length == 0 ||
                authority.Any(character =>
                    character is '/' or '?' or '#' or '@' or '%' or '\\' ||
                    char.IsWhiteSpace(character) ||
                    char.IsControl(character)))
            {
                return false;
            }

            string host;
            string port = null;
            UriHostNameType kind;
            if (authority[0] == '[')
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
                kind = UriHostNameType.IPv6;
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
                if (ReplayProxy.IsIPv4(host))
                {
                    kind = UriHostNameType.IPv4;
                }
                else
                {
                    if (!host.All(char.IsAscii))
                    {
                        try
                        {
                            host = Idn.GetAscii(host);
                        }
                        catch (ArgumentException)
                        {
                            return false;
                        }
                    }

                    host = host.ToLowerInvariant();
                    if (!ReplayProxy.IsName(host))
                    {
                        return false;
                    }

                    kind = UriHostNameType.Dns;
                }
            }

            int number = DefaultPort(scheme);
            if (port != null)
            {
                if (!ReplayConnectTo.IsDecimal(port, 5, 65535) || port == "0")
                {
                    return false;
                }

                number = int.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture);
            }

            var candidate = new ReplayRawTarget(scheme, host, kind, number);

            // The HTTP and TLS stacks take the host and the port from a Uri: it must read exactly what was written.
            if (!Uri.TryCreate(candidate.ToString() + "/", UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, scheme, StringComparison.Ordinal) ||
                uri.HostNameType != kind ||
                !string.Equals(uri.IdnHost, host, StringComparison.Ordinal) ||
                uri.Port != number)
            {
                return false;
            }

            target = candidate;
            return true;
        }

        // The origin of a Parsed request's URL under the same rules; null when Raw mode accepts none, as for a URL with user
        // information, an IPv6 zone or another scheme.
        internal static ReplayRawTarget FromUrl(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri || !string.IsNullOrEmpty(url.UserInfo))
            {
                return null;
            }

            string host;
            try
            {
                host = url.HostNameType == UriHostNameType.IPv6 ? "[" + url.IdnHost + "]" : url.IdnHost;
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                return null;
            }

            string text = url.Scheme + "://" + host +
                (url.IsDefaultPort ? string.Empty : ":" + url.Port.ToString(CultureInfo.InvariantCulture));
            return TryParse(text, out ReplayRawTarget target) ? target : null;
        }

        public bool Equals(ReplayRawTarget other) =>
            other != null &&
            string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
            string.Equals(Host, other.Host, StringComparison.Ordinal) &&
            Port == other.Port;

        public override bool Equals(object obj) => Equals(obj as ReplayRawTarget);

        public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port);

        public override string ToString() => Scheme + "://" + AuthorityWithPort;

        private static int DefaultPort(string scheme) => scheme == HttpsScheme ? 443 : 80;
    }
}
