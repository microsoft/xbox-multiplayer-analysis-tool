// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.AgentConnection;

namespace XMAT.Tests
{
    public class AgentConnectionDiscoveryTests
    {
        [Fact]
        public void StatusDiscoveryDoesNotCreateStorageAndDistinguishesReadinessAndAmbiguity()
        {
            using var fixture = new DiscoveryDirectory();
            Assert.Equal(AgentDiscoveryFailure.NotRunning, fixture.Store.Select().Failure);
            Assert.False(Directory.Exists(fixture.Path));
            var first = AgentInstanceRecord.ForCurrentProcess(Guid.NewGuid(), "headless", "ready", true);
            fixture.Store.Publish(first);
            Assert.Equal(first, fixture.Store.Select().Instance);
            var second = first with { InstanceId = Guid.NewGuid(), Mode = "desktop" };
            fixture.Store.Publish(second);
            Assert.Equal(AgentDiscoveryFailure.Ambiguous, fixture.Store.Select().Failure);
            Assert.Equal(first, fixture.Store.Select(first.InstanceId).Instance);
            fixture.Store.Publish(second with { Enabled = false, State = "disabled" });
            Assert.Equal(first, fixture.Store.Select().Instance);
            fixture.Store.Publish(first with { State = "starting" });
            Assert.Equal(AgentDiscoveryFailure.NotReady, fixture.Store.Select().Failure);
            fixture.Store.Publish(first with { State = "disabled", Enabled = false });
            Assert.Equal(AgentDiscoveryFailure.AccessDisabled, fixture.Store.Select().Failure);
            fixture.Store.Remove(first);
            fixture.Store.Remove(second);
            Assert.Empty(fixture.Store.Read());
        }

        [Fact]
        public void ReusedProcessIdentityWrongExecutableAndForgedUserMetadataAreNotConnectionAuthority()
        {
            using var fixture = new DiscoveryDirectory();
            var record = AgentInstanceRecord.ForCurrentProcess(Guid.NewGuid(), "headless", "ready", true);
            fixture.Store.Publish(record with { StartedUtcTicks = record.StartedUtcTicks + 1 });
            Assert.Empty(fixture.Store.Read());
            fixture.Store.Publish(record);
            var wrongExecutable = new AgentInstanceDirectory(Path.Combine(fixture.Path, "unrelated.exe"), fixture.Path);
            Assert.Empty(wrongExecutable.Read());
            fixture.Store.Publish(record with { Elevated = !record.Elevated });
            Assert.Empty(fixture.Store.Read());
            Assert.Throws<ArgumentException>(() => fixture.Store.Publish(record with { UserSid = "S-1-0-0" }));
        }

        [Fact]
        public void DiscoveryBoundsDirectoryAndRecordInspection()
        {
            using var fixture = new DiscoveryDirectory();
            var record = AgentInstanceRecord.ForCurrentProcess(Guid.NewGuid(), "headless", "ready", true);
            fixture.Store.Publish(record);
            File.WriteAllText(Path.Combine(fixture.Path, record.InstanceId.ToString("N") + ".json"),
                new string('x', 32 * 1024));
            Assert.Empty(fixture.Store.Read());
            for (var index = 0; index < 65; index++)
                fixture.Store.Publish(record with { InstanceId = Guid.NewGuid() });
            Assert.Equal(AgentDiscoveryFailure.LimitExceeded, fixture.Store.Select().Failure);
        }

        [Fact]
        public async Task ASecondReadyInstanceAppearingDuringLaunchIsNotSilentlySelected()
        {
            using var fixture = new DiscoveryDirectory();
            Directory.CreateDirectory(fixture.Path);
            File.WriteAllBytes(Path.Combine(fixture.Path, "XboxMultiplayerAnalysisTool.exe"), []);
            var handshakes = 0;
            var launcher = new AgentHostLauncher(fixture.Path, fixture.Store, () => true,
                (_, _) => { handshakes++; return Task.CompletedTask; }, (id, _) =>
                {
                    fixture.Store.Publish(AgentInstanceRecord.ForCurrentProcess(id, "headless", "ready", true));
                    fixture.Store.Publish(AgentInstanceRecord.ForCurrentProcess(Guid.NewGuid(), "desktop", "ready", true));
                    return Process.GetCurrentProcess();
                });
            var failure = await Assert.ThrowsAsync<AgentLaunchException>(() =>
                launcher.StartAsync(AgentLaunchMode.Headless, default));
            Assert.Equal("agent.Ambiguous", failure.Code);
            Assert.Equal(0, handshakes);
            Assert.Equal(2, fixture.Store.Read().Count);
        }

        [Theory]
        [InlineData("Copilot")]
        [InlineData("Claude")]
        [InlineData("Gemini")]
        public void ClientJsonUsesStableLaunchArgumentsAndEscapesWindowsPaths(string clientName)
        {
            var client = Enum.Parse<AgentClientKind>(clientName);
            var installation = @"C:\Program Files\XMAT $tools\O'Brien";
            var setup = AgentClientConfiguration.Create(installation, client);
            using var json = JsonDocument.Parse(setup.Configuration);
            var server = json.RootElement.GetProperty("mcpServers").GetProperty("xmat");
            Assert.Equal(Path.Combine(installation, "AgentBridge", "XMAT.AgentBridge.exe"),
                server.GetProperty("command").GetString());
            Assert.Equal(new[] { "--discover", "--launch", "headless" },
                server.GetProperty("args").EnumerateArray().Select(value => value.GetString()));
            Assert.False(server.TryGetProperty("trust", out _));
            Assert.DoesNotContain("allow-all", setup.Command);
            Assert.Contains("O''Brien", setup.Command);
            if (client != AgentClientKind.Copilot)
                Assert.False(server.TryGetProperty("tools", out _));
        }

        [Fact]
        public void CodexUsesTomlAndPinnedConfigurationsDoNotSilentlySelectAnotherInstance()
        {
            var root = @"C:\XMAT";
            var setup = AgentClientConfiguration.Create(root, AgentClientKind.Codex, AgentLaunchMode.ConnectOnly);
            Assert.Null(setup.Command);
            Assert.StartsWith("[mcp_servers.xmat]\r\n", setup.Configuration);
            Assert.Contains("args = [\"--discover\"]", setup.Configuration);
            var record = AgentInstanceRecord.ForCurrentProcess(Guid.NewGuid(), "desktop", "ready", true);
            var pinned = AgentClientConfiguration.Create(root, AgentClientKind.Copilot, instance: record);
            using var json = JsonDocument.Parse(pinned.Configuration);
            var args = json.RootElement.GetProperty("mcpServers").GetProperty("xmat").GetProperty("args")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal("--instance", args[0]);
            Assert.Equal(record.InstanceId.ToString("D"), args[1]);
            Assert.DoesNotContain("--discover", args);
            Assert.DoesNotContain("--launch", args);
        }

        [Theory]
        [InlineData("--discover")]
        [InlineData("--discover --launch headless")]
        [InlineData("--discover --launch desktop")]
        public void GeneratedDiscoveryArgumentsHaveNoImplicitPolicyOverride(string value)
        {
            var parsed = AgentBridgeArguments.Parse(value.Split(' '));
            Assert.NotNull(parsed);
            Assert.Null(parsed.InstanceId);
            Assert.Null(AgentBridgeArguments.Parse([.. value.Split(' '), "--enable"]));
        }

        private sealed class DiscoveryDirectory : IDisposable
        {
            private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "XMAT.DiscoveryTests." + Guid.NewGuid().ToString("N"));
            internal string Path => System.IO.Path.Combine(_parent, "instances");
            internal AgentInstanceDirectory Store { get; }
            internal DiscoveryDirectory()
            {
                using var process = Process.GetCurrentProcess();
                Store = new(process.MainModule.FileName, Path);
            }
            public void Dispose()
            {
                if (Directory.Exists(_parent))
                    Directory.Delete(_parent, recursive: true);
            }
        }
    }
}
