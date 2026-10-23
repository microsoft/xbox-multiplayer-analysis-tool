// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XMAT.WebServiceCapture
{
    internal enum RecoveryResult
    {
        Restored,
        NotNeeded,
        SettingsChanged,
        Failed
    }

    // Makes the Local PC proxy crash-safe. A lease is journaled and a guard process is running before
    // the proxy is enabled; the guard, the fatal-error handlers, normal shutdown or the next start then
    // restore the settings from before capture, but only while they are unchanged.
    internal sealed class PcProxyRecovery
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FatalLockTimeout = TimeSpan.FromSeconds(2);
        private static readonly Lazy<PcProxyRecovery> CurrentRecovery = new(() => new PcProxyRecovery(
            WinInetProxySettingsStore.Instance,
            Path.Combine(DefaultDirectory, "pc-proxy.json"),
            ProcessIdentity.Current,
            Path.ChangeExtension(typeof(PcProxyRecovery).Assembly.Location, ".exe"),
            true));

        private readonly IProxySettingsStore _store;
        private readonly ProcessIdentity _owner;
        private readonly string _guardExecutable;
        private readonly bool _requireIndependentGuard;
        private readonly object _guardSync = new();
        private GuardSession _guard;
        // This process's lease, kept in case its journal goes missing or becomes unreadable. Only read or
        // changed while the recovery lock is held.
        private ProxyLease _lease;

        internal PcProxyRecovery(
            IProxySettingsStore store, string journalPath, ProcessIdentity owner,
            string guardExecutable, bool requireIndependentGuard)
        {
            _store = store;
            JournalPath = journalPath;
            _owner = owner;
            _guardExecutable = guardExecutable;
            _requireIndependentGuard = requireIndependentGuard;
        }

        internal static PcProxyRecovery Current => CurrentRecovery.Value;

        internal static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "XboxMultiplayerAnalysisTool", "ProxyRecovery");

        internal string JournalPath { get; }

        internal TimeSpan GuardReadyTimeout { get; init; } = TimeSpan.FromSeconds(15);

        // Test hook: runs in Disable after the recovery lock is released and before its guard is disarmed.
        internal Action DisableUnlocked { get; set; }

        internal int? GuardProcessId
        {
            get
            {
                lock (_guardSync)
                {
                    return _guard?.ProcessId;
                }
            }
        }

        // Raised on a worker thread after the guard ended unexpectedly, with the outcome of the recovery
        // that then ran.
        internal event Action<RecoveryResult> GuardLost;

        internal void Enable(ProxyEndpoint endpoint)
        {
            using (AcquireLock(LockTimeout))
            {
                var lease = ReadOwnLease() ?? _lease;
                var current = _store.Read();
                lease = new ProxyLease(
                    lease?.Nonce ?? Guid.NewGuid().ToString("N"), _owner.ProcessId, _owner.CreationTime,
                    lease?.Prior ?? current, current.WithProxy(endpoint), null);
                LeaseJournal.Write(JournalPath, lease);
                _lease = lease;
                try
                {
                    EnsureGuard(lease.Nonce);
                    _store.Write(lease.Intended);
                    _lease = lease with { Applied = _store.Read() };
                    LeaseJournal.Write(JournalPath, _lease);
                }
                catch
                {
                    // A failed change is undone only when recognizable; otherwise the lease, and the guard
                    // protecting it, stay for a retry, shutdown or the next start.
                    if (TryRollBack(_lease))
                    {
                        _lease = null;
                        DisarmGuard();
                    }
                    throw;
                }
            }
        }

        // Callers have already verified that the current proxy is their own endpoint.
        internal void Disable()
        {
            string nonce;
            using (AcquireLock(LockTimeout))
            {
                var lease = ReadOwnLease() ?? _lease;
                var current = _store.Read();
                if (lease?.IsApplied(current) == true)
                {
                    _store.Write(lease.RestoreTarget);
                }
                else if (current.UsesProxy)
                {
                    _store.Write(current.WithoutProxy());
                }
                SharedFile.Delete(JournalPath);
                nonce = _lease?.Nonce;
                _lease = null;
            }
            DisableUnlocked?.Invoke();
            // A capture started since the lock was released has its own guard; only this lease's guard is disarmed.
            if (nonce != null)
            {
                DisarmGuard(nonce);
            }
        }

        internal RecoveryResult RestoreOnShutdown() => RestoreOwnLease(LockTimeout);

        // Bounded and never throws, so fatal-error handlers can call it before logging.
        internal RecoveryResult TryRecoverAfterFatalError() => RestoreOwnLease(FatalLockTimeout);

        // Runs before capture starts. Undoes a lease whose owner is gone. Returns an unjournaled loopback
        // proxy that nothing listens on, as an older XMAT could leave; changing it needs confirmation.
        internal ProxySettings HealAtStartup()
        {
            using (AcquireLock(LockTimeout))
            {
                var lease = LeaseJournal.TryRead(JournalPath);
                if (lease != null)
                {
                    if (lease.Owner.GetState() == ProcessState.Dead)
                    {
                        Recover(_store, JournalPath, lease);
                    }
                    return null;
                }
                // Nothing can be done with an unreadable journal.
                SharedFile.Delete(JournalPath);
                var current = _store.Read();
                return LooksAbandoned(current) ? current : null;
            }
        }

        internal bool DisableAbandonedProxy(ProxySettings confirmed)
        {
            using (AcquireLock(LockTimeout))
            {
                if (LeaseJournal.TryRead(JournalPath) != null || _store.Read() != confirmed)
                {
                    return false;
                }
                _store.Write(confirmed.WithoutProxy());
                return true;
            }
        }

        // Writes only if the settings are still exactly what the lease applied.
        internal static RecoveryResult Restore(IProxySettingsStore store, ProxyLease lease)
        {
            var current = store.Read();
            if (lease.IsApplied(current))
            {
                store.Write(lease.RestoreTarget);
                return RecoveryResult.Restored;
            }
            return current == lease.Prior || current == lease.RestoreTarget
                ? RecoveryResult.NotNeeded
                : RecoveryResult.SettingsChanged;
        }

        // Shared by XMAT, its guard and the next start; the lease is finished afterwards. A failure leaves
        // the journal in place for a later attempt.
        internal static RecoveryResult Recover(IProxySettingsStore store, string journalPath, ProxyLease lease)
        {
            var result = Restore(store, lease);
            SharedFile.Delete(journalPath);
            return result;
        }

        internal static bool LooksAbandoned(ProxySettings settings) =>
            settings.UsesProxy && ProxyEndpoint.TryParse(settings.Server, out var endpoint) && IsLoopback(endpoint.Host) &&
            !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == endpoint.Port);

        private static bool IsLoopback(string host) =>
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

        private RecoveryLock AcquireLock(TimeSpan timeout) =>
            RecoveryLock.TryAcquire(JournalPath, timeout) ??
            throw new TimeoutException(Localization.GetLocalizedString("PROXY_RECOVERY_BUSY"));

        // Returns this process's lease. A lease left by an XMAT that died is undone first.
        private ProxyLease ReadOwnLease()
        {
            var lease = LeaseJournal.TryRead(JournalPath);
            if (lease == null || lease.Owner == _owner)
            {
                return lease;
            }
            if (lease.Owner.GetState() != ProcessState.Dead)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_RECOVERY_OTHER_INSTANCE"));
            }
            Recover(_store, JournalPath, lease);
            return null;
        }

        // Rolls back a failed Enable. Settings that are neither the ones from before nor the requested
        // ones are not taken over as this change just to undo them; they and the lease are kept.
        private bool TryRollBack(ProxyLease lease)
        {
            try
            {
                if (Restore(_store, lease) == RecoveryResult.SettingsChanged)
                {
                    PublicUtilities.AppLog(
                        LogLevel.ERROR,
                        "A failed Windows proxy change left settings that are neither the previous nor the requested ones; they and the recovery lease were kept.");
                    return false;
                }
                SharedFile.Delete(JournalPath);
                return true;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not undo a failed Windows proxy change: {ex}");
                return false;
            }
        }

        // Undoes this process's lease (only the one with `nonce`, when given) from its journal or, if that
        // is missing or unreadable, from memory. A readable lease with another nonce has replaced it and is
        // left alone with its settings.
        private RecoveryResult RestoreOwnLease(TimeSpan timeout, string nonce = null)
        {
            try
            {
                ProxyLease lease;
                RecoveryResult result;
                using (var gate = RecoveryLock.TryAcquire(JournalPath, timeout))
                {
                    if (gate == null)
                    {
                        return RecoveryResult.Failed;
                    }
                    lease = _lease;
                    if (lease == null || (nonce != null && lease.Nonce != nonce))
                    {
                        return RecoveryResult.NotNeeded;
                    }
                    var journal = LeaseJournal.TryRead(JournalPath);
                    result = journal != null && journal.Nonce != lease.Nonce
                        ? RecoveryResult.SettingsChanged
                        : Recover(_store, JournalPath, journal ?? lease);
                    _lease = null;
                }
                DisarmGuard(lease.Nonce);
                return result;
            }
            catch (Exception)
            {
                return RecoveryResult.Failed;
            }
        }

        private void EnsureGuard(string nonce)
        {
            lock (_guardSync)
            {
                if (_guard?.Nonce == nonce)
                {
                    return;
                }
            }
            DisarmGuard();
            var session = StartGuard(nonce);
            lock (_guardSync)
            {
                _guard = session;
            }
            // Registered after publishing, so an immediate exit is still treated as unexpected.
            session.Watch(() => OnGuardExited(session));
        }

        private GuardSession StartGuard(string nonce)
        {
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Ready(nonce));
            var session = GuardSession.Start(
                _guardExecutable,
                ProxyGuard.Arguments(
                    JournalPath, nonce, _requireIndependentGuard, (_store as FileProxySettingsStore)?.FilePath),
                nonce,
                _requireIndependentGuard);
            try
            {
                if (WaitHandle.WaitAny(new WaitHandle[] { ready, session.Exit }, GuardReadyTimeout) != 0)
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString(
                        session.ExitCode == ProxyGuard.JobRefused ? "PROXY_RECOVERY_JOB_REFUSED" : "PROXY_RECOVERY_GUARD_FAILED"));
                }
                return session;
            }
            catch
            {
                session.Stop();
                throw;
            }
        }

        // Disarms the current guard, or only the one for `nonce` when given.
        private void DisarmGuard(string nonce = null)
        {
            GuardSession session;
            lock (_guardSync)
            {
                session = _guard;
                if (session == null || (nonce != null && session.Nonce != nonce))
                {
                    return;
                }
                _guard = null;
            }
            session.Disarm();
        }

        private void OnGuardExited(GuardSession session)
        {
            lock (_guardSync)
            {
                if (!ReferenceEquals(_guard, session))
                {
                    return;
                }
                _guard = null;
            }
            session.Disarm();
            // Nothing protects the proxy any more, so it is reset whether or not anyone is listening.
            var result = RestoreOwnLease(LockTimeout, session.Nonce);
            GuardLost?.Invoke(result);
        }

        private sealed class GuardSession
        {
            private readonly SafeProcessHandle _process;
            private readonly EventWaitHandle _disarm;
            private RegisteredWaitHandle _watch;

            private GuardSession(string nonce, SafeProcessHandle process, int processId, EventWaitHandle disarm)
            {
                Nonce = nonce;
                ProcessId = processId;
                _process = process;
                _disarm = disarm;
                Exit = new ProcessExitHandle(process);
            }

            internal string Nonce { get; }

            internal int ProcessId { get; }

            internal ProcessExitHandle Exit { get; }

            internal int ExitCode => RecoveryNativeMethods.GetExitCodeProcess(_process, out var code) ? (int)code : -1;

            internal static GuardSession Start(string executable, string[] arguments, string nonce, bool requireIndependence)
            {
                var disarm = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Disarm(nonce));
                try
                {
                    var process = ProxyGuardLauncher.Start(executable, arguments, requireIndependence, out var processId);
                    return new GuardSession(nonce, process, processId, disarm);
                }
                catch
                {
                    disarm.Dispose();
                    throw;
                }
            }

            internal void Watch(Action exited) =>
                _watch = ThreadPool.RegisterWaitForSingleObject(Exit, (_, _) => exited(), null, Timeout.Infinite, true);

            // Lets the guard exit without touching the settings.
            internal void Disarm()
            {
                _watch?.Unregister(null);
                _disarm.Set();
                Close();
            }

            // For a guard that never reported READY.
            internal void Stop()
            {
                _disarm.Set();
                RecoveryNativeMethods.TerminateProcess(_process, 1);
                Close();
            }

            private void Close()
            {
                Exit.Dispose();
                _process.Dispose();
                _disarm.Dispose();
            }
        }
    }

    internal static class ProxyGuardLauncher
    {
        internal static SafeProcessHandle Start(
            string executable, string[] arguments, bool requireIndependence, out int processId)
        {
            var commandLine = string.Join(" ", arguments.Prepend(executable).Select(argument => $"\"{argument}\""));
            // Tried in any job, so that a job ending XMAT does not end the guard; outside a job it has no effect.
            var guard = TryCreate(
                executable, commandLine, RecoveryNativeMethods.CreateBreakawayFromJob, out processId, out var error);
            if (guard != null)
            {
                return guard;
            }
            if (error != RecoveryNativeMethods.ErrorAccessDenied)
            {
                throw new Win32Exception(error);
            }
            // The job does not allow breaking away, so the guard may only stay in it if the job is known to
            // let it outlive XMAT.
            if (requireIndependence && !ProcessJob.ImmediateJobIsSafe())
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_RECOVERY_JOB_REFUSED"));
            }
            return TryCreate(executable, commandLine, 0, out processId, out var failure) ?? throw new Win32Exception(failure);
        }

        private static SafeProcessHandle TryCreate(
            string executable, string commandLine, uint flags, out int processId, out int error)
        {
            var startup = new RecoveryNativeMethods.StartupInfo { Size = Marshal.SizeOf<RecoveryNativeMethods.StartupInfo>() };
            if (!RecoveryNativeMethods.CreateProcess(
                    executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var information))
            {
                error = Marshal.GetLastWin32Error();
                processId = 0;
                return null;
            }
            RecoveryNativeMethods.CloseHandle(information.Thread);
            error = 0;
            processId = information.ProcessId;
            return new SafeProcessHandle(information.Process, true);
        }
    }

    internal static class ProcessJob
    {
        // Whether this process's immediate job is known to let it outlive XMAT: no job, or one that does not
        // end its processes when it is closed. A failed membership or job query counts as not known. Outer
        // (nested) jobs are not checked.
        internal static bool ImmediateJobIsSafe()
        {
            using var current = RecoveryNativeMethods.OpenProcess(ProcessIdentity.WaitAccess, false, Environment.ProcessId);
            if (current.IsInvalid || !RecoveryNativeMethods.IsProcessInJob(current, IntPtr.Zero, out var inJob))
            {
                return false;
            }
            return !inJob ||
                (RecoveryNativeMethods.QueryInformationJobObject(
                    IntPtr.Zero, RecoveryNativeMethods.JobExtendedLimitInformationClass, out var information,
                    Marshal.SizeOf<RecoveryNativeMethods.JobExtendedLimitInformation>(), IntPtr.Zero) &&
                 (information.BasicLimitInformation.LimitFlags & RecoveryNativeMethods.JobKillOnClose) == 0);
        }
    }
}
