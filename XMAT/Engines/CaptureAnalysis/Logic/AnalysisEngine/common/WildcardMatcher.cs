// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace CaptureAnalysisEngine
{
    internal static class WildcardMatcher
    {
        // Matches the exact language produced by Regex.Escape(pattern), followed by the
        // legacy '?' -> '.' and '*' -> '.*' substitutions, under default Regex options.
        internal static bool IsMatch(
            string pattern,
            string input)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(input);
            return MatchCore(
                    pattern,
                    input.AsSpan()) ||
                (input.EndsWith('\n') &&
                 MatchCore(
                     pattern,
                     input.AsSpan(
                         0,
                         input.Length - 1)));
        }

        private static bool MatchCore(
            string pattern,
            ReadOnlySpan<char> input)
        {
            var patternIndex = 0;
            var inputIndex = 0;
            var starIndex = -1;
            var retryInputIndex = 0;

            while (inputIndex < input.Length)
            {
                if (patternIndex < pattern.Length &&
                    pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    retryInputIndex = inputIndex;
                    continue;
                }
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] ==
                        input[inputIndex] ||
                     (pattern[patternIndex] == '?' &&
                      input[inputIndex] != '\n')))
                {
                    patternIndex++;
                    inputIndex++;
                    continue;
                }
                if (starIndex >= 0 &&
                    retryInputIndex < input.Length &&
                    input[retryInputIndex] != '\n')
                {
                    patternIndex = starIndex + 1;
                    inputIndex = ++retryInputIndex;
                    continue;
                }
                return false;
            }

            while (patternIndex < pattern.Length &&
                pattern[patternIndex] == '*')
            {
                patternIndex++;
            }
            return patternIndex == pattern.Length;
        }
    }
}
