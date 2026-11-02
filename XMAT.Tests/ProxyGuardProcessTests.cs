// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // Runs the real --proxy-guard process against a stand-in owner process and file-backed settings.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class ProxyGuardProcessTests : IDisposable
    {
        private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
        private readonly ProxyRecoveryTestEnvironment _environment = new();

        private static ProxySettings User => ProxyRecoveryTestEnvironment.UserSettings;

        private static ProxySettings Capturing => ProxyRecoveryTestEnvironment.CapturingSettings;

        private static ProxyEndpoint Endpoint => ProxyRecoveryTestEnvironment.Endpoint;

        private static ProxyEndpoint OtherEndpoint => new("127.0.0.1", 8881);

        // Settings the user chose during a capture: the proxy turned off, with another bypass list and setup script.
        private static ProxySettings Newer =>
            Capturing.WithoutProxy() with { Bypass = "<local>;*.example.com", AutoConfigUrl = "http://wpad/new.pac" };

        public void Dispose() => _environment.Dispose();

        [Fact]
        public void KilledOwner_GuardRestoresTheSettingsFromBeforeCapture()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);
            Assert.Equal(Capturing, _environment.Store.Read());

            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KilledOwner_WithoutAReadableJournal_GuardRestoresFromItsLease(bool corrupt)
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);

            // Held so that the guard only recovers after the journal is gone.
            using (var gate = RecoveryLock.TryAcquire(_environment.JournalPath, GuardTimeout))
            {
                Assert.NotNull(gate);
                owner.Kill();
                owner.WaitForExit();
                BreakJournal(corrupt);
            }

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void NormalShutdown_RestoresTheSettingsAndDisarmsTheGuard()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);

            Assert.Equal(RecoveryResult.Restored, recovery.RestoreOnShutdown());

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // Disable is paused after it releases the lock while a new capture starts; when it resumes, it must
        // not disarm the new capture's guard.
        [Fact]
        public void DisableResumingAfterANewCapture_LeavesTheNewGuardInCharge()
        {
            var owner = _environment.StartOwner();
            var oldGuard = StartCapture(owner, out var recovery);
            Process newGuard = null;
            recovery.DisableUnlocked = () =>
            {
                recovery.Enable(Endpoint);
                newGuard = _environment.Guard(recovery);
            };

            recovery.Disable();

            Assert.NotNull(newGuard);
            Assert.True(oldGuard.WaitForExit(GuardTimeout));
            Assert.Equal(newGuard.Id, recovery.GuardProcessId);
            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));

            owner.Kill();

            Assert.True(newGuard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, newGuard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        // A re-enable keeps the running guard. If XMAT ends just before or just after it writes the new settings,
        // that guard still restores the settings from before the first capture.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OwnerDeathDuringReEnable_GuardRestoresTheSettingsFromBeforeCapture(bool settingsWritten)
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out var recovery);
            ProxyLease journal = null;
            ProxySettings settings = null;
            recovery.SettingsWrite = written =>
            {
                if (written == settingsWritten)
                {
                    journal = LeaseJournal.TryRead(_environment.JournalPath);
                    settings = _environment.Store.Read();
                }
            };

            recovery.Enable(OtherEndpoint);

            Assert.Equal(guard.Id, recovery.GuardProcessId);
            Assert.Null(journal.Applied);
            Assert.Equal(Capturing, journal.PreviousApplied);
            // XMAT ends at that moment, leaving the journal and settings as they were then.
            LeaseJournal.Write(_environment.JournalPath, journal);
            _environment.Store.Write(settings);
            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
        }

        // A re-enable carries the restore baseline, and the guard protecting it, forward only while the settings are
        // still all the capture's own. Once the user turned the proxy off and changed other settings, the earlier
        // lease is settled first and those settings become the new baseline, so stopping capture restores them, not
        // older ones.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReEnable_KeepsTheRestoreBaselineOnlyWhileTheSettingsAreTheCapturesOwn(bool changed)
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var first = LeaseJournal.TryRead(_environment.JournalPath);
            if (changed)
            {
                _environment.Store.Write(Newer);
            }

            recovery.Enable(Endpoint);

            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            Assert.Equal(changed ? Newer : User, lease.Prior);
            Assert.Equal(!changed, lease.Nonce == first.Nonce);
            if (changed)
            {
                Assert.True(guard.WaitForExit(GuardTimeout));
                Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
                Assert.NotEqual(guard.Id, _environment.Guard(recovery).Id);
            }
            else
            {
                Assert.Equal(guard.Id, recovery.GuardProcessId);
                Assert.False(guard.HasExited);
            }

            recovery.Disable();

            Assert.Equal(changed ? Newer : User, _environment.Store.Read());
        }

        // Restarting capture after a stop that did not restore the settings: one that found another proxy, which ends
        // the lease, or one whose restore failed, which keeps it. Once the user then turned the proxy off and changed
        // other settings, stopping the new capture restores those, not the ones from before the first capture.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RestartAfterAStopThatDidNotRestore_RestoresTheNewerSettings(bool stopFailed)
        {
            var store = new RewritingStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            if (stopFailed)
            {
                store.Rewrite = written => written.UsesProxy
                    ? written
                    : throw new IOException("Simulated failure while writing the Windows proxy settings.");
            }
            else
            {
                _environment.Store.Write(User with { Flags = ProxySettings.Proxy, Server = "other-proxy:3128" });
            }
            Assert.Throws<ProxyRecoveryException>(() => recovery.Disable(Endpoint));
            Assert.Equal(stopFailed, File.Exists(_environment.JournalPath));
            store.Rewrite = null;
            _environment.Store.Write(Newer);

            recovery.Enable(Endpoint);

            Assert.Equal(Newer, LeaseJournal.TryRead(_environment.JournalPath).Prior);
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);

            recovery.Disable(Endpoint);

            Assert.Equal(Newer, _environment.Store.Read());
        }

        // A re-enable after XMAT's endpoint was kept with other changes, here another bypass list, cannot settle the
        // earlier lease, as such settings are never overwritten automatically. Enable fails as uncertain without
        // writing, and the lease, its journal and its guard stay until stopping capture turns off that endpoint.
        [Fact]
        public void ReEnableAfterXmatsEndpointWasChanged_FailsAndKeepsItsProtection()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            var changed = Capturing with { Bypass = "<local>;*.example.com" };
            _environment.Store.Write(changed);

            var error = Assert.Throws<ProxyActivationUncertainException>(() => recovery.Enable(Endpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880"), error.Message);
            Assert.Equal(changed, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Equal(guard.Id, recovery.GuardProcessId);
            Assert.False(guard.HasExited);

            recovery.Disable(Endpoint);

            Assert.Equal(changed.WithoutProxy(), _environment.Store.Read());
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // A re-enable whose earlier lease cannot be settled, because its journal cannot be removed, keeps that lease's
        // protection: Enable fails without writing, as an ordinary error since the settings are definitely not XMAT's,
        // and the lease, its journal and its guard stay. The next attempt completes the cleanup and starts from the
        // newer settings.
        [Fact]
        public void ReEnableWhoseEarlierLeaseCannotBeSettled_KeepsItsProtectionUntilTheNextAttempt()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            _environment.Store.Write(Newer);

            InvalidOperationException error;
            // Held open without sharing deletion, so the journal cannot be removed.
            using (new FileStream(_environment.JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(Endpoint));
            }

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880"), error.Message);
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.Equal(Newer, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Equal(guard.Id, recovery.GuardProcessId);
            Assert.False(guard.HasExited);

            recovery.Enable(Endpoint);
            recovery.Disable();

            Assert.Equal(Newer, _environment.Store.Read());
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // Settings that cannot be read again once the earlier lease is settled leave nothing new behind: that lease and
        // its guard have ended, no baseline is recorded or written, and since whether Windows still uses XMAT is
        // unknown, Enable is uncertain.
        [Fact]
        public void ReEnableThatCannotReadTheSettingsAfterSettlingTheEarlierLease_RecordsNothing()
        {
            var store = new ScriptedStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            _environment.Store.Write(Newer);
            // Enable reads the settings, settling the earlier lease reads them again, and the read after that fails.
            store.ScriptReads(Newer, Newer, null);

            var error = Assert.Throws<ProxyActivationUncertainException>(() => recovery.Enable(Endpoint));

            Assert.Equal(
                Localization.GetLocalizedString("PROXY_RECOVERY_SETTINGS_UNREADABLE", "127.0.0.1:8880"), error.Message);
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal(Newer, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // The guard dies while a new capture starts. Its recovery, which runs only after the new capture is in
        // place, must not undo that capture, disarm its guard or report a loss.
        [Fact]
        public async Task GuardExitRecoveredAfterANewCapture_LeavesTheNewCaptureAlone()
        {
            var owner = _environment.StartOwner();
            var oldGuard = StartCapture(owner, out var recovery);
            var oldNonce = LeaseJournal.TryRead(_environment.JournalPath).Nonce;
            var reported = 0;
            recovery.GuardLost += (_, _) => Interlocked.Increment(ref reported);
            Process newGuard = null;
            Exception failure = null;
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recovery.AroundGuardExitRecovery = recover =>
            {
                recovery.AroundGuardExitRecovery = null;
                try
                {
                    recovery.Enable(Endpoint);
                    newGuard = _environment.Guard(recovery);
                    recover();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    recovered.TrySetResult();
                }
            };

            oldGuard.Kill();

            await recovered.Task.WaitAsync(GuardTimeout);
            Assert.Null(failure);
            Assert.Equal(0, Volatile.Read(ref reported));
            Assert.True(recovery.IsSuperseded(oldNonce));
            Assert.Equal(newGuard.Id, recovery.GuardProcessId);
            Assert.False(newGuard.HasExited);
            Assert.Equal(Capturing, _environment.Store.Read());
            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            Assert.NotEqual(oldNonce, lease.Nonce);
            Assert.Equal(User, lease.Prior);

            owner.Kill();

            Assert.True(newGuard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, newGuard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
        }

        [Fact]
        public void FatalErrorRecovery_RestoresBeforeTheOwnerEnds()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out var recovery);

            Assert.Equal(RecoveryResult.Restored, recovery.TryRecoverAfterFatalError());
            Assert.Equal(User, _environment.Store.Read());
            var later = User with { Bypass = "changed after recovery" };
            _environment.Store.Write(later);
            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(later, _environment.Store.Read());
        }

        [Fact]
        public void SettingsChangedDuringCapture_AreNotOverwritten()
        {
            var owner = _environment.StartOwner();
            var guard = StartCapture(owner, out _);
            var changed = User with { Flags = ProxySettings.Proxy, Server = "user-proxy:3128" };
            _environment.Store.Write(changed);

            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(changed, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public async Task UnexpectedGuardExit_RestoresTheSettingsAndReportsIt()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);

            guard.Kill();

            Assert.Equal(RecoveryResult.Restored, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void UnexpectedGuardExit_WithoutSubscribers_StillRestores()
        {
            var guard = StartCapture(_environment.StartOwner(), out _);

            guard.Kill();

            // The journal is removed only after the settings have been written.
            Assert.True(SpinWait.SpinUntil(() => !File.Exists(_environment.JournalPath), GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnexpectedGuardExit_WithoutAReadableJournal_RestoresFromTheLeaseInMemory(bool corrupt)
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            BreakJournal(corrupt);

            guard.Kill();

            Assert.Equal(RecoveryResult.Restored, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public async Task UnexpectedGuardExit_WithoutAReadableJournal_LeavesSettingsTheUserChanged()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            BreakJournal(corrupt: false);
            var changed = User with { Flags = ProxySettings.Proxy, Server = "user-proxy:3128" };
            _environment.Store.Write(changed);

            guard.Kill();

            Assert.Equal(RecoveryResult.SettingsChanged, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(changed, _environment.Store.Read());
        }

        [Fact]
        public async Task UnexpectedGuardExit_LeavesAReplacementLeaseAndItsSettingsAlone()
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var lost = GuardLost(recovery);
            var replacement = LeaseJournal.TryRead(_environment.JournalPath) with { Nonce = Guid.NewGuid().ToString("N") };
            LeaseJournal.Write(_environment.JournalPath, replacement);

            guard.Kill();

            Assert.Equal(RecoveryResult.SettingsChanged, await lost.WaitAsync(GuardTimeout));
            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.Equal(replacement, LeaseJournal.TryRead(_environment.JournalPath));
        }

        [Fact]
        public void GuardThatCannotStart_LeavesTheProxyUnchanged()
        {
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                Path.Combine(_environment.Folder, "missing.exe"));

            Assert.Throws<Win32Exception>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void ReusedOwnerProcessId_GuardNeverBecomesReady()
        {
            var live = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            var recovery = _environment.Recovery(live with { CreationTime = live.CreationTime + 1 });

            Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void FailedProxyWrite_RemovesTheLeaseAndKeepsTheSettings()
        {
            var recovery = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));
            File.SetAttributes(_environment.Store.FilePath, FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsAny<UnauthorizedAccessException>(() => recovery.Enable(Endpoint));
            }
            finally
            {
                File.SetAttributes(_environment.Store.FilePath, FileAttributes.Normal);
            }

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        [Fact]
        public void ProxyWriteThatFailsAfterWritingEverything_IsRolledBack()
        {
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new FailingStore(_environment.Store.FilePath, requested => requested));

            Assert.Throws<IOException>(() => recovery.Enable(Endpoint));

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        // A failed write can leave a mix of the settings from before capture and the requested ones: here XMAT's server
        // with the proxy still off, or the proxy turned on with the user's own server. Someone else could have made the
        // same change, so XMAT cannot prove such a mix is its own: it is not rolled back, and it and the lease stay. A
        // retry settles that lease first and records the mix as its new baseline, so stopping capture keeps the mix,
        // which may need correcting by hand.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProxyWriteThatFailsLeavingAMix_BecomesTheBaselineThatStopKeeps(bool proxyTurnedOn)
        {
            var mixed = proxyTurnedOn ? User with { Flags = ProxySettings.Proxy } : User with { Server = Endpoint.ToString() };
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new FailingStore(_environment.Store.FilePath, _ => mixed));

            Assert.Throws<IOException>(() => recovery.Enable(Endpoint));

            Assert.Equal(mixed, _environment.Store.Read());
            var lease = LeaseJournal.TryRead(_environment.JournalPath);
            Assert.Equal(User, lease.Prior);
            Assert.Null(lease.Applied);

            recovery.Enable(Endpoint);
            Assert.Equal(mixed, LeaseJournal.TryRead(_environment.JournalPath).Prior);
            recovery.Disable();
            Assert.Equal(mixed, _environment.Store.Read());
        }

        // A failed write that leaves XMAT's endpoint with another bypass list cannot be rolled back without
        // overwriting it, and Windows may still send traffic to XMAT: Enable fails as uncertain, so its caller keeps
        // the listener, and the lease, journal and guard stay until stopping capture turns that endpoint off.
        [Fact]
        public void ProxyWriteThatFailsLeavingXmatsEndpointChanged_IsUncertainAndKeepsItsProtection()
        {
            var changed = Capturing with { Bypass = "<local>;*.example.com" };
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new FailingStore(_environment.Store.FilePath, _ => changed));

            var error = Assert.Throws<ProxyActivationUncertainException>(() => recovery.Enable(Endpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880"), error.Message);
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal(changed, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
            var guard = _environment.Guard(recovery);
            Assert.False(guard.HasExited);

            recovery.Disable(Endpoint);

            Assert.Equal(changed.WithoutProxy(), _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // A read-back of someone else's settings fails Enable without a rollback that could overwrite them, and
        // retires its generation.
        [Fact]
        public void ForeignReadBack_FailsEnableAndKeepsThoseSettings()
        {
            var foreign = User with { Flags = ProxySettings.Proxy, Server = "other-proxy:3128" };
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new RewritingStore(_environment.Store.FilePath, _ => foreign));

            var error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(Endpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_FOREIGN_SETTINGS"), error.Message);
            Assert.Equal(foreign, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.Equal(RecoveryResult.NotNeeded, recovery.RestoreOnShutdown());
            Assert.Equal(foreign, _environment.Store.Read());
        }

        // XMAT's endpoint read back with another direct-connection flag, a distinct connection type, is not what
        // Enable wrote, yet still sends traffic to XMAT, whether it was the new endpoint or the previous capture's.
        // Enable fails as uncertain, so its caller keeps the listener; those settings are never overwritten
        // automatically, and their lease, journal and guard stay.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReadBackOfXmatsEndpointWithAnotherFlag_KeepsItsProtection(bool reEnable)
        {
            var store = new RewritingStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            if (reEnable)
            {
                recovery.Enable(Endpoint);
            }
            var changed = Capturing with { Flags = Capturing.Flags | ProxySettings.Direct };
            store.Rewrite = written => written.UsesProxy ? changed : written;

            var error = Assert.Throws<ProxyActivationUncertainException>(() => recovery.Enable(reEnable ? OtherEndpoint : Endpoint));

            Assert.Equal(
                Localization.GetLocalizedString(
                    "PROXY_RECOVERY_NOT_APPLIED_UNRESTORED", reEnable ? "127.0.0.1:8881, 127.0.0.1:8880" : "127.0.0.1:8880"),
                error.Message);
            Assert.Equal(changed, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
            Assert.False(_environment.Guard(recovery).HasExited);
            Assert.Equal(RecoveryResult.Failed, recovery.RestoreOnShutdown());
            Assert.Equal(changed, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void ReadBackWithAnEmptyStringMissing_IsStillTheCapturesOwn()
        {
            var before = User with { Bypass = string.Empty };
            _environment.Store.Write(before);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()),
                store: new RewritingStore(
                    _environment.Store.FilePath, written => written.UsesProxy ? written with { Bypass = null } : written));

            recovery.Enable(Endpoint);

            Assert.Equal(RecoveryResult.Restored, recovery.RestoreOnShutdown());
            Assert.Equal(before, _environment.Store.Read());
        }

        // A re-enable that reads back XMAT's own previous settings did not take effect; nobody else changed them.
        // The settings from before capture are restored, and only then does that capture's protection end.
        [Fact]
        public void ReEnableThatReadsBackThePreviousSettings_RestoresTheSettingsFromBeforeCapture()
        {
            var store = new RewritingStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            store.Rewrite = written => written.UsesProxy ? Capturing : written;

            var error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(OtherEndpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_NOT_APPLIED"), error.Message);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // The settings from before capture are restored and read back as such, but the journal cannot be removed.
        // Windows is then known not to use XMAT, so Enable fails as an ordinary error, and the lease and its guard
        // stay until a later attempt finishes the cleanup.
        [Fact]
        public void ReEnableRestoredButNotCleanedUp_FailsAsAnOrdinaryErrorAndKeepsTheLease()
        {
            var store = new RewritingStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            store.Rewrite = written => written.UsesProxy ? Capturing : written;

            InvalidOperationException error;
            using (var journal = new HeldOpen(_environment.JournalPath))
            {
                // Held open once the new settings are written, so the journal cannot be removed.
                recovery.SettingsWrite = written =>
                {
                    if (written)
                    {
                        journal.Open();
                    }
                };
                try
                {
                    error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(OtherEndpoint));
                }
                finally
                {
                    recovery.SettingsWrite = null;
                }
            }

            Assert.Equal(
                Localization.GetLocalizedString("PROXY_RECOVERY_NOT_APPLIED_CLEANUP", "127.0.0.1:8881, 127.0.0.1:8880"),
                error.Message);
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.Equal(User, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
            Assert.Equal(guard.Id, recovery.GuardProcessId);

            Assert.Equal(RecoveryResult.NotNeeded, recovery.RestoreOnShutdown());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // When the settings from before capture cannot be restored either, because that write fails or does not
        // take effect, the lease, its journal and its guard stay, so the guard still restores them once XMAT ends.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReEnableThatCannotRestoreThePreviousSettings_KeepsItsProtection(bool restoreThrows)
        {
            var owner = _environment.StartOwner();
            var store = new RewritingStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(owner), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            store.Rewrite = written => written.UsesProxy || !restoreThrows
                ? Capturing
                : throw new IOException("Simulated failure while writing the Windows proxy settings.");

            var error = Assert.Throws<ProxyActivationUncertainException>(() => recovery.Enable(OtherEndpoint));

            Assert.Equal(
                Localization.GetLocalizedString("PROXY_RECOVERY_NOT_APPLIED_UNRESTORED", "127.0.0.1:8881, 127.0.0.1:8880"),
                error.Message);
            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.Equal(User, LeaseJournal.TryRead(_environment.JournalPath).RestoreTarget);
            Assert.Equal(guard.Id, recovery.GuardProcessId);

            owner.Kill();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
            Assert.Equal(User, _environment.Store.Read());
        }

        // The read-back after a re-enable shows someone else's settings, but read again they are XMAT's: its
        // previous ones, when the new ones did not take effect, or the new ones. They are restored rather than
        // left pointing at XMAT without protection.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReEnableReadingBackOtherSettingsThenXmats_RestoresTheSettingsFromBeforeCapture(bool newSettingsTookEffect)
        {
            var store = new ScriptedStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            // Enable reads the current settings, then, after its write, someone else's.
            store.ScriptReads(Capturing, User with { Flags = ProxySettings.Proxy, Server = "other-proxy:3128" });
            if (!newSettingsTookEffect)
            {
                store.ScriptWrites(Capturing);
            }

            var error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(OtherEndpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_NOT_APPLIED"), error.Message);
            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // After the restore, the settings read back as neither the safe target nor XMAT's own. That is not a
        // restore, whether capture is stopping or a re-enable is being undone: the lease, its journal and its guard
        // stay, and only a later attempt finds those settings someone else's and keeps them. As those settings are
        // definitely not XMAT's, either failure is an ordinary one.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RestoreThatReadsBackOtherSettings_KeepsTheLeaseJournalAndGuard(bool stoppingCapture)
        {
            var store = new ScriptedStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            var other = User with { Flags = ProxySettings.Proxy, Server = "other-proxy:3128" };

            InvalidOperationException error;
            if (stoppingCapture)
            {
                store.ScriptWrites(other);
                error = Assert.Throws<ProxyRecoveryException>(recovery.Disable);
            }
            else
            {
                // The new settings do not take effect, and the restore then leaves other settings.
                store.ScriptWrites(Capturing, other);
                error = Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(OtherEndpoint));
            }

            Assert.Equal(
                stoppingCapture
                    ? Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880")
                    : Localization.GetLocalizedString("PROXY_RECOVERY_NOT_APPLIED_CLEANUP", "127.0.0.1:8881, 127.0.0.1:8880"),
                error.Message);
            Assert.Equal(other, _environment.Store.Read());
            Assert.Equal(User, LeaseJournal.TryRead(_environment.JournalPath).RestoreTarget);
            Assert.Equal(guard.Id, recovery.GuardProcessId);
            Assert.False(guard.HasExited);

            Assert.Equal(RecoveryResult.SettingsChanged, recovery.RestoreOnShutdown());
            Assert.Equal(other, _environment.Store.Read());
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // A restore that leaves XMAT's endpoint with another flag or bypass list has not restored anything, and a
        // retry neither overwrites those settings nor ends the lease, so the endpoint keeps its journal and guard.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RestoreThatLeavesXmatsEndpointChanged_KeepsTheJournalOnRetry(bool changedFlags)
        {
            var store = new ScriptedStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), store: store);
            recovery.Enable(Endpoint);
            var guard = _environment.Guard(recovery);
            var changed = changedFlags
                ? Capturing with { Flags = Capturing.Flags | ProxySettings.Direct }
                : Capturing with { Bypass = "<local>;*.example.com" };
            store.ScriptWrites(changed);

            Assert.Equal(RecoveryResult.Failed, recovery.RestoreOnShutdown());
            Assert.Equal(RecoveryResult.Failed, recovery.RestoreOnShutdown());

            Assert.Equal(changed, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
            Assert.Equal(guard.Id, recovery.GuardProcessId);
            Assert.False(guard.HasExited);
        }

        // Stopping capture turns off only XMAT's exact endpoint. A proxy on another loopback address that shares
        // the port is someone else's: it is kept and reported, and the lease and its guard end.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Disable_KeepsAProxyOnAnotherLoopbackAddressWithTheSamePort(bool expectedGiven)
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var other = Capturing with { Server = "127.0.0.2:8880" };
            _environment.Store.Write(other);

            var error = Assert.Throws<ProxyRecoveryException>(() => recovery.Disable(expectedGiven ? Endpoint : null));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FOREIGN", other.Server), error.Message);
            Assert.Equal(other, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        // Stopping capture after XMAT's endpoint was changed turns off only its proxy, keeping the other changes;
        // after the proxy was turned off, it changes nothing. Either way the lease and its guard end.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Disable_TurnsOffOnlyXmatsProxyAndKeepsOtherChanges(bool proxyAlreadyOff)
        {
            var guard = StartCapture(_environment.StartOwner(), out var recovery);
            var changed = Capturing with { Bypass = "<local>;*.example.com" };
            _environment.Store.Write(proxyAlreadyOff ? changed.WithoutProxy() : changed);

            recovery.Disable();

            Assert.Equal(changed.WithoutProxy(), _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.Completed, guard.ExitCode);
        }

        [Fact]
        public void SecondInstance_CannotTakeOverALiveCapture()
        {
            StartCapture(_environment.StartOwner(), out _);
            var second = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));
            var running = Localization.GetLocalizedString("PROXY_RECOVERY_OTHER_INSTANCE");

            var error = Assert.Throws<ProxyRecoveryException>(() => second.Enable(new ProxyEndpoint("127.0.0.1", 8881)));
            Assert.Equal(running, error.Message);
            Assert.Equal(running, Assert.Throws<ProxyRecoveryException>(second.Disable).Message);
            Assert.Null(second.HealAtStartup());

            Assert.Equal(Capturing, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void GuardProcess_RunsWithoutStartingTheApplication()
        {
            var guard = StartCapture(_environment.StartOwner(), out _);
            guard.Refresh();

            var modules = guard.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).ToArray();

            Assert.DoesNotContain(modules, name =>
                name.StartsWith("PresentationNative", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("wpfgfx", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("e_sqlite3", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IntPtr.Zero, guard.MainWindowHandle);
        }

        // The guard is started in a job made here, so its own job check runs the same way wherever the
        // tests run.
        [Theory]
        [InlineData(true, ProxyGuard.JobRefused)]
        [InlineData(false, ProxyGuard.Completed)]
        public void IndependentGuard_ReportsReadyOnlyInAJobThatLetsItOutliveXmat(bool killOnClose, int exitCode)
        {
            var owner = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            var lease = _environment.WriteLease(owner, User, applied: false);
            using var ready = ProxyGuardSignals.Create(ProxyGuardSignals.Ready(lease.Nonce));
            using var disarm = ProxyGuardSignals.Create(ProxyGuardSignals.Disarm(lease.Nonce));
            using var job = new TestJob(killOnClose);
            var guard = _environment.Track(job.Start(
                ProxyRecoveryTestEnvironment.GuardExecutable,
                ProxyGuard.Arguments(_environment.JournalPath, lease.Nonce, true, _environment.Store.FilePath)));
            using var exited = new ProcessExitHandle(guard.SafeHandle);

            WaitHandle.WaitAny(new WaitHandle[] { ready, exited }, GuardTimeout);
            disarm.Set();

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(exitCode, guard.ExitCode);
            Assert.Equal(!killOnClose, ready.WaitOne(0));
        }

        // The guard only accepts signals limited to its user, as XMAT creates them.
        [Fact]
        public void GuardIgnoresSignalsNotLimitedToTheUser()
        {
            var owner = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            var lease = _environment.WriteLease(owner, User, applied: false);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Ready(lease.Nonce));
            using var disarm = new EventWaitHandle(false, EventResetMode.ManualReset, ProxyGuardSignals.Disarm(lease.Nonce));
            var start = new ProcessStartInfo(ProxyRecoveryTestEnvironment.GuardExecutable)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            foreach (var argument in ProxyGuard.Arguments(_environment.JournalPath, lease.Nonce, false, _environment.Store.FilePath))
            {
                start.ArgumentList.Add(argument);
            }
            var guard = _environment.Track(Process.Start(start));

            Assert.True(guard.WaitForExit(GuardTimeout));
            Assert.Equal(ProxyGuard.NotOwner, guard.ExitCode);
            Assert.False(ready.WaitOne(0));
        }

        private Process StartCapture(Process owner, out PcProxyRecovery recovery)
        {
            recovery = _environment.Recovery(ProxyRecoveryTestEnvironment.Identity(owner));
            recovery.Enable(Endpoint);
            return _environment.Guard(recovery);
        }

        private void BreakJournal(bool corrupt)
        {
            if (corrupt)
            {
                File.WriteAllText(_environment.JournalPath, "{");
            }
            else
            {
                File.Delete(_environment.JournalPath);
            }
        }

        private static Task<RecoveryResult> GuardLost(PcProxyRecovery recovery)
        {
            var lost = new TaskCompletionSource<RecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            recovery.GuardLost += (result, _) => lost.TrySetResult(result);
            return lost.Task;
        }

        // Fails the first write that turns the proxy on, after writing what `leftBehind` returns.
        private sealed class FailingStore : FileProxySettingsStore
        {
            private readonly Func<ProxySettings, ProxySettings> _leftBehind;
            private bool _failed;

            internal FailingStore(string path, Func<ProxySettings, ProxySettings> leftBehind)
                : base(path) => _leftBehind = leftBehind;

            public override void Write(ProxySettings settings)
            {
                if (_failed || !settings.UsesProxy)
                {
                    base.Write(settings);
                    return;
                }
                _failed = true;
                base.Write(_leftBehind(settings));
                throw new IOException("Simulated failure while writing the Windows proxy settings.");
            }
        }

        // Writes what `Rewrite` returns instead of the requested settings, as a write that does not take effect,
        // a change someone else makes right after XMAT's write, or WinINet reporting the same settings
        // differently would; `Rewrite` may also throw, as a failed write would.
        private sealed class RewritingStore : FileProxySettingsStore
        {
            internal RewritingStore(string path, Func<ProxySettings, ProxySettings> rewrite = null)
                : base(path) => Rewrite = rewrite;

            internal Func<ProxySettings, ProxySettings> Rewrite { get; set; }

            public override void Write(ProxySettings settings) => base.Write(Rewrite?.Invoke(settings) ?? settings);
        }

        // Replaces the next reads and writes with scripted settings, in order: a scripted read returns them instead
        // of the file's, or fails when null, as a failed WinINet query does, and a scripted write stores them instead
        // of the requested ones, as WinINet changing under XMAT would. Once a script is used up, the file is read and
        // written as usual. Deriving from the file store keeps the guard on the same file, never on WinINet.
        private sealed class ScriptedStore : FileProxySettingsStore
        {
            private readonly Queue<ProxySettings> _reads = new();
            private readonly Queue<ProxySettings> _writes = new();

            internal ScriptedStore(string path)
                : base(path)
            {
            }

            internal void ScriptReads(params ProxySettings[] reads) => Array.ForEach(reads, _reads.Enqueue);

            internal void ScriptWrites(params ProxySettings[] writes) => Array.ForEach(writes, _writes.Enqueue);

            public override ProxySettings Read() =>
                _reads.TryDequeue(out var scripted)
                    ? scripted ?? throw new IOException("Simulated failure while reading the Windows proxy settings.")
                    : base.Read();

            public override void Write(ProxySettings settings) =>
                base.Write(_writes.TryDequeue(out var scripted) ? scripted : settings);
        }

        // Holds a file open without sharing deletion, so it cannot be removed, from Open() until disposed. Opening it
        // again keeps the handle it has, and disposing it again does nothing.
        private sealed class HeldOpen : IDisposable
        {
            private readonly string _path;
            private FileStream _stream;

            internal HeldOpen(string path) => _path = path;

            internal void Open() => _stream ??= new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            public void Dispose()
            {
                _stream?.Dispose();
                _stream = null;
            }
        }

        private sealed class TestJob : IDisposable
        {
            private const uint CreateSuspended = 0x4;
            private readonly SafeWaitHandle _job;

            internal TestJob(bool killOnClose)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var limits = new RecoveryNativeMethods.JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = killOnClose ? RecoveryNativeMethods.JobKillOnClose : 0;
                if (_job.IsInvalid ||
                    !SetInformationJobObject(
                        _job, RecoveryNativeMethods.JobExtendedLimitInformationClass, ref limits, Marshal.SizeOf(limits)))
                {
                    var error = new Win32Exception();
                    _job.Dispose();
                    throw error;
                }
            }

            // The process starts suspended, so it runs nothing before it is in the job.
            internal Process Start(string executable, IEnumerable<string> arguments)
            {
                var commandLine = string.Join(" ", arguments.Prepend(executable).Select(argument => $"\"{argument}\""));
                var startup = new RecoveryNativeMethods.StartupInfo { Size = Marshal.SizeOf<RecoveryNativeMethods.StartupInfo>() };
                if (!RecoveryNativeMethods.CreateProcess(
                        executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, CreateSuspended,
                        IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var information))
                {
                    throw new Win32Exception();
                }
                using var process = new SafeProcessHandle(information.Process, true);
                try
                {
                    if (!AssignProcessToJobObject(_job, process))
                    {
                        throw new Win32Exception();
                    }
                    var started = Process.GetProcessById(information.ProcessId);
                    _ = started.SafeHandle;
                    if (ResumeThread(information.Thread) == uint.MaxValue)
                    {
                        throw new Win32Exception();
                    }
                    return started;
                }
                catch
                {
                    RecoveryNativeMethods.TerminateProcess(process, 1);
                    throw;
                }
                finally
                {
                    RecoveryNativeMethods.CloseHandle(information.Thread);
                }
            }

            public void Dispose() => _job.Dispose();

            [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern SafeWaitHandle CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetInformationJobObject(
                SafeWaitHandle job, int informationClass,
                ref RecoveryNativeMethods.JobExtendedLimitInformation information, int length);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint ResumeThread(IntPtr thread);
        }
    }
}
