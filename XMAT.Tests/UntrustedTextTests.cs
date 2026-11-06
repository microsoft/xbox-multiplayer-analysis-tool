// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // UntrustedText.ShowParts: text from outside XMAT shown escaped, as Show shows it, but never longer than its bound, the
    // ellipsis included, and read from parts as if they were joined.
    public class UntrustedTextTests
    {
        private const string Ellipsis = "\u2026";

        // H14: the bound holds the ellipsis too; text that fits shows whole, without one; no bound shows nothing.
        [Theory]
        [InlineData(0, new[] { "" }, "")]
        [InlineData(0, new[] { "a" }, "")]
        [InlineData(1, new[] { "a" }, "a")]
        [InlineData(1, new[] { "ab" }, Ellipsis)]
        [InlineData(2, new[] { "ab" }, "ab")]
        [InlineData(2, new[] { "abc" }, "a" + Ellipsis)]
        [InlineData(3, new[] { "ab", "c" }, "abc")]
        [InlineData(3, new[] { "ab", "cd" }, "ab" + Ellipsis)]
        [InlineData(5, new[] { "", null, "ab", "", "c" }, "abc")]
        public void ShowParts_KeepsItsBound_EllipsisIncluded(int maxChars, string[] parts, string shown)
        {
            Assert.Equal(shown, UntrustedText.ShowParts(maxChars, parts));
        }

        // H14: an escape is shown whole or not at all: one that does not fit, or that the ellipsis needs the room of, goes.
        [Theory]
        [InlineData(8, "ab\u0001", "ab\\u0001")]
        [InlineData(7, "ab\u0001", "ab" + Ellipsis)]
        [InlineData(7, "a\u0001b", "a" + Ellipsis)]
        [InlineData(13, "\u0001\u0002b", "\\u0001\\u0002b")]
        [InlineData(12, "\u0001\u0002b", "\\u0001" + Ellipsis)]
        public void ShowParts_ShowsAnEscapeWholeOrNotAtAll(int maxChars, string text, string shown)
        {
            Assert.Equal(shown, UntrustedText.ShowParts(maxChars, text));
        }

        // H14: the two halves of a character in two parts are one character, as in the joined text, also at the bound; a
        // half alone is escaped.
        [Fact]
        public void ShowParts_ReadsACharacterAcrossParts_AndEscapesLoneHalves()
        {
            const string Smile = "\uD83D\uDE00";
            Assert.Equal("x" + Smile + "y", UntrustedText.ShowParts(10, "x\uD83D", "\uDE00y"));
            Assert.Equal("x" + Smile + "y", UntrustedText.ShowParts(10, "x\uD83D", "", null, "\uDE00y"));
            Assert.Equal("x" + Ellipsis, UntrustedText.ShowParts(2, "x\uD83D", "\uDE00"));
            Assert.Equal("x" + Smile, UntrustedText.ShowParts(3, "x\uD83D", "\uDE00"));
            Assert.Equal("\\uD83Dx", UntrustedText.ShowParts(20, "\uD83D", "x"));
            Assert.Equal("\\uDE00", UntrustedText.ShowParts(20, "\uDE00"));
            Assert.Equal("\\uD83D", UntrustedText.ShowParts(20, "\uD83D"));
            // A pair that is not shown as itself, here a private-use character, is escaped as its two units together.
            Assert.Equal("\\uDB80\\uDC00", UntrustedText.ShowParts(12, "\uDB80", "\uDC00"));
            Assert.Equal(Ellipsis, UntrustedText.ShowParts(11, "\uDB80", "\uDC00"));
        }

        // H14: however the text is split into parts, it shows as it does joined; what fits shows as Show shows it, and what
        // does not is a whole-piece prefix of that, then the ellipsis, within the bound.
        [Fact]
        public void ShowParts_ShowsPartsAsTheJoinedText_AndAsShowWhenItFits()
        {
            var random = new Random(4321);
            string[] pieces = { "a", "Z", " ", "\u00A0", "\t", "\u0001", "\u200E", "\u0301", "\uD83D", "\uDE00", "\uD83D\uDE00", ":", "/" };
            for (int sample = 0; sample < 3000; sample++)
            {
                var text = new StringBuilder();
                for (int count = random.Next(10); count > 0; count--)
                {
                    text.Append(pieces[random.Next(pieces.Length)]);
                }

                string joined = text.ToString();
                int maxChars = random.Next(25);
                string[] parts = Split(joined, random);
                string shown = UntrustedText.ShowParts(maxChars, parts);

                Assert.Equal(UntrustedText.ShowParts(maxChars, joined), shown);
                Assert.True(shown.Length <= maxChars, $"{shown.Length} > {maxChars}");
                string whole = UntrustedText.Show(joined, int.MaxValue);
                if (whole.Length <= maxChars)
                {
                    Assert.Equal(whole, shown);
                }
                else if (maxChars > 0)
                {
                    Assert.EndsWith(Ellipsis, shown, StringComparison.Ordinal);
                    Assert.StartsWith(shown[..^1], whole, StringComparison.Ordinal);
                }
            }
        }

        // Show is as it was: its ellipsis can follow maxChars characters.
        [Fact]
        public void Show_IsUnchanged()
        {
            Assert.Equal("ab" + Ellipsis, UntrustedText.Show("abc", 2));
            Assert.Equal("ab", UntrustedText.Show("ab", 2));
            Assert.Equal("\\u0001" + Ellipsis, UntrustedText.Show("\u0001b", 6));
        }

        private static string[] Split(string text, Random random)
        {
            var parts = new List<string>();
            int start = 0;
            for (int count = random.Next(4); count > 0 && start < text.Length; count--)
            {
                int end = random.Next(start, text.Length + 1);
                parts.Add(text[start..end]);
                start = end;
            }

            parts.Add(text[start..]);
            return parts.ToArray();
        }
    }
}
