// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace XMAT.WebServiceCapture
{
    // The headless --proxy-guard process. XMAT starts it before enabling the Local PC proxy and it runs
    // before any WPF, certificate or database code. It exits when XMAT disarms it or another lease replaces
    // its own; if XMAT dies first, it restores the settings from before capture.
    internal static class ProxyGuard
    {
        internal const string Command = "--proxy-guard";
        internal const int Completed = 0;
        internal const int InvalidArguments = 2;
        internal const int NotOwner = 3;
        internal const int RecoveryFailed = 4;
        internal const int Faulted = 5;
        internal const int JobRefused = 6;
        private const string Independent = "independent";
        private const string AnyJob = "any-job";
        private const int RecoveryAttempts = 5;
        private static readonly TimeSpan OwnershipCheckInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        internal static bool IsGuardCommand(string[] args) => args.Length > 0 && args[0] == Command;

        // --proxy-guard <journal> <nonce> <independent|any-job> [<settings file used instead of WinINet, for tests>]
        internal static string[] Arguments(string journalPath, string nonce, bool requireIndependence, string settingsFile) =>
            new[] { Command, journalPath, nonce, requireIndependence ? Independent : AnyJob, settingsFile }
                .Where(argument => argument != null)
                .ToArray();

        internal static int Run(string[] args)
        {
            try
            {
                return (args.Length is 4 or 5 && args[3] is Independent or AnyJob) ? Guard(args) : InvalidArguments;
            }
            catch (Exception)
            {
                // The helper's terminal boundary: whatever reaches here ends it at once with Faulted rather than letting
                // it go on in a damaged state. A headless helper shows no dialog or crash report; XMAT sees the exit.
                return Faulted;
            }
        }

        private static int Guard(string[] args)
        {
            var journalPath = args[1];
            var nonce = args[2];
            IProxySettingsStore store = args.Length == 5
                ? new FileProxySettingsStore(args[4])
                : WinInetProxySettingsStore.Instance;
            var lease = LeaseJournal.TryRead(journalPath);
            if (lease == null || lease.Nonce != nonce ||
                !ProxyGuardSignals.TryOpen(ProxyGuardSignals.Disarm(nonce), out var disarm))
            {
                return NotOwner;
            }
            using (disarm)
            using (var owner = RecoveryNativeMethods.OpenProcess(ProcessIdentity.WaitAccess, false, lease.OwnerProcessId))
            {
                // An owner that is already gone, or a reused process ID, never gets READY.
                if (owner.IsInvalid ||
                    !RecoveryNativeMethods.GetProcessTimes(owner, out var created, out _, out _, out _) ||
                    created != lease.OwnerCreationTime ||
                    !ProxyGuardSignals.TryOpen(ProxyGuardSignals.Ready(nonce), out var ready))
                {
                    return NotOwner;
                }
                using (ready)
                {
                    // Breaking away from XMAT's job can stop at an outer job, so the job the guard ended up in
                    // must also be known to let it outlive XMAT.
                    if (args[3] == Independent && !ProcessJob.ImmediateJobIsSafe())
                    {
                        return JobRefused;
                    }
                    ready.Set();
                }
                using var ownerExit = new ProcessExitHandle(owner);
                while (true)
                {
                    switch (WaitHandle.WaitAny(new WaitHandle[] { disarm, ownerExit }, OwnershipCheckInterval))
                    {
                        case 0:
                            return Completed;
                        case 1:
                            return RecoverAfterOwnerExit(store, journalPath, lease);
                        default:
                            if (!StillOwned(journalPath, ref lease))
                            {
                                return NotOwner;
                            }
                            break;
                    }
                }
            }
        }

        // Keeps the newest copy of this guard's lease. Only a readable lease with another nonce ends the
        // guard; while the journal is missing or unreadable, the copy in memory stays in charge.
        private static bool StillOwned(string journalPath, ref ProxyLease lease)
        {
            ProxyLease journal;
            try
            {
                journal = LeaseJournal.TryRead(journalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
            if (journal == null)
            {
                return true;
            }
            if (journal.Nonce != lease.Nonce)
            {
                return false;
            }
            lease = journal;
            return true;
        }

        private static int RecoverAfterOwnerExit(IProxySettingsStore store, string journalPath, ProxyLease lease)
        {
            for (var attempt = 1; attempt <= RecoveryAttempts; attempt++)
            {
                try
                {
                    using var gate = RecoveryLock.TryAcquire(journalPath, LockTimeout);
                    if (gate != null)
                    {
                        var journal = LeaseJournal.TryRead(journalPath);
                        if (journal != null && journal.Nonce != lease.Nonce)
                        {
                            // A readable replacement belongs to someone else; it and its settings stay.
                            return NotOwner;
                        }
                        if (PcProxyRecovery.Settle(store, journalPath, journal ?? lease, SettleMode.Recover).Result !=
                            RecoveryResult.Failed)
                        {
                            return Completed;
                        }
                    }
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // Retried; after the last attempt the journal stays for the next start. A fatal failure ends the
                    // helper at its terminal boundary instead.
                }
                Thread.Sleep(RetryDelay);
            }
            return RecoveryFailed;
        }
    }
}
