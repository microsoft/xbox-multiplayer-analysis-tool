// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.Tests.Proxy;

namespace XMAT.Tests
{
    // These tests point the process-wide NetworkTraceCaptureMethod at their own live table,
    // so they run alone and put it back afterwards.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class NetworkCaptureReloadTests
    {
        // Long enough for the packet model to parse an Ethernet and an IPv4 header.
        private static readonly string ValidPayload =
            Convert.ToBase64String(
                new byte[34]);

        private static readonly TimeSpan WaitLimit =
            TimeSpan.FromSeconds(30);

        private const int InFlightProcessId = 9;

        [Fact]
        public async Task LoadDataCaptures_ReloadedNetworkTabAddsEachLivePacketOnce()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload),
                ("console", ValidPayload));

            session.Model.LoadDataCaptures(path);
            session.Model.LoadDataCaptures(path);
            var controller =
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        Assert.Single(
                            session.Model.CaptureDeviceContexts)
                            .CaptureController);
            Assert.Equal(
                2,
                controller.NetworkTracePackets.Count);

            session.AddLivePacket("console");

            Assert.Equal(
                3,
                controller.NetworkTracePackets.Count);
        }

        [Fact]
        public async Task LoadDataCaptures_NetworkReloadsKeepOneLiveSubscriberUntilTheTabCloses()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            session.Model.LoadDataCaptures(path);
            ICaptureDeviceContext context =
                Assert.Single(
                    session.Model.CaptureDeviceContexts);

            for (var reload = 0; reload < 3; reload++)
            {
                session.Model.LoadDataCaptures(path);
            }

            Assert.Same(
                context,
                Assert.Single(
                    session.Model.CaptureDeviceContexts));
            Assert.Equal(
                new object[] { context.CaptureController },
                session.LiveSubscribers());

            await session.Model.RemoveDeviceContextAsync(
                context);

            Assert.Empty(
                session.LiveSubscribers());
        }

        [Fact]
        public async Task LoadDataCaptures_FailedNetworkLoadDetachesStagedControllersAndKeepsTabs()
        {
            await using var session = new LiveNetworkSession();
            string loadedPath = session.WriteCapture(
                "loaded.xmatcap",
                ("console", ValidPayload),
                ("console", ValidPayload));
            // The second device fails while loading its packets, after both staged controllers were created.
            string corruptPath = session.WriteCapture(
                "corrupt.xmatcap",
                ("console", ValidPayload),
                ("corrupt", "not-base64!"));
            session.Model.LoadDataCaptures(loadedPath);
            ICaptureDeviceContext context =
                Assert.Single(
                    session.Model.CaptureDeviceContexts);
            var controller =
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        context.CaptureController);
            var packets = controller.NetworkTracePackets;
            IReadonlyDatabase database =
                session.Model.LoadedDatabase;

            Assert.Throws<FormatException>(
                () => session.Model.LoadDataCaptures(
                    corruptPath));

            Assert.Same(
                context,
                Assert.Single(
                    session.Model.CaptureDeviceContexts));
            Assert.Same(
                controller,
                context.CaptureController);
            Assert.Same(
                packets,
                controller.NetworkTracePackets);
            Assert.Same(
                database,
                session.Model.LoadedDatabase);
            Assert.Equal(
                new object[] { controller },
                session.LiveSubscribers());

            session.AddLivePacket("console");

            Assert.Equal(
                3,
                packets.Count);
        }

        [Fact]
        public async Task NetTraceImportTab_KeepsReceivingItsLiveRowsAcrossCaptureReloads()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            // The .etl import creates this read-only tab and then fills it through RowAdded.
            ICaptureDeviceContext imported =
                session.Model.AddDeviceContext(
                    DeviceType.Unknown,
                    "NetTrace - trace.etl",
                    CaptureType.NetworkTrace,
                    true,
                    null);
            session.Model.LoadDataCaptures(path);
            session.Model.LoadDataCaptures(path);

            session.AddLivePacket("NetTrace - trace.etl");

            Assert.Single(
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        imported.CaptureController)
                    .NetworkTracePackets);
        }

        [Fact]
        public async Task RemoveDeviceContextAsync_FailedRemovalKeepsLiveUpdatesUntilTheTabIsRemoved()
        {
            await using var session = new LiveNetworkSession();
            var registrations = new RegistrationTable
            {
                FailRemovals = true,
            };
            session.Model.CaptureDevicesTable = registrations;
            var context =
                new CaptureDeviceContextModel(
                    DeviceType.LocalPC,
                    "local",
                    CaptureType.NetworkTrace,
                    false,
                    null);
            session.Model.CaptureDeviceContexts.Add(context);
            try
            {
                await Assert.ThrowsAsync<IOException>(
                    () => session.Model.RemoveDeviceContextAsync(
                        context));
            }
            finally
            {
                registrations.FailRemovals = false;
            }

            Assert.Same(
                context,
                Assert.Single(
                    session.Model.CaptureDeviceContexts));
            session.AddLivePacket("local");
            Assert.Single(
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        context.CaptureController)
                    .NetworkTracePackets);

            await session.Model.RemoveDeviceContextAsync(
                context);

            Assert.Empty(
                session.LiveSubscribers());
        }

        [Fact]
        public async Task RemoveDeviceContextAsync_NotificationFailureAfterRemovalStillDetachesAndReleases()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            session.Model.LoadDataCaptures(path);
            ICaptureDeviceContext context =
                Assert.Single(
                    session.Model.CaptureDeviceContexts);
            var database =
                Assert.IsType<
                    SqLtReadonlyDatabase>(
                        session.Model.LoadedDatabase);
            NotifyCollectionChangedEventHandler throwing =
                (_, args) =>
                {
                    if (args.Action ==
                        NotifyCollectionChangedAction.Remove)
                    {
                        throw new InvalidOperationException(
                            "Injected collection notification failure.");
                    }
                };
            session.Model.CaptureDeviceContexts.CollectionChanged +=
                throwing;
            try
            {
                await Assert.ThrowsAsync<
                    InvalidOperationException>(
                        () => session.Model.RemoveDeviceContextAsync(
                            context));
            }
            finally
            {
                session.Model.CaptureDeviceContexts.CollectionChanged -=
                    throwing;
            }

            Assert.Empty(
                session.Model.CaptureDeviceContexts);
            Assert.Empty(
                session.LiveSubscribers());
            Assert.Null(
                session.Model.LoadedDatabase);
            Assert.Equal(
                0,
                LoadedCaptureDatabaseRegistry.ReferenceCount(
                    database));
        }

        // A row whose RowAdded delivery is already running when a reload hands the file's packets to
        // the reused tab. The pausing handler is subscribed before any controller, so it holds a
        // delivery whose handler list is already fixed until LoadDataCaptures has returned.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task LoadDataCaptures_RowDeliveredDuringAReloadAppearsOnce(
            bool reload)
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            using var delivering = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            bool resumedByTest = false;
            Action<IDataTable, long> pause = (_, _) =>
            {
                delivering.Set();
                Volatile.Write(
                    ref resumedByTest,
                    resume.Wait(WaitLimit));
            };
            session.LiveTable.RowAdded += pause;
            Task delivery = null;
            try
            {
                session.Model.LoadDataCaptures(path);
                var controller =
                    Assert.IsType<
                        NetworkTraceCaptureController>(
                            Assert.Single(
                                session.Model.CaptureDeviceContexts)
                                .CaptureController);
                if (reload)
                {
                    // The row starts while the reused tab takes the reloaded packets.
                    bool pausedDuringReload = false;
                    PropertyChangedEventHandler startRow = (_, args) =>
                    {
                        if (delivery == null &&
                            args.PropertyName == nameof(
                                NetworkTraceCaptureController.NetworkTracePackets))
                        {
                            delivery = Task.Run(
                                () => session.AddLivePacket(
                                    "console",
                                    InFlightProcessId));
                            pausedDuringReload =
                                delivering.Wait(WaitLimit);
                        }
                    };
                    controller.PropertyChanged += startRow;
                    session.Model.LoadDataCaptures(path);
                    controller.PropertyChanged -= startRow;
                    Assert.True(
                        pausedDuringReload,
                        "The live row's delivery did not start before the reload finished.");
                }
                else
                {
                    delivery = Task.Run(
                        () => session.AddLivePacket(
                            "console",
                            InFlightProcessId));
                }

                Assert.True(
                    delivering.Wait(WaitLimit),
                    "The live row's delivery did not start.");
                resume.Set();
                await delivery.WaitAsync(WaitLimit);
                Assert.True(
                    Volatile.Read(ref resumedByTest),
                    "The live row's delivery resumed only because its wait timed out.");

                Assert.Single(
                    controller.NetworkTracePackets,
                    packet => packet.ProcessId == InFlightProcessId);
            }
            finally
            {
                resume.Set();
                if (delivery != null)
                {
                    await delivery.WaitAsync(WaitLimit);
                }
                session.LiveTable.RowAdded -= pause;
            }
        }

        [Fact]
        public async Task LoadDataCaptures_NewNetworkTabAttachesToLiveRowsOnlyAfterTheLoadCommits()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            object[] subscribersWhenAdded = null;
            NotifyCollectionChangedEventHandler observe =
                (_, args) =>
                {
                    if (args.Action ==
                        NotifyCollectionChangedAction.Add)
                    {
                        subscribersWhenAdded =
                            session.LiveSubscribers();
                    }
                };
            session.Model.CaptureDeviceContexts.CollectionChanged +=
                observe;
            try
            {
                session.Model.LoadDataCaptures(path);
            }
            finally
            {
                session.Model.CaptureDeviceContexts.CollectionChanged -=
                    observe;
            }

            var controller =
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        Assert.Single(
                            session.Model.CaptureDeviceContexts)
                            .CaptureController);
            Assert.NotNull(subscribersWhenAdded);
            Assert.Empty(subscribersWhenAdded);
            Assert.Equal(
                new object[] { controller },
                session.LiveSubscribers());

            session.AddLivePacket("console");

            Assert.Equal(
                2,
                controller.NetworkTracePackets.Count);
        }

        [Fact]
        public async Task LoadDataCaptures_RejectedNetworkTabAddsNoLiveSubscriber()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            NotifyCollectionChangedEventHandler reject =
                (_, args) =>
                {
                    if (args.Action ==
                        NotifyCollectionChangedAction.Add)
                    {
                        throw new InvalidOperationException(
                            "Injected collection notification failure.");
                    }
                };
            session.Model.CaptureDeviceContexts.CollectionChanged +=
                reject;
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => session.Model.LoadDataCaptures(
                        path));
            }
            finally
            {
                session.Model.CaptureDeviceContexts.CollectionChanged -=
                    reject;
            }

            Assert.Empty(
                session.Model.CaptureDeviceContexts);
            Assert.Empty(
                session.LiveSubscribers());
        }

        [Fact]
        public async Task LoadedNetworkTab_ShowsLiveRowsWhoseIdsMatchFileRowsOrAreReused()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload),
                ("console", ValidPayload));
            session.Model.LoadDataCaptures(path);
            var controller =
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        Assert.Single(
                            session.Model.CaptureDeviceContexts)
                            .CaptureController);

            // The live table is new, so its first rows get the file rows' ids.
            long first = session.AddLivePacket("console");
            long second = session.AddLivePacket("console");

            Assert.Equal(
                new[] { 1L, 2L },
                new[] { first, second });
            Assert.Equal(
                4,
                controller.NetworkTracePackets.Count);

            // Without AUTOINCREMENT, an emptied table hands out the same id again.
            session.LiveTable.RemoveRowsWhere();
            long reused = session.AddLivePacket("console");

            Assert.Equal(
                first,
                reused);
            Assert.Equal(
                5,
                controller.NetworkTracePackets.Count);
        }

        [Fact]
        public async Task LoadDataCaptures_ReloadKeepsTheLiveTabAndTheLiveTableComplete()
        {
            await using var session = new LiveNetworkSession();
            string path = session.WriteCapture(
                "network.xmatcap",
                ("console", ValidPayload));
            session.Model.CaptureDevicesTable = new RegistrationTable();
            var live =
                new CaptureDeviceContextModel(
                    DeviceType.XboxConsole,
                    "console",
                    CaptureType.NetworkTrace,
                    false,
                    null);
            session.Model.CaptureDeviceContexts.Add(live);
            session.Model.LoadDataCaptures(path);
            var loaded =
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        Assert.Single(
                            session.Model.CaptureDeviceContexts,
                            context => context.IsReadOnly)
                            .CaptureController);
            // Rows arrive while the file opens and while the reused tab takes the reloaded packets.
            PropertyChangedEventHandler addRow = (_, args) =>
            {
                if (args.PropertyName == nameof(
                    NetworkTraceCaptureController.NetworkTracePackets))
                {
                    session.AddLivePacket("console", 2);
                }
            };
            loaded.PropertyChanged += addRow;
            try
            {
                session.Model.LoadDataCaptures(
                    path,
                    file =>
                    {
                        session.AddLivePacket("console", 1);
                        return new SqLtReadonlyDatabase(file);
                    });
            }
            finally
            {
                loaded.PropertyChanged -= addRow;
            }
            session.AddLivePacket("console", 3);

            Assert.Equal(
                new long[] { 1, 2, 3 },
                Assert.IsType<
                    NetworkTraceCaptureController>(
                        live.CaptureController)
                    .NetworkTracePackets
                    .Select(packet => packet.ProcessId));
            Assert.Equal(
                new long[] { 1, 2, 3 },
                session.LiveTable.Dataset.DataRecords
                    .Select(record => record.Int("ProcessId")));
        }

        // Owns a fixture directory, a model and its live database. Disposing it closes the model's
        // tabs and returns NetworkTraceCaptureMethod to the state it had before.
        private sealed class LiveNetworkSession : IAsyncDisposable
        {
            private readonly ICaptureAppModel _previousModel =
                Method.CaptureAppModel;
            private readonly IDataTable _previousTable =
                Method.NetworkTracePacketsTable;
            private readonly SqLtDatabase _liveDatabase = new();
            private readonly IDataTable _liveTable;
            private readonly string _directory = Path.Combine(
                AppContext.BaseDirectory,
                "NetworkCaptureReloadFixtures",
                Guid.NewGuid().ToString("N"));

            internal LiveNetworkSession()
            {
                try
                {
                    _liveDatabase.Initialize();
                    Model.ActiveDatabase = _liveDatabase;
                    Method.Initialize(Model);
                    _liveTable = Method.NetworkTracePacketsTable;
                    Directory.CreateDirectory(_directory);
                }
                catch
                {
                    RestoreMethod();
                    _liveDatabase.Dispose();
                    throw;
                }
            }

            internal CaptureAppModel Model { get; } = new();

            internal IDataTable LiveTable => _liveTable;

            private static NetworkTraceCaptureMethod Method =>
                NetworkTraceCaptureMethod.MethodInstance;

            internal string WriteCapture(
                string fileName,
                params (string DeviceName, string Payload)[] packets)
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                IDataTable devices = database.CreateTable(
                    "CaptureDevices",
                    new Field<long>("DeviceType"),
                    new Field<string>("DeviceName"),
                    new Field<long>("CaptureType"));
                IDataTable table = database.CreateTable(
                    "NetworkTracePackets",
                    new Field<string>("DeviceName"),
                    new Field<int>("ProcessId"),
                    new Field<int>("ThreadId"),
                    new Field<string>("Timestamp"),
                    new Field<string>("MediaType"),
                    new Field<int>("StartPacket"),
                    new Field<int>("EndPacket"),
                    new Field<int>("Fragment"),
                    new Field<int>("Send"),
                    new Field<int>("Receive"),
                    new Field<string>("Payload"));
                foreach (string deviceName in packets
                    .Select(packet => packet.DeviceName)
                    .Distinct())
                {
                    devices.AddRow(
                        (long)DeviceType.XboxConsole,
                        deviceName,
                        (long)CaptureType.NetworkTrace);
                }
                foreach ((string deviceName, string payload) in packets)
                {
                    AddPacket(
                        table,
                        deviceName,
                        payload);
                }
                string path = Path.Combine(
                    _directory,
                    fileName);
                database.SaveToFile(path);
                return path;
            }

            internal long AddLivePacket(
                string deviceName,
                int processId = 1) =>
                AddPacket(
                    _liveTable,
                    deviceName,
                    ValidPayload,
                    processId);

            // The controllers whose handlers are subscribed to the live table's RowAdded event.
            internal object[] LiveSubscribers() =>
                ((Delegate)_liveTable.GetType()
                    .GetField(
                        nameof(IDataTable.RowAdded),
                        BindingFlags.Instance |
                        BindingFlags.NonPublic)
                    .GetValue(_liveTable))
                    ?.GetInvocationList()
                    .Select(handler => handler.Target)
                    .ToArray() ??
                Array.Empty<object>();

            public async ValueTask DisposeAsync()
            {
                try
                {
                    // One attempt per tab, so a tab that cannot be removed cannot keep this loop running.
                    foreach (ICaptureDeviceContext context in
                        Model.CaptureDeviceContexts.ToArray())
                    {
                        await Model.RemoveDeviceContextAsync(
                            context);
                    }
                }
                finally
                {
                    RestoreMethod();
                    _liveDatabase.Dispose();
                    Directory.Delete(
                        _directory,
                        true);
                }
            }

            private static long AddPacket(
                IDataTable table,
                string deviceName,
                string payload,
                int processId = 1) =>
                table.AddRow(
                    deviceName,
                    processId,
                    1,
                    string.Empty,
                    "ethernet",
                    1,
                    1,
                    0,
                    0,
                    1,
                    payload);

            // Initialize cannot return the method to an uninitialized state, so the previous
            // values go back through the properties' private setters.
            private void RestoreMethod()
            {
                SetMethodProperty(
                    nameof(NetworkTraceCaptureMethod.CaptureAppModel),
                    _previousModel);
                SetMethodProperty(
                    nameof(NetworkTraceCaptureMethod.NetworkTracePacketsTable),
                    _previousTable);
            }

            private static void SetMethodProperty(
                string name,
                object value) =>
                typeof(NetworkTraceCaptureMethod)
                    .GetProperty(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.NonPublic)
                    .SetValue(
                        Method,
                        value);
        }

        // A CaptureDevices table whose registration removals can be made to fail.
        private sealed class RegistrationTable : IDataTable
        {
            public event Action<IDataTable, long> RowAdded
            {
                add { }
                remove { }
            }
            public event Action<IDataTable, long> RowUpdated
            {
                add { }
                remove { }
            }
            public event Action<IDataTable, long> RowsRemoved
            {
                add { }
                remove { }
            }

            internal bool FailRemovals { get; set; }

            public string Name => "CaptureDevices";
            public IDataset Dataset => null;

            public IDataset Subset(params string[] fieldNames)
            {
                return null;
            }

            public long AddRow(params object[] fieldValueArgs)
            {
                return 0;
            }

            public void UpdateRow(long rowId, params IFieldValue[] fieldNamesAndValues)
            {
            }

            public void RemoveRowsWhere(params IFieldValue[] fieldNamesAndValues)
            {
                if (FailRemovals)
                {
                    throw new IOException(
                        "Injected registration removal failure.");
                }
            }
        }
    }
}
