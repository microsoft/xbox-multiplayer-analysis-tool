// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    public class ReplayDestinationTests
    {
        [Theory]
        [InlineData("http://[fe80::1%12]/")]
        [InlineData("http://[fe80::1%Ethernet]/")]
        [InlineData("http://[fe80::1%unknown-interface]/")]
        [InlineData("http://[fe80::1%2518]/")]
        [InlineData("http://[fe80::1%0]/")]
        [InlineData("http://[fe80::1%12]/items/#?next=#")]
        public void Detector_FindsEveryScopedIpv6Host(string url) =>
            Assert.True(ReplayDestination.IsScopedIpv6(url));

        [Theory]
        [InlineData("http://[fe80::1]/")]
        [InlineData("http://example.test/items/%25?q=%2518")]
        [InlineData("http://[2001:db8::1]/%25?zone=%12")]
        [InlineData("not a url%12")]
        [InlineData("")]
        [InlineData(null)]
        public void Detector_IgnoresEverythingButAParsedIpv6HostZone(string url) =>
            Assert.False(ReplayDestination.IsScopedIpv6(url));

        [Fact]
        public void UriDetector_UsesTheHostRatherThanOtherPercentSigns()
        {
            Assert.True(ReplayDestination.IsScopedIpv6(
                new Uri("https://[fe80::1%25Ethernet]/items/%25?q=%2518")));
            Assert.False(ReplayDestination.IsScopedIpv6(
                new Uri("https://[fe80::1]/items/%25?q=%2518")));
        }
    }
}
