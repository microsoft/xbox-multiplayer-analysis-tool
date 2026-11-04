// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class InputRegexTests
    {
        [Theory]
        [InlineData("abc|def", "def")]
        [InlineData("ab?c", "ac")]
        [InlineData("ab{0}c", "ac")]
        [InlineData("(abc)?def", "def")]
        [InlineData("a\\|b", "a|b")]
        [InlineData("^prefix.+suffix", "prefix-value-suffix")]
        public void RequiredLiteralPrefilterDoesNotDropValidMatches(
            string pattern,
            string input)
        {
            var matcher = new InputRegex(pattern);

            Assert.True(matcher.IsMatch(input));
            Assert.True(matcher.MatcherCreated);
        }

        [Fact]
        public void MissingRequiredLiteralSkipsExactMatcherConstruction()
        {
            var matcher =
                new InputRegex(
                    "https://one\\.example/users/.+");

            Assert.False(
                matcher.IsMatch(
                    "https://two.example/users/1"));
            Assert.False(matcher.MatcherCreated);
        }
    }
}
