// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using XMAT.Operations.Inspection;

namespace XMAT.Operations.Replay
{
    // What one verification detail may still do and show, and the limitations that it met: the work (shared with the end
    // checks), the steps, the scratch and the characters. Each is checked before what it pays for; the methods that end
    // with "OrStop" throw VerifyDiffLimitException, after they record the limitation, to end the stage that called them.
    internal sealed class VerifyDiffBudget
    {
        private readonly List<string> _limitations = new();
        private long _steps;
        private long _scratch;
        private long _chars;

        internal VerifyDiffBudget(VerifyDiffLimits limits, CancellationToken cancellationToken)
        {
            Limits = limits;
            Work = new BodyPreviewWork(limits.MaxWork, cancellationToken);
        }

        internal VerifyDiffLimits Limits { get; }

        internal BodyPreviewWork Work { get; }

        internal IReadOnlyList<string> Limitations => _limitations;

        // The characters that the detail may still show.
        internal long CharsLeft => Limits.MaxChars - _chars;

        internal void Limit(string code)
        {
            if (!_limitations.Contains(code))
            {
                _limitations.Add(code);
            }
        }

        // One comparison; StepLimit once MaxSteps were taken. Cancellation is checked every 1,024 steps.
        internal void StepOrStop()
        {
            if ((_steps & 1023) == 0)
            {
                Work.CheckCancellation();
            }

            if (_steps >= Limits.MaxSteps)
            {
                Limit(VerifyDiffLimitation.StepLimit);
                throw new VerifyDiffLimitException();
            }

            _steps++;
        }

        // Reserves bytes of scratch before they are allocated; ScratchLimit when they do not fit.
        internal void ReserveOrStop(long bytes)
        {
            if (bytes > Limits.MaxScratchBytes - _scratch)
            {
                Limit(VerifyDiffLimitation.ScratchLimit);
                throw new VerifyDiffLimitException();
            }

            _scratch += bytes;
        }

        // Returns scratch that a stage no longer holds.
        internal void Release(long bytes) => _scratch = Math.Max(0, _scratch - bytes);

        // Spends units of work before they are done; false, with WorkLimit, when they are more than is left.
        internal bool Spend(long units)
        {
            Work.CheckCancellation();
            if (units <= 0)
            {
                return true;
            }

            if (units > Work.Remaining)
            {
                Limit(VerifyDiffLimitation.WorkLimit);
                return false;
            }

            Work.Spend(units);
            return true;
        }

        internal void SpendOrStop(long units)
        {
            if (!Spend(units))
            {
                throw new VerifyDiffLimitException();
            }
        }

        // Text for the detail: at most MaxValueChars of it and what MaxChars leaves, never half of a surrogate pair. cut when
        // it is only the start: ValueCut when the text is longer than MaxValueChars, CharLimit when MaxChars cut it. Null,
        // with CharLimit, when no characters are left.
        internal string Take(ReadOnlySpan<char> text, out bool cut)
        {
            cut = false;
            if (text.IsEmpty)
            {
                return string.Empty;
            }

            long left = Limits.MaxChars - _chars;
            if (left <= 0)
            {
                Limit(VerifyDiffLimitation.CharLimit);
                return null;
            }

            int allowed = (int)Math.Min(text.Length, Math.Min(Limits.MaxValueChars, left));
            if (allowed < text.Length)
            {
                cut = true;
                if (text.Length > Limits.MaxValueChars)
                {
                    Limit(VerifyDiffLimitation.ValueCut);
                }

                if (left < Math.Min(text.Length, Limits.MaxValueChars))
                {
                    Limit(VerifyDiffLimitation.CharLimit);
                }

                if (char.IsHighSurrogate(text[allowed - 1]))
                {
                    allowed--;
                }
            }

            _chars += allowed;
            return new string(text[..allowed]);
        }

        internal string TakeOrStop(ReadOnlySpan<char> text, out bool cut) =>
            Take(text, out cut) ?? throw new VerifyDiffLimitException();
    }

    // A ceiling of VerifyDiffLimits ended a stage of the detail; the budget recorded which.
    internal sealed class VerifyDiffLimitException : Exception
    {
    }
}
