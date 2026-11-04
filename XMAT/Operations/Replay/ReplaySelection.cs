// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XMAT.Operations.Replay
{
    // The captured requests to replay, by request number: "all", or a comma-separated list of numbers (5) and inclusive
    // ranges (9-12). Numbers named on their own must exist in the capture; a range selects the requests it contains.
    internal sealed class ReplaySelection
    {
        internal const string AllText = "all";

        // At most this many numbers may be named, ranges included.
        internal const long MaxNumbers = 10_000;

        // Request numbers are positive and at most this long, so no run of digits can overflow.
        private const int MaxDigits = 15;

        private readonly HashSet<long> _named;

        // Sorted by First and merged, so Contains can search them.
        private readonly (long First, long Last)[] _ranges;

        private ReplaySelection(bool isAll, IReadOnlyList<long> numbers, IEnumerable<(long First, long Last)> ranges)
        {
            IsAll = isAll;
            Numbers = numbers;
            _named = new HashSet<long>(numbers);
            _ranges = Merge(ranges);
        }

        internal static ReplaySelection All { get; } = new(true, Array.Empty<long>(), Array.Empty<(long, long)>());

        internal bool IsAll { get; }

        // The numbers named on their own, in the order given, without repeats.
        internal IReadOnlyList<long> Numbers { get; }

        internal static ReplaySelection Of(params long[] numbers) =>
            new(false, numbers.Distinct().ToList(), Array.Empty<(long, long)>());

        // True when the number was named on its own rather than through a range or "all".
        internal bool Names(long number) => _named.Contains(number);

        internal bool Contains(long number)
        {
            if (IsAll || _named.Contains(number))
            {
                return true;
            }

            int low = 0;
            int high = _ranges.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (number < _ranges[middle].First)
                {
                    high = middle - 1;
                }
                else if (number > _ranges[middle].Last)
                {
                    low = middle + 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool TryParse(string text, out ReplaySelection selection)
        {
            selection = null;
            if (string.Equals(text, AllText, StringComparison.Ordinal))
            {
                selection = All;
                return true;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var numbers = new List<long>();
            var seen = new HashSet<long>();
            var ranges = new List<(long First, long Last)>();
            long count = 0;
            foreach (string item in text.Split(','))
            {
                int dash = item.IndexOf('-');
                if (dash < 0)
                {
                    if (!TryParseNumber(item, out long number))
                    {
                        return false;
                    }

                    if (seen.Add(number))
                    {
                        numbers.Add(number);
                    }

                    count++;
                }
                else
                {
                    if (!TryParseNumber(item[..dash], out long first) ||
                        !TryParseNumber(item[(dash + 1)..], out long last) ||
                        last < first ||
                        last - first >= MaxNumbers)
                    {
                        return false;
                    }

                    ranges.Add((first, last));
                    count += last - first + 1;
                }

                if (count > MaxNumbers)
                {
                    return false;
                }
            }

            selection = new ReplaySelection(false, numbers, ranges);
            return true;
        }

        // ASCII digits only, without a sign, space or leading zero.
        private static bool TryParseNumber(string text, out long number)
        {
            number = 0;
            if (text.Length == 0 || text.Length > MaxDigits || text[0] == '0' || !text.All(char.IsAsciiDigit))
            {
                return false;
            }

            number = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        private static (long First, long Last)[] Merge(IEnumerable<(long First, long Last)> ranges)
        {
            var merged = new List<(long First, long Last)>();
            foreach ((long first, long last) in ranges.OrderBy(range => range.First))
            {
                if (merged.Count > 0 && first <= merged[^1].Last + 1)
                {
                    merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, last));
                }
                else
                {
                    merged.Add((first, last));
                }
            }

            return merged.ToArray();
        }
    }
}
