// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // A port change disables the old endpoint and waits until the device no longer uses it. On this PC, recovery then
    // restores the settings from before capture, which may use another proxy; an Xbox must still read as disabled.
    public sealed class ProxyPortChangeRecoveryTests
    {
        private static readonly ProxyEndpoint PcOld = new("127.0.0.1", 8880);
        private static readonly ProxyEndpoint PcDesired = new("127.0.0.1", 8881);
        private static readonly ProxyEndpoint XboxOld = new("192.0.2.10", 8880);
        private static readonly ProxyEndpoint XboxDesired = new("192.0.2.10", 8881);
        private static readonly ProxyConfiguration Corporate = ProxyConfiguration.FromSetting("corp-proxy:8080");

        [Theory]
        [InlineData("disabled", true, true)]
        [InlineData("corp-proxy:8080", true, false)]
        [InlineData("127.0.0.1:8880", false, false)]
        [InlineData("enabled", false, false)]
        [InlineData("unknown", false, false)]
        [InlineData("unreachable", false, false)]
        public void DisableCompletion_DependsOnTheDevice(string read, bool onPc, bool onXbox)
        {
            var configuration = read switch
            {
                "disabled" => new ProxyConfiguration(ProxyConfigurationState.Disabled),
                "enabled" => new ProxyConfiguration(ProxyConfigurationState.Enabled),
                "unknown" => new ProxyConfiguration(ProxyConfigurationState.Unknown),
                "unreachable" => new ProxyConfiguration(ProxyConfigurationState.Unreachable),
                _ => ProxyConfiguration.FromSetting(read)
            };
            IProxyDeviceConfiguration pc = new PcProxyDeviceConfiguration();
            IProxyDeviceConfiguration xbox = new XboxProxyDeviceConfiguration("console");

            Assert.Equal(onPc, pc.IsDisableComplete(configuration, PcOld));
            Assert.Equal(onXbox, xbox.IsDisableComplete(configuration, PcOld));
        }

        // Recovery restores a corporate proxy once this PC's old endpoint is disabled. That completes the disable,
        // after any unknown or unreachable readings, and the new port is enabled.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Pc_RestoringACorporateProxy_StillMovesToTheNewPort(bool unsettledReadsFirst)
        {
            var reads = new List<ProxyConfiguration> { new(ProxyConfigurationState.Enabled, PcOld) };
            if (unsettledReadsFirst)
            {
                reads.Add(new(ProxyConfigurationState.Unknown));
                reads.Add(new(ProxyConfigurationState.Unreachable));
            }
            reads.Add(Corporate);
            reads.Add(new(ProxyConfigurationState.Enabled, PcDesired));
            var device = new Device(new PcProxyDeviceConfiguration(), PcOld, PcDesired, reads.ToArray());

            await device.ApplyAsync();

            Assert.Equal(
                new[] { "start", "disable 127.0.0.1:8880", "commit", "enable 127.0.0.1:8881" },
                device.Events.Where(name => name != "read"));
            Assert.Equal(reads.Count, device.Events.Count(name => name == "read"));
            Assert.Equal("PROXY_PORT_APPLIED", device.Status);
        }

        // Unknown or unreachable readings, or the old endpoint still in use, never complete the disable: the change
        // times out without committing the new port.
        [Theory]
        [InlineData((int)ProxyConfigurationState.Unknown)]
        [InlineData((int)ProxyConfigurationState.Unreachable)]
        [InlineData((int)ProxyConfigurationState.Enabled)]
        public async Task Pc_UnsettledReadings_NeverCompleteTheDisable(int state)
        {
            var unsettled = (ProxyConfigurationState)state == ProxyConfigurationState.Enabled
                ? new ProxyConfiguration(ProxyConfigurationState.Enabled, PcOld)
                : new ProxyConfiguration((ProxyConfigurationState)state);
            var device = new Device(
                new PcProxyDeviceConfiguration(), PcOld, PcDesired, new(ProxyConfigurationState.Enabled, PcOld), unsettled);

            await Assert.ThrowsAsync<TimeoutException>(() => device.ApplyAsync(TimeSpan.Zero));

            Assert.DoesNotContain("commit", device.Events);
            Assert.Equal("stop", device.Events.Last());
        }

        // A rebooting Xbox still has to read as disabled: unreachable and unknown readings, or another proxy, are
        // waited out.
        [Fact]
        public async Task Xbox_RebootStillWaitsForADisabledProxy()
        {
            var device = new Device(
                new XboxProxyDeviceConfiguration("console"), XboxOld, XboxDesired,
                new(ProxyConfigurationState.Enabled, XboxOld),
                new(ProxyConfigurationState.Unreachable),
                new(ProxyConfigurationState.Unknown),
                Corporate,
                new(ProxyConfigurationState.Disabled),
                new(ProxyConfigurationState.Enabled, XboxDesired));

            await device.ApplyAsync();

            Assert.Equal(
                new[] { "start", "disable 192.0.2.10:8880", "commit", "enable 192.0.2.10:8881" },
                device.Events.Where(name => name != "read"));
            Assert.Equal(6, device.Events.Count(name => name == "read"));
        }

        // Plays its readings in order, repeating the last one, and judges a disable as `kind`, a real adapter, does.
        private sealed class Device : IProxyDeviceConfiguration
        {
            private readonly IProxyDeviceConfiguration _kind;
            private readonly ProxyEndpoint _old;
            private readonly ProxyEndpoint _desired;
            private readonly Queue<ProxyConfiguration> _reads;

            internal Device(
                IProxyDeviceConfiguration kind, ProxyEndpoint old, ProxyEndpoint desired, params ProxyConfiguration[] reads)
            {
                _kind = kind;
                _old = old;
                _desired = desired;
                _reads = new(reads);
            }

            internal List<string> Events { get; } = new();

            internal string Status { get; private set; }

            internal Task ApplyAsync(TimeSpan? timeout = null) =>
                new ProxyPortChangeOperation(
                        this, () => Record("start"), () => Record("stop"), () => Record("commit"),
                        status => Status = status, _ => Task.CompletedTask, timeout)
                    .ApplyAsync(endpoint => endpoint.Matches(_old), _desired, CancellationToken.None);

            public Task<ProxyConfiguration> ReadAsync()
            {
                Events.Add("read");
                return Task.FromResult(_reads.Count > 1 ? _reads.Dequeue() : _reads.Peek());
            }

            public Task DisableAsync() => throw new NotSupportedException();

            public Task DisableAsync(ProxyEndpoint previous) => Record($"disable {previous}");

            public Task EnableAsync(ProxyEndpoint endpoint) => Record($"enable {endpoint}");

            public bool IsDisableComplete(ProxyConfiguration configuration, ProxyEndpoint previous) =>
                _kind.IsDisableComplete(configuration, previous);

            private Task Record(string name)
            {
                Events.Add(name);
                return Task.CompletedTask;
            }
        }
    }
}
