// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Linq;
using XMAT.Operations.Replay;
using Xunit;

namespace XMAT.Tests.Replay
{
    public class ReplaySequenceTests
    {
        // Fiddler's rule: a Start written with a leading zero pads every number to its width.
        [Theory]
        [InlineData("08-11", "08,09,10,11")]
        [InlineData("8-11", "8,9,10,11")]
        [InlineData("0-2", "0,1,2")]
        [InlineData("00-2", "00,01,02")]
        [InlineData("098-101", "098,099,100,101")]
        [InlineData("7-7", "7")]
        public void Values_AreNumberedAndPaddedLikeFiddler(string text, string expected)
        {
            Assert.True(ReplaySequence.TryParse(text, out ReplaySequence sequence));
            Assert.Equal(expected, string.Join(",", sequence.Values.Select(sequence.Format)));
            Assert.Equal(sequence.Values.Count(), sequence.Count);
        }

        [Fact]
        public void WiderNumbers_AreNeverCut()
        {
            Assert.True(ReplaySequence.TryParse("08-100", out ReplaySequence sequence));
            Assert.Equal(93, sequence.Count);
            Assert.Equal("08", sequence.Format(sequence.Start));
            Assert.Equal("100", sequence.Format(sequence.End));
        }

        [Fact]
        public void TryParse_AcceptsTheBoundaries()
        {
            Assert.True(ReplaySequence.TryParse("1-10000", out ReplaySequence most));
            Assert.Equal(10_000, most.Count);
            Assert.True(ReplaySequence.TryParse("999999999999990-999999999999999", out ReplaySequence widest));
            Assert.Equal(10, widest.Count);
            Assert.True(ReplaySequence.TryParse("000000000000000-000000000000001", out ReplaySequence padded));
            Assert.Equal("000000000000001", padded.Format(padded.End));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("5")]
        [InlineData("5-")]
        [InlineData("-5")]
        [InlineData("5-1")]
        [InlineData("1-2-3")]
        [InlineData(" 1-2")]
        [InlineData("1-2 ")]
        [InlineData("+1-2")]
        [InlineData("1-0x2")]
        [InlineData("\uFF11-2")]
        [InlineData("1\u20132")]
        [InlineData("1234567890123456-1234567890123456")]
        [InlineData("1-10001")]
        public void TryParse_RejectsAnythingElse(string text) => Assert.False(ReplaySequence.TryParse(text, out _));

        // The numbers may change only the path and query, after a non-empty authority.
        [Theory]
        [InlineData("http://h.test/#", true)]
        [InlineData("http://h.test/item/#?page=#", true)]
        [InlineData("http://h.test?q=#", true)]
        [InlineData("http://h.test\\#", true)]
        [InlineData("https://[::1]:8443/#", true)]
        [InlineData("http://h.test/", false)]
        [InlineData("http://h.test/%23", false)]
        [InlineData("http://#.test/", false)]
        [InlineData("http://h#.test/", false)]
        [InlineData("http://h.test:#/", false)]
        [InlineData("http://u#@h.test/", false)]
        [InlineData("ht#tp://h.test/", false)]
        [InlineData("http://h.test#", false)]
        [InlineData("http:///#", false)]
        [InlineData("h.test/#", false)]
        [InlineData(null, false)]
        public void Template_HasPlaceholdersOnlyInThePathAndQuery(string template, bool valid) =>
            Assert.Equal(valid, ReplaySequence.IsPathAndQueryTemplate(template));

        // Every '#' gets the same number; "%23" is an escaped '#' and stays as it is.
        [Fact]
        public void Expand_ReplacesEveryPlaceholder_AndKeepsEscapedOnes()
        {
            Assert.True(ReplaySequence.TryParse("08-11", out ReplaySequence sequence));

            Assert.Equal("http://h.test/%23/09?a=09&b=09", sequence.Expand("http://h.test/%23/#?a=#&b=#", 9));
        }

        // The longest expansion uses the widest number, End, at every placeholder.
        [Theory]
        [InlineData("08-100", "http://h.test/#?a=#")]
        [InlineData("8-11", "http://h.test/#?a=#")]
        [InlineData("000-7", "http://h.test/#")]
        public void MaxExpandedLength_IsTheLengthOfTheLastUrl(string range, string template)
        {
            Assert.True(ReplaySequence.TryParse(range, out ReplaySequence sequence));

            Assert.Equal(sequence.Expand(template, sequence.End).Length, sequence.MaxExpandedLength(template));
        }
    }
}
