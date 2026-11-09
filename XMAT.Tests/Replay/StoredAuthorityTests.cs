// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    public class StoredAuthorityTests
    {
        [Theory]
        [InlineData("[fe80::1%0]:8080", "fe80::1%0", 8080)]
        [InlineData("[FE80::1%Ethernet]", "fe80::1%Ethernet", -1)]
        [InlineData("[fe80::1%25NoSuchIf]:80", "fe80::1%25NoSuchIf", 80)]
        [InlineData("[fe80::1%eth0.1_a-b~c]", "fe80::1%eth0.1_a-b~c", -1)]
        [InlineData("[fe80::1%en%2F0]", "fe80::1%en%2F0", -1)]
        public void CaptureDataAuthority_AcceptsOnlyLexicalRfc6874Zones(
            string authority,
            string expectedHost,
            int expectedPort)
        {
            Assert.True(StoredAuthority.TryParse(authority, out string host, out int? port));
            int? expected = expectedPort < 0 ? null : expectedPort;
            Assert.Equal((expectedHost, expected), (host, port));
        }

        [Theory]
        [InlineData("[fe80::1%]")]
        [InlineData("[fe80::1%a b]")]
        [InlineData("[fe80::1%a@[2001:db8::1]:8080")]
        [InlineData("[fe80::1%a/b]")]
        [InlineData("[fe80::1%a?b]")]
        [InlineData("[fe80::1%a#b]")]
        [InlineData("[fe80::1%a:b]")]
        [InlineData("[fe80::1%a!b]")]
        [InlineData("[fe80::1%a%zz]")]
        [InlineData("[fe80::1%a%]")]
        [InlineData("[fe80::1%a\u001Bb]")]
        [InlineData("[fe80::1%a\u202Eb]")]
        public void CaptureDataAuthority_RefusesTextOutsideTheZoneIdGrammar(string authority)
        {
            Assert.False(StoredAuthority.TryParse(authority, out string host, out int? port));
            Assert.Null(host);
            Assert.Null(port);
        }

        [Fact]
        public void CaptureDataAuthority_Accepts255HostCharacters_AndRefuses256()
        {
            string accepted = "[fe80::1%" + new string('a', 247) + "]";
            string refused = "[fe80::1%" + new string('a', 248) + "]";

            Assert.True(StoredAuthority.TryParse(accepted, out string host, out _));
            Assert.Equal(255, host.Length);
            Assert.False(StoredAuthority.TryParse(refused, out string refusedHost, out int? refusedPort));
            Assert.Null(refusedHost);
            Assert.Null(refusedPort);
        }
    }
}
