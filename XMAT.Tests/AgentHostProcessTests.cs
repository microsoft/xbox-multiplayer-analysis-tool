// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.AgentConnection;
using XMAT.Cli;
using XMAT.Tests.Proxy;

namespace XMAT.Tests
{
    [Collection(IsolatedProxyTests.Name)]
    public class AgentHostProcessTests
    {
        [Fact]
        public async Task StartingPresenceValidatesTheKernelImageBeforeTheManagedHostLoads()
        {
            await using var fixture = new HostInstallation();
            var executable = Path.Combine(fixture.Path, "XboxMultiplayerAnalysisTool.exe");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            const uint CreateSuspended = 4, CreateNoWindow = 0x08000000;
            Assert.True(CreateProcess(executable, new(WindowsIndependentProcess.Quote(executable)),
                IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNoWindow, IntPtr.Zero, fixture.Path, ref startup, out var created),
                $"Could not create the suspended test host: {Marshal.GetLastWin32Error()}");
            try
            {
                using var process = Process.GetProcessById((int)created.ProcessId);
                fixture.Track(process);
                var identity = AgentProcessIdentity.Current();
                var record = new AgentInstanceRecord(1, Guid.NewGuid(), process.Id,
                    process.StartTime.ToUniversalTime().Ticks, identity.UserSid, identity.Elevated, "headless", "starting", true);
                fixture.Instances.PublishStarting(record);
                Assert.Equal(record, Assert.Single(fixture.Instances.Read()));
                Assert.Equal(AgentDiscoveryFailure.NotReady, fixture.Instances.Select().Failure);
            }
            finally
            {
                CloseHandle(created.Thread);
                CloseHandle(created.Process);
            }
        }

        [Fact]
        public async Task IndependentLauncherSurvivesItsParentOrTruthfullyReportsTheJobConstraint()
        {
            await using var fixture = new HostInstallation();
            await fixture.Store.SaveAsync(new() { Enabled = true }, 0);
            var startInfo = new ProcessStartInfo(Path.Combine(fixture.Path, "XMAT.Tests.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--test-launch");
            using var launcher = Process.Start(startInfo);
            var output = launcher.StandardOutput.ReadToEndAsync();
            var error = launcher.StandardError.ReadToEndAsync();
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var text = await output;
            using var result = JsonDocument.Parse(text);
            if (launcher.ExitCode != 0)
            {
                Assert.Contains(result.RootElement.GetProperty("errors").EnumerateArray(),
                    item => item.GetProperty("code").GetString() == "agent.ExternalLaunchRequired");
                Assert.Empty(fixture.Instances.Read());
                return;
            }
            var instance = Assert.Single(fixture.Instances.Read());
            fixture.Own(instance);
            Assert.NotEqual(launcher.Id, instance.ProcessId);
            Assert.True(result.RootElement.GetProperty("agent").GetProperty("handshakeConfirmed").GetBoolean(), await error);
            await ValidateHostAsync(fixture, instance);
        }

        [Fact]
        public async Task FreshRuntimeHasNoApplicationPublishesCallbacksAndSurvivesBridgeDisconnect()
        {
            await using var fixture = new HostInstallation();
            await fixture.Store.SaveAsync(new() { Enabled = true }, 0);
            var start = new ProcessStartInfo(Path.Combine(fixture.Path, "XboxMultiplayerAnalysisTool.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--agent-host", "--instance", Guid.NewGuid().ToString("D"), "--mode", "headless" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            fixture.Track(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            AgentInstanceRecord instance;
            while ((instance = fixture.Instances.Select().Instance) == null)
            {
                Assert.False(process.HasExited, "The isolated test runtime exited before readiness.");
                await Task.Delay(20, timeout.Token);
            }
            fixture.Own(instance);
            await ValidateHostAsync(fixture, instance);
        }

        private static async Task ValidateHostAsync(HostInstallation fixture, AgentInstanceRecord instance)
        {
            await fixture.WaitForObservationsAsync();
            using var observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.State, "observed.json")));
            Assert.False(observed.RootElement.GetProperty("hasApplication").GetBoolean());
            Assert.False(observed.RootElement.GetProperty("proxyInitialized").GetBoolean());
            Assert.False(observed.RootElement.GetProperty("webViewLoaded").GetBoolean());
            Assert.True(observed.RootElement.GetProperty("sta").GetBoolean());
            Assert.Equal(0, observed.RootElement.GetProperty("devices").GetInt32());
            Assert.Equal(0, observed.RootElement.GetProperty("windows").GetInt32());
            Assert.Equal(observed.RootElement.GetProperty("dispatcherThread").GetInt32(),
                observed.RootElement.GetProperty("publishedThread").GetInt32());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var first = await AgentHostConnection.ConnectAsync(fixture.Path, instance, timeout.Token))
            {
                Assert.False((await first.CallToolAsync("xmat_list_sources", cancellationToken: timeout.Token)).IsError ?? false);
                var input = Path.Combine(fixture.Path, "capture.xmatcap");
                CaptureFileLoadTests.WriteCapture(input, [("loaded", XMAT.SharedInterfaces.CaptureType.WebProxy)]);
                var original = File.ReadAllBytes(input);
                var opened = await RunOperationAsync(first, "xmat_open_capture", new()
                {
                    ["path"] = input,
                    ["idempotencyKey"] = "isolated native open"
                }, timeout.Token);
                var query = await RunOperationAsync(first, "xmat_run_query", new()
                {
                    ["sourceId"] = opened.GetProperty("sourceId").GetGuid(),
                    ["sql"] = "SELECT 42 AS Answer",
                    ["idempotencyKey"] = "projected SQL"
                }, timeout.Token);
                var output = Path.Combine(fixture.Path, "answer.csv");
                await RunOperationAsync(first, "xmat_save_query_result", new()
                {
                    ["resultId"] = query.GetProperty("resultId").GetGuid(),
                    ["path"] = output,
                    ["format"] = "Csv",
                    ["idempotencyKey"] = "typed output"
                }, timeout.Token);
                Assert.Contains("42", File.ReadAllText(output));
                Assert.Equal(original, File.ReadAllBytes(input));
            }
            using (var host = Process.GetProcessById(instance.ProcessId))
                Assert.False(host.HasExited);
            await using (var reconnected = await AgentHostConnection.ConnectAsync(fixture.Path, instance, timeout.Token))
                Assert.False((await reconnected.CallToolAsync("xmat_get_session", cancellationToken: timeout.Token)).IsError ?? false);
            var stop = await CliAgentCommand.RunAsync(new("stop", InstanceId: instance.InstanceId, Accept: true), "stop",
                timeout.Token, fixture.Path, fixture.Store, fixture.Instances);
            Assert.Equal(0, stop.ExitCode);
            Assert.True(stop.Agent.Value.GetProperty("processExitConfirmed").GetBoolean());
            Assert.Empty(fixture.Instances.Read());
        }

        private static async Task<JsonElement> RunOperationAsync(
            McpClient client, string tool, Dictionary<string, object> arguments, CancellationToken token)
        {
            var requested = await client.CallToolAsync(tool, arguments, cancellationToken: token);
            Assert.False(requested.IsError ?? false, requested.StructuredContent?.GetRawText());
            var initial = requested.StructuredContent.Value;
            var id = initial.GetProperty("id").GetGuid();
            if (initial.TryGetProperty("confirmation", out var confirmation))
            {
                var accepted = await client.CallToolAsync("xmat_confirm_operation", new Dictionary<string, object>
                {
                    ["operationId"] = id,
                    ["confirmationToken"] = confirmation.GetProperty("confirmationToken").GetString(),
                    ["accept"] = true
                }, cancellationToken: token);
                Assert.False(accepted.IsError ?? false, accepted.StructuredContent?.GetRawText());
            }
            while (true)
            {
                var operation = await client.CallToolAsync("xmat_get_operation",
                    new Dictionary<string, object> { ["operationId"] = id }, cancellationToken: token);
                Assert.False(operation.IsError ?? false, operation.StructuredContent?.GetRawText());
                var state = operation.StructuredContent.Value.GetProperty("state").GetString();
                if (state == "Succeeded")
                    break;
                Assert.True(state is "Queued" or "Running", operation.StructuredContent?.GetRawText());
                await Task.Delay(20, token);
            }
            var result = await client.CallToolAsync("xmat_get_result",
                new Dictionary<string, object> { ["operationId"] = id }, cancellationToken: token);
            Assert.False(result.IsError ?? false, result.StructuredContent?.GetRawText());
            return result.StructuredContent.Value;
        }

        [Fact]
        public async Task StartDoesNotEnablePolicyOrCreateAHostWhenFirstUseIsDisabled()
        {
            await using var fixture = new HostInstallation();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var report = await CliAgentCommand.RunAsync(new("start"), "disabled", timeout.Token,
                fixture.Path, fixture.Store, fixture.Instances);
            Assert.Equal(4, report.ExitCode);
            Assert.Contains(report.Errors, error => error.Code == "agent.AccessDisabled");
            Assert.False(File.Exists(fixture.Store.Path));
            Assert.Empty(fixture.Instances.Read());
        }

        [Fact]
        public async Task ConcurrentLaunchRequestsReuseOneReadyInstanceAndBothConfirmTheHandshake()
        {
            await using var fixture = new HostInstallation();
            await fixture.Store.SaveAsync(new() { Enabled = true }, 0);
            var launches = 0;
            var launcher = new AgentHostLauncher(fixture.Path, fixture.Instances, () => fixture.Store.Read().Settings.Enabled,
                (instance, token) => AgentHostConnection.HandshakeAsync(fixture.Path, instance, token), (id, _) =>
                {
                    Interlocked.Increment(ref launches);
                    var start = new ProcessStartInfo(Path.Combine(fixture.Path, "XboxMultiplayerAnalysisTool.exe"))
                    { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var argument in new[] { "--agent-host", "--instance", id.ToString("D"), "--mode", "headless" })
                        start.ArgumentList.Add(argument);
                    var process = Process.Start(start);
                    fixture.Track(process);
                    return process;
                });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await Task.WhenAll(launcher.StartAsync(AgentLaunchMode.Headless, timeout.Token),
                launcher.StartAsync(AgentLaunchMode.Desktop, timeout.Token));
            Assert.Equal(1, launches);
            Assert.Equal(results[0].Instance.InstanceId, results[1].Instance.InstanceId);
            var instance = Assert.Single(fixture.Instances.Read());
            fixture.Own(instance);
            Assert.Equal(1, results.Count(result => result.Launched));
            var stop = await CliAgentCommand.RunAsync(new("stop", InstanceId: instance.InstanceId, Accept: true), "stop",
                timeout.Token, fixture.Path, fixture.Store, fixture.Instances);
            Assert.Equal(0, stop.ExitCode);
        }

        private sealed class HostInstallation : IAsyncDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                TestAgentHostProgram.DirectoryPrefix + Guid.NewGuid().ToString("N"));
            internal string State => TestAgentHostProgram.StateDirectory(Path);
            internal AgentAccessSettingsStore Store => new(System.IO.Path.Combine(State, "policy.json"));
            internal AgentInstanceDirectory Instances => new(System.IO.Path.Combine(Path, "XboxMultiplayerAnalysisTool.exe"),
                System.IO.Path.Combine(State, "instances"));
            private AgentInstanceRecord _owned;
            private readonly List<(int Id, long Started)> _started = [];

            internal HostInstallation()
            {
                Directory.CreateDirectory(Path);
                File.WriteAllText(System.IO.Path.Combine(Path, "test-owned.marker"), "owned");
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                    File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(file)), true);
                foreach (var relative in new[] { "AgentBridge", "Data", "Langs" })
                {
                    var source = System.IO.Path.Combine(AppContext.BaseDirectory, relative);
                    if (!Directory.Exists(source))
                        continue;
                    Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));
                    foreach (var file in Directory.EnumerateFiles(source))
                        File.Copy(file, System.IO.Path.Combine(Path, relative, System.IO.Path.GetFileName(file)));
                }
                File.Copy(System.IO.Path.Combine(Path, "XMAT.Tests.exe"),
                    System.IO.Path.Combine(Path, "XboxMultiplayerAnalysisTool.exe"), true);
            }

            internal void Own(AgentInstanceRecord instance) => _owned = instance;
            internal void Track(Process process) => _started.Add((process.Id, process.StartTime.ToUniversalTime().Ticks));

            internal async Task WaitForObservationsAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!File.Exists(System.IO.Path.Combine(State, "observed.json")))
                    await Task.Delay(20, timeout.Token);
            }

            public async ValueTask DisposeAsync()
            {
                var candidates = (_owned == null ? Instances.Read() : [_owned])
                    .Select(instance => (Id: instance.ProcessId, Started: instance.StartedUtcTicks)).Concat(_started).Distinct().ToArray();
                foreach (var instance in candidates)
                {
                    try
                    {
                        using var process = Process.GetProcessById(instance.Id);
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == instance.Started &&
                            string.Equals(AgentProcessIdentity.ExecutablePath((uint)instance.Id),
                                System.IO.Path.Combine(Path, "XboxMultiplayerAnalysisTool.exe"),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill();
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    catch (ArgumentException) { }
                }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    { Directory.Delete(Path, true); break; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 100)
                    { await Task.Delay(50); }
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int Size;
            internal IntPtr Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedBytes;
            internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr Process, Thread;
            internal uint ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
            string directory, ref StartupInfo startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
