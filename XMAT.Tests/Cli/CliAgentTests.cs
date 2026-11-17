// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.AgentConnection;
using XMAT.Cli;

namespace XMAT.Tests.Cli
{
    public class CliAgentTests
    {
        [Theory]
        [InlineData("agent setup --data s3cretcanary9")]
        [InlineData("agent setup --enable yes")]
        [InlineData("agent start --mode s3cretcanary9")]
        [InlineData("agent config --client s3cretcanary9")]
        [InlineData("agent stop --instance s3cretcanary9")]
        [InlineData("agent setup --accept=s3cretcanary9")]
        [InlineData("agent setup --accept --accept")]
        [InlineData("agent s3cretcanary9")]
        public void InvalidAgentArgumentsFailWithoutEchoingPotentialSecrets(string text)
        {
            var parsed = CliCommandLine.Parse(text.Split(' '));
            Assert.False(parsed.Succeeded);
            Assert.DoesNotContain("s3cretcanary9", parsed.Error.Message);
        }

        [Fact]
        public async Task SetupIsExplicitGlobalAndDoesNotMutateThePolicyUntilAcknowledged()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var store = new AgentAccessSettingsStore(Path.Combine(directory.Path, "policy.json"));
            var request = new CliAgentRequest("setup", DataSharing: AgentDataSharing.Complete,
                Actions: AgentActionAccess.FullControl, Backups: true);
            var preview = await CliAgentCommand.RunAsync(request, "preview", default, directory.Path, store);
            Assert.Equal(4, preview.ExitCode);
            Assert.False(File.Exists(store.Path));
            Assert.True(preview.Agent.Value.GetProperty("confirmationRequired").GetBoolean());
            Assert.Contains("cloud-backed", preview.Agent.Value.GetProperty("disclosure").GetString());
            var saved = await CliAgentCommand.RunAsync(request with { Accept = true }, "save", default, directory.Path, store);
            Assert.Equal(0, saved.ExitCode);
            var policy = store.Read().Settings;
            Assert.True(policy.Enabled);
            Assert.True(policy.AutomaticBackups);
            Assert.Equal(AgentActionAccess.FullControl, policy.Actions);
            Assert.Equal(AgentDataSharing.Complete, policy.DataSharing);
            Assert.False(saved.Agent.Value.GetProperty("humanAttestation").GetBoolean());
        }

        [Fact]
        public async Task StatusAndConfigurationDoNotCreatePolicyOrInstanceStorage()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var store = new AgentAccessSettingsStore(Path.Combine(directory.Path, "settings", "policy.json"));
            var instancesPath = Path.Combine(directory.Path, "instances");
            var instances = new AgentInstanceDirectory(Path.Combine(directory.Path, "XboxMultiplayerAnalysisTool.exe"), instancesPath);
            foreach (var request in new[] { new CliAgentRequest("status"), new("config", Client: AgentClientKind.Copilot) })
            {
                var report = await CliAgentCommand.RunAsync(request, "read-only", default, directory.Path, store, instances);
                Assert.Equal(0, report.ExitCode);
                using var json = JsonDocument.Parse(CliJson.Serialize(report));
                Assert.Equal("agent", json.RootElement.GetProperty("command").GetString());
            }
            Assert.False(File.Exists(store.Path));
            Assert.False(Directory.Exists(instancesPath));
        }

        [Theory]
        [InlineData("simple")]
        [InlineData("C:\\Program Files\\XMAT\\")]
        [InlineData("C:\\O'Brien\\$tools\\\u00E9\\\"quoted\"")]
        public void NativeCommandLineQuotingRoundTripsThroughWindows(string argument)
        {
            var pointer = CommandLineToArgvW("host.exe " + WindowsIndependentProcess.Quote(argument), out var count);
            try
            {
                Assert.NotEqual(IntPtr.Zero, pointer);
                Assert.Equal(2, count);
                Assert.Equal(argument, Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, IntPtr.Size)));
            }
            finally { LocalFree(pointer); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr pointer);
    }
}
