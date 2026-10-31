// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class WildcardMatcherTests
    {
        [Fact]
        public void MatchesLegacyRegexLanguageForSeededCorpus()
        {
            char[] patternAlphabet =
            {
                'a', 'A', 'z', 'Z', '0', '9',
                '*', '?', '.', '+', '(', ')',
                '[', ']', '{', '}', '^', '$',
                '|', '\\', '#', ' ', '-', '_',
                '\r', '\n',
            };
            char[] inputAlphabet =
            {
                'a', 'A', 'z', 'Z', '0', '9',
                '*', '?', '.', '+', '(', ')',
                '[', ']', '{', '}', '^', '$',
                '|', '\\', '#', ' ', '-', '_',
                '\r', '\n',
            };
            var random = new Random(0x57494C44);

            for (var sample = 0;
                sample < 20_000;
                sample++)
            {
                string pattern = RandomString(
                    random,
                    patternAlphabet,
                    random.Next(0, 13));
                string input = RandomString(
                    random,
                    inputAlphabet,
                    random.Next(0, 21));
                if (sample % 4 == 0)
                {
                    input += "\n";
                }
                string legacyPattern =
                    "^" +
                    Regex.Escape(pattern)
                        .Replace("\\?", ".")
                        .Replace("\\*", ".*") +
                    "$";

                bool expected =
                    Regex.IsMatch(
                        input,
                        legacyPattern);
                bool actual =
                    WildcardMatcher.IsMatch(
                        pattern,
                        input);

                Assert.True(
                    expected == actual,
                    $"Pattern '{Escape(pattern)}', input '{Escape(input)}'.");
            }
        }

        [Theory]
        [InlineData(null, "value")]
        [InlineData("*", null)]
        public void NullArgumentsKeepLegacyFailure(
            string pattern,
            string input)
        {
            Assert.Throws<ArgumentNullException>(
                () => WildcardMatcher.IsMatch(
                    pattern,
                    input));
        }

        private static string RandomString(
            Random random,
            IReadOnlyList<char> alphabet,
            int length)
        {
            var characters = new char[length];
            for (var index = 0;
                index < characters.Length;
                index++)
            {
                characters[index] =
                    alphabet[
                        random.Next(
                            alphabet.Count)];
            }
            return new string(characters);
        }

        private static string Escape(
            string value) =>
            value.Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
    }
}
