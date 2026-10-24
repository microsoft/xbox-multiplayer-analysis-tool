// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
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
            var listener = new TcpListener(IPAddress.Loopback, 0);
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
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            try
            {
                var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                var abandoned = User.WithProxy(new ProxyEndpoint("127.0.0.1", port));
                _environment.Store.Write(abandoned);

                Assert.Equal(abandoned, NewStart().HealAtStartup());
            }
            finally
            {
                listener.Stop();
            }
        }

        // A dead XMAT's endpoint whose other settings changed is never overwritten at startup: its journal stays,
        // and once nothing listens there, the user is asked before only its proxy is turned off.
        [Fact]
        public void Startup_AsksBeforeTurningOffAChangedXmatEndpointThatItsLeaseKeeps()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
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
        // and port; another proxy, or any proxy when no endpoint is expected, is kept and reported.
        [Theory]
        [InlineData("127.0.0.1:8880", true, true)]
        [InlineData("127.0.0.2:8880", true, false)]
        [InlineData("localhost:8880", true, false)]
        [InlineData("127.0.0.1:8881", true, false)]
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
                var error = Assert.Throws<InvalidOperationException>(() => NewStart().Disable(expected));
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

            var error = Assert.Throws<InvalidOperationException>(() => NewStart().Disable());

            Assert.Equal(Localization.GetLocalizedString("PROXY_RECOVERY_DISABLE_FAILED_UNKNOWN"), error.Message);
            Assert.DoesNotContain("127.0.0.1", error.Message);
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
    }
}
