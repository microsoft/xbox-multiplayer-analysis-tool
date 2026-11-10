// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using XMAT.Operations.Inspection;

namespace XMAT.Operations.Replay
{
    // A hash of a line with its break, or of a JSON member name (break 0): VerifyDiffTextSide.LineHash, or for names the
    // ordinal string hash, unless a test gives one. It only tells texts apart: texts with equal hashes are compared.
    internal delegate int VerifyDiffHasher(ReadOnlySpan<char> text, byte lineBreak);

    // A side's examined bytes read as text by BodyTextRules (strictly, in the charset that the rules choose), split into at
    // most MaxLines lines, each with the break that ends it: "\r\n", "\n", "\r", "\u2028", "\u2029" or none.
    internal sealed class VerifyDiffTextSide
    {
        // The break that ends a line, as an index into BreakTexts.
        private const byte NoBreak = 0;
        private const byte CarriageReturnLineFeed = 1;
        private const byte LineFeed = 2;
        private const byte CarriageReturn = 3;
        private const byte LineSeparator = 4;
        private const byte ParagraphSeparator = 5;

        private static readonly string[] BreakTexts = { string.Empty, "\r\n", "\n", "\r", "\u2028", "\u2029" };

        // A line's start, length, break (an index into BreakTexts) and hash: 4 + 4 + 1 + 4 bytes of scratch.
        private const int LineBytes = 13;

        private readonly char[] _chars;
        private readonly int[] _starts;
        private readonly int[] _lengths;
        private readonly byte[] _breaks;
        private readonly int[] _hashes;

        private VerifyDiffTextSide(
            string charset, int preamble, char[] chars, int[] starts, int[] lengths, byte[] breaks, int[] hashes, bool whole)
        {
            Charset = charset;
            Preamble = preamble;
            _chars = chars;
            _starts = starts;
            _lengths = lengths;
            _breaks = breaks;
            _hashes = hashes;
            Whole = whole;
        }

        internal string Charset { get; }

        // The length of the byte order mark that the text was read after.
        internal int Preamble { get; }

        internal int Count => _starts.Length;

        // Whether the lines are all of the side's compared text: its compared bytes were all examined and decoded, and no
        // line was left out (MaxLines).
        internal bool Whole { get; }

        internal ReadOnlySpan<char> Line(int index) => _chars.AsSpan(_starts[index], _lengths[index]);

        internal string Break(int index) => BreakTexts[_breaks[index]];

        // The examined bytes as text, or null when they do not read as text: a charset that the rules do not decode,
        // invalid text, or a control character other than tab, CR and LF. allExamined says that the examined bytes are all
        // of the compared bytes; otherwise the text may end inside a character, which is then left out. Charged to work;
        // the decoded text and the line table are reserved as scratch before they are allocated. read is how many of the
        // examined bytes were decoded: all of them once they decoded as text, otherwise none.
        internal static VerifyDiffTextSide TryRead(
            VerifyBody side,
            ReadOnlySpan<byte> examined,
            bool allExamined,
            VerifyDiffBudget budget,
            VerifyDiffHasher hasher,
            out long read)
        {
            read = 0;
            BodyTextFormat format =
                BodyTextRules.Read(side.ContentType, BodyPreviewLimits.Default.MaxHeaderChars, examined);
            if (!format.Supported)
            {
                return null;
            }

            ReadOnlySpan<byte> bytes = examined[format.Preamble..];
            int most = format.Encoding.GetMaxCharCount(bytes.Length);
            try
            {
                budget.SpendOrStop(bytes.Length);
                budget.ReserveOrStop((long)most * sizeof(char));
            }
            catch (VerifyDiffLimitException)
            {
                return null;
            }

            var chars = new char[most];
            int length;
            try
            {
                format.Encoding.GetDecoder().Convert(bytes, chars, allExamined, out _, out length, out _);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }

            read = examined.Length;

            ReadOnlySpan<char> text = chars.AsSpan(0, length);

            // Read for control characters, then twice for line breaks, then hashed.
            if (!budget.Spend(4L * length) || BodyTextRules.HasNotText(text))
            {
                return null;
            }

            int lines = 0;
            bool more = false;
            for (int position = 0; position < length;)
            {
                if (lines == budget.Limits.MaxLines)
                {
                    more = true;
                    break;
                }

                position = NextLine(text, position, out _, out _);
                lines++;
            }

            if (!allExamined && !more && lines > 0)
            {
                // The text stops where the examined bytes stop, maybe inside its last line, which is left out.
                lines--;
            }

            try
            {
                budget.ReserveOrStop((long)lines * LineBytes);
            }
            catch (VerifyDiffLimitException)
            {
                return null;
            }

            var starts = new int[lines];
            var lengths = new int[lines];
            var breaks = new byte[lines];
            var hashes = new int[lines];
            VerifyDiffHasher hash = hasher ?? new VerifyDiffHasher(LineHash);
            int start = 0;
            for (int line = 0; line < lines; line++)
            {
                int next = NextLine(text, start, out int textLength, out byte lineBreak);
                starts[line] = start;
                lengths[line] = textLength;
                breaks[line] = lineBreak;
                hashes[line] = hash(text.Slice(start, textLength), lineBreak);
                start = next;
            }

            if (more)
            {
                budget.Limit(VerifyDiffLimitation.LineLimit);
            }

            return new VerifyDiffTextSide(
                format.Charset, format.Preamble, chars, starts, lengths, breaks, hashes, allExamined && !more);
        }

        // A line's hash, which only tells lines apart: lines with equal hashes are compared by their text.
        internal static int LineHash(ReadOnlySpan<char> text, byte lineBreak) =>
            HashCode.Combine(string.GetHashCode(text), lineBreak);

        // Whether line i of a and line j of b are equal, breaks included: a step, and their text compared when the hashes
        // match, since equal hashes do not prove equal lines.
        internal static bool Equal(VerifyDiffTextSide a, int i, VerifyDiffTextSide b, int j, VerifyDiffBudget budget)
        {
            budget.StepOrStop();
            if (a._hashes[i] != b._hashes[j] || a._breaks[i] != b._breaks[j] || a._lengths[i] != b._lengths[j])
            {
                return false;
            }

            budget.SpendOrStop(a._lengths[i]);
            return a.Line(i).SequenceEqual(b.Line(j));
        }

        // The line that starts at start: its text length, its break, and where the next line starts.
        private static int NextLine(ReadOnlySpan<char> text, int start, out int textLength, out byte lineBreak)
        {
            for (int position = start; position < text.Length; position++)
            {
                switch (text[position])
                {
                    case '\r':
                        textLength = position - start;
                        if (position + 1 < text.Length && text[position + 1] == '\n')
                        {
                            lineBreak = CarriageReturnLineFeed;
                            return position + 2;
                        }

                        lineBreak = CarriageReturn;
                        return position + 1;

                    case '\n':
                        textLength = position - start;
                        lineBreak = LineFeed;
                        return position + 1;

                    case '\u2028':
                        textLength = position - start;
                        lineBreak = LineSeparator;
                        return position + 1;

                    case '\u2029':
                        textLength = position - start;
                        lineBreak = ParagraphSeparator;
                        return position + 1;
                }
            }

            textLength = text.Length - start;
            lineBreak = NoBreak;
            return text.Length;
        }
    }

    // The text view: the lines of both sides compared whole, with their breaks. The common first and last lines are set
    // aside, then the lines between them are compared with Myers' algorithm, whose frontiers are reserved as scratch
    // before they are allocated; a limit that stops it leaves one coarse hunk for those lines, which is still a correct
    // difference. Hunks have up to ContextLines unchanged lines around their changes.
    internal static class VerifyDiffLines
    {
        private enum Op : byte
        {
            Equal,
            Delete,
            Insert,
        }

        internal static VerifyTextDiff Diff(VerifyDiffTextSide a, VerifyDiffTextSide b, VerifyDiffBudget budget)
        {
            IReadOnlyList<VerifyTextHunk> none = Array.Empty<VerifyTextHunk>();
            int n = a.Count;
            int m = b.Count;
            int prefix = 0;
            try
            {
                while (prefix < n && prefix < m && VerifyDiffTextSide.Equal(a, prefix, b, prefix, budget))
                {
                    prefix++;
                }
            }
            catch (VerifyDiffLimitException)
            {
                // A limit stopped the comparison before it found a difference: the texts are not known to differ.
                return new VerifyTextDiff(VerifyEquivalence.Unknown, none);
            }

            // The texts are known to differ when two lines differ, or when a side that ends where its lines end has fewer
            // lines. A side that was not compared in full may go on as the other does, so it proves nothing by ending.
            bool differ =
                (prefix < n && prefix < m) ||
                (prefix == n && prefix < m && a.Whole) ||
                (prefix == m && prefix < n && b.Whole);
            if (!differ)
            {
                return new VerifyTextDiff(
                    prefix == n && prefix == m && a.Whole && b.Whole ? VerifyEquivalence.Same : VerifyEquivalence.Unknown,
                    none);
            }

            int suffix = 0;
            try
            {
                while (suffix < n - prefix && suffix < m - prefix &&
                    VerifyDiffTextSide.Equal(a, n - 1 - suffix, b, m - 1 - suffix, budget))
                {
                    suffix++;
                }
            }
            catch (VerifyDiffLimitException)
            {
                // The common last lines found so far stand; the lines between them are only more.
            }

            List<Op> middle = Myers(a, b, prefix, n - suffix, prefix, m - suffix, budget) ??
                Coarse(n - suffix - prefix, m - suffix - prefix);
            var ops = new List<Op>(prefix + middle.Count + suffix);
            for (int index = 0; index < prefix; index++)
            {
                ops.Add(Op.Equal);
            }

            ops.AddRange(middle);
            for (int index = 0; index < suffix; index++)
            {
                ops.Add(Op.Equal);
            }

            return new VerifyTextDiff(VerifyEquivalence.Different, Hunks(ops, a, b, budget));
        }

        // Every line of the first side removed, then every line of the second added.
        private static List<Op> Coarse(int deleted, int inserted)
        {
            var ops = new List<Op>(deleted + inserted);
            for (int index = 0; index < deleted; index++)
            {
                ops.Add(Op.Delete);
            }

            for (int index = 0; index < inserted; index++)
            {
                ops.Add(Op.Insert);
            }

            return ops;
        }

        // The shortest edit script from a[a0..a1) to b[b0..b1), or null when a limit stopped it. Frontier d holds, for each
        // diagonal k from -d to d, the furthest x that d edits reach on it, or -1 when none on the grid does.
        private static List<Op> Myers(
            VerifyDiffTextSide a, VerifyDiffTextSide b, int a0, int a1, int b0, int b1, VerifyDiffBudget budget)
        {
            int n = a1 - a0;
            int m = b1 - b0;
            var trace = new List<int[]>();
            long reserved = 0;
            try
            {
                int[] previous = null;
                for (int d = 0; d <= n + m; d++)
                {
                    long bytes = ((2L * d) + 1) * sizeof(int);
                    budget.ReserveOrStop(bytes);
                    reserved += bytes;
                    var frontier = new int[(2 * d) + 1];
                    for (int k = -d; k <= d; k += 2)
                    {
                        budget.StepOrStop();
                        int x = Start(previous, d, k, n, m, out _);
                        if (x < 0)
                        {
                            frontier[k + d] = -1;
                            continue;
                        }

                        int y = x - k;
                        while (x < n && y < m && VerifyDiffTextSide.Equal(a, a0 + x, b, b0 + y, budget))
                        {
                            x++;
                            y++;
                        }

                        frontier[k + d] = x;
                        if (x == n && y == m)
                        {
                            trace.Add(frontier);
                            return Backtrack(trace, n, m);
                        }
                    }

                    trace.Add(frontier);
                    previous = frontier;
                }
            }
            catch (VerifyDiffLimitException)
            {
                return null;
            }
            finally
            {
                // The trace is not kept once the script is built.
                budget.Release(reserved);
            }

            return null;
        }

        // Where diagonal k starts at d edits: down from diagonal k + 1 (an insertion) or right from k - 1 (a deletion),
        // whichever reaches further on the grid, down when both reach as far; -1 when neither is on the grid.
        private static int Start(int[] previous, int d, int k, int n, int m, out bool down)
        {
            down = false;
            if (d == 0)
            {
                return 0;
            }

            int best = -1;
            if (k + 1 <= d - 1)
            {
                int x = previous[k + 1 + d - 1];
                if (x >= 0 && x - k <= m)
                {
                    best = x;
                    down = true;
                }
            }

            if (k - 1 >= -(d - 1))
            {
                int x = previous[k - 1 + d - 1];
                if (x >= 0 && x + 1 <= n && x + 1 > best)
                {
                    best = x + 1;
                    down = false;
                }
            }

            return best;
        }

        // The edit script that the trace's last frontier reached (n, m) with, by the choices that Start made.
        private static List<Op> Backtrack(List<int[]> trace, int n, int m)
        {
            var ops = new List<Op>(n + m);
            int x = n;
            int y = m;
            for (int d = trace.Count - 1; d > 0; d--)
            {
                int k = x - y;
                int startX = Start(trace[d - 1], d, k, n, m, out bool down);
                while (x > startX)
                {
                    ops.Add(Op.Equal);
                    x--;
                    y--;
                }

                if (down)
                {
                    ops.Add(Op.Insert);
                    y--;
                }
                else
                {
                    ops.Add(Op.Delete);
                    x--;
                }
            }

            while (x > 0)
            {
                ops.Add(Op.Equal);
                x--;
            }

            ops.Reverse();
            return ops;
        }

        // The hunks of an edit script over all of the lines compared: each change with up to ContextLines unchanged lines
        // around it, changes that fewer than twice as many unchanged lines separate in one hunk. MaxHunks and the characters
        // bound them; a hunk cut by the characters keeps the lines that it shows, and its counts are of those lines.
        private static IReadOnlyList<VerifyTextHunk> Hunks(
            List<Op> ops, VerifyDiffTextSide a, VerifyDiffTextSide b, VerifyDiffBudget budget)
        {
            int context = budget.Limits.ContextLines;
            var hunks = new List<VerifyTextHunk>();
            long tables = 2L * sizeof(int) * (ops.Count + 1);
            try
            {
                budget.ReserveOrStop(tables);
            }
            catch (VerifyDiffLimitException)
            {
                return hunks.AsReadOnly();
            }

            var aBefore = new int[ops.Count + 1];
            var bBefore = new int[ops.Count + 1];
            for (int index = 0; index < ops.Count; index++)
            {
                aBefore[index + 1] = aBefore[index] + (ops[index] != Op.Insert ? 1 : 0);
                bBefore[index + 1] = bBefore[index] + (ops[index] != Op.Delete ? 1 : 0);
            }

            int next = 0;
            while (next < ops.Count)
            {
                int first = ops.FindIndex(next, op => op != Op.Equal);
                if (first < 0)
                {
                    break;
                }

                int last = first;
                for (int scan = first + 1; scan < ops.Count && scan - last <= 2 * context; scan++)
                {
                    if (ops[scan] != Op.Equal)
                    {
                        last = scan;
                    }
                }

                if (hunks.Count == budget.Limits.MaxHunks)
                {
                    budget.Limit(VerifyDiffLimitation.HunkLimit);
                    break;
                }

                int from = Math.Max(next, first - context);
                int to = Math.Min(ops.Count - 1, last + context);
                var lines = new List<VerifyTextLine>();
                int aCount = 0;
                int bCount = 0;
                bool stopped = false;
                for (int index = from; index <= to; index++)
                {
                    Op op = ops[index];
                    VerifyDiffTextSide side = op == Op.Insert ? b : a;
                    int line = op == Op.Insert ? bBefore[index] : aBefore[index];

                    // The break is shown too: it is counted first, so that a line shows its true break, and its text then
                    // gets what is left.
                    if (!budget.Count(side.Break(line).Length))
                    {
                        stopped = true;
                        break;
                    }

                    string text = budget.Take(side.Line(line), out bool cut);
                    if (text == null)
                    {
                        stopped = true;
                        break;
                    }

                    VerifyLineKind kind = op switch
                    {
                        Op.Delete => VerifyLineKind.Removed,
                        Op.Insert => VerifyLineKind.Added,
                        _ => VerifyLineKind.Context,
                    };
                    lines.Add(new VerifyTextLine(kind, text, side.Break(line), cut));
                    aCount += op != Op.Insert ? 1 : 0;
                    bCount += op != Op.Delete ? 1 : 0;
                }

                if (lines.Count > 0)
                {
                    // As in a unified diff, a side with no lines in the hunk names the line that the hunk follows.
                    hunks.Add(new VerifyTextHunk(
                        aCount > 0 ? aBefore[from] + 1 : aBefore[from],
                        aCount,
                        bCount > 0 ? bBefore[from] + 1 : bBefore[from],
                        bCount,
                        lines.AsReadOnly()));
                }

                if (stopped)
                {
                    break;
                }

                next = to + 1;
            }

            budget.Release(tables);
            return hunks.AsReadOnly();
        }
    }
}
