// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // Startup recovery and its journals; these tests start no guard process, only stand-in owners.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class PcProxyRecoveryTests : IDisposable
    {
        private readonly ProxyRecoveryTestEnvironment _environment = new();

        private static ProxySettings User => ProxyRecoveryTestEnvironment.UserSettings;

        public void Dispose() => _environment.Dispose();

        [Fact]
        public void Startup_RestoresTheSettingsLeftByAnOwnerThatDied()
        {
            _environment.WriteLease(_environment.DeadOwner(), User, applied: true);

            Assert.Null(NewStart().HealAtStartup());

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void Startup_DoesNotOverwriteSettingsTheUserChanged()
        {
            _environment.WriteLease(_environment.DeadOwner(), User, applied: true);
            var changed = User with { Flags = ProxySettings.Proxy, Server = "user-proxy:3128" };
            _environment.Store.Write(changed);

            NewStart().HealAtStartup();

            Assert.Equal(changed, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void Startup_DropsALeaseWhoseProxyWasNeverEnabled()
        {
            _environment.WriteLease(_environment.DeadOwner(), User, applied: false);

            NewStart().HealAtStartup();

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void Startup_TreatsAReusedProcessIdAsADeadOwner()
        {
            var live = ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner());
            _environment.WriteLease(live with { CreationTime = live.CreationTime + 1 }, User, applied: true);

            NewStart().HealAtStartup();

            Assert.Equal(User, _environment.Store.Read());
        }

        [Fact]
        public void Startup_LeavesTheLeaseOfARunningOwnerAlone()
        {
            var lease = _environment.WriteLease(
                ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()), User, applied: true);

            Assert.Null(NewStart().HealAtStartup());

            Assert.Equal(lease.Intended, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
        }

        // A process that cannot be opened, as a protected process that took over the recorded ID, is looked up in one
        // snapshot of every process. It is gone only if a snapshot that shows this process as it is lacks its ID or
        // shows that ID with another creation time; a snapshot that shows it unchanged, fails or cannot be trusted
        // leaves it possibly running.
        [Theory]
        [InlineData("absent", true)]
        [InlineData("reused", true)]
        [InlineData("same", false)]
        [InlineData("failed", false)]
        [InlineData("without this process", false)]
        [InlineData("with this process changed", false)]
        public void InaccessibleProcess_IsGoneOnlyWhenATrustedSnapshotShowsIt(string snapshot, bool gone)
        {
            var self = ProcessIdentity.Current;
            var process = new ProcessIdentity(self.ProcessId + 4, 1000);
            _environment.MakeInaccessible(process, snapshot switch
            {
                "absent" => ProxyRecoveryTestEnvironment.Snapshot(),
                "reused" => ProxyRecoveryTestEnvironment.Snapshot(process with { CreationTime = 1001 }),
                "same" => ProxyRecoveryTestEnvironment.Snapshot(process),
                "failed" => null,
                "without this process" => new Dictionary<int, long>(),
                _ => new Dictionary<int, long> { [self.ProcessId] = self.CreationTime + 1 }
            });

            Assert.Equal(gone ? ProcessState.Dead : ProcessState.Unknown, process.GetState());
        }

        // Wherever recovery checks an owner that cannot be opened, it acts only once that owner is known to be gone:
        // its consoles are offered, startup restores its settings, a proxy the user confirmed is turned off, and
        // stopping capture settles its lease. Otherwise nothing changes, and starting or stopping capture says that
        // ownership could not be verified, not that the owner is running.
        [Theory]
        [InlineData("reused", true)]
        [InlineData("same", false)]
        [InlineData("failed", false)]
        public void InaccessibleOwner_IsRecoveredOnlyOnceItIsKnownToBeGone(string snapshot, bool gone)
        {
            var owner = _environment.DeadOwner();
            _environment.MakeInaccessible(owner, snapshot switch
            {
                "reused" => ProxyRecoveryTestEnvironment.Snapshot(owner with { CreationTime = owner.CreationTime + 1 }),
                "same" => ProxyRecoveryTestEnvironment.Snapshot(owner),
                _ => null
            });
            new XboxProxySessions(_environment.Folder, owner).SetCapturing("console-a", true);

            Assert.Equal(
                gone ? new[] { "console-a" } : Array.Empty<string>(),
                XboxProxySessions.FindAbandoned(_environment.Folder).Consoles);

            var lease = _environment.WriteLease(owner, User, applied: true);
            Assert.Null(NewStart().HealAtStartup());
            Assert.Equal(gone ? User : lease.Intended, _environment.Store.Read());
            Assert.Equal(!gone, File.Exists(_environment.JournalPath));

            lease = _environment.WriteLease(owner, User, applied: true);
            Assert.Equal(gone, NewStart().DisableAbandonedProxy(lease.Intended));
            Assert.Equal(gone ? User : lease.Intended, _environment.Store.Read());

            lease = _environment.WriteLease(owner, User, applied: true);
            var recovery = NewStart();
            if (gone)
            {
                recovery.Disable(ProxyRecoveryTestEnvironment.Endpoint);
                Assert.Equal(User, _environment.Store.Read());
                Assert.False(File.Exists(_environment.JournalPath));
                return;
            }
            var unverified = Localization.GetLocalizedString("PROXY_RECOVERY_OWNER_UNVERIFIED");
            var endpoint = ProxyRecoveryTestEnvironment.Endpoint;
            Assert.Equal(unverified, Assert.Throws<ProxyRecoveryException>(() => recovery.Enable(endpoint)).Message);
            Assert.Equal(unverified, Assert.Throws<ProxyRecoveryException>(() => recovery.Disable(endpoint)).Message);
            Assert.Equal(lease.Intended, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        // The same on a real process that this user cannot open, such as a protected or system process: a lease whose
        // owner has its ID but another creation time, as when a restart reused the ID, is recovered at startup, and one
        // with its exact creation time, an owner that may still be running, is left alone. A process that ends or is
        // replaced while it is measured proves nothing and is skipped; this process, which can be opened, is tried
        // last, as when every process can be opened because the tests run elevated.
        [Fact]
        public void LeaseOfAProcessThatCannotBeOpened_IsRecoveredOnlyOnceItsIdWasReused()
        {
            var snapshot = ProcessSnapshot.Take();
            Assert.NotNull(snapshot);
            var self = ProcessIdentity.Current;
            var candidates = snapshot
                .Where(process => process.Key != self.ProcessId && CannotOpen(process.Key))
                .Take(10)
                .Append(new KeyValuePair<int, long>(self.ProcessId, self.CreationTime));

            foreach (var (id, created) in candidates)
            {
                var reused = new ProcessIdentity(id, created + 1);
                var running = new ProcessIdentity(id, created);
                var reusedState = reused.GetState();
                var runningState = running.GetState();
                _environment.WriteLease(reused, User, applied: true);
                var reusedHealed = NewStart().HealAtStartup();
                var reusedSettings = _environment.Store.Read();
                var reusedJournal = File.Exists(_environment.JournalPath);
                var lease = _environment.WriteLease(running, User, applied: true);
                var runningHealed = NewStart().HealAtStartup();
                var runningSettings = _environment.Store.Read();
                var runningJournal = LeaseJournal.TryRead(_environment.JournalPath);
                var after = ProcessSnapshot.Take();
                if (after == null || !after.TryGetValue(id, out var stillCreated) || stillCreated != created)
                {
                    continue;
                }

                Assert.Equal(ProcessState.Dead, reusedState);
                Assert.NotEqual(ProcessState.Dead, runningState);
                Assert.Null(reusedHealed);
                Assert.Equal(User, reusedSettings);
                Assert.False(reusedJournal);
                Assert.Null(runningHealed);
                Assert.Equal(lease.Intended, runningSettings);
                Assert.Equal(lease, runningJournal);
                return;
            }
            Assert.Fail("No process stayed the same while it was measured.");
        }

        [Fact]
        public void Startup_DiscardsAnUnreadableLeaseWithoutWriting()
        {
            Directory.CreateDirectory(_environment.Folder);
            File.WriteAllText(_environment.JournalPath, "{");

            Assert.Null(NewStart().HealAtStartup());

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        // XMAT ended during a re-enable, just before or just after it wrote the new settings; either way the
        // settings from before the first capture come back.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Startup_RestoresAnOwnerThatDiedDuringReEnable(bool settingsWritten)
        {
            var owner = _environment.DeadOwner();
            var previous = ProxyRecoveryTestEnvironment.CapturingSettings;
            var lease = new ProxyLease(
                Guid.NewGuid().ToString("N"), owner.ProcessId, owner.CreationTime, User,
                User.WithProxy(new ProxyEndpoint("127.0.0.1", 8881)), null, PreviousApplied: previous, SafeTarget: User);
            LeaseJournal.Write(_environment.JournalPath, lease);
            _environment.Store.Write(settingsWritten ? lease.Intended : previous);

            Assert.Null(NewStart().HealAtStartup());

            Assert.Equal(User, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        [Fact]
        public void Recovery_NeverRestoresAnObsoleteXmatEndpoint()
        {
            var leftover = ProxyRecoveryTestEnvironment.CapturingSettings;
            _environment.WriteLease(_environment.DeadOwner(), leftover, applied: true);

            NewStart().HealAtStartup();

            Assert.Equal(leftover.WithoutProxy(), _environment.Store.Read());
            Assert.False(_environment.Store.Read().UsesProxy);
        }

        [Fact]
        public void Startup_AsksBeforeTurningOffAnUnjournaledLoopbackProxy()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            var abandoned = User.WithProxy(new ProxyEndpoint("127.0.0.1", port));
            _environment.Store.Write(abandoned);
            try
            {
                Assert.Null(NewStart().HealAtStartup());
            }
            finally
            {
                listener.Stop();
            }

            var startup = NewStart();
            Assert.Equal(abandoned, startup.HealAtStartup());
            Assert.Equal(abandoned, _environment.Store.Read());

            Assert.False(startup.DisableAbandonedProxy(abandoned with { Bypass = "changed" }));
            Assert.Equal(abandoned, _environment.Store.Read());
            Assert.True(startup.DisableAbandonedProxy(abandoned));
            Assert.Equal(abandoned.WithoutProxy(), _environment.Store.Read());
        }

        [Fact]
        public void Startup_TreatsAListenerOnlyOnAnotherAddressAsAbandoned()
        {
            using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            var abandoned = User.WithProxy(new ProxyEndpoint("127.0.0.1", port));
            _environment.Store.Write(abandoned);

            Assert.Equal(abandoned, NewStart().HealAtStartup());
        }

        // A dead XMAT's endpoint whose other settings changed is never overwritten at startup: its journal stays,
        // and once nothing listens there, the user is asked before only its proxy is turned off.
        [Fact]
        public void Startup_AsksBeforeTurningOffAChangedXmatEndpointThatItsLeaseKeeps()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var owner = _environment.DeadOwner();
            var lease = new ProxyLease(
                Guid.NewGuid().ToString("N"), owner.ProcessId, owner.CreationTime, User,
                User.WithProxy(new ProxyEndpoint("127.0.0.1", port)), null);
            LeaseJournal.Write(_environment.JournalPath, lease);
            var changed = lease.Intended with { Bypass = "<local>;*.example.com" };
            _environment.Store.Write(changed);
            var startup = NewStart();

            Assert.Equal(changed, startup.HealAtStartup());
            Assert.Equal(changed, _environment.Store.Read());
            Assert.True(File.Exists(_environment.JournalPath));

            Assert.True(startup.DisableAbandonedProxy(changed));
            Assert.Equal(changed.WithoutProxy(), _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        // Without a lease, stopping capture turns off only the endpoint the caller expects, matched exactly by host
        // and port; another proxy, one that cannot be parsed, or any proxy when no endpoint is expected, is kept and
        // reported.
        [Theory]
        [InlineData("127.0.0.1:8880", true, true)]
        [InlineData("127.0.0.2:8880", true, false)]
        [InlineData("localhost:8880", true, false)]
        [InlineData("127.0.0.1:8881", true, false)]
        [InlineData("http=127.0.0.1:8880;https=127.0.0.1:8880", true, false)]
        [InlineData("127.0.0.1:8880", false, false)]
        public void DisableWithoutALease_TurnsOffOnlyTheExpectedEndpoint(string server, bool expectedGiven, bool turnedOff)
        {
            var settings = User with { Flags = ProxySettings.Proxy, Server = server };
            _environment.Store.Write(settings);
            var expected = expectedGiven ? ProxyRecoveryTestEnvironment.Endpoint : null;

            if (turnedOff)
            {
                NewStart().Disable(expected);
                Assert.Equal(settings.WithoutProxy(), _environment.Store.Read());
            }
            else
            {
                var error = Assert.Throws<ProxyRecoveryException>(() => NewStart().Disable(expected));
                Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FOREIGN", server), error.Message);
                Assert.Equal(settings, _environment.Store.Read());
            }
        }

        // A failed Disable with neither a lease nor an expected endpoint knows no endpoint of XMAT's, so it names
        // none to turn off by hand.
        [Fact]
        public void DisableWithoutALeaseOrExpectedEndpoint_ThatFails_NamesNoEndpoint()
        {
            File.WriteAllText(_environment.Store.FilePath, "{");

            var error = Assert.Throws<ProxyRecoveryException>(() => NewStart().Disable());

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FAILED_UNKNOWN"), error.Message);
            Assert.DoesNotContain("127.0.0.1", error.Message);
        }

        // Stopping capture with the proxy already off, even with XMAT's endpoint still filled in, changes nothing.
        [Fact]
        public void DisableWithoutALease_WithTheProxyAlreadyOff_ChangesNothing()
        {
            var settings = User with { Server = "127.0.0.1:8880" };
            _environment.Store.Write(settings);

            NewStart().Disable(ProxyRecoveryTestEnvironment.Endpoint);

            Assert.Equal(settings, _environment.Store.Read());
        }

        // With a lease of this process's, stopping capture keeps a proxy setting that cannot be parsed, reports it,
        // and ends the lease.
        [Fact]
        public void DisableWithALease_KeepsAnUnparseableProxyAndEndsTheLease()
        {
            _environment.WriteLease(ProcessIdentity.Current, User, applied: true);
            var settings = User with { Flags = ProxySettings.Proxy, Server = "http=127.0.0.1:8880;https=127.0.0.1:8880" };
            _environment.Store.Write(settings);

            var error = Assert.Throws<ProxyRecoveryException>(
                () => NewStart().Disable(ProxyRecoveryTestEnvironment.Endpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FOREIGN", settings.Server), error.Message);
            Assert.Equal(settings, _environment.Store.Read());
            Assert.False(File.Exists(_environment.JournalPath));
        }

        // Stopping capture with settings that cannot be read, as when the WinINet query fails, writes nothing and
        // fails as an ordinary error: without a lease it names the expected endpoint, and a lease keeps its journal.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DisableWithSettingsThatCannotBeRead_WritesNothing(bool journaled)
        {
            if (journaled)
            {
                _environment.WriteLease(ProcessIdentity.Current, User, applied: true);
            }
            var store = new UnreadableStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(ProcessIdentity.Current, store: store);

            var error = Assert.Throws<ProxyRecoveryException>(
                () => recovery.Disable(ProxyRecoveryTestEnvironment.Endpoint));

            Assert.Equal(
                Localization.GetLocalizedString(
                    journaled ? "PROXY_RECOVERY_UNRESTORED" : "PROXY_RECOVERY_DISABLE_FAILED", "127.0.0.1:8880"),
                error.Message);
            Assert.Equal(0, store.Writes);
            Assert.Equal(journaled, File.Exists(_environment.JournalPath));
        }

        // An earlier session's lease that cannot be settled, because Windows still uses its endpoint with other
        // changes, may be this capture's own endpoint: enabling fails as uncertain without writing anything, and the
        // journal stays. Stopping capture is no activation, so it reports the same failure as an ordinary one.
        [Fact]
        public void EarlierSessionsLeaseThatCannotBeSettled_LeavesEnableUncertain()
        {
            var lease = _environment.WriteLease(_environment.DeadOwner(), User, applied: true);
            var changed = lease.Intended with { Bypass = "<local>;*.example.com" };
            _environment.Store.Write(changed);
            var recovery = NewStart();

            var error = Assert.Throws<ProxyActivationUncertainException>(
                () => recovery.Enable(ProxyRecoveryTestEnvironment.Endpoint));

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880"), error.Message);
            Assert.Equal(changed, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
            Assert.Throws<ProxyRecoveryException>(() => recovery.Disable(ProxyRecoveryTestEnvironment.Endpoint));
        }

        // An earlier session's lease on another endpoint is settled, but its journal cannot be removed. If Windows may
        // use the endpoint being enabled, through its address or another loopback name on its port, enabling is
        // uncertain although that lease never used it; settings that are definitely elsewhere fail as an ordinary
        // error. The journal stays for the pending cleanup either way.
        [Theory]
        [InlineData("127.0.0.1:8881", true)]
        [InlineData("localhost:8881", true)]
        [InlineData("other-proxy:3128", false)]
        public void EarlierSessionsLeaseThatCannotBeCleanedUp_IsUncertainWhileTheEnabledEndpointMayBeInUse(
            string server, bool uncertain)
        {
            var lease = _environment.WriteLease(_environment.DeadOwner(), User, applied: true);
            var settings = User with { Flags = ProxySettings.Proxy, Server = server };
            _environment.Store.Write(settings);
            var recovery = NewStart();

            InvalidOperationException error;
            // Held open without sharing deletion, so the journal cannot be removed.
            using (new FileStream(_environment.JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                error = Assert.ThrowsAny<InvalidOperationException>(() => recovery.Enable(new ProxyEndpoint("127.0.0.1", 8881)));
            }

            Assert.Equal(uncertain ? typeof(ProxyActivationUncertainException) : typeof(ProxyRecoveryException), error.GetType());
            Assert.Equal(
                Localization.GetLocalizedString("PROXY_RECOVERY_UNRESTORED", "127.0.0.1:8880, 127.0.0.1:8881"), error.Message);
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.Equal(settings, _environment.Store.Read());
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        // Enabling capture with settings that cannot be read, as when the WinINet query fails, changes nothing: no
        // record is written or replaced, and one that already exists stays. Whether Windows still uses XMAT is
        // unknown, so the activation is uncertain.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EnableWithSettingsThatCannotBeRead_IsUncertainAndChangesNothing(bool journaled)
        {
            var lease = journaled ? _environment.WriteLease(ProcessIdentity.Current, User, applied: true) : null;
            var store = new UnreadableStore(_environment.Store.FilePath);
            var recovery = _environment.Recovery(ProcessIdentity.Current, store: store);

            var error = Assert.Throws<ProxyActivationUncertainException>(
                () => recovery.Enable(ProxyRecoveryTestEnvironment.Endpoint));

            Assert.Equal(
                Localization.GetLocalizedString("PROXY_RECOVERY_SETTINGS_UNREADABLE", "127.0.0.1:8880"), error.Message);
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal(0, store.Writes);
            Assert.Equal(lease, LeaseJournal.TryRead(_environment.JournalPath));
            Assert.Null(recovery.GuardProcessId);
        }

        // Only listeners that receive connections made to the proxy's own loopback address count.
        [Theory]
        [InlineData("127.0.0.1", "127.0.0.1", true)]
        [InlineData("0.0.0.0", "127.0.0.1", true)]
        [InlineData("::", "127.0.0.1", true)]
        [InlineData("::ffff:127.0.0.1", "127.0.0.1", true)]
        [InlineData("192.168.1.5", "127.0.0.1", false)]
        [InlineData("127.0.0.2", "127.0.0.1", false)]
        [InlineData("::1", "127.0.0.1", false)]
        [InlineData("::1", "::1", true)]
        [InlineData("::", "::1", true)]
        [InlineData("0.0.0.0", "::1", false)]
        [InlineData("127.0.0.1", "localhost", true)]
        [InlineData("::1", "localhost", true)]
        public void Listener_CountsOnlyForTheProxyAddress(string listener, string host, bool counts)
        {
            Assert.Equal(counts, PcProxyRecovery.IsListenerFor(
                new IPEndPoint(IPAddress.Parse(listener), 8880), new ProxyEndpoint(host, 8880)));
        }

        // Someone can create the recovery mutex first under its predictable name. One that is not limited to
        // this user is never used; recovery reports busy instead.
        [Fact]
        public void RecoveryLockNotLimitedToTheUser_IsTreatedAsBusy()
        {
            using var squatter = new Mutex(false, RecoveryLock.Name(_environment.JournalPath));

            Assert.Null(RecoveryLock.TryAcquire(_environment.JournalPath, TimeSpan.Zero));
            Assert.Throws<TimeoutException>(() => NewStart().HealAtStartup());
            Assert.Equal(User, _environment.Store.Read());
        }

        [Fact]
        public void XboxSessions_OfAnXmatThatDiedAreOfferedUntilAcknowledged()
        {
            new XboxProxySessions(_environment.Folder, _environment.DeadOwner()).SetCapturing("console-a", true);
            var running = new XboxProxySessions(
                _environment.Folder, ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));
            running.SetCapturing("console-b", true);

            var offered = XboxProxySessions.FindAbandoned(_environment.Folder);
            Assert.Equal(["console-a"], offered.Consoles);
            // A start that ends before the offer is answered leaves it for the next start.
            Assert.Equal(["console-a"], XboxProxySessions.FindAbandoned(_environment.Folder).Consoles);

            XboxProxySessions.Acknowledge(offered);
            Assert.Empty(XboxProxySessions.FindAbandoned(_environment.Folder).Consoles);

            running.SetCapturing("console-b", false);
            Assert.Empty(Directory.EnumerateFiles(_environment.Folder, "xbox-*.json"));
        }

        private PcProxyRecovery NewStart() => _environment.Recovery(ProcessIdentity.Current);

        private static bool CannotOpen(int processId)
        {
            using var process = RecoveryNativeMethods.OpenProcess(ProcessIdentity.WaitAccess, false, processId);
            return process.IsInvalid && Marshal.GetLastWin32Error() != RecoveryNativeMethods.ErrorInvalidParameter;
        }

        // Fails every read, as a failed WinINet query does, and counts the writes it is asked for.
        private sealed class UnreadableStore : FileProxySettingsStore
        {
            internal UnreadableStore(string path)
                : base(path)
            {
            }

            internal int Writes { get; private set; }

            public override ProxySettings Read() =>
                throw new IOException("Simulated failure while reading the Windows proxy settings.");

            public override void Write(ProxySettings settings)
            {
                Writes++;
                base.Write(settings);
            }
        }
    }
}
