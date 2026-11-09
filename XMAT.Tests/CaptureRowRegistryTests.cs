// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public sealed class CaptureRowRegistryTests
    {
        [Fact]
        public void ResolvedBeforeRegistration_IsReturnedForCatchUpAndAppliesOnce()
        {
            var applied = new List<(CaptureRowKey Key, ClientProcessInfo Process)>();
            var registry = Registry(applied);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1002);
            registry.StartRun(run);
            ClientProcessInfo resolved = Local("game.exe", 88);
            connection.Publish(resolved);

            CaptureRowKey key = registry.RegisterInserted(
                connection,
                requestToken: 8,
                rowId: 32,
                ClientProcessInfo.NotRecorded,
                out ClientProcessInfo catchUp);
            registry.Resolve(connection, catchUp);

            Assert.Equal(resolved, catchUp);
            Assert.Equal((key, resolved), Assert.Single(applied));
        }

        [Fact]
        public void AdvanceGeneration_InvalidatesRowsButNotConnectionIdentity()
        {
            var applied = new List<(CaptureRowKey Key, ClientProcessInfo Process)>();
            var registry = Registry(applied);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1004);
            registry.StartRun(run);
            registry.Resolve(connection, Local("game.exe", 99));
            CaptureRowKey oldKey = Register(registry, connection, requestToken: 10, rowId: 1);

            registry.AdvanceGeneration();
            CaptureRowKey newKey = Register(registry, connection, requestToken: 11, rowId: 1);

            Assert.False(registry.IsCurrent(oldKey));
            Assert.True(registry.IsCurrent(newKey));
            Assert.NotEqual(oldKey.Generation, newKey.Generation);
            Assert.NotEqual(oldKey.Incarnation, newKey.Incarnation);
            Assert.Equal(Local("game.exe", 99), applied[^1].Process);
        }

        [Fact]
        public void FinishRun_DisablesWritesWithoutInvalidatingRowsForViewing()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1005);
            registry.StartRun(run);
            CaptureRowKey key = Register(registry, connection, requestToken: 12, rowId: 40);

            registry.FinishRun(run);

            Assert.True(registry.IsCurrent(key));
            Assert.False(registry.TryUpdate(run, key, () => throw new InvalidOperationException()));
        }

        [Fact]
        public void TryUpdate_RejectsAnotherRunAndExecutesCurrentWriteOnce()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1006);
            registry.StartRun(run);
            CaptureRowKey key = Register(registry, connection, requestToken: 13, rowId: 41);
            var writes = 0;

            Assert.False(registry.TryUpdate(Guid.NewGuid(), key, () => writes++));
            Assert.True(registry.TryUpdate(run, key, () => writes++));
            Assert.Equal(1, writes);
        }

        [Fact]
        public void RestartNeverReusesAnIncarnationForTheSameSqliteRowId()
        {
            var registry = Registry([]);
            Guid firstRun = Guid.NewGuid();
            ClientProcessContext firstConnection = Connection(firstRun, 1007);
            registry.StartRun(firstRun);
            CaptureRowKey first = Register(registry, firstConnection, requestToken: 14, rowId: 1);
            registry.FinishRun(firstRun);

            Guid secondRun = Guid.NewGuid();
            ClientProcessContext secondConnection = Connection(secondRun, 1008);
            registry.StartRun(secondRun);
            CaptureRowKey second = Register(registry, secondConnection, requestToken: 1, rowId: 1);

            Assert.NotEqual(first, second);
            Assert.NotEqual(first.Incarnation, second.Incarnation);
            Assert.False(registry.TryUpdate(firstRun, first, () => throw new InvalidOperationException()));
            Assert.True(registry.IsCurrent(second));
        }

        [Fact]
        public void Register_SeededIdentityDoesNotReapplyTheValueAlreadyInserted()
        {
            var applied = new List<(CaptureRowKey Key, ClientProcessInfo Process)>();
            var registry = Registry(applied);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1010);
            ClientProcessInfo inserted = Local("seeded.exe", 110);
            connection.Publish(inserted);
            registry.StartRun(run);

            CaptureRowKey key = registry.RegisterInserted(
                connection,
                requestToken: 20,
                rowId: 50,
                inserted,
                out ClientProcessInfo catchUp);

            Assert.True(registry.IsCurrent(key));
            Assert.Equal(inserted, catchUp);
            Assert.Empty(applied);
        }

        [Fact]
        public void Register_SeededPendingThenPublicationAppliesExactlyOnce()
        {
            var applied = new List<(CaptureRowKey Key, ClientProcessInfo Process)>();
            var registry = Registry(applied);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1011);
            registry.StartRun(run);
            CaptureRowKey key = registry.RegisterInserted(
                connection,
                requestToken: 21,
                rowId: 51,
                ClientProcessInfo.NotRecorded,
                out ClientProcessInfo catchUp);
            ClientProcessInfo resolved = Local("racing.exe", 111);

            connection.Publish(resolved);

            Assert.Equal(ClientProcessInfo.NotRecorded, catchUp);
            Assert.Equal((key, resolved), Assert.Single(applied));
        }

        [Fact]
        public void FailedApplicationIsNotAcknowledgedAndCanBeRetried()
        {
            var attempts = 0;
            var registry =
                new CaptureRowRegistry(
                    Guid.NewGuid(),
                    (_, _) =>
                    {
                        attempts++;
                        return false;
                    });
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1012);
            registry.StartRun(run);
            _ = Register(registry, connection, requestToken: 22, rowId: 52);
            ClientProcessInfo resolved = Local("retry.exe", 112);

            registry.Resolve(connection, resolved);
            registry.Resolve(connection, resolved);

            Assert.Equal(2, attempts);
        }

        [Fact]
        public void FinishRun_TerminalizesPendingRowsBeforeDisablingWrites()
        {
            var model = new ProxyConnectionModel(1, 1, 1000);
            model.SetClientProcess(ClientProcessInfo.NotRecorded, resolving: true);
            var registry =
                new CaptureRowRegistry(
                    Guid.NewGuid(),
                    (_, process) =>
                    {
                        model.SetClientProcess(process);
                        return true;
                    });
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1013);
            registry.StartRun(run);
            CaptureRowKey key = Register(registry, connection, requestToken: 23, rowId: 53);

            registry.FinishRun(run);
            connection.Publish(Local("late.exe", 113));

            Assert.True(registry.IsCurrent(key));
            Assert.False(model.IsProcessResolving);
            Assert.Equal(ClientProcessStatus.Unresolved, model.ClientProcess.Status);
        }

        [Fact]
        public void Resolve_IgnoresAnObserverArgumentOlderThanThePublishedIdentity()
        {
            var applied = new List<(CaptureRowKey Key, ClientProcessInfo Process)>();
            var registry = Registry(applied);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1014);
            ClientProcessInfo older = Local("older.exe", 114);
            ClientProcessInfo latest = Local("latest.exe", 115);
            connection.Publish(latest);
            registry.StartRun(run);
            _ = registry.RegisterInserted(connection, requestToken: 24, rowId: 54, latest, out _);
            applied.Clear();

            registry.Resolve(connection, older);

            Assert.Equal(latest, connection.Identity);
            Assert.DoesNotContain(applied, item => item.Process == older);
        }

        [Fact]
        public void AdvanceGeneration_CreatesANonDefaultCurrentAssociationForAContinuingStream()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1015);
            registry.StartRun(run);
            CaptureRowKey old = Register(registry, connection, requestToken: 25, rowId: 55);

            registry.AdvanceGeneration();
            bool associated = registry.TryGetOrCreateStream(connection, 25, out CaptureRowKey current);

            Assert.False(registry.IsCurrent(old));
            Assert.True(associated);
            Assert.NotEqual(default, current);
            Assert.NotEqual(old.Generation, current.Generation);
            Assert.True(registry.IsCurrent(current));
        }

        [Fact]
        public void ApplicationFault_DoesNotLoseTheRegisteredBinding()
        {
            var registry = new CaptureRowRegistry(
                Guid.NewGuid(),
                (_, _) => throw new InvalidOperationException("Injected application fault."));
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 1016);
            ClientProcessInfo process = Local("fault.exe", 116);
            connection.Publish(process);
            registry.StartRun(run);
            CaptureRowKey key = default;

            Exception exception = Record.Exception(() =>
            {
                key = registry.RegisterInserted(
                    connection,
                    requestToken: 26,
                    rowId: 56,
                    ClientProcessInfo.NotRecorded,
                    out ClientProcessInfo catchUp);
                registry.Resolve(connection, catchUp);
            });

            Assert.Null(exception);
            Assert.NotEqual(default, key);
            Assert.True(registry.IsCurrent(key));
        }

        [Fact]
        public void CloseConnection_ReleasesResolvedContextWithoutInvalidatingItsRow()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            registry.StartRun(run);
            (WeakReference<ClientProcessContext> reference, CaptureRowKey key) = RegisterAndClose(
                registry,
                run,
                1017,
                57);

            ForceCollection();

            Assert.True(registry.IsCurrent(key));
            Assert.False(reference.TryGetTarget(out _));
        }

        [Fact]
        public void CloseAndClearCycles_ReleaseObsoleteConnections()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            registry.StartRun(run);
            var references = new List<WeakReference<ClientProcessContext>>();
            for (var index = 0; index < 64; index++)
            {
                (WeakReference<ClientProcessContext> reference, _) = RegisterAndClose(
                    registry,
                    run,
                    1100 + index,
                    index + 100);
                references.Add(reference);
                registry.AdvanceGeneration();
            }

            ForceCollection();

            Assert.All(references, reference => Assert.False(reference.TryGetTarget(out _)));
        }

        [Fact]
        public void CloseConnection_VisitsOnlyThatConnectionsIndexes()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            registry.StartRun(run);
            List<ClientProcessContext> retained = PopulateUnrelatedHistory(registry, run, 128);
            ClientProcessContext target = Connection(run, 3000);
            ClientProcessInfo process = Local("target.exe", 3000);
            target.Publish(process);
            _ = Register(registry, target, 3000, 3000, process);
            Assert.True(registry.TryGetOrCreateStream(target, 3000, out _));
            var visits = 0;
            registry.VisitObserver = (_, count) => visits += count;

            registry.CloseConnection(target);

            GC.KeepAlive(retained);
            Assert.InRange(visits, 0, 3);
        }

        [Fact]
        public void ClosedConnectionsLateResolution_VisitsOnlyItsOwnBindings()
        {
            var registry = Registry([]);
            Guid run = Guid.NewGuid();
            registry.StartRun(run);
            List<ClientProcessContext> retained = PopulateUnrelatedHistory(registry, run, 128);
            ClientProcessContext target = Connection(run, 3001);
            _ = Register(registry, target, 3001, 3001);
            registry.CloseConnection(target);
            var visits = 0;
            registry.VisitObserver =
                (kind, count) =>
                {
                    if (kind == "bindings")
                    {
                        visits += count;
                    }
                };

            target.Publish(Local("late-target.exe", 3001));

            GC.KeepAlive(retained);
            Assert.InRange(visits, 0, 1);
        }

        [Fact]
        public void FinishRun_RetriesKnownDesiredIdentityAfterConnectionClose()
        {
            var attempts = 0;
            var persisted = new List<ClientProcessInfo>();
            var registry =
                new CaptureRowRegistry(
                    Guid.NewGuid(),
                    (_, process) =>
                    {
                        attempts++;
                        if (attempts == 1)
                        {
                            return false;
                        }
                        persisted.Add(process);
                        return true;
                    });
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 3002);
            ClientProcessInfo known = Local("known.exe", 3002);
            registry.StartRun(run);
            _ = Register(registry, connection, 3002, 3002);

            connection.Publish(known);
            registry.CloseConnection(connection);
            registry.FinishRun(run);

            Assert.Equal(2, attempts);
            Assert.Equal(known, Assert.Single(persisted));
        }

        [Fact]
        public void FinishRun_KeepsKnownDesiredIdentityAfterLaterUnresolvedPublication()
        {
            var attempts = 0;
            var persisted = new List<ClientProcessInfo>();
            var registry =
                new CaptureRowRegistry(
                    Guid.NewGuid(),
                    (_, process) =>
                    {
                        attempts++;
                        if (attempts == 1)
                        {
                            return false;
                        }
                        persisted.Add(process);
                        return true;
                    });
            Guid run = Guid.NewGuid();
            ClientProcessContext connection = Connection(run, 3003);
            ClientProcessInfo known = Local("known.exe", 3003);
            registry.StartRun(run);
            _ = Register(registry, connection, 3003, 3003);

            connection.Publish(known);
            connection.Publish(new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Unresolved));
            registry.CloseConnection(connection);
            registry.FinishRun(run);

            Assert.Equal(2, attempts);
            Assert.Equal(known, Assert.Single(persisted));
        }

        private static CaptureRowRegistry Registry(List<(CaptureRowKey Key, ClientProcessInfo Process)> applied) => new(
            Guid.NewGuid(),
            (key, process) =>
            {
                applied.Add((key, process));
                return true;
            });

        private static CaptureRowKey Register(
            CaptureRowRegistry registry,
            ClientProcessContext connection,
            long requestToken,
            long rowId,
            ClientProcessInfo insertedIdentity = null)
        {
            ClientProcessInfo inserted = insertedIdentity ?? ClientProcessInfo.NotRecorded;
            CaptureRowKey key = registry.RegisterInserted(
                connection,
                requestToken,
                rowId,
                inserted,
                out ClientProcessInfo catchUp);
            if (catchUp.Status != ClientProcessStatus.NotRecorded && catchUp != inserted)
            {
                registry.Resolve(connection, catchUp);
            }
            return key;
        }

        private static ClientProcessContext Connection(Guid run, long token) => new(
            run,
            token,
            new IPEndPoint(IPAddress.Loopback, 50000 + (int)(token % 1000)),
            new IPEndPoint(IPAddress.Loopback, 8080),
            DateTimeOffset.Parse("2026-11-03T20:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture),
            Stopwatch.GetTimestamp());

        private static ClientProcessInfo Local(string name, int processId) => new(
            name,
            processId,
            DateTimeOffset.Parse("2026-11-03T19:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture),
            ClientProcessStatus.Local);

        private static List<ClientProcessContext> PopulateUnrelatedHistory(
            CaptureRowRegistry registry,
            Guid run,
            int count)
        {
            var retained = new List<ClientProcessContext>(count);
            for (var index = 0; index < count; index++)
            {
                long token = 4000 + index;
                ClientProcessContext connection = Connection(run, token);
                ClientProcessInfo process = Local($"unrelated-{index}.exe", (int)token);
                connection.Publish(process);
                _ = Register(registry, connection, token, token, process);
                Assert.True(registry.TryGetOrCreateStream(connection, token, out _));
                retained.Add(connection);
            }
            return retained;
        }

        [MethodImpl(
            MethodImplOptions.NoInlining)]
        private static (WeakReference<ClientProcessContext> Reference, CaptureRowKey Key) RegisterAndClose(
            CaptureRowRegistry registry,
            Guid run,
            long token,
            long rowId)
        {
            ClientProcessContext connection = Connection(run, token);
            ClientProcessInfo process = Local("closed.exe", (int)token);
            connection.Publish(process);
            CaptureRowKey key = Register(registry, connection, token, rowId, process);
            var reference = new WeakReference<ClientProcessContext>(connection);
            registry.CloseConnection(connection);
            return (reference, key);
        }

        private static void ForceCollection()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
}
