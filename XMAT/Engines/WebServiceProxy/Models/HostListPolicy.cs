// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace XMAT.WebServiceCapture.Models
{
    /// <summary>
    /// The one validator and normalizer behind the Block and Bypass lists and their editors,
    /// used for adding, exact membership, removal and matching. DNS hosts keep their trimmed
    /// spelling and compare without regard to case, but a final dot is significant. IP
    /// literals have one canonical spelling, so "[::1]", "::1" and "0:0::1" are the same entry.
    /// Text containing '*' or '/' stays an opaque glob pattern in which colons and brackets are
    /// literal; it is matched against bracket-free IPv6 text, so IPv6 patterns omit brackets.
    /// </summary>
    internal static class HostListEntry
    {
        private static readonly char[] PatternCharacters = { '*', '/' };

        internal static bool TryNormalize(string text, out string entry, out string error)
        {
            entry = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.Trim();
            if (value.IndexOfAny(PatternCharacters) >= 0)
            {
                entry = value;
                return true;
            }
            if (HostAuthority.TryNormalizeHost(value, out string host))
            {
                entry = host;
                return true;
            }
            if (IsPortAttempt(value))
            {
                // Entries apply to every port; silently dropping the port would broaden the rule.
                error = Localization.GetLocalizedString("HOST_LIST_PORT_NOT_ALLOWED", value);
                return false;
            }

            // Any other text keeps the existing literal pattern behavior.
            entry = value;
            return true;
        }

        // A bracketed token followed by ':' is a port attempt whatever it contains, and a host
        // followed by ':' is one whatever follows it, so "[example.com]:443", "example.com:" and
        // "[::1]:notaport" are rejected like "example.com:443". A complete bare IPv6 literal
        // never gets here: it is a valid host whose last group is not a port.
        private static bool IsPortAttempt(string value)
        {
            int colon;
            if (value[0] == '[')
            {
                colon = value.IndexOf(']') + 1;
                return colon > 1 && colon < value.Length && value[colon] == ':';
            }

            colon = value.IndexOf(':');
            return colon > 0 && HostAuthority.TryNormalizeHost(value[..colon], out _);
        }

        // The host form compared with patterns: the host identity if there is one, otherwise the
        // host of an authority as sent (trimmed, without its port), otherwise the trimmed text.
        // DNS spelling and a final dot are kept so that dotted and dotless names match different
        // patterns, and IPv6 is canonical text without brackets.
        internal static string MatchCandidate(string host)
        {
            if (HostAuthority.TryNormalizeHost(host, out string identity) ||
                HostAuthority.TryParse(host, out identity, out _))
            {
                return identity;
            }
            return host?.Trim();
        }

        internal static Regex CreatePattern(string entry) =>
            new("^" + Regex.Escape(entry).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The application's session-only Block and Bypass lists, shared by every capture and
    /// analysis view of one application instance. Nothing here is persisted.
    /// </summary>
    public sealed class HostListPolicy
    {
        public BlockListModel BlockList { get; } = new BlockListModel();
        public BypassListModel BypassList { get; } = new BypassListModel();
    }
}
