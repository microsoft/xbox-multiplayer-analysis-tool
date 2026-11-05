// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;

namespace XMAT.Operations.Replay
{
    // The byte view: the examined bytes of both sides scanned side by side, once, for runs of differing bytes, then the
    // bytes that only the longer side has. The scan never reads more than the work left allows, and excerpts are read only
    // from the examined bytes.
    internal static class VerifyDiffBytes
    {
        internal static VerifyByteDiff Diff(
            ReadOnlySpan<byte> original,
            long originalLength,
            ReadOnlySpan<byte> actual,
            long actualLength,
            VerifyDiffBudget budget)
        {
            var ranges = new List<VerifyByteRange>();
            int common = Math.Min(original.Length, actual.Length);
            int position = 0;
            bool stopped = false;
            while (position < common)
            {
                int window = Window(common - position, budget);
                if (window == 0)
                {
                    break;
                }

                int equal = original.Slice(position, window).CommonPrefixLength(actual.Slice(position, window));
                budget.Spend(equal);
                position += equal;
                if (equal == window)
                {
                    continue;
                }

                window = Window(common - position, budget);
                int run = 0;
                while (run < window && original[position + run] != actual[position + run])
                {
                    run++;
                }

                budget.Spend(run);
                if (run == 0 || !Add(ranges, position, run, run, original, actual, budget))
                {
                    stopped = run != 0;
                    break;
                }

                position += run;
            }

            if (!stopped && originalLength != actualLength)
            {
                long offset = Math.Min(originalLength, actualLength);
                Add(ranges, offset, originalLength - offset, actualLength - offset, original, actual, budget);
            }

            return new VerifyByteDiff(originalLength, actualLength, ranges.AsReadOnly());
        }

        // How many bytes the work left lets the scan read next, at most wanted; 0, with WorkLimit, when none.
        private static int Window(int wanted, VerifyDiffBudget budget)
        {
            int window = (int)Math.Clamp(budget.Work.Remaining, 0, wanted);
            if (window == 0 && wanted > 0)
            {
                budget.Limit(VerifyDiffLimitation.WorkLimit);
            }

            return window;
        }

        // Adds a range unless MaxChanges or the characters are reached, which end the scan.
        private static bool Add(
            List<VerifyByteRange> ranges,
            long offset,
            long originalRun,
            long actualRun,
            ReadOnlySpan<byte> original,
            ReadOnlySpan<byte> actual,
            VerifyDiffBudget budget)
        {
            if (ranges.Count == budget.Limits.MaxChanges)
            {
                budget.Limit(VerifyDiffLimitation.ChangeLimit);
                return false;
            }

            string originalExcerpt = Excerpt(original, offset, originalRun, budget);
            string actualExcerpt = originalExcerpt == null ? null : Excerpt(actual, offset, actualRun, budget);
            if (actualExcerpt == null)
            {
                return false;
            }

            ranges.Add(new VerifyByteRange(offset, originalRun, actualRun, originalExcerpt, actualExcerpt));
            return true;
        }

        // "XX XX ...": at most ExcerptBytes bytes of a run, read only from the examined bytes and as many as a value and the
        // characters left allow; null, with CharLimit, when the characters left allow none.
        private static string Excerpt(ReadOnlySpan<byte> examined, long offset, long run, VerifyDiffBudget budget)
        {
            int count = offset >= examined.Length
                ? 0
                : (int)Math.Min(Math.Min(run, VerifyDiffLimits.ExcerptBytes), examined.Length - offset);
            if (count == 0)
            {
                return string.Empty;
            }

            // Each byte is two hex digits and a space, but the last.
            long fits = (Math.Min(budget.Limits.MaxValueChars, budget.CharsLeft) + 1) / 3;
            count = (int)Math.Min(count, fits);
            if (count == 0)
            {
                budget.Limit(VerifyDiffLimitation.CharLimit);
                return null;
            }

            var text = new StringBuilder((count * 3) - 1);
            for (int index = 0; index < count; index++)
            {
                byte value = examined[(int)offset + index];
                if (index > 0)
                {
                    text.Append(' ');
                }

                text.Append("0123456789ABCDEF"[value >> 4]).Append("0123456789ABCDEF"[value & 0xF]);
            }

            return budget.Take(text.ToString(), out _);
        }
    }
}
