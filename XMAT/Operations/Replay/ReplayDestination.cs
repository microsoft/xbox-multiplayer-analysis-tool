// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Replay
{
    internal enum ReplayCaptureDestinationStatus
    {
        Loadable,
        ScopedIpv6,
        Unreadable,
    }

    internal enum AbsoluteHttpTargetRecognition
    {
        NotAbsolute,
        Recognized,
        Undecided,
    }

    // A replay destination's local IPv6 zone is data that XMAT may retain, but never a destination that it sends to.
    internal static class ReplayDestination
    {
        internal const string ScopedIpv6Code = "replay.destination.scoped-ipv6";
        internal const string ScopedIpv6Message =
            "IPv6 zone identifiers are not supported for sending requests or exporting cURL commands.";
        private const int MaxCaptureHostChars = 255;
        private const int MaxCapturePortChars = 5;
        private const int MaxAbsoluteAuthorityChars = 512;
        private const int MaxLeadingWhitespaceChars = 32;

        internal readonly record struct AbsoluteHttpTarget(bool Https, int AuthorityStart, int AuthorityLength);

        // True only when the parsed host itself is IPv6 and keeps a zone. A sequence's '#' is replaced as drafting does;
        // percent signs in any path, query or fragment are never host zones.
        internal static bool IsScopedIpv6(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            string probe = url.Contains(ReplaySequence.Placeholder)
                ? url.Replace(ReplaySequence.Placeholder.ToString(), "0", StringComparison.Ordinal)
                : url;
            return Uri.TryCreate(probe, UriKind.Absolute, out Uri uri) && IsScopedIpv6(uri);
        }

        internal static bool IsScopedIpv6(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.HostNameType != UriHostNameType.IPv6)
            {
                return false;
            }

            try
            {
                return uri.IdnHost.Contains('%', StringComparison.Ordinal);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or UriFormatException)
            {
                return false;
            }
        }

        // The capture's effective destination, with absolute-target precedence, without reading its body.
        internal static bool IsScopedIpv6(ProxyConnectionModel capture)
        {
            if (capture == null)
            {
                return false;
            }

            try
            {
                return IsScopedIpv6(RequestReplayDraft.CaptureUrl(capture));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                return false;
            }
        }

        // Recognizes the implicit HTTP forms that Uri accepts: up to 32 leading URI whitespace characters, then http(s),
        // ':', and either slash spelling in both positions. AuthorityLength is -1 when no '/', '?' or '#' occurs inside
        // the authority bound. More leading URI whitespace is Undecided, so bounded callers fail closed.
        internal static AbsoluteHttpTargetRecognition RecognizeAbsoluteHttpTarget(
            string text, int maxAuthorityChars, out AbsoluteHttpTarget target)
        {
            target = default;
            if (string.IsNullOrEmpty(text) || maxAuthorityChars < 0)
            {
                return AbsoluteHttpTargetRecognition.NotAbsolute;
            }

            ReadOnlySpan<char> span = text.AsSpan();
            int start = 0;
            while (start < span.Length && start < MaxLeadingWhitespaceChars && IsUriLeadingWhitespace(span[start]))
            {
                start++;
            }

            if (start < span.Length && IsUriLeadingWhitespace(span[start]))
            {
                return AbsoluteHttpTargetRecognition.Undecided;
            }

            bool https;
            int schemeChars;
            if (span[start..].StartsWith("http:", StringComparison.OrdinalIgnoreCase))
            {
                https = false;
                schemeChars = "http:".Length;
            }
            else if (span[start..].StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                https = true;
                schemeChars = "https:".Length;
            }
            else
            {
                return AbsoluteHttpTargetRecognition.NotAbsolute;
            }

            int authorityStart = start + schemeChars;
            if (authorityStart + 2 > span.Length ||
                !IsUriSlash(span[authorityStart]) ||
                !IsUriSlash(span[authorityStart + 1]))
            {
                return AbsoluteHttpTargetRecognition.NotAbsolute;
            }

            authorityStart += 2;
            int authorityLength = BoundedAuthorityLength(span[authorityStart..], maxAuthorityChars);
            target = new AbsoluteHttpTarget(https, authorityStart, authorityLength);
            return AbsoluteHttpTargetRecognition.Recognized;
        }

        // Snapshot admission reads at most a 512-character absolute authority, or a 255-character structured host and
        // five-character port. A longer or invalid authority, or one whose international host name Uri does not convert to
        // ASCII (ReplayAuthority.HasIdnHost), is not offered for Load. Only the authority prefix is parsed, so an arbitrarily
        // large path costs no more than the displayed preview.
        internal static ReplayCaptureDestinationStatus ClassifyCaptureDestination(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            string path = capture.Path ?? string.Empty;
            AbsoluteHttpTargetRecognition recognition =
                RecognizeAbsoluteHttpTarget(path, MaxAbsoluteAuthorityChars, out AbsoluteHttpTarget target);
            if (recognition == AbsoluteHttpTargetRecognition.Undecided)
            {
                return ReplayCaptureDestinationStatus.Unreadable;
            }

            if (recognition == AbsoluteHttpTargetRecognition.Recognized)
            {
                if (target.AuthorityLength < 0)
                {
                    return ReplayCaptureDestinationStatus.Unreadable;
                }

                ReadOnlySpan<char> authority = path.AsSpan(target.AuthorityStart, target.AuthorityLength);
                string scheme = target.Https ? "https://" : "http://";
                string probe = string.Concat(scheme, authority.ToString(), "/");
                if (!Uri.TryCreate(probe, UriKind.Absolute, out Uri uri) || !ReplayAuthority.HasIdnHost(uri))
                {
                    return ReplayCaptureDestinationStatus.Unreadable;
                }

                return IsScopedIpv6(uri)
                    ? ReplayCaptureDestinationStatus.ScopedIpv6
                    : ReplayCaptureDestinationStatus.Loadable;
            }

            string host = capture.Host ?? string.Empty;
            string port = capture.Port ?? string.Empty;
            if (host.Length > MaxCaptureHostChars || port.Length > MaxCapturePortChars)
            {
                return ReplayCaptureDestinationStatus.Unreadable;
            }

            try
            {
                string authority = RequestReplayDraft.CaptureAuthority(capture);
                if (Uri.TryCreate(authority, UriKind.Absolute, out Uri uri) && !ReplayAuthority.HasIdnHost(uri))
                {
                    return ReplayCaptureDestinationStatus.Unreadable;
                }

                return IsScopedIpv6(authority)
                    ? ReplayCaptureDestinationStatus.ScopedIpv6
                    : ReplayCaptureDestinationStatus.Loadable;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                return ReplayCaptureDestinationStatus.Unreadable;
            }
        }

        // The canonical authority that Uri produces, with only its retained IPv6 zone put back into the host.
        internal static string AuthorityKeepingZone(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            bool scoped = IsScopedIpv6(uri);
            string authority = uri.GetLeftPart(UriPartial.Authority);
            return scoped ? PutZoneBack(uri, authority) : authority;
        }

        // The canonical absolute URL that Uri produces, with only its retained IPv6 zone put back into the host.
        internal static string AbsoluteUriKeepingZone(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            bool scoped = IsScopedIpv6(uri);
            string absolute = uri.AbsoluteUri;
            if (!scoped)
            {
                return absolute;
            }

            string authority = uri.GetLeftPart(UriPartial.Authority);
            if (!absolute.StartsWith(authority, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The absolute URL does not start with its authority.");
            }

            string scopedAuthority = PutZoneBack(uri, authority);
            return string.Concat(scopedAuthority.AsSpan(), absolute.AsSpan(authority.Length));
        }

        internal static string ToStringKeepingZone(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            string text = uri.ToString();
            if (!IsScopedIpv6(uri))
            {
                return text;
            }

            return PutZoneBackInDisplayText(uri, text) ?? AbsoluteUriKeepingZone(uri);
        }

        private static int BoundedAuthorityLength(ReadOnlySpan<char> text, int maxAuthorityChars)
        {
            int examined = Math.Min(text.Length, maxAuthorityChars + 1);
            int delimiter = text[..examined].IndexOfAny("/?#".AsSpan());
            if (delimiter >= 0)
            {
                return delimiter;
            }

            return text.Length <= maxAuthorityChars ? text.Length : -1;
        }

        private static bool IsUriLeadingWhitespace(char value) => value is ' ' or '\t' or '\r' or '\n';

        private static bool IsUriSlash(char value) => value is '/' or '\\';

        private static string PutZoneBackInDisplayText(Uri uri, string text)
        {
            int authorityStart = text.IndexOf("://", StringComparison.Ordinal);
            if (authorityStart < 0)
            {
                return null;
            }

            authorityStart += 3;
            int tail = text.AsSpan(authorityStart).IndexOfAny("/?#".AsSpan());
            int authorityEnd = tail < 0 ? text.Length : authorityStart + tail;
            if (authorityEnd <= authorityStart)
            {
                return null;
            }

            string host = uri.Host;
            int hostStart = text.LastIndexOf(
                host, authorityEnd - 1, authorityEnd - authorityStart, StringComparison.OrdinalIgnoreCase);
            if (hostStart < authorityStart)
            {
                return null;
            }

            string scopedHost = "[" + uri.IdnHost + "]";
            return string.Concat(
                text.AsSpan(0, hostStart), scopedHost.AsSpan(), text.AsSpan(hostStart + host.Length));
        }

        private static string PutZoneBack(Uri uri, string authority)
        {
            string host = uri.Host;
            int hostStart = authority.LastIndexOf(host, StringComparison.OrdinalIgnoreCase);
            if (hostStart < 0)
            {
                throw new InvalidOperationException("The URL authority does not contain its host.");
            }

            string scopedHost = "[" + uri.IdnHost + "]";
            return string.Concat(
                authority.AsSpan(0, hostStart), scopedHost.AsSpan(), authority.AsSpan(hostStart + host.Length));
        }
    }
}
