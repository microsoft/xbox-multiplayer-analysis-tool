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
    // Written before the proxy is enabled, so XMAT, its guard or the next start can undo it.
    internal sealed record ProxyLease(
        string Nonce, int OwnerProcessId, long OwnerCreationTime,
        ProxySettings Prior, ProxySettings Intended, ProxySettings Applied)
    {
        internal ProcessIdentity Owner => new(OwnerProcessId, OwnerCreationTime);

        // The settings from before capture, but never the obsolete XMAT endpoint itself.
        internal ProxySettings RestoreTarget => Prior.ProxiesTo(Intended.Server) ? Prior.WithoutProxy() : Prior;

        internal bool IsApplied(ProxySettings current) => current == Intended || current == Applied;
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

        internal static RecoveryLock TryAcquire(string journalPath, TimeSpan timeout)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(journalPath).ToUpperInvariant()));
            var mutex = new Mutex(false, @"Global\XMAT.ProxyRecovery." + Convert.ToHexString(hash, 0, 16));
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

    internal static class ProxyGuardSignals
    {
        internal static string Ready(string nonce) => $@"Local\XMAT.ProxyGuard.{nonce}.Ready";

        internal static string Disarm(string nonce) => $@"Local\XMAT.ProxyGuard.{nonce}.Disarm";
    }
}
