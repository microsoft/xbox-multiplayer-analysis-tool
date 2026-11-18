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

    // Which destination a captured request has (ReplayDestination.ReadCaptureDestination): the absolute http or https URL
    // in its target, which wins; the stored scheme, host and port, which its target then follows as a path; or none, a
    // malformed destination, including one that the decision's limit refuses.
    internal enum CaptureDestinationKind
    {
        Absolute,
        Structured,
        Malformed,
    }

    // A captured request's destination, decided once for each of its uses. Authority is the URL of its scheme and
    // authority, with an IPv6 zone as stored, and null when the destination is malformed; for the structured branch,
    // AuthorityText is that authority as RequestReplayDraft.CaptureAuthority writes it, which a run's URL starts with.
    // LimitExceeded tells a malformed destination that the decision's limit refused (ReplayDestination
    // .MaxCaptureDestinationChars) from one whose text makes no URL authority.
    internal readonly record struct CaptureDestination(
        CaptureDestinationKind Kind, Uri Authority, string AuthorityText, bool LimitExceeded = false)
    {
        internal static CaptureDestination Malformed { get; } = new(CaptureDestinationKind.Malformed, null, null);

        internal static CaptureDestination OverLimit { get; } = new(CaptureDestinationKind.Malformed, null, null, true);

        internal bool IsMalformed => Kind == CaptureDestinationKind.Malformed;
    }

    // A replay destination's local IPv6 zone is data that XMAT may retain, but never a destination that it sends to.
    internal static class ReplayDestination
    {
        internal const string ScopedIpv6Code = "replay.destination.scoped-ipv6";
        internal const string ScopedIpv6Message =
            "IPv6 zone identifiers are not supported for sending requests or exporting cURL commands.";

        // The most UTF-16 code units that a captured request's destination decision reads (ReadCaptureDestination): the
        // head of an absolute http or https target, from its leading URI whitespace through the first terminator of its
        // authority, and each stored scheme, host and port. A longer head or field makes the destination malformed. No real
        // authority comes near it: it limits the decision's work, not a URL's length.
        internal const int MaxCaptureDestinationChars = 8192;

        private const int MaxLeadingWhitespaceChars = 32;

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

        // Whether a captured request's destination (ReadCaptureDestination) is an IPv6 address with a zone, as the URL that a
        // run makes of it is; a malformed destination is none.
        internal static bool IsScopedIpv6(ProxyConnectionModel capture) =>
            capture != null && ReadCaptureDestination(capture) is { IsMalformed: false } destination &&
            IsScopedIpv6(destination.Authority);

        // A captured request's destination, decided once for every use: the run, which makes its URL from it
        // (RequestReplayDraft.CaptureUrl); Reissue's admission before anything is asked
        // (ReplayOperation.CheckCaptureDestinations); the origin that Reissue counts; and History (ClassifyCaptureDestination).
        // An absolute http or https URL in the target wins. A target whose complete head Uri refuses as one falls back to
        // the stored scheme, host and port (RequestReplayDraft.CaptureAuthority), which the target then follows as a path.
        // Only the head is read: Uri gives a target's head, through the first '/', '?' or '#' that ends its authority, the
        // verdict and authority that it gives the whole target, whatever follows; a '\' there, or a '/' inside the brackets of
        // an IPv6 address, makes both fail. A head, or a stored scheme, host or port, longer than MaxCaptureDestinationChars
        // makes the destination malformed, and is never a reason to fall back. Neither the full URL nor the body is made.
        internal static CaptureDestination ReadCaptureDestination(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            switch (ReadAbsoluteHead(capture.Path ?? string.Empty, out string head))
            {
                case AbsoluteHeadReading.OverLimit:
                    return CaptureDestination.OverLimit;
                case AbsoluteHeadReading.Complete:
                    if (Uri.TryCreate(head, UriKind.Absolute, out Uri absolute) &&
                        (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                    {
                        return new CaptureDestination(CaptureDestinationKind.Absolute, absolute, null);
                    }

                    break;
            }

            if (StoredFieldsExceedLimit(capture))
            {
                return CaptureDestination.OverLimit;
            }

            try
            {
                string authority = RequestReplayDraft.CaptureAuthority(capture);
                return Uri.TryCreate(authority, UriKind.Absolute, out Uri structured)
                    ? new CaptureDestination(CaptureDestinationKind.Structured, structured, authority)
                    : CaptureDestination.Malformed;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                return CaptureDestination.Malformed;
            }
        }

        private enum AbsoluteHeadReading
        {
            NotAbsolute,
            Complete,
            OverLimit,
        }

        // Whether a capture's stored scheme, host or port is longer than a destination decision reads, which is checked
        // before anything parses them.
        internal static bool StoredFieldsExceedLimit(ProxyConnectionModel capture) =>
            (capture.Scheme?.Length ?? 0) > MaxCaptureDestinationChars ||
            (capture.Host?.Length ?? 0) > MaxCaptureDestinationChars ||
            (capture.Port?.Length ?? 0) > MaxCaptureDestinationChars;

        // The head of a target that may be an absolute http or https URL, read within MaxCaptureDestinationChars, one code
        // unit at a time: the leading URI whitespace that Uri trims, "http:" or "https:" in ASCII letters of either case,
        // the slashes and backslashes after it, and the authority through its first terminator, which the head includes:
        // '/', '?', '#' or '\' outside brackets, or '/' inside them. NotAbsolute as soon as the target shows that it is no
        // such URL, OverLimit when the head would need more code units than the limit, and Complete otherwise: a target
        // that ends within the limit without a terminator is its own head.
        private static AbsoluteHeadReading ReadAbsoluteHead(string text, out string head)
        {
            head = null;
            int index = 0;
            while (true)
            {
                if (index >= text.Length)
                {
                    return AbsoluteHeadReading.NotAbsolute;
                }

                if (index >= MaxCaptureDestinationChars)
                {
                    return AbsoluteHeadReading.OverLimit;
                }

                if (!IsUriLeadingWhitespace(text[index]))
                {
                    break;
                }

                index++;
            }

            foreach (char expected in "http")
            {
                if (index >= text.Length || (index < MaxCaptureDestinationChars && !IsAsciiLetter(text[index], expected)))
                {
                    return AbsoluteHeadReading.NotAbsolute;
                }

                if (index >= MaxCaptureDestinationChars)
                {
                    return AbsoluteHeadReading.OverLimit;
                }

                index++;
            }

            if (index < text.Length && index < MaxCaptureDestinationChars && IsAsciiLetter(text[index], 's'))
            {
                index++;
            }

            if (index >= text.Length || (index < MaxCaptureDestinationChars && text[index] != ':'))
            {
                return AbsoluteHeadReading.NotAbsolute;
            }

            if (index >= MaxCaptureDestinationChars)
            {
                return AbsoluteHeadReading.OverLimit;
            }

            index++;
            bool slashes = true;
            bool bracketed = false;
            for (; index < text.Length; index++)
            {
                if (index >= MaxCaptureDestinationChars)
                {
                    return AbsoluteHeadReading.OverLimit;
                }

                char value = text[index];
                if (slashes && IsUriSlash(value))
                {
                    continue;
                }

                slashes = false;
                if (value == '[')
                {
                    bracketed = true;
                }
                else if (value == ']')
                {
                    bracketed = false;
                }
                else if (value == '/' || (!bracketed && value is '?' or '#' or '\\'))
                {
                    head = text[..(index + 1)];
                    return AbsoluteHeadReading.Complete;
                }
            }

            head = text;
            return AbsoluteHeadReading.Complete;
        }

        private static bool IsAsciiLetter(char value, char lowerCase) =>
            value == lowerCase || value == char.ToUpperInvariant(lowerCase);

        // Whether History shows a target as stored rather than as a URL that it previews (ReplayHistory): the implicit HTTP
        // forms that Uri accepts, up to 32 leading URI whitespace characters, then http(s), ':', and either slash spelling
        // in both positions. More leading URI whitespace is Undecided, which History also shows as stored. Only the preview
        // reads this; a request's destination is ReadCaptureDestination's.
        internal static AbsoluteHttpTargetRecognition RecognizeAbsoluteHttpTarget(string text)
        {
            if (string.IsNullOrEmpty(text))
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

            int schemeChars;
            if (span[start..].StartsWith("http:", StringComparison.OrdinalIgnoreCase))
            {
                schemeChars = "http:".Length;
            }
            else if (span[start..].StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                schemeChars = "https:".Length;
            }
            else
            {
                return AbsoluteHttpTargetRecognition.NotAbsolute;
            }

            int authorityStart = start + schemeChars;
            return authorityStart + 2 <= span.Length &&
                IsUriSlash(span[authorityStart]) &&
                IsUriSlash(span[authorityStart + 1])
                    ? AbsoluteHttpTargetRecognition.Recognized
                    : AbsoluteHttpTargetRecognition.NotAbsolute;
        }

        // History's status for a row, derived from its destination decision (ReadCaptureDestination), so that Load, Reissue and
        // a run agree on it: Unreadable when the destination is malformed, its limit included, or Uri gives its host no ASCII
        // form (ReplayAuthority.HasIdnHost), as for an international host name that Uri does not convert; ScopedIpv6 for an
        // IPv6 address with a zone; Loadable otherwise. Only the destination's head and stored fields are read, so an
        // arbitrarily large path costs no more than the displayed preview.
        internal static ReplayCaptureDestinationStatus ClassifyCaptureDestination(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            CaptureDestination destination = ReadCaptureDestination(capture);
            if (destination.IsMalformed || !ReplayAuthority.HasIdnHost(destination.Authority))
            {
                return ReplayCaptureDestinationStatus.Unreadable;
            }

            return IsScopedIpv6(destination.Authority)
                ? ReplayCaptureDestinationStatus.ScopedIpv6
                : ReplayCaptureDestinationStatus.Loadable;
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
