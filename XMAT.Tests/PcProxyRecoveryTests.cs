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
        public void XboxSessions_OfAnXmatThatDiedAreReportedOnce()
        {
            new XboxProxySessions(_environment.Folder, _environment.DeadOwner()).SetCapturing("console-a", true);
            var running = new XboxProxySessions(
                _environment.Folder, ProxyRecoveryTestEnvironment.Identity(_environment.StartOwner()));
            running.SetCapturing("console-b", true);

            Assert.Equal(["console-a"], XboxProxySessions.CollectAbandoned(_environment.Folder));
            Assert.Empty(XboxProxySessions.CollectAbandoned(_environment.Folder));

            running.SetCapturing("console-b", false);
            Assert.Empty(Directory.EnumerateFiles(_environment.Folder, "xbox-*.json"));
        }

        private PcProxyRecovery NewStart() => _environment.Recovery(ProcessIdentity.Current);
    }
}
