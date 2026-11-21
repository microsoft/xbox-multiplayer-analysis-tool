// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.AgentConnection
{
    internal sealed record AgentHostStart(AgentInstanceRecord Instance, bool Launched);
    internal sealed class AgentLaunchException(string code, AgentInstanceRecord instance = null) : IOException(code)
    {
        internal string Code { get; } = code;
        internal AgentInstanceRecord Instance { get; } = instance;
    }

    internal sealed class AgentHostLauncher
    {
        private readonly string _installation;
        private readonly string _executable;
        private readonly AgentInstanceDirectory _instances;
        private readonly Func<bool> _enabled;
        private readonly Func<Guid, AgentLaunchMode, Process> _launch;
        private readonly Func<AgentInstanceRecord, CancellationToken, Task> _handshake;
        private readonly TimeSpan _timeout;

        internal AgentHostLauncher(
            string installation, AgentInstanceDirectory instances, Func<bool> enabled,
            Func<AgentInstanceRecord, CancellationToken, Task> handshake,
            Func<Guid, AgentLaunchMode, Process> launch = null, TimeSpan? timeout = null)
        {
            _installation = Path.GetFullPath(installation);
            _executable = Path.Combine(_installation, "XboxMultiplayerAnalysisTool.exe");
            _instances = instances;
            _enabled = enabled;
            _handshake = handshake;
            _launch = launch ?? Launch;
            _timeout = timeout ?? TimeSpan.FromSeconds(35);
        }

        internal Task<AgentHostStart> StartAsync(AgentLaunchMode mode, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                if (mode is not (AgentLaunchMode.Headless or AgentLaunchMode.Desktop))
                    throw new ArgumentOutOfRangeException(nameof(mode));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_timeout);
                var identity = AgentProcessIdentity.Current();
                var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.UserSid))) +
                    (identity.Elevated ? ".Elevated" : ".Standard");
                using var mutex = new Mutex(false, @"Local\XMAT.AgentLaunch." + suffix);
                var acquired = false;
                AgentInstanceRecord started = null;
                try
                {
                    try
                    { acquired = WaitHandle.WaitAny([mutex, deadline.Token.WaitHandle]) == 0; }
                    catch (AbandonedMutexException) { acquired = true; }
                    deadline.Token.ThrowIfCancellationRequested();
                    var selection = _instances.Select();
                    if (selection.Failure == AgentDiscoveryFailure.None)
                    {
                        _handshake(selection.Instance, deadline.Token).GetAwaiter().GetResult();
                        return new AgentHostStart(selection.Instance, false);
                    }
                    if (selection.Failure != AgentDiscoveryFailure.NotRunning)
                        throw new AgentLaunchException("agent." + selection.Failure);
                    if (!_enabled())
                        throw new AgentLaunchException("agent.AccessDisabled");
                    if (!File.Exists(_executable))
                        throw new AgentLaunchException("agent.InstallationIncomplete");
                    var instanceId = Guid.NewGuid();
                    using var process = _launch(instanceId, mode);
                    if (process == null || process.HasExited)
                        throw new AgentLaunchException("agent.StartupFailed");
                    try
                    {
                        started = new(1, instanceId, process.Id, process.StartTime.ToUniversalTime().Ticks,
                            identity.UserSid, identity.Elevated, mode == AgentLaunchMode.Headless ? "headless" : "desktop",
                            "starting", true);
                    }
                    catch (InvalidOperationException)
                    { throw new AgentLaunchException("agent.StartupFailed"); }
                    _instances.PublishStarting(started);
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (process.HasExited)
                            throw new AgentLaunchException("agent.StartupFailed", started);
                        var current = _instances.Read().SingleOrDefault(record => record.InstanceId == instanceId &&
                            record.ProcessId == started.ProcessId && record.StartedUtcTicks == started.StartedUtcTicks);
                        if (current?.State == "failed")
                            throw new AgentLaunchException("agent.StartupFailed", current);
                        if (current != null && !current.Enabled)
                            throw new AgentLaunchException("agent.AccessDisabled", current);
                        if (current?.State == "ready")
                        {
                            var ready = _instances.Select();
                            if (ready.Failure != AgentDiscoveryFailure.None || ready.Instance.InstanceId != instanceId)
                                throw new AgentLaunchException("agent." + (ready.Failure == AgentDiscoveryFailure.None
                                    ? AgentDiscoveryFailure.NotReady : ready.Failure), current);
                            _handshake(current, deadline.Token).GetAwaiter().GetResult();
                            return new AgentHostStart(current, true);
                        }
                        deadline.Token.WaitHandle.WaitOne(100);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new AgentLaunchException("agent.StartupTimeout", started); }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }, cancellationToken);

        private Process Launch(Guid instance, AgentLaunchMode mode)
        {
            var arguments = new[] { _executable, "--agent-host", "--instance", instance.ToString("D"),
                "--mode", mode == AgentLaunchMode.Headless ? "headless" : "desktop" };
            return WindowsIndependentProcess.Start(_executable, arguments, _installation);
        }
    }

    internal static class WindowsIndependentProcess
    {
        internal static Process Start(string executable, string[] arguments, string directory)
        {
            if (!IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out var inJob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            const uint DetachedProcess = 8, NewProcessGroup = 0x200, Breakaway = 0x01000000;
            if (!CreateProcess(executable, new(string.Join(" ", arguments.Select(Quote))), IntPtr.Zero, IntPtr.Zero,
                false, DetachedProcess | NewProcessGroup | (inJob ? Breakaway : 0), IntPtr.Zero,
                directory, ref startup, out var information))
            {
                var error = Marshal.GetLastWin32Error();
                if (inJob && error == 5)
                    throw new AgentLaunchException("agent.ExternalLaunchRequired");
                throw new Win32Exception(error);
            }
            try
            { return Process.GetProcessById((int)information.ProcessId); }
            catch (ArgumentException)
            { throw new AgentLaunchException("agent.StartupFailed"); }
            finally
            {
                CloseHandle(information.Thread);
                CloseHandle(information.Process);
            }
        }

        internal static string Quote(string value)
        {
            var output = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                output.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                output.Append(character);
                slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
