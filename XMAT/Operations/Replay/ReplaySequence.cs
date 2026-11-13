// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XMAT.Operations.Replay
{
    // Sequential requests, as Fiddler's composer makes them: every '#' in a URL template is replaced by each number from
    // Start to End in turn. A Start written with a leading zero, such as 08, pads every number to its width; a wider
    // number is never cut. The numbers may change only the path and query: a '#' in the scheme, host or port is refused,
    // so a sequence never walks other hosts. "%23" is an escaped '#' and stays as it is.
    internal sealed class ReplaySequence
    {
        internal const char Placeholder = '#';
        internal const int MaxDigits = 15;

        // A sequence has at most as many numbers as a run has sends.
        internal const int MaxValues = ReplayOptions.MaxSends;

        private ReplaySequence(long start, long end, int width)
        {
            Start = start;
            End = end;
            Width = width;
        }

        internal long Start { get; }

        internal long End { get; }

        // The width every number is padded to with leading zeros, or 0 for none.
        internal int Width { get; }

        internal int Count => (int)(End - Start + 1);

        internal IEnumerable<long> Values
        {
            get
            {
                for (long value = Start; value <= End; value++)
                {
                    yield return value;
                }
            }
        }

        // START-END: 1 to 15 ASCII digits each, START no greater than END, at most MaxValues numbers.
        internal static bool TryParse(string text, out ReplaySequence sequence)
        {
            sequence = null;
            int dash = text?.IndexOf('-') ?? -1;
            if (dash < 0 || dash != text.LastIndexOf('-'))
            {
                return false;
            }

            string first = text[..dash];
            string last = text[(dash + 1)..];
            if (!IsNumber(first) || !IsNumber(last))
            {
                return false;
            }

            long start = long.Parse(first, NumberStyles.None, CultureInfo.InvariantCulture);
            long end = long.Parse(last, NumberStyles.None, CultureInfo.InvariantCulture);
            if (start > end || end - start >= MaxValues)
            {
                return false;
            }

            sequence = new ReplaySequence(start, end, first.Length > 1 && first[0] == '0' ? first.Length : 0);
            return true;
        }

        // True when the template has a '#' and every '#' is in its path or query: after "scheme://" and a non-empty
        // authority, which ends at the first '/', '\' or '?' (http and https treat '\' as '/'). An empty authority is
        // refused, because some parsers would take the number for the host.
        internal static bool IsPathAndQueryTemplate(string template)
        {
            if (template == null || !template.Contains(Placeholder))
            {
                return false;
            }

            int schemeEnd = template.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return false;
            }

            int authorityEnd = template.IndexOfAny(new[] { '/', '\\', '?' }, schemeEnd + 3);
            return authorityEnd > schemeEnd + 3 && template.IndexOf(Placeholder, 0, authorityEnd) < 0;
        }

        internal string Format(long value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0');

        // The length of the longest URL that the template expands to: End is the widest number.
        internal long MaxExpandedLength(string template)
        {
            ArgumentNullException.ThrowIfNull(template);
            long placeholders = template.Count(character => character == Placeholder);
            return template.Length + placeholders * (Format(End).Length - 1);
        }

        // The template with every '#' replaced by the number, formatted.
        internal string Expand(string template, long value)
        {
            ArgumentNullException.ThrowIfNull(template);
            return template.Replace(Placeholder.ToString(), Format(value), StringComparison.Ordinal);
        }

        private static bool IsNumber(string text) =>
            text.Length is >= 1 and <= MaxDigits && text.All(character => character is >= '0' and <= '9');
    }
}
