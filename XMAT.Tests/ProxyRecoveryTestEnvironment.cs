// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // Each test gets its own journal and file-backed proxy settings, so neither the code under test nor
    // the guard processes it starts ever write the real WinINet settings.
    internal sealed class ProxyRecoveryTestEnvironment : IDisposable
    {
        internal static readonly ProxyEndpoint Endpoint = new("127.0.0.1", 8880);
        internal static readonly ProxySettings UserSettings =
            new(ProxySettings.Direct | AutoDetect, "corp-proxy:8080", "<local>", "http://wpad/proxy.pac");
        private const int AutoDetect = 0x8;
        private readonly List<Process> _processes = new();
        private readonly List<PcProxyRecovery> _recoveries = new();

        internal ProxyRecoveryTestEnvironment()
        {
            Folder = Path.Combine(Path.GetTempPath(), "xmat-proxy-recovery-tests", Guid.NewGuid().ToString("N"));
            Store = new FileProxySettingsStore(Path.Combine(Folder, "settings.json"));
            Store.Write(UserSettings);
        }

        internal string Folder { get; }

        internal string JournalPath => Path.Combine(Folder, "pc-proxy.json");

        internal FileProxySettingsStore Store { get; }

        internal static ProxySettings CapturingSettings => UserSettings.WithProxy(Endpoint);

        internal static string GuardExecutable => Path.Combine(AppContext.BaseDirectory, "XboxMultiplayerAnalysisTool.exe");

        // Stands in for an XMAT process: it runs until it is killed.
        internal Process StartOwner()
        {
            var process = Process.Start(new ProcessStartInfo("ping.exe", "-n 600 127.0.0.1")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            _processes.Add(process);
            return process;
        }

        internal static ProcessIdentity Identity(Process process) =>
            ProcessIdentity.TryGet(process.Id, out var identity)
                ? identity
                : throw new InvalidOperationException("The process has already ended.");

        internal ProcessIdentity DeadOwner()
        {
            var owner = StartOwner();
            var identity = Identity(owner);
            owner.Kill();
            owner.WaitForExit();
            return identity;
        }

        internal PcProxyRecovery Recovery(
            ProcessIdentity owner, string guardExecutable = null, FileProxySettingsStore store = null)
        {
            var recovery = new PcProxyRecovery(
                store ?? Store, JournalPath, owner, guardExecutable ?? GuardExecutable, false);
            _recoveries.Add(recovery);
            return recovery;
        }

        internal Process Guard(PcProxyRecovery recovery) =>
            Track(Process.GetProcessById(recovery.GuardProcessId.Value));

        // Ended when the test ends. The handle is opened now, so the exit code stays available.
        internal Process Track(Process process)
        {
            _ = process.SafeHandle;
            _processes.Add(process);
            return process;
        }

        // Journals a lease as if `owner` had started capturing, without starting a guard.
        internal ProxyLease WriteLease(ProcessIdentity owner, ProxySettings prior, bool applied)
        {
            var lease = new ProxyLease(
                Guid.NewGuid().ToString("N"), owner.ProcessId, owner.CreationTime, prior, prior.WithProxy(Endpoint), null);
            LeaseJournal.Write(JournalPath, lease);
            Store.Write(applied ? lease.Intended : prior);
            return lease;
        }

        public void Dispose()
        {
            foreach (var recovery in _recoveries)
            {
                recovery.RestoreOnShutdown();
            }
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }
                process.Dispose();
            }
            try
            {
                Directory.Delete(Folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
