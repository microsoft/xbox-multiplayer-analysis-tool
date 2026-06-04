// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class BlockListModelTests
    {
        [Fact]
        public void Add_TrimsAndDeduplicates()
        {
            var list = new BlockListModel();
            Assert.True(list.Add("  example.com  "));
            Assert.False(list.Add("example.com"));
            Assert.False(list.Add("EXAMPLE.COM"));
            Assert.Single(list.BlockedUrls);
            Assert.Equal("example.com", list.BlockedUrls[0]);
        }

        [Fact]
        public void Add_RejectsEmptyOrWhitespace()
        {
            var list = new BlockListModel();
            Assert.False(list.Add(null));
            Assert.False(list.Add(""));
            Assert.False(list.Add("   "));
            Assert.Empty(list.BlockedUrls);
        }

        [Fact]
        public void IsBlocked_ExactHostMatchIsCaseInsensitive()
        {
            var list = new BlockListModel();
            list.Add("example.com");
            Assert.True(list.IsBlocked("example.com"));
            Assert.True(list.IsBlocked("EXAMPLE.COM"));
            Assert.False(list.IsBlocked("sub.example.com"));
            Assert.False(list.IsBlocked("other.com"));
        }

        [Fact]
        public void IsBlocked_WildcardMatchesSubdomains()
        {
            var list = new BlockListModel();
            list.Add("*.example.com");
            Assert.True(list.IsBlocked("api.example.com"));
            Assert.True(list.IsBlocked("a.b.example.com"));
            Assert.False(list.IsBlocked("example.com"));
            Assert.False(list.IsBlocked("notexample.com"));
        }

        [Fact]
        public void IsBlocked_WildcardMatchesIpPrefix()
        {
            var list = new BlockListModel();
            list.Add("192.168.1.*");
            Assert.True(list.IsBlocked("192.168.1.5"));
            Assert.True(list.IsBlocked("192.168.1.255"));
            Assert.False(list.IsBlocked("192.168.2.5"));
        }

        [Fact]
        public void IsBlocked_MatchesAgainstHostAndPath()
        {
            var list = new BlockListModel();
            list.Add("example.com/blocked/*");
            Assert.True(list.IsBlocked("example.com", "/blocked/page"));
            Assert.True(list.IsBlocked("example.com", "blocked/page"));
            Assert.False(list.IsBlocked("example.com", "/allowed"));
            Assert.False(list.IsBlocked("example.com"));
        }

        [Fact]
        public void IsBlocked_EmptyHostReturnsFalse()
        {
            var list = new BlockListModel();
            list.Add("*");
            Assert.False(list.IsBlocked(null));
            Assert.False(list.IsBlocked(string.Empty));
        }

        [Fact]
        public void IsBlocked_EmptyBlockListReturnsFalse()
        {
            var list = new BlockListModel();
            Assert.False(list.IsBlocked("example.com"));
        }

        [Fact]
        public void Remove_RemovesPatternCaseInsensitively()
        {
            var list = new BlockListModel();
            list.Add("example.com");
            list.Remove("EXAMPLE.COM");
            Assert.Empty(list.BlockedUrls);
            Assert.False(list.IsBlocked("example.com"));
        }

        [Fact]
        public void Clear_RemovesAllPatterns()
        {
            var list = new BlockListModel();
            list.Add("a.com");
            list.Add("b.com");
            list.Clear();
            Assert.Empty(list.BlockedUrls);
            Assert.False(list.IsBlocked("a.com"));
        }

        [Fact]
        public void IsBlocked_SpecialRegexCharsInPatternAreEscaped()
        {
            var list = new BlockListModel();
            list.Add("example.com");
            // '.' must be a literal, not a regex any-char.
            Assert.False(list.IsBlocked("exampleXcom"));
        }
    }
}
