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

        [Theory]
        [InlineData("fe80::1%12", "8080", "/path?q=%25", "http://[fe80::1%12]:8080/path?q=%25")]
        [InlineData("fe80::1%Ethernet", "", "/path", "http://[fe80::1%Ethernet]/path")]
        [InlineData("fe80::1%25Ethernet", "", "/path", "http://[fe80::1%25Ethernet]/path")]
        public void GetAbsoluteUri_KeepsAnIpv6Zone(string host, string port, string path, string expected)
        {
            Assert.Equal(expected, UriUtils.GetAbsoluteUri("http", host, port, path));
        }

        [Fact]
        public void GetAbsoluteUri_KeepsTheZoneOfAnAbsoluteTarget()
        {
            Assert.Equal(
                "http://[fe80::1%Ethernet]/absolute?q=%25",
                UriUtils.GetAbsoluteUri("https", "ignored.test", "443",
                    "http://[fe80::1%Ethernet]/absolute?q=%25"));
        }

        [Theory]
        [InlineData("/a%20b")]
        [InlineData("/caf%C3%A9")]
        public void GetAbsoluteUri_UsesTheSameDisplayFormWithAZone(string path)
        {
            string unscoped = new Uri("http://[fe80::1]" + path).ToString();
            string scoped = UriUtils.GetAbsoluteUri("http", "fe80::1%12", "", path);

            Assert.Equal(unscoped.Replace("[fe80::1]", "[fe80::1%12]", StringComparison.Ordinal), scoped);
        }

        [Theory]
        [InlineData("http://a{b@[fe80::1%12]/a%20b")]
        [InlineData("http://caf\u00e9@[fe80::1%12]/a%20b")]
        public void GetAbsoluteUri_KeepsAZoneWhenUserInformationNeedsEscaping(string scopedTarget)
        {
            string unscopedTarget = scopedTarget.Replace("[fe80::1%12]", "[fe80::1]", StringComparison.Ordinal);
            string unscoped = UriUtils.GetAbsoluteUri("http", "ignored.test", "", unscopedTarget);
            string scoped = UriUtils.GetAbsoluteUri("http", "ignored.test", "", scopedTarget);

            Assert.Equal(unscoped.Replace("[fe80::1]", "[fe80::1%12]", StringComparison.Ordinal), scoped);
        }
    }
}
