// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;

namespace XMAT.Operations.Replay
{
    // The byte view: the examined bytes of both sides scanned side by side, once, for runs of differing bytes, then the
    // bytes that only the longer side has. Every byte compared, the one that ends a run included, and every byte of an
    // excerpt is charged, and the scan never reads more than the work left allows; excerpts are read only from the examined
    // bytes. originalRead and actualRead are how far into each side's bytes it read.
    internal static class VerifyDiffBytes
    {
        internal static VerifyByteDiff Diff(
            ReadOnlySpan<byte> original,
            long originalLength,
            ReadOnlySpan<byte> actual,
            long actualLength,
            VerifyDiffBudget budget,
            out long originalRead,
            out long actualRead)
        {
            var ranges = new List<VerifyByteRange>();
            var reached = new Reached();
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
                Compared(position, Math.Min(window, equal + 1), budget, reached);
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

                Compared(position, Math.Min(window, run + 1), budget, reached);
                if (run == 0 || !Add(ranges, position, run, run, original, actual, budget, reached))
                {
                    stopped = run != 0;
                    break;
                }

                position += run;
            }

            if (!stopped && originalLength != actualLength)
            {
                long offset = Math.Min(originalLength, actualLength);
                Add(ranges, offset, originalLength - offset, actualLength - offset, original, actual, budget, reached);
            }

            originalRead = reached.Original;
            actualRead = reached.Actual;
            return new VerifyByteDiff(originalLength, actualLength, ranges.AsReadOnly());
        }

        // Charges count positions compared from position, which both sides were read up to.
        private static void Compared(int position, int count, VerifyDiffBudget budget, Reached reached)
        {
            budget.Spend(count);
            reached.Original = Math.Max(reached.Original, position + count);
            reached.Actual = Math.Max(reached.Actual, position + count);
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
            VerifyDiffBudget budget,
            Reached reached)
        {
            if (ranges.Count == budget.Limits.MaxChanges)
            {
                budget.Limit(VerifyDiffLimitation.ChangeLimit);
                return false;
            }

            string originalExcerpt = Excerpt(original, offset, originalRun, budget, out long originalTo);
            reached.Original = Math.Max(reached.Original, originalTo);
            string actualExcerpt = null;
            if (originalExcerpt != null)
            {
                actualExcerpt = Excerpt(actual, offset, actualRun, budget, out long actualTo);
                reached.Actual = Math.Max(reached.Actual, actualTo);
            }

            if (actualExcerpt == null)
            {
                return false;
            }

            ranges.Add(new VerifyByteRange(offset, originalRun, actualRun, originalExcerpt, actualExcerpt));
            return true;
        }

        // "XX XX ...": at most ExcerptBytes bytes of a run, read only from the examined bytes and as many as a value and the
        // characters left allow, each byte charged; empty, with WorkLimit, when the work left allows none, and null, with
        // CharLimit, when the characters left allow none. to is how far into the bytes the excerpt read.
        private static string Excerpt(ReadOnlySpan<byte> examined, long offset, long run, VerifyDiffBudget budget, out long to)
        {
            to = 0;
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

            if (!budget.Spend(count))
            {
                return string.Empty;
            }

            to = offset + count;
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

        // How far into each side's bytes the byte view read.
        private sealed class Reached
        {
            internal long Original { get; set; }

            internal long Actual { get; set; }
        }
    }
}
