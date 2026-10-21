// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ProxyDeviceConfigurationTests
    {
        [Theory]
        [InlineData("192.0.2.1:8880", "192.0.2.1", 8880)]
        [InlineData("http://proxy.example:8080/", "proxy.example", 8080)]
        [InlineData("[::1]:65535", "::1", 65535)]
        public void Endpoint_ParsesExplicitPort(string input, string host, int port)
        {
            Assert.True(ProxyEndpoint.TryParse(input, out var endpoint));
            Assert.Equal(host, endpoint.Host);
            Assert.Equal(port, endpoint.Port);
            Assert.True(endpoint.Matches(ProxyEndpoint.TryParse(endpoint.ToString(), out var roundTrip) ? roundTrip : null));
        }

        [Theory]
        [InlineData("proxy.example")]
        [InlineData("192.0.2.1:0")]
        [InlineData("192.0.2.1:65536")]
        [InlineData("proxy:8080/path")]
        [InlineData("http=proxy:8080;https=other:8080")]
        [InlineData("user:password@proxy:8080")]
        [InlineData("ftp://proxy:8080")]
        public void Endpoint_RejectsUnverifiableSetting(string input)
        {
            Assert.False(ProxyEndpoint.TryParse(input, out _));
            Assert.Equal(ProxyConfigurationState.Unknown, ProxyConfiguration.FromSetting(input).State);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void EmptySetting_IsDisabled(string setting) =>
            Assert.Equal(ProxyConfigurationState.Disabled, ProxyConfiguration.FromSetting(setting).State);

        [Theory]
        [InlineData(unchecked((int)0x80070002), true)]
        [InlineData(unchecked((int)0x80070003), true)]
        [InlineData(unchecked((int)0x80070005), false)]
        public void NativeDelete_OnlyMissingFileErrorsAreIdempotent(int hresult, bool missing)
        {
            var error = new System.Reflection.TargetInvocationException(
                new System.Runtime.InteropServices.COMException("native error", hresult));
            Assert.Equal(missing, XboxClient.XboxClientConnection.IsMissingFile(error));
        }

        [Fact]
        public void Endpoint_UsesCanonicalCaseInsensitiveHost()
        {
            Assert.True(new ProxyEndpoint("CONSOLE.example.", 8880).Matches(new("console.EXAMPLE", 8880)));
            Assert.False(new ProxyEndpoint("console.example", 8880).Matches(new("console.example", 8881)));
        }

        [Fact]
        public void TargetGuard_RejectsAliasOverlapUntilActuallyReleased()
        {
            using var target = ProxyDeviceOperations.AcquireTarget("192.0.2.1");
            Assert.Throws<InvalidOperationException>(() => ProxyDeviceOperations.AcquireTarget("192.0.2.1"));
            using var other = ProxyDeviceOperations.AcquireTarget("192.0.2.2");
            target.Dispose();
            using var retry = ProxyDeviceOperations.AcquireTarget("192.0.2.1");
            target.Dispose();
            Assert.Throws<InvalidOperationException>(() => ProxyDeviceOperations.AcquireTarget("192.0.2.1"));
        }

        [Fact]
        public async Task Apply_BindsThenDisablesThenCommitsThenEnables()
        {
            var setup = new Setup();
            await setup.ApplyAsync();
            Assert.Equal(new[] { "read", "start", "disable", "read", "commit", "enable", "read" }, setup.Events);
            Assert.True(setup.Committed);
            Assert.Equal("PROXY_PORT_APPLIED", setup.Status);
        }

        [Fact]
        public async Task Apply_AlreadyDisabled_SkipsDisable()
        {
            var setup = new Setup { Current = new(ProxyConfigurationState.Disabled) };
            await setup.ApplyAsync();
            Assert.DoesNotContain("disable", setup.Events);
            Assert.True(setup.Committed);
        }

        [Fact]
        public async Task Apply_AlreadyAtRequestedEndpoint_DoesNotReboot()
        {
            var setup = new Setup { Current = new(ProxyConfigurationState.Enabled, Setup.Desired) };
            await setup.ApplyAsync();
            Assert.Equal(new[] { "read", "start", "commit" }, setup.Events);
        }

        [Theory]
        [InlineData((int)ProxyConfigurationState.Unknown)]
        [InlineData((int)ProxyConfigurationState.Unreachable)]
        public async Task Apply_UnknownOrUnreachable_DoesNotBindOrConfigure(int state)
        {
            var setup = new Setup { Current = new((ProxyConfigurationState)state) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.ApplyAsync());
            Assert.Equal(new[] { "read" }, setup.Events);
        }

        [Fact]
        public async Task Apply_ForeignEndpoint_IsNotOverwritten()
        {
            var setup = new Setup { Current = new(ProxyConfigurationState.Enabled, new("other.example", 9999)) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.ApplyAsync());
            Assert.Equal(new[] { "read" }, setup.Events);
        }

        [Fact]
        public async Task Apply_BindFailure_DoesNotTouchDevice()
        {
            var setup = new Setup { Failure = "start" };
            await Assert.ThrowsAsync<IOException>(() => setup.ApplyAsync());
            Assert.Equal(new[] { "read", "start" }, setup.Events);
        }

        [Theory]
        [InlineData("disable")]
        [InlineData("commit")]
        [InlineData("enable")]
        public async Task Apply_PhaseFailure_RetiresOnlyUncommittedListener(string phase)
        {
            var setup = new Setup { Failure = phase };
            await Assert.ThrowsAsync<IOException>(() => setup.ApplyAsync());
            if (phase == "enable")
            {
                Assert.DoesNotContain("stop", setup.Events);
            }
            else
            {
                Assert.Equal("stop", setup.Events.Last());
            }
            Assert.Equal(phase == "enable", setup.Committed);
            if (phase != "enable")
            {
                Assert.DoesNotContain("enable", setup.Events);
            }
        }

        [Fact]
        public async Task Apply_WaitsForDisabledReadbackBeforeCommitting()
        {
            var setup = new Setup();
            var reads = 0;
            setup.ReadOverride = () => ++reads switch
            {
                1 => new(ProxyConfigurationState.Enabled, Setup.Old),
                2 => new(ProxyConfigurationState.Unreachable),
                3 => new(ProxyConfigurationState.Unknown),
                4 => new(ProxyConfigurationState.Disabled),
                _ => new(ProxyConfigurationState.Enabled, Setup.Desired)
            };
            await setup.ApplyAsync();
            Assert.Equal(new[] { "read", "start", "disable", "read", "read", "read", "commit", "enable", "read" }, setup.Events);
        }

        [Fact]
        public async Task Apply_DisableNotConfirmed_KeepsOldAssignment()
        {
            var setup = new Setup { ReadOverride = () => new(ProxyConfigurationState.Enabled, Setup.Old) };
            await Assert.ThrowsAsync<TimeoutException>(() => setup.ApplyAsync(timeout: TimeSpan.Zero));
            Assert.False(setup.Committed);
            Assert.DoesNotContain("enable", setup.Events);
            Assert.Equal("stop", setup.Events.Last());
        }

        [Fact]
        public async Task Apply_EnableNotConfirmed_KeepsSavedListenerAvailable()
        {
            var setup = new Setup();
            var reads = 0;
            setup.ReadOverride = () => ++reads switch
            {
                1 => new(ProxyConfigurationState.Enabled, Setup.Old),
                2 => new(ProxyConfigurationState.Disabled),
                _ => new(ProxyConfigurationState.Unknown)
            };
            await Assert.ThrowsAsync<TimeoutException>(() => setup.ApplyAsync(timeout: TimeSpan.Zero));
            Assert.True(setup.Committed);
            Assert.DoesNotContain("stop", setup.Events);
        }

        [Fact]
        public async Task Apply_CancellationDoesNotAbandonInFlightWrite()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var setup = new Setup
            {
                DisableOverride = () =>
                {
                    started.SetResult();
                    return write.Task;
                }
            };
            using var cancellation = new CancellationTokenSource();
            var applying = setup.ApplyAsync(cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            Assert.False(applying.IsCompleted);
            Assert.DoesNotContain("stop", setup.Events);
            write.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying);
            Assert.False(setup.Committed);
            Assert.DoesNotContain("enable", setup.Events);
            Assert.Equal("stop", setup.Events.Last());
        }

        private sealed class Setup : IProxyDeviceConfiguration
        {
            internal static readonly ProxyEndpoint Old = new("192.0.2.10", 8880);
            internal static readonly ProxyEndpoint Desired = new("192.0.2.10", 8080);
            internal List<string> Events { get; } = new();
            internal ProxyConfiguration Current { get; set; } = new(ProxyConfigurationState.Enabled, Old);
            internal string Failure { get; set; }
            internal bool Committed { get; private set; }
            internal string Status { get; private set; }
            internal Func<ProxyConfiguration> ReadOverride { get; set; }
            internal Func<Task> DisableOverride { get; set; }

            internal Task ApplyAsync(CancellationToken token = default, TimeSpan? timeout = null)
            {
                var operation = new ProxyPortChangeOperation(this,
                    () => Event("start"), () => Event("stop"),
                    () =>
                    {
                        Event("commit").GetAwaiter().GetResult();
                        Committed = true;
                    },
                    value => Status = value, _ => Task.CompletedTask, timeout);
                return operation.ApplyAsync(endpoint => endpoint.Matches(Old), Desired, token);
            }

            private Task Event(string name)
            {
                Events.Add(name);
                if (Failure == name)
                {
                    throw new IOException(name);
                }
                return Task.CompletedTask;
            }

            public Task<ProxyConfiguration> ReadAsync()
            {
                Events.Add("read");
                return Task.FromResult(ReadOverride?.Invoke() ?? Current);
            }

            public async Task DisableAsync()
            {
                await Event("disable");
                if (DisableOverride != null)
                {
                    await DisableOverride();
                }
                Current = new(ProxyConfigurationState.Disabled);
            }

            public async Task EnableAsync(ProxyEndpoint endpoint)
            {
                await Event("enable");
                Current = new(ProxyConfigurationState.Enabled, endpoint);
            }
        }
    }
}
