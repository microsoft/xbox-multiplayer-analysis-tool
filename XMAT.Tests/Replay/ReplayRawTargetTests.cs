// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // The target of a raw request (Raw mode): the text that is accepted, its canonical form, its identity, the
    // origin of a Parsed URL, and the addresses that no request may be sent to.
    public sealed class ReplayRawTargetTests
    {
        [Theory]
        [InlineData("http://example.test", "http", "example.test", 80, "example.test", "http://example.test:80")]
        [InlineData("http://example.test/", "http", "example.test", 80, "example.test", "http://example.test:80")]
        [InlineData("HTTPS://Example.TEST:8443/", "https", "example.test", 8443, "example.test:8443", "https://example.test:8443")]
        [InlineData("https://example.test:443", "https", "example.test", 443, "example.test", "https://example.test:443")]
        [InlineData("http://example.test:443", "http", "example.test", 443, "example.test:443", "http://example.test:443")]
        [InlineData("http://localhost:1", "http", "localhost", 1, "localhost:1", "http://localhost:1")]
        [InlineData("http://127.0.0.1:65535", "http", "127.0.0.1", 65535, "127.0.0.1:65535", "http://127.0.0.1:65535")]
        [InlineData("https://[2001:DB8::1]", "https", "2001:db8::1", 443, "[2001:db8::1]", "https://[2001:db8::1]:443")]
        [InlineData("http://[::1]:8080/", "http", "::1", 8080, "[::1]:8080", "http://[::1]:8080")]
        [InlineData("http://b\u00fccher.example", "http", "xn--bcher-kva.example", 80, "xn--bcher-kva.example",
            "http://xn--bcher-kva.example:80")]
        [InlineData("http://xn--bcher-kva.example", "http", "xn--bcher-kva.example", 80, "xn--bcher-kva.example",
            "http://xn--bcher-kva.example:80")]
        public void Target_AcceptsAnOrigin_InCanonicalForm(
            string text, string scheme, string host, int port, string authority, string canonical)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));

            Assert.Equal((scheme, host, port), (target.Scheme, target.Host, target.Port));
            Assert.Equal((authority, canonical), (target.Authority, target.ToString()));
            Assert.Equal(canonical, target.Scheme + "://" + target.AuthorityWithPort);
            Assert.Equal(host, target.TlsName);
            Assert.Equal(scheme + "://" + authority + "/", target.Origin.AbsoluteUri);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("example.test")]
        [InlineData("ftp://example.test")]
        [InlineData("ws://example.test")]
        [InlineData("http:/example.test")]
        [InlineData("http://")]
        [InlineData("http:///")]
        [InlineData("http://:80")]
        [InlineData("http://user@example.test")]
        [InlineData("http://user:pass@example.test")]
        [InlineData("http://example.test/path")]
        [InlineData("http://example.test/?q=1")]
        [InlineData("http://example.test?q=1")]
        [InlineData("http://example.test#f")]
        [InlineData("http://example.test//")]
        [InlineData("http://example.test:")]
        [InlineData("http://example.test:0")]
        [InlineData("http://example.test:65536")]
        [InlineData("http://example.test:080")]
        [InlineData("http://example.test:+80")]
        [InlineData("http://example.test:80:80")]
        [InlineData("http://example.test.")]
        [InlineData("http://example..test")]
        [InlineData("http://-example.test")]
        [InlineData("http://example-.test")]
        [InlineData("http://exa_mple.test")]
        [InlineData("http://example.123")]
        [InlineData("http://example.0x10")]
        [InlineData("http://127.1")]
        [InlineData("http://127.0.0.1.5")]
        [InlineData("http://010.0.0.1")]
        [InlineData("http://256.0.0.1")]
        [InlineData("http://2130706433")]
        [InlineData("http://0x7f.0.0.1")]
        [InlineData(" http://example.test")]
        [InlineData("http://example.test ")]
        [InlineData("http://exa mple.test")]
        [InlineData("http://exa\tmple.test")]
        [InlineData("http://exa\u0007mple.test")]
        [InlineData("http://example.test\\")]
        [InlineData("http:\\\\example.test")]
        [InlineData("http://example%2Etest")]
        [InlineData("http://[fe80::1%253]:80")]
        [InlineData("http://[::1]8080")]
        [InlineData("http://[::1]:")]
        [InlineData("http://[127.0.0.1]:80")]
        [InlineData("http://[]:80")]
        [InlineData("http://::1:80")]
        [InlineData("http://\uFFFD.test")]
        public void Target_RefusesEverythingElse(string text) => Assert.False(ReplayRawTarget.TryParse(text, out _));

        [Fact]
        public void Target_HostNamesKeepToDnsLengths()
        {
            string label = new('a', 63);
            string longest = string.Join('.', label, label, label, new string('b', 61));

            Assert.Equal(253, longest.Length);
            Assert.True(ReplayRawTarget.TryParse("https://" + longest, out _));
            Assert.False(ReplayRawTarget.TryParse("https://" + longest + "c", out _));
            Assert.False(ReplayRawTarget.TryParse("https://" + new string('a', 64) + ".test", out _));
        }

        [Fact]
        public void Target_IsEqual_ByScheme_HostAndPort()
        {
            Assert.True(ReplayRawTarget.TryParse("http://Example.test", out ReplayRawTarget first));
            Assert.True(ReplayRawTarget.TryParse("HTTP://example.TEST:80/", out ReplayRawTarget same));
            Assert.True(ReplayRawTarget.TryParse("https://example.test:80", out ReplayRawTarget otherScheme));
            Assert.True(ReplayRawTarget.TryParse("http://example.test:81", out ReplayRawTarget otherPort));
            Assert.True(ReplayRawTarget.TryParse("http://www.example.test", out ReplayRawTarget otherHost));

            Assert.Equal(first, same);
            Assert.Equal(first.GetHashCode(), same.GetHashCode());
            Assert.NotEqual(first, otherScheme);
            Assert.NotEqual(first, otherPort);
            Assert.NotEqual(first, otherHost);
        }

        [Theory]
        [InlineData("https://Example.test:8443/a/b?q=1#f", "https://example.test:8443")]
        [InlineData("http://example.test/", "http://example.test:80")]
        [InlineData("http://b\u00fccher.example/x", "http://xn--bcher-kva.example:80")]
        [InlineData("https://[2001:db8::1]:9443/x", "https://[2001:db8::1]:9443")]
        [InlineData("http://127.0.0.1:8080/x", "http://127.0.0.1:8080")]
        // Uri reads a legacy IPv4 form as the address it means, which is where a Parsed send of that URL goes.
        [InlineData("http://127.1/x", "http://127.0.0.1:80")]
        public void FromUrl_GivesTheUrlsOrigin(string url, string canonical) =>
            Assert.Equal(canonical, ReplayRawTarget.FromUrl(new Uri(url)).ToString());

        [Theory]
        [InlineData("http://user@example.test/")]
        [InlineData("ftp://example.test/")]
        [InlineData("http://[fe80::1%253]/")]
        public void FromUrl_GivesNothing_ForAnOriginThatRawModeRefuses(string url) =>
            Assert.Null(ReplayRawTarget.FromUrl(new Uri(url)));

        [Fact]
        public void FromUrl_GivesNothing_ForNoUrl() => Assert.Null(ReplayRawTarget.FromUrl(null));

        [Theory]
        [InlineData("http://0.0.0.0", true)]
        [InlineData("http://224.0.0.1", true)]
        [InlineData("http://239.255.255.250", true)]
        [InlineData("http://255.255.255.255", true)]
        [InlineData("http://[::]", true)]
        [InlineData("http://[ff02::1]", true)]
        [InlineData("http://[::ffff:224.0.0.1]", true)]
        [InlineData("http://[::ffff:0.0.0.0]", true)]
        [InlineData("http://127.0.0.1", false)]
        [InlineData("http://192.0.2.1", false)]
        [InlineData("http://[::1]", false)]
        [InlineData("http://[::ffff:127.0.0.1]", false)]
        [InlineData("http://example.test", false)]
        public void Target_DeniesUnspecifiedMulticastAndBroadcastAddresses(string text, bool denied)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));

            Assert.Equal(denied, target.IsDenied);
        }

        [Fact]
        public void Target_GivesTheAddressOfAnIpLiteralOnly()
        {
            Assert.True(ReplayRawTarget.TryParse("http://192.0.2.7", out ReplayRawTarget literal));
            Assert.True(ReplayRawTarget.TryParse("http://example.test", out ReplayRawTarget name));

            Assert.Equal(System.Net.IPAddress.Parse("192.0.2.7"), literal.Address);
            Assert.True(literal.IsIPLiteral);
            Assert.Null(name.Address);
            Assert.False(name.IsIPLiteral);
        }
    }
}
