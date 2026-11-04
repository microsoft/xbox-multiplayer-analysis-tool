// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        Failed,
        // A guard exit whose capture had already ended or been replaced by a newer one; nothing is done or reported.
        Superseded
    }

    // What settling a lease does with settings that are neither XMAT's own nor the safe target.
    internal enum SettleMode
    {
        // Automatic recovery: someone else's settings are kept, and the lease ends.
        Recover,
        // Stopping capture, an explicit request: XMAT's exact endpoint is turned off; someone else's proxy is kept.
        Disable,
        // Undoing a failed Enable, whose write may have left them half done: they, the lease and its guard are kept.
        RollBack
    }

    // The outcome of settling a lease, with the settings read last, if any, and what made it fail.
    internal readonly record struct Settlement(RecoveryResult Result, ProxySettings Found, Exception Failure);

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
        // The nonce of the newest guard; a replacement guard always starts a new one.
        private string _latestNonce;

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

        // Test hook: wraps the recovery after an unexpected guard exit, so a test can act just before it and
        // observe its end.
        internal Action<Action> AroundGuardExitRecovery { get; set; }

        // Test hook: runs in Enable just before (false) and just after (true) the new settings are written.
        internal Action<bool> SettingsWrite { get; set; }

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
        // that then ran and the nonce of the lease the guard protected.
        internal event Action<RecoveryResult, string> GuardLost;

        // Whether a newer guard, and so a newer capture, started after the one with `nonce`, so a notice about
        // that one no longer applies.
        internal bool IsSuperseded(string nonce) => Volatile.Read(ref _latestNonce) != nonce;

        internal void Enable(ProxyEndpoint endpoint)
        {
            using (AcquireLock(LockTimeout))
            {
                var previous = ReadOwnLease(endpoint) ?? _lease;
                var current = ReadBeforeEnable(previous, endpoint);
                // A capture's restore baseline is carried forward only while the settings are still all its own. Once
                // they changed, its lease is settled first, as recovery would, and the settings read afterwards become
                // the new baseline, so a later restore never brings back settings older than the user's.
                if (previous != null && !previous.IsApplied(current))
                {
                    var settlement = Settle(_store, JournalPath, previous, SettleMode.Recover);
                    if (settlement.Result == RecoveryResult.Failed)
                    {
                        PublicUtilities.AppLog(
                            LogLevel.ERROR,
                            $"Could not settle the previous capture's recovery lease before enabling capture; it and its guard were kept. {settlement.Failure}");
                        throw EnableFailure(
                            "PROXY_RECOVERY_UNRESTORED", "PROXY_RECOVERY_UNRESTORED", previous, endpoint, settlement.Found,
                            settlement.Failure);
                    }
                    PublicUtilities.AppLog(
                        LogLevel.INFO,
                        $"The previous capture's recovery lease was settled ({settlement.Result}) because the Windows proxy settings were no longer all its own; the settings read next become the ones to restore.");
                    _lease = null;
                    DisarmGuard(previous.Nonce);
                    previous = null;
                    current = ReadBeforeEnable(null, endpoint);
                }
                var intended = current.WithProxy(endpoint);
                // A generation is one guard: a re-enable keeps the guard only while it is still running for this
                // lease, and a replacement always gets a new nonce, so a late exit of an earlier guard can never
                // be taken for it.
                var reuse = IsLiveGuardFor(previous);
                var lease = new ProxyLease(
                    reuse ? previous.Nonce : Guid.NewGuid().ToString("N"), _owner.ProcessId, _owner.CreationTime,
                    previous?.Prior ?? current, intended, null,
                    previous?.Applied ?? previous?.PreviousApplied,
                    (previous?.RestoreTarget ?? current).WithoutProxyTo(intended.Server));
                LeaseJournal.Write(JournalPath, lease);
                _lease = lease;
                if (!reuse)
                {
                    Volatile.Write(ref _latestNonce, lease.Nonce);
                }
                try
                {
                    if (!reuse)
                    {
                        StartNewGuard(lease.Nonce);
                    }
                    SettingsWrite?.Invoke(false);
                    _store.Write(lease.Intended);
                    SettingsWrite?.Invoke(true);
                    var readBack = _store.Read();
                    if (readBack.IsEquivalentTo(lease.Intended))
                    {
                        _lease = lease with { Applied = readBack, PreviousApplied = null };
                        LeaseJournal.Write(JournalPath, _lease);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // A failed change is undone only when recognizable; otherwise the lease, and the guard
                    // protecting it, stay for a retry, shutdown or the next start.
                    var settlement = Settle(_store, JournalPath, lease, SettleMode.RollBack);
                    if (settlement.Result == RecoveryResult.Failed)
                    {
                        PublicUtilities.AppLog(
                            LogLevel.ERROR,
                            $"Could not undo a failed Windows proxy change; the recovery lease was kept. {settlement.Failure}");
                        throw EnableFailure(
                            "PROXY_RECOVERY_UNRESTORED", "PROXY_RECOVERY_UNRESTORED", lease, endpoint, settlement.Found, ex);
                    }
                    if (settlement.Result == RecoveryResult.SettingsChanged)
                    {
                        PublicUtilities.AppLog(
                            LogLevel.ERROR,
                            "A failed Windows proxy change left settings that are neither the previous nor the requested ones; they and the recovery lease were kept.");
                    }
                    else
                    {
                        _lease = null;
                        DisarmGuard(lease.Nonce);
                    }
                    throw;
                }
                throw RejectReadBack(lease);
            }
        }

        // Stops capture, an explicit request. XMAT's own settings are restored; otherwise the proxy is turned off only
        // while the settings read now use XMAT's exact endpoint: `expected`, the one the caller configured, or one of
        // its lease's. Someone else's proxy is kept and reported; the lease ends either way. A failed attempt keeps
        // the lease, its journal and its guard.
        internal void Disable() => Disable(null);

        internal void Disable(ProxyEndpoint expected)
        {
            string nonce;
            Settlement settlement;
            using (AcquireLock(LockTimeout))
            {
                var lease = ReadOwnLease(enabling: null) ?? _lease;
                settlement = Settle(_store, JournalPath, lease, SettleMode.Disable, expected);
                if (settlement.Result == RecoveryResult.Failed)
                {
                    PublicUtilities.AppLog(
                        LogLevel.ERROR, $"Could not turn off the Windows proxy when capture stopped. {settlement.Failure}");
                    // Manual guidance names only an endpoint known to be XMAT's: the lease's, or the one the caller expects.
                    throw new ProxyRecoveryException(
                        lease != null ? Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", EndpointsOf(lease))
                        : expected != null ? Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FAILED", expected.ToString())
                        : Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FAILED_UNKNOWN"));
                }
                nonce = _lease?.Nonce;
                _lease = null;
            }
            DisableUnlocked?.Invoke();
            // A capture started since the lock was released has its own guard; only this lease's guard is disarmed.
            if (nonce != null)
            {
                DisarmGuard(nonce);
            }
            if (settlement.Result == RecoveryResult.SettingsChanged)
            {
                throw new ProxyRecoveryException(
                    Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FOREIGN", settlement.Found.Server));
            }
        }

        internal RecoveryResult RestoreOnShutdown() => RestoreOwnLease(LockTimeout);

        // Bounded and never throws, so fatal-error handlers can call it before logging.
        internal RecoveryResult TryRecoverAfterFatalError() => RestoreOwnLease(FatalLockTimeout);

        // Runs before capture starts. Undoes a lease whose owner is gone. Returns a loopback proxy that nothing
        // listens on, so that the user is asked before it is turned off: one without a journal, as an older XMAT
        // could leave, or a dead lease's endpoint whose other settings changed, which is never overwritten unasked.
        internal ProxySettings HealAtStartup()
        {
            using (AcquireLock(LockTimeout))
            {
                var lease = LeaseJournal.TryRead(JournalPath);
                if (lease != null)
                {
                    if (lease.Owner.GetState() != ProcessState.Dead)
                    {
                        return null;
                    }
                    // A failed attempt keeps the journal for the next one.
                    var settlement = Settle(_store, JournalPath, lease, SettleMode.Recover);
                    return settlement.Result == RecoveryResult.Failed && settlement.Found is { } found &&
                        lease.ProxiesToItsEndpoint(found) && LooksAbandoned(found)
                        ? found
                        : null;
                }
                // Nothing can be done with an unreadable journal.
                SharedFile.Delete(JournalPath);
                var current = _store.Read();
                return LooksAbandoned(current) ? current : null;
            }
        }

        // Turns off the proxy the user confirmed, if the settings are still exactly those. A dead XMAT's lease that
        // keeps that endpoint is settled as stopping capture would.
        internal bool DisableAbandonedProxy(ProxySettings confirmed)
        {
            using (AcquireLock(LockTimeout))
            {
                if (_store.Read() != confirmed)
                {
                    return false;
                }
                var lease = LeaseJournal.TryRead(JournalPath);
                if (lease == null)
                {
                    _store.Write(confirmed.WithoutProxy());
                    return true;
                }
                return lease.Owner.GetState() == ProcessState.Dead &&
                    Settle(_store, JournalPath, lease, SettleMode.Disable).Result is
                        RecoveryResult.Restored or RecoveryResult.NotNeeded;
            }
        }

        // The one way a lease is settled, under the recovery lock, by XMAT, its guard or the next start. The
        // settings are read afresh: XMAT's own ones are restored to the safe target, which counts only once they
        // read back as that target, and settings already at the safe target need nothing. Settings that still send
        // traffic to an endpoint of the lease but differ otherwise are never overwritten automatically: the attempt
        // fails and the journal stays. Only stopping capture may turn off XMAT's exact endpoint, `expected` or the
        // lease's, keeping the other changes. Anyone else's settings are kept, and the lease ends, except after a
        // failed Enable. The journal is removed once the lease is settled; a failed read, write or verification
        // keeps it, and callers then keep the lease and its guard. `lease` is null only when stopping capture with
        // nothing journaled.
        internal static Settlement Settle(
            IProxySettingsStore store, string journalPath, ProxyLease lease, SettleMode mode, ProxyEndpoint expected = null)
        {
            ProxySettings found = null;
            try
            {
                found = store.Read();
                ProxySettings target = null;
                RecoveryResult result;
                if (lease?.IsApplied(found) == true)
                {
                    target = lease.RestoreTarget;
                    result = RecoveryResult.Restored;
                }
                else if (lease != null && found.IsEquivalentTo(lease.RestoreTarget))
                {
                    result = RecoveryResult.NotNeeded;
                }
                else if (mode == SettleMode.Disable &&
                    ((expected != null && found.ProxiesTo(expected)) || lease?.ProxiesToItsEndpoint(found) == true))
                {
                    target = found.WithoutProxy();
                    result = RecoveryResult.Restored;
                }
                else if (lease?.ProxiesToItsEndpoint(found) == true)
                {
                    return new(RecoveryResult.Failed, found, null);
                }
                else if (mode == SettleMode.RollBack)
                {
                    return new(RecoveryResult.SettingsChanged, found, null);
                }
                else
                {
                    // Stopping capture has nothing to turn off once the proxy is off.
                    result = mode == SettleMode.Disable && !found.UsesProxy
                        ? RecoveryResult.NotNeeded
                        : RecoveryResult.SettingsChanged;
                }
                if (target != null)
                {
                    store.Write(target);
                    found = store.Read();
                    if (!found.IsEquivalentTo(target))
                    {
                        return new(RecoveryResult.Failed, found, null);
                    }
                }
                SharedFile.Delete(journalPath);
                return new(result, found, null);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return new(RecoveryResult.Failed, found, ex);
            }
        }

        // The XMAT endpoints of `lease`, if any, and `attempted`, the one being enabled, if given.
        private static List<ProxyEndpoint> XmatEndpoints(ProxyLease lease, ProxyEndpoint attempted = null)
        {
            var endpoints = new List<ProxyEndpoint>();
            foreach (var server in new[]
                { lease?.Intended.Server, lease?.Applied?.Server, lease?.PreviousApplied?.Server, attempted?.ToString() })
            {
                if (ProxyEndpoint.TryParse(server, out var endpoint) && !endpoints.Any(endpoint.Matches))
                {
                    endpoints.Add(endpoint);
                }
            }
            return endpoints;
        }

        // The same endpoints as shown to the user, for example "127.0.0.1:8880".
        private static string EndpointsOf(ProxyLease lease, ProxyEndpoint attempted = null) =>
            string.Join(", ", XmatEndpoints(lease, attempted));

        // Whether Windows may still send traffic to one of `endpoints` with `found`, the settings read last: they are
        // unknown, their proxy server cannot be parsed, or it is one of those endpoints, or any loopback name or
        // address on one of their ports, which can reach the same listener. Only no proxy at all, or a parsed one
        // elsewhere, is definitely not XMAT's.
        private static bool MayUseXmat(ProxySettings found, IReadOnlyCollection<ProxyEndpoint> endpoints) =>
            found == null ||
            (found.UsesProxy &&
             (!ProxyEndpoint.TryParse(found.Server, out var server) ||
              endpoints.Any(endpoint =>
                  server.Matches(endpoint) || (server.Port == endpoint.Port && IsLoopback(server.Host)))));

        // The error for an Enable that fails but keeps a lease, and its journal, for a later attempt. Unless the
        // settings read last are definitely not XMAT's, the activation is uncertain, so the caller keeps its listener.
        private static InvalidOperationException EnableFailure(
            string uncertainKey, string ordinaryKey, ProxyLease lease, ProxyEndpoint attempted, ProxySettings found,
            Exception inner)
        {
            var endpoints = XmatEndpoints(lease, attempted);
            var shown = string.Join(", ", endpoints);
            return MayUseXmat(found, endpoints)
                ? new ProxyActivationUncertainException(Localization.GetLocalizedString(uncertainKey, shown), inner)
                : new ProxyRecoveryException(Localization.GetLocalizedString(ordinaryKey, shown), inner);
        }

        internal static bool LooksAbandoned(ProxySettings settings) =>
            settings.UsesProxy && ProxyEndpoint.TryParse(settings.Server, out var endpoint) && IsLoopback(endpoint.Host) &&
            !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => IsListenerFor(listener, endpoint));

        // Whether connections to the proxy's loopback host and port reach this listener: one on that exact
        // address, an IPv4 wildcard for an IPv4 host, or an IPv6 wildcard, which may also accept IPv4.
        internal static bool IsListenerFor(IPEndPoint listener, ProxyEndpoint endpoint)
        {
            if (listener.Port != endpoint.Port)
            {
                return false;
            }
            var address = listener.Address.IsIPv4MappedToIPv6 ? listener.Address.MapToIPv4() : listener.Address;
            var targets = string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                : new[] { IPAddress.Parse(endpoint.Host) };
            return targets.Any(target =>
                address.Equals(target) ||
                address.Equals(IPAddress.IPv6Any) ||
                (address.Equals(IPAddress.Any) && target.AddressFamily == AddressFamily.InterNetwork));
        }

        private static bool IsLoopback(string host) =>
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

        private RecoveryLock AcquireLock(TimeSpan timeout) =>
            RecoveryLock.TryAcquire(JournalPath, timeout) ??
            throw new TimeoutException(Localization.GetLocalizedString("PROXY_RECOVERY_BUSY"));

        // Returns this process's lease. A lease left by an XMAT that died is settled first; until that has
        // worked, nothing else may change the settings. `enabling` is the endpoint being enabled, or null when
        // stopping capture; while enabling, Windows may still use that lease's endpoint or this one.
        private ProxyLease ReadOwnLease(ProxyEndpoint enabling)
        {
            var lease = LeaseJournal.TryRead(JournalPath);
            if (lease == null || lease.Owner == _owner)
            {
                return lease;
            }
            var state = lease.Owner.GetState();
            if (state != ProcessState.Dead)
            {
                // Only an owner known to be running is reported as such; one that cannot be verified may have ended.
                throw new ProxyRecoveryException(Localization.GetLocalizedString(
                    state == ProcessState.Alive ? "PROXY_RECOVERY_OTHER_INSTANCE" : "PROXY_RECOVERY_OWNER_UNVERIFIED"));
            }
            var settlement = Settle(_store, JournalPath, lease, SettleMode.Recover);
            if (settlement.Result == RecoveryResult.Failed)
            {
                throw enabling != null
                    ? EnableFailure(
                        "PROXY_RECOVERY_UNRESTORED", "PROXY_RECOVERY_UNRESTORED", lease, enabling, settlement.Found,
                        settlement.Failure)
                    : new ProxyRecoveryException(
                        Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", EndpointsOf(lease)), settlement.Failure);
            }
            return null;
        }

        // Under the recovery lock: the settings Enable starts from. A failed read writes nothing more, and `lease`, if
        // it already protects the settings, stays as it is; whether Windows still uses XMAT is then unknown, so the
        // caller keeps its listener.
        private ProxySettings ReadBeforeEnable(ProxyLease lease, ProxyEndpoint endpoint)
        {
            try
            {
                return _store.Read();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new ProxyActivationUncertainException(
                    Localization.GetLocalizedString("PROXY_RECOVERY_SETTINGS_UNREADABLE", EndpointsOf(lease, endpoint)),
                    ex);
            }
        }

        // Under the recovery lock, after Enable read back other settings than it wrote; returns the error it throws.
        // The lease is settled from a fresh read, so settings that are XMAT's by then are restored rather than left
        // unprotected; if that fails, or XMAT's endpoint is still in use with other changes, the lease and its guard
        // stay, and unless the settings are definitely not XMAT's, the caller keeps its listener.
        private Exception RejectReadBack(ProxyLease lease)
        {
            var settlement = Settle(_store, JournalPath, lease, SettleMode.Recover);
            if (settlement.Result == RecoveryResult.Failed)
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Enabling capture did not change the Windows proxy settings, and the settings from before capture could not be restored; the recovery lease and its guard were kept. {settlement.Failure}");
                return EnableFailure(
                    "PROXY_RECOVERY_NOT_APPLIED_UNRESTORED", "PROXY_RECOVERY_NOT_APPLIED_CLEANUP", lease, null,
                    settlement.Found, settlement.Failure);
            }
            _lease = null;
            DisarmGuard(lease.Nonce);
            var error = settlement.Result == RecoveryResult.SettingsChanged
                ? "PROXY_RECOVERY_FOREIGN_SETTINGS"
                : "PROXY_RECOVERY_NOT_APPLIED";
            PublicUtilities.AppLog(
                LogLevel.WARN,
                $"Capture of this PC was not enabled because the Windows proxy settings read back were not the ones written ({error}).");
            return new ProxyRecoveryException(Localization.GetLocalizedString(error));
        }

        // Undoes this process's lease at shutdown or after a fatal error.
        private RecoveryResult RestoreOwnLease(TimeSpan timeout)
        {
            try
            {
                string nonce;
                RecoveryResult result;
                using (var gate = RecoveryLock.TryAcquire(JournalPath, timeout))
                {
                    if (gate == null)
                    {
                        return RecoveryResult.Failed;
                    }
                    nonce = _lease?.Nonce;
                    result = RestoreLease(null);
                }
                // A lease that could not be settled keeps its guard, which tries again once XMAT has ended.
                if (nonce != null && result != RecoveryResult.Failed)
                {
                    DisarmGuard(nonce);
                }
                return result;
            }
            catch (Exception)
            {
                // Terminal boundary: fatal-error handlers and shutdown call this last-chance restore, so it never throws.
                return RecoveryResult.Failed;
            }
        }

        // Under the recovery lock: settles this process's lease (only the one with `nonce`, when given) from its
        // journal or, if that is missing or unreadable, from memory, and keeps it if that fails. A readable lease
        // with another nonce has replaced it and is left alone with its settings.
        private RecoveryResult RestoreLease(string nonce)
        {
            var lease = _lease;
            if (lease == null || (nonce != null && lease.Nonce != nonce))
            {
                // Only a lost guard names a lease; when that one has ended, nothing is undone or reported.
                return nonce == null ? RecoveryResult.NotNeeded : RecoveryResult.Superseded;
            }
            var journal = LeaseJournal.TryRead(JournalPath);
            var result = journal != null && journal.Nonce != lease.Nonce
                ? RecoveryResult.SettingsChanged
                : Settle(_store, JournalPath, journal ?? lease, SettleMode.Recover).Result;
            if (result != RecoveryResult.Failed)
            {
                _lease = null;
            }
            return result;
        }

        // Whether the guard is still running for this lease; a guard is only kept once it reported READY.
        private bool IsLiveGuardFor(ProxyLease lease)
        {
            lock (_guardSync)
            {
                var guard = _guard;
                return lease != null && guard != null && guard.Nonce == lease.Nonce && guard.IsAlive;
            }
        }

        // Replaces any earlier guard. Only ever called with a new nonce.
        private void StartNewGuard(string nonce)
        {
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
            using var ready = ProxyGuardSignals.Create(ProxyGuardSignals.Ready(nonce));
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
                    throw new ProxyRecoveryException(Localization.GetLocalizedString(
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
            // Nothing protects the proxy any more, so it is reset whether or not anyone is listening. An exit
            // that a newer capture has superseded changes and reports nothing.
            void RecoverAndReport()
            {
                var result = RecoverLostGuard(session);
                if (result != RecoveryResult.Superseded)
                {
                    GuardLost?.Invoke(result, session.Nonce);
                }
            }
            var around = AroundGuardExitRecovery;
            if (around == null)
            {
                RecoverAndReport();
            }
            else
            {
                around(RecoverAndReport);
            }
        }

        // Under the recovery lock, as Enable chooses its guard, so a guard that Enable has meanwhile replaced, or
        // whose capture has ended, is stale and left alone.
        private RecoveryResult RecoverLostGuard(GuardSession session)
        {
            try
            {
                using (var gate = RecoveryLock.TryAcquire(JournalPath, LockTimeout))
                {
                    if (gate == null)
                    {
                        return RecoveryResult.Failed;
                    }
                    lock (_guardSync)
                    {
                        if (!ReferenceEquals(_guard, session))
                        {
                            return RecoveryResult.Superseded;
                        }
                        _guard = null;
                    }
                    session.Disarm();
                    return RestoreLease(session.Nonce);
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return RecoveryResult.Failed;
            }
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

            internal bool IsAlive => !Exit.WaitOne(0);

            internal static GuardSession Start(string executable, string[] arguments, string nonce, bool requireIndependence)
            {
                var disarm = ProxyGuardSignals.Create(ProxyGuardSignals.Disarm(nonce));
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
                throw new ProxyRecoveryException(Localization.GetLocalizedString("PROXY_RECOVERY_JOB_REFUSED"));
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
