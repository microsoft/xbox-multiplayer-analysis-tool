// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class XboxProxyAddressFileTests
    {
        [Theory]
        [InlineData("192.0.2.152", 8889)]
        [InlineData("192.0.2.1", 1)]
        [InlineData("192.0.2.1", 65535)]
        public void AddressFile_IsUtf8WithExactlyOneTerminatorAndNoBomOrLineEnding(string host, int port)
        {
            var payload = CaptureUtilities.EncodeXboxProxyAddress(host, port);
            var address = Encoding.UTF8.GetBytes($"{host}:{port}");
            Assert.Equal(address.Length + 1, payload.Length);
            Assert.Equal(address, payload[..^1]);
            Assert.Equal((byte)0, payload[^1]);
            Assert.Single(payload, value => value == 0);
            Assert.DoesNotContain((byte)13, payload);
            Assert.DoesNotContain((byte)10, payload);
            Assert.False(payload.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        }

        [Fact]
        public void AddressFile_StopsCStringConsumptionBeforeFollowingMemory()
        {
            var payload = CaptureUtilities.EncodeXboxProxyAddress("192.0.2.152", 8889);
            var memory = payload.Concat(Encoding.UTF8.GetBytes("\uFFFDcz")).ToArray();
            var decoded = Encoding.UTF8.GetString(memory.TakeWhile(value => value != 0).ToArray());
            Assert.Equal("192.0.2.152:8889", decoded);
            Assert.Equal(ProxyConfigurationState.Enabled, ProxyConfiguration.FromSetting(decoded).State);
        }

        [Fact]
        public void StatusCheckFailure_DoesNotImplyAConfigurationChangeWasAttempted()
        {
            var message = Localization.GetLocalizedString("PROXY_CHECK_STATUS_MESSAGE_UNKNOWN", "invalid response");
            Assert.Contains("Checking status did not change", message);
            Assert.DoesNotContain("No new proxy configuration was applied", message);
        }
    }
}
