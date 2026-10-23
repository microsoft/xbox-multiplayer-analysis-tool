// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XMAT.WebServiceCapture
{
    // Written before the proxy is enabled, so XMAT, its guard or the next start can undo it. A re-enable also
    // keeps the previous capture's verified settings until the new ones are verified, so XMAT ending before or
    // after it writes them is still undone, and keeps the safe restore target of the first capture.
    internal sealed record ProxyLease(
        string Nonce, int OwnerProcessId, long OwnerCreationTime,
        ProxySettings Prior, ProxySettings Intended, ProxySettings Applied,
        ProxySettings PreviousApplied = null, ProxySettings SafeTarget = null)
    {
        internal ProcessIdentity Owner => new(OwnerProcessId, OwnerCreationTime);

        // The settings from before capture, but never an obsolete XMAT endpoint itself.
        internal ProxySettings RestoreTarget => SafeTarget ?? Prior.WithoutProxyTo(Intended.Server);

        // XMAT's own settings: the ones this lease applied or is applying, and the previous capture's until those
        // are verified. Settings read back the same, as for IsEquivalentTo, count too.
        internal bool IsApplied(ProxySettings current) =>
            current.IsEquivalentTo(Intended) || current.IsEquivalentTo(Applied) || current.IsEquivalentTo(PreviousApplied);

        // Whether traffic still goes to an XMAT endpoint of this lease, whatever else changed: the same host and
        // port as the settings it applied, is applying or replaces, with the proxy on.
        internal bool ProxiesToItsEndpoint(ProxySettings current) =>
            current.ProxiesTo(Intended.Server) ||
            (Applied != null && current.ProxiesTo(Applied.Server)) ||
            (PreviousApplied != null && current.ProxiesTo(PreviousApplied.Server));
    }

    internal static class LeaseJournal
    {
        // A missing or unreadable lease reads as null. Only callers holding the recovery lock remove an
        // unreadable journal, so a lease that has just replaced it is never deleted.
        internal static ProxyLease TryRead(string path)
        {
            string text;
            try
            {
                text = SharedFile.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            try
            {
                var lease = JsonSerializer.Deserialize<ProxyLease>(text);
                return lease?.Nonce != null && lease.Prior != null && lease.Intended != null ? lease : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static void Write(string path, ProxyLease lease) =>
            SharedFile.Replace(path, JsonSerializer.Serialize(lease));
    }

    // Serializes every XMAT process that changes the proxy guarded by one journal.
    internal sealed class RecoveryLock : IDisposable
    {
        private readonly Mutex _mutex;

        private RecoveryLock(Mutex mutex) => _mutex = mutex;

        internal static string Name(string journalPath) =>
            @"Global\XMAT.ProxyRecovery." + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(journalPath).ToUpperInvariant())), 0, 16);

        // Returns null while another holder has the lock, or when the name is taken by a mutex this user cannot
        // use or that is not limited to this user. That keeps other users out but cannot stop them from taking
        // the name first; capture then stays unavailable, as when busy.
        internal static RecoveryLock TryAcquire(string journalPath, TimeSpan timeout)
        {
            Mutex mutex;
            try
            {
                // Global, so this user's XMATs in every session share the one per-user journal.
                mutex = new Mutex(
                    Name(journalPath), new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
            {
                return null;
            }
            try
            {
                if (mutex.WaitOne(timeout))
                {
                    return new RecoveryLock(mutex);
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died; journal writes are atomic, so the state is still consistent.
                return new RecoveryLock(mutex);
            }
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    internal enum ProcessState
    {
        Alive,
        Dead,
        Unknown
    }

    // A process ID alone can be reused; the creation time tells the processes apart.
    internal readonly record struct ProcessIdentity(int ProcessId, long CreationTime)
    {
        internal const uint WaitAccess = RecoveryNativeMethods.Synchronize | RecoveryNativeMethods.ProcessQueryLimitedInformation;

        internal static ProcessIdentity Current { get; } =
            TryGet(Environment.ProcessId, out var current) ? current : throw new InvalidOperationException();

        internal static bool TryGet(int processId, out ProcessIdentity identity)
        {
            using var process = RecoveryNativeMethods.OpenProcess(WaitAccess, false, processId);
            if (!process.IsInvalid && RecoveryNativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
            {
                identity = new(processId, created);
                return true;
            }
            identity = default;
            return false;
        }

        // Unknown (for example, access denied) must be treated as possibly alive.
        internal ProcessState GetState()
        {
            using var process = RecoveryNativeMethods.OpenProcess(WaitAccess, false, ProcessId);
            if (process.IsInvalid)
            {
                return Marshal.GetLastWin32Error() == RecoveryNativeMethods.ErrorInvalidParameter
                    ? ProcessState.Dead
                    : ProcessState.Unknown;
            }
            if (!RecoveryNativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
            {
                return ProcessState.Unknown;
            }
            if (created != CreationTime)
            {
                return ProcessState.Dead;
            }
            using var exited = new ProcessExitHandle(process);
            return exited.WaitOne(0) ? ProcessState.Dead : ProcessState.Alive;
        }
    }

    // Lets a process handle take part in WaitHandle waits; the caller keeps the handle open.
    internal sealed class ProcessExitHandle : WaitHandle
    {
        internal ProcessExitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
    }

    // Session-local events between XMAT and its guard, which always share a session and a user.
    internal static class ProxyGuardSignals
    {
        private static NamedWaitHandleOptions Options => new() { CurrentUserOnly = true, CurrentSessionOnly = true };

        internal static string Ready(string nonce) => $@"Local\XMAT.ProxyGuard.{nonce}.Ready";

        internal static string Disarm(string nonce) => $@"Local\XMAT.ProxyGuard.{nonce}.Disarm";

        // A name taken by an event this user cannot use, or one not limited to this user, leaves the helper
        // unavailable.
        internal static EventWaitHandle Create(string name)
        {
            try
            {
                return new EventWaitHandle(false, EventResetMode.ManualReset, name, Options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_RECOVERY_GUARD_FAILED"), ex);
            }
        }

        internal static bool TryOpen(string name, out EventWaitHandle signal) =>
            EventWaitHandle.TryOpenExisting(name, Options, out signal);
    }
}
