// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using XMAT.AgentConnection;

namespace XMAT.AgentBridge
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var options = AgentBridgeArguments.Parse(args);
            if (options == null)
            {
                await Console.Error.WriteLineAsync("XMAT_AGENT_INVALID_ARGUMENTS");
                return 2;
            }

            try
            {
                var instance = options.InstanceId;
                var pid = options.ProcessId;
                var started = options.StartedUtcTicks;
                if (!instance.HasValue)
                {
                    var installation = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                    var executable = Path.Combine(installation, "XboxMultiplayerAnalysisTool.exe");
                    if (!File.Exists(executable))
                    {
                        await Console.Error.WriteLineAsync("XMAT_AGENT_INSTALLATION_INCOMPLETE");
                        return 4;
                    }
                    var directory = new AgentInstanceDirectory(executable);
                    var selection = directory.Select();
                    if (selection.Failure == AgentDiscoveryFailure.NotRunning && options.Launch != AgentLaunchMode.ConnectOnly)
                    {
                        var launched = await LaunchAsync(installation, options.Launch);
                        if (!launched.HasValue)
                            return 4;
                        selection = directory.Select(launched);
                    }
                    if (selection.Failure != AgentDiscoveryFailure.None)
                    {
                        await Console.Error.WriteLineAsync("XMAT_AGENT_" + selection.Failure.ToString().ToUpperInvariant());
                        return 4;
                    }
                    instance = selection.Instance.InstanceId;
                    pid = selection.Instance.ProcessId;
                    started = selection.Instance.StartedUtcTicks;
                }
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime().Ticks != started)
                {
                    await Console.Error.WriteLineAsync("XMAT_AGENT_INSTANCE_MISMATCH");
                    return 3;
                }

                await using var pipe = new NamedPipeClientStream(
                    ".", $"XMAT.Agent.{instance.Value:N}", PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await pipe.ConnectAsync(timeout.Token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ||
                    serverPid != (uint)pid || process.HasExited ||
                    process.StartTime.ToUniversalTime().Ticks != started)
                {
                    await Console.Error.WriteLineAsync("XMAT_AGENT_INSTANCE_MISMATCH");
                    return 3;
                }

                using var forwarding = new CancellationTokenSource();
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                var toApp = input.CopyToAsync(pipe, forwarding.Token);
                var toClient = pipe.CopyToAsync(output, forwarding.Token);
                try
                {
                    await await Task.WhenAny(toApp, toClient);
                    await output.FlushAsync();
                }
                finally
                {
                    await forwarding.CancelAsync();
                    pipe.Close();
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                Win32Exception or ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("XMAT_AGENT_UNAVAILABLE_OR_WRONG_ELEVATION");
                return 4;
            }
        }

        private static async Task<Guid?> LaunchAsync(string installation, AgentLaunchMode mode)
        {
            var executable = Path.Combine(installation, "xmat.exe");
            if (!File.Exists(executable))
            {
                await Console.Error.WriteLineAsync("XMAT_AGENT_INSTALLATION_INCOMPLETE");
                return null;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = installation
                }
            };
            foreach (var argument in new[] { "agent", "start", "--mode", mode == AgentLaunchMode.Headless ? "headless" : "desktop" })
                process.StartInfo.ArgumentList.Add(argument);
            Task<string> output = null;
            Task error = null;
            var started = false;
            try
            {
                if (!process.Start())
                    return null;
                started = true;
                process.StandardInput.Close();
                output = ReadLaunchResultAsync(process.StandardOutput.BaseStream, timeout.Token);
                error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                var exited = process.WaitForExitAsync(timeout.Token);
                await Task.WhenAny(exited, output);
                if (output.IsFaulted)
                    await output;
                await exited;
                var json = await output;
                await error;
                using (var document = JsonDocument.Parse(json))
                {
                    if (process.ExitCode == 0 && document.RootElement.TryGetProperty("agent", out var result) &&
                        result.TryGetProperty("instanceId", out var instance) && instance.TryGetGuid(out var id))
                        return id;
                    if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        foreach (var failure in errors.EnumerateArray())
                            if (failure.TryGetProperty("code", out var code))
                            {
                                if (code.GetString() == "agent.ExternalLaunchRequired")
                                {
                                    await Console.Error.WriteLineAsync("XMAT_AGENT_EXTERNAL_LAUNCH_REQUIRED: Run xmat agent start from an external Windows terminal; this job forbids independent lifetime.");
                                    return null;
                                }
                                if (code.GetString() == "agent.AccessDisabled")
                                {
                                    await Console.Error.WriteLineAsync("XMAT_AGENT_ACCESS_DISABLED: Explicit local xmat agent setup is required; connecting never enables access.");
                                    return null;
                                }
                            }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or Win32Exception)
            {
                // Only the short-lived CLI handoff is ours to terminate, never the persistent host.
            }
            finally
            {
                await timeout.CancelAsync();
                if (started && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync();
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
                }
                try
                { await Task.WhenAll(output ?? Task.FromResult(string.Empty), error ?? Task.CompletedTask); }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            await Console.Error.WriteLineAsync("XMAT_AGENT_LAUNCH_FAILED");
            return null;
        }

        private static async Task<string> ReadLaunchResultAsync(Stream input, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + read > 65536)
                    throw new IOException("XMAT_AGENT_LAUNCH_RESULT_LIMIT");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    }
}
