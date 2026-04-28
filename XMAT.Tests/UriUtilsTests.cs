// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    public class UriUtilsTests
    {
        [Fact]
        public void GetAbsoluteUri_BuildsUri_FromRelativePath()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", "443", "/api/test");
            Assert.Contains("example.com", result);
            Assert.Contains("/api/test", result);
        }

        [Fact]
        public void GetAbsoluteUri_UsesDefaultPort_WhenPortIsEmpty()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", "", "/path");
            Assert.StartsWith("https://example.com/path", result);
        }

        [Fact]
        public void GetAbsoluteUri_UsesDefaultPort_WhenPortIsNull()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", null, "/path");
            Assert.StartsWith("https://example.com/path", result);
        }

        [Fact]
        public void GetAbsoluteUri_IncludesCustomPort()
        {
            string result = UriUtils.GetAbsoluteUri("http", "example.com", "8080", "/path");
            Assert.Contains("8080", result);
        }

        [Fact]
        public void GetAbsoluteUri_PreservesQueryString()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", "", "/path?key=value");
            Assert.Contains("key=value", result);
        }

        [Fact]
        public void GetAbsoluteUri_PreservesFragment()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", "", "/path#section");
            Assert.Contains("section", result);
        }

        [Fact]
        public void GetAbsoluteUri_ReturnsAbsoluteUri_WhenPathIsAlreadyAbsolute()
        {
            string result = UriUtils.GetAbsoluteUri("https", "example.com", "", "https://other.com/api");
            Assert.StartsWith("https://other.com/api", result);
        }

        [Fact]
        public void GetAbsoluteUri_UsesHttpScheme()
        {
            string result = UriUtils.GetAbsoluteUri("http", "example.com", "", "/path");
            Assert.StartsWith("http://example.com/path", result);
        }
    }
}
