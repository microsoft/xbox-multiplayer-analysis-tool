// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.Operations.Replay
{
    // The challenges of a WWW-Authenticate or Proxy-Authenticate value as a Windows-authentication run records them: a
    // Negotiate, NTLM or Kerberos challenge keeps only its scheme name, its token or parameters dropped, since they can
    // carry a server's authentication blob; any other challenge is kept as received. The value is read as RFC 9110
    // §11.6.1's challenge list exactly, and any doubt drops the whole value:
    //   challenge = auth-scheme [ 1*SP ( token68 / #auth-param ) ]
    //   auth-param = token BWS "=" BWS ( token / quoted-string )
    // The list's members are split on commas outside quoted strings (a quoted-pair escapes one character) and empty members
    // skipped; a member that is an auth-param belongs to the challenge before it, and any other member starts a challenge.
    // A challenge takes parameters only after the SP that follows its scheme, so "Basic , realm=x" has one and
    // "Basic, realm=x" is no challenge list; and each parameter name occurs once per challenge (§11.2), in any case.
    // A value is dropped (Sanitize gives null) for an unbalanced quote, a character outside the grammar, a parameter before
    // any challenge, after a challenge's token68 or without that SP, a parameter name that its challenge already has, or no
    // challenge at all.
    internal static class ReplayAuthChallenges
    {
        // Whether a header holds challenges, in any casing of its name.
        internal static bool IsChallengeHeader(string name) =>
            string.Equals(name, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase);

        // The value with each Windows challenge reduced to its scheme name and every other challenge kept as received,
        // joined by ", "; null when the value is not a challenge list.
        internal static string Sanitize(string value)
        {
            if (value == null)
            {
                return null;
            }

            List<(int Start, int End)> members = SplitMembers(value);
            if (members == null)
            {
                return null;
            }

            var kept = new List<string>();
            int challengeStart = -1;
            int challengeEnd = -1;
            int schemeEnd = -1;
            bool windows = false;
            bool takesParameters = false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((int start, int end) in members)
            {
                if (IsAuthParam(value, start, end))
                {
                    if (!takesParameters || !names.Add(NameOf(value, start, end)))
                    {
                        return null;
                    }

                    challengeEnd = end;
                    continue;
                }

                if (!TryChallenge(value, start, end, out int scheme, out int parameter))
                {
                    return null;
                }

                if (challengeStart >= 0)
                {
                    kept.Add(windows ? value[challengeStart..schemeEnd] : value[challengeStart..challengeEnd]);
                }

                challengeStart = start;
                challengeEnd = end;
                schemeEnd = scheme;
                windows = IsWindowsScheme(value[start..scheme]);
                names.Clear();
                if (parameter >= 0)
                {
                    names.Add(NameOf(value, parameter, end));
                }

                // A challenge that is its scheme alone takes the parameters of the members after it only when an SP follows
                // its scheme; one with a token68 takes none.
                takesParameters = parameter >= 0 || (scheme == end && scheme < value.Length && value[scheme] == ' ');
            }

            if (challengeStart < 0)
            {
                return null;
            }

            kept.Add(windows ? value[challengeStart..schemeEnd] : value[challengeStart..challengeEnd]);
            return string.Join(", ", kept);
        }

        private static bool IsWindowsScheme(string scheme) =>
            scheme.Equals("Negotiate", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("NTLM", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("Kerberos", StringComparison.OrdinalIgnoreCase);

        // The list's members without the optional whitespace around them, empty ones skipped; null for an unbalanced quote
        // or a quoted-pair that escapes nothing.
        private static List<(int Start, int End)> SplitMembers(string value)
        {
            var members = new List<(int, int)>();
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quoted)
                {
                    if (c == '\\')
                    {
                        if (++i == value.Length)
                        {
                            return null;
                        }
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    AddMember(value, start, i, members);
                    start = i + 1;
                }
            }

            if (quoted)
            {
                return null;
            }

            AddMember(value, start, value.Length, members);
            return members;
        }

        private static void AddMember(string value, int start, int end, List<(int, int)> members)
        {
            while (start < end && IsWhitespace(value[start]))
            {
                start++;
            }

            while (end > start && IsWhitespace(value[end - 1]))
            {
                end--;
            }

            if (start < end)
            {
                members.Add((start, end));
            }
        }

        // auth-param = token BWS "=" BWS ( token / quoted-string ), the whole member.
        private static bool IsAuthParam(string value, int start, int end)
        {
            int i = SkipToken(value, start, end);
            if (i == start)
            {
                return false;
            }

            i = SkipWhitespace(value, i, end);
            if (i == end || value[i] != '=')
            {
                return false;
            }

            i = SkipWhitespace(value, i + 1, end);
            if (i == end)
            {
                return false;
            }

            int after = value[i] == '"' ? SkipQuotedString(value, i, end) : SkipToken(value, i, end);
            return after > i && after == end;
        }

        // challenge = auth-scheme [ 1*SP ( token68 / auth-param ) ], the whole member: the first of a challenge's
        // auth-params shares its member with the scheme. scheme is where the scheme ends; parameter is where that first
        // auth-param starts, or -1 when the member is the scheme alone or the scheme and a token68.
        private static bool TryChallenge(string value, int start, int end, out int scheme, out int parameter)
        {
            parameter = -1;
            scheme = SkipToken(value, start, end);
            if (scheme == start)
            {
                return false;
            }

            if (scheme == end)
            {
                return true;
            }

            int i = scheme;
            while (i < end && value[i] == ' ')
            {
                i++;
            }

            if (i == scheme || i == end)
            {
                return false;
            }

            if (IsToken68(value, i, end))
            {
                return true;
            }

            if (!IsAuthParam(value, i, end))
            {
                return false;
            }

            parameter = i;
            return true;
        }

        // The name of the auth-param that a member is, from start: its leading token.
        private static string NameOf(string value, int start, int end) => value[start..SkipToken(value, start, end)];

        // token68 = 1*( ALPHA / DIGIT / "-" / "." / "_" / "~" / "+" / "/" ) *"=", the rest of the member.
        private static bool IsToken68(string value, int start, int end)
        {
            int i = start;
            while (i < end && IsToken68Character(value[i]))
            {
                i++;
            }

            if (i == start)
            {
                return false;
            }

            while (i < end && value[i] == '=')
            {
                i++;
            }

            return i == end;
        }

        private static int SkipToken(string value, int start, int end)
        {
            int i = start;
            while (i < end && IsTokenCharacter(value[i]))
            {
                i++;
            }

            return i;
        }

        // DQUOTE *( qdtext / quoted-pair ) DQUOTE from start; where it ends, or start when it is not one.
        private static int SkipQuotedString(string value, int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                char c = value[i];
                if (c == '"')
                {
                    return i + 1;
                }

                if (c == '\\')
                {
                    if (++i == end || !IsQuotedPairCharacter(value[i]))
                    {
                        return start;
                    }
                }
                else if (!IsQuotedTextCharacter(c))
                {
                    return start;
                }
            }

            return start;
        }

        private static int SkipWhitespace(string value, int start, int end)
        {
            int i = start;
            while (i < end && IsWhitespace(value[i]))
            {
                i++;
            }

            return i;
        }

        private static bool IsWhitespace(char c) => c is ' ' or '\t';

        // tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "." / "^" / "_" / "`" / "|" / "~" / DIGIT / ALPHA
        private static bool IsTokenCharacter(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or
                '_' or '`' or '|' or '~';

        private static bool IsToken68Character(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/';

        // qdtext = HTAB / SP / %x21 / %x23-5B / %x5D-7E / obs-text
        private static bool IsQuotedTextCharacter(char c) =>
            c is '\t' or ' ' or '!' || (c >= '#' && c <= '[') || (c >= ']' && c <= '~') || (c >= '\u0080' && c <= '\u00FF');

        // quoted-pair = "\" ( HTAB / SP / VCHAR / obs-text )
        private static bool IsQuotedPairCharacter(char c) =>
            c is '\t' or ' ' || (c >= '!' && c <= '~') || (c >= '\u0080' && c <= '\u00FF');
    }
}
