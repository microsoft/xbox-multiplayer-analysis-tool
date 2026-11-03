// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureFileLoadTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void LoadingUsesFrozenDataAndPreservesRunningWritableCapturesAndBoundSelection()
        {
            application.Run(owner =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var human = setup.Created.Single();
                var model = setup.Model;
                model.SelectedDeviceContext = human;
                var tabs = new TabControl { ItemsSource = model.CaptureDeviceContexts, IsSynchronizedWithCurrentItem = true };
                tabs.SetBinding(TabControl.SelectedItemProperty, new Binding(nameof(CaptureAppModel.SelectedDeviceContext))
                { Source = model, Mode = BindingMode.TwoWay });
                WpfTestHost.Show(owner, tabs);
                Finish(model.CaptureOperations.SetRunningAsync(human, true));
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var original = File.ReadAllBytes(path);
                try
                {
                    var receipt = Complete(model.CaptureFileLoads.LoadDesktopAsync(path));
                    Assert.True(receipt.Published);
                    Assert.True(receipt.ViewsNotified);
                    Assert.Equal(1, receipt.AddedContexts);
                    Assert.Same(human, model.SelectedDeviceContext);
                    Assert.Same(human, tabs.SelectedItem);
                    Assert.True(human.CaptureController.IsRunning);
                    Assert.Single(setup.Database.TableByName("CaptureDevices").Dataset.DataRecords);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    var context = model.CaptureDeviceContexts.Single(item => item.IsReadOnly);
                    var proxy = Assert.IsType<WebServiceDeviceCaptureController>(context.CaptureController);
                    Assert.Single(proxy.ProxyConnections);
                    Assert.False(proxy.HasPortReservation);
                    Assert.Empty(proxy.Scripts.ActiveScripts);
                    WriteCapture(path, [("replacement", CaptureType.WebProxy)]);
                    Assert.Equal("loaded", Assert.Single(proxy.GetCaptureRecords()).Str("DeviceName"));
                    Finish(model.CaptureOperations.SetRunningAsync(human, false));
                    Assert.Single(Complete(model.CaptureQueries.GetSchemaAsync(receipt.SourceId)),
                        table => table.Name == "CaptureDevices");
                    Assert.Equal("capture.xmatcap", Complete(model.CaptureQueries.GetSourcesAsync())
                        .Single(source => source.Id == receipt.SourceId).DisplayName);
                }
                finally { Finish(model.CaptureFileLoads.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void LoadingDoesNotNavigateAnInitiallyEmptyBoundWorkspace()
        {
            application.Run(owner =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var model = new CaptureAppModel();
                var tabs = new TabControl { ItemsSource = model.CaptureDeviceContexts, IsSynchronizedWithCurrentItem = true };
                tabs.SetBinding(TabControl.SelectedItemProperty, new Binding(nameof(CaptureAppModel.SelectedDeviceContext))
                { Source = model, Mode = BindingMode.TwoWay });
                WpfTestHost.Show(owner, tabs);
                try
                {
                    Complete(model.CaptureFileLoads.LoadDesktopAsync(path));
                    WpfTestHost.Pump();
                    Assert.Null(model.SelectedDeviceContext);
                    Assert.Null(tabs.SelectedItem);
                }
                finally
                {
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void InvalidLaterDeviceRollsBackPreparationWithoutChangingAnyPublishedView()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var good = Path.Combine(directory.Path, "good.xmatcap");
                var bad = Path.Combine(directory.Path, "bad.xmatcap");
                WriteCapture(good, [("web", CaptureType.WebProxy), ("trace", CaptureType.NetworkTrace)]);
                WriteCapture(bad, [("web", CaptureType.WebProxy), ("new", CaptureType.WebProxy),
                    ("trace", CaptureType.NetworkTrace)], invalidPacket: true);
                var model = new CaptureAppModel();
                string stagingPath = null;
                var loads = new CaptureFileLoads(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, model.CaptureFileInputs, path =>
                    {
                        stagingPath = path;
                        return DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile(path);
                    });
                try
                {
                    var initial = Complete(loads.LoadDesktopAsync(good));
                    var database = model.LoadedDatabase;
                    var contexts = model.CaptureDeviceContexts.ToArray();
                    var web = (WebServiceDeviceCaptureController)contexts[0].CaptureController;
                    var trace = (NetworkTraceCaptureController)contexts[1].CaptureController;
                    var connections = web.ProxyConnections;
                    var packets = trace.NetworkTracePackets;
                    model.SelectedDeviceContext = contexts[1];
                    Assert.Equal(CaptureFileFailure.InvalidCapture, Assert.Throws<CaptureFileException>(() =>
                        Complete(loads.LoadDesktopAsync(bad))).Failure);
                    Assert.Equal(contexts, model.CaptureDeviceContexts);
                    Assert.Same(database, model.LoadedDatabase);
                    Assert.Same(connections, web.ProxyConnections);
                    Assert.Same(packets, trace.NetworkTracePackets);
                    Assert.Same(contexts[1], model.SelectedDeviceContext);
                    Assert.Equal(initial.SourceId, Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id);
                    Assert.Equal("web", Assert.Single(web.GetCaptureRecords()).Str("DeviceName"));
                    Assert.False(Directory.Exists(Path.GetDirectoryName(stagingPath)));
                    Assert.False(loads.IsLoading);
                    Assert.False(model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    Finish(loads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DeduplicatedReloadsReplacePacketsAndRetainOlderDatabasesOnlyForTheirRemainingViews()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var first = Path.Combine(directory.Path, "first.xmatcap");
                var second = Path.Combine(directory.Path, "second.xmatcap");
                WriteCapture(first, [("old", CaptureType.WebProxy), ("web", CaptureType.WebProxy),
                    ("trace", CaptureType.NetworkTrace)]);
                WriteCapture(second, [("web", CaptureType.WebProxy), ("trace", CaptureType.NetworkTrace)],
                    modify: database => database.TableByName("CaptureDevices").AddRow(
                        (long)DeviceType.Unknown, "trace", (long)CaptureType.NetworkTrace));
                var model = new CaptureAppModel();
                var paths = new List<string>();
                var loads = new CaptureFileLoads(model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)model.CaptureQueries, model.CaptureFileInputs, path =>
                    {
                        paths.Add(path);
                        return DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile(path);
                    });
                try
                {
                    var initial = Complete(loads.LoadDesktopAsync(first));
                    var old = model.CaptureDeviceContexts[0];
                    var web = model.CaptureDeviceContexts[1];
                    var trace = model.CaptureDeviceContexts[2];
                    var next = Complete(loads.LoadDesktopAsync(second));
                    Assert.Equal(0, next.AddedContexts);
                    Assert.Equal(2, next.UpdatedContexts);
                    Assert.NotEqual(initial.SourceId, next.SourceId);
                    Assert.Equal(new[] { old, web, trace }, model.CaptureDeviceContexts);
                    Assert.Single(((NetworkTraceCaptureController)trace.CaptureController).NetworkTracePackets);
                    Assert.Single(((WebServiceDeviceCaptureController)old.CaptureController).GetCaptureRecords());
                    Assert.True(File.Exists(paths[0]));
                    Assert.Equal(QueryError.SourceChanged, Assert.Throws<QueryException>(() =>
                        Complete(model.CaptureQueries.GetSchemaAsync(initial.SourceId))).Error);
                    Finish(model.RemoveDeviceContextAsync(old));
                    PumpUntil(() => !File.Exists(paths[0]));
                    Complete(loads.LoadDesktopAsync(second));
                    Assert.Equal(new[] { web, trace }, model.CaptureDeviceContexts);
                    Assert.Single(((NetworkTraceCaptureController)trace.CaptureController).NetworkTracePackets);
                    PumpUntil(() => !File.Exists(paths[1]));
                    Finish(model.RemoveDeviceContextAsync(web));
                    Finish(model.RemoveDeviceContextAsync(trace));
                    Assert.True(File.Exists(paths[2]));
                    Assert.NotEmpty(Complete(model.CaptureQueries.GetSchemaAsync(
                        Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id)));
                }
                finally
                {
                    Finish(loads.DisposeAsync().AsTask());
                    Assert.All(paths, path => Assert.False(Directory.Exists(Path.GetDirectoryName(path))));
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void NotificationFailuresAreCommittedWarningsAndObserversSeeOnlyTheCompleteLoad()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("web", CaptureType.WebProxy), ("trace", CaptureType.NetworkTrace)]);
                var model = new CaptureAppModel();
                var observed = 0;
                model.CaptureDeviceContexts.CollectionChanged += (_, _) =>
                {
                    Assert.NotNull(model.LoadedDatabase);
                    Assert.Equal(2, model.CaptureDeviceContexts.Count);
                    Assert.Single(((WebServiceDeviceCaptureController)model.CaptureDeviceContexts[0]
                        .CaptureController).ProxyConnections);
                    Assert.Single(((NetworkTraceCaptureController)model.CaptureDeviceContexts[1]
                        .CaptureController).NetworkTracePackets);
                    observed++;
                    throw new InvalidOperationException("PRIVATE observer failure");
                };
                try
                {
                    var receipt = Complete(model.CaptureFileLoads.LoadDesktopAsync(path));
                    Assert.Equal(1, observed);
                    Assert.True(receipt.Published);
                    Assert.False(receipt.ViewsNotified);
                    Assert.Equal(receipt.SourceId, Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id);
                    Assert.False(model.IsCaptureDataOperationRunning);
                    Assert.False(model.CaptureFileLoads.IsLoading);
                }
                finally
                {
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void SourceChangesDuringAdmissionNotificationAreRejectedBeforeAnyViewCommit()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var model = new CaptureAppModel();
                using var newer = new SqLtReadonlyDatabase(path);
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CaptureAppModel.IsCaptureDataOperationRunning) &&
                        model.IsCaptureDataOperationRunning)
                        model.LoadedDatabase = newer;
                };
                try
                {
                    Assert.Equal(QueryError.SourceChanged, Assert.Throws<QueryException>(() =>
                        Complete(model.CaptureFileLoads.LoadDesktopAsync(path))).Error);
                    Assert.Same(newer, model.LoadedDatabase);
                    Assert.Empty(model.CaptureDeviceContexts);
                    Assert.False(model.IsCaptureDataOperationRunning);
                    Assert.False(model.IsCaptureFileLoadRunning);
                }
                finally
                {
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void LeaseReleaseNotificationFailureDoesNotMisreportACommittedLoadAsInvalidInput()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var model = new CaptureAppModel();
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CaptureAppModel.IsCaptureDataOperationRunning) &&
                        !model.IsCaptureDataOperationRunning)
                        throw new InvalidOperationException("PRIVATE notification failure");
                };
                try
                {
                    var receipt = Complete(model.CaptureFileLoads.LoadDesktopAsync(path));
                    Assert.True(receipt.Published);
                    Assert.False(receipt.ViewsNotified);
                    Assert.Equal(receipt.SourceId, Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id);
                    Assert.Single(model.CaptureDeviceContexts);
                    Assert.False(model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("source")]
        [InlineData("contexts")]
        [InlineData("analysis")]
        [InlineData("dispose")]
        public void InterruptedOrSupersededPreparationPreservesDataAndNeverHoldsTheGlobalLease(string change)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var human = setup.Created.Single();
                setup.Model.SelectedDeviceContext = human;
                Finish(setup.Model.CaptureOperations.SetRunningAsync(human, true));
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("web", CaptureType.WebProxy)]);
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var cancellation = new CancellationTokenSource();
                string preparedPath = null;
                var loads = new CaptureFileLoads(setup.Model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)setup.Model.CaptureQueries, setup.Model.CaptureFileInputs, staged =>
                    {
                        preparedPath = staged;
                        started.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
                        return DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile(staged);
                    });
                IDisposable analysis = null;
                SqLtReadonlyDatabase replacement = null;
                Task disposing = null;
                try
                {
                    var load = loads.LoadDesktopAsync(path, cancellation.Token);
                    PumpUntil(() => started.IsSet);
                    Assert.True(loads.IsLoading);
                    Assert.False(setup.Model.IsCaptureDataOperationRunning);
                    Assert.False(Complete(setup.Model.CanCloseAsync()));
                    Finish(setup.Model.CaptureOperations.SetRunningAsync(human, false));
                    if (change == "cancel")
                        cancellation.Cancel();
                    else if (change == "source")
                        setup.Model.LoadedDatabase = replacement = new SqLtReadonlyDatabase(path);
                    else if (change == "contexts")
                        setup.Model.AddDeviceContext(DeviceType.Unknown, "new read-only view",
                            CaptureType.WebProxy, true, null);
                    else if (change == "analysis")
                        Assert.True(setup.Model.TryBeginAnalysis(out analysis));
                    else
                    {
                        disposing = loads.DisposeAsync().AsTask();
                        Assert.False(disposing.IsCompleted);
                    }
                    release.Set();
                    if (change is "cancel" or "dispose")
                        Assert.ThrowsAny<OperationCanceledException>(() => Complete(load));
                    else
                        Assert.Equal(change == "analysis" ? QueryError.SourceBusy : QueryError.SourceChanged,
                            Assert.Throws<QueryException>(() => Complete(load)).Error);
                    Assert.Same(human, setup.Model.SelectedDeviceContext);
                    Assert.DoesNotContain(setup.Model.CaptureDeviceContexts, context => context.DeviceName == "web");
                    Assert.False(Directory.Exists(Path.GetDirectoryName(preparedPath)));
                    Assert.False(loads.IsLoading);
                }
                finally
                {
                    release.Set();
                    analysis?.Dispose();
                    Finish(disposing ?? loads.DisposeAsync().AsTask());
                    replacement?.Dispose();
                }
            });
        }

        [Fact]
        public void ReadonlyTraceViewsDoNotObserveOrDeleteActiveTableRows()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                WriteCapture(path, [("test-device", CaptureType.NetworkTrace)]);
                var model = new CaptureAppModel();
                using var active = CaptureInvestigationTestData.NetworkDatabase();
                var method = NetworkTraceCaptureMethod.MethodInstance;
                var tableProperty = typeof(NetworkTraceCaptureMethod).GetProperty(
                    "NetworkTracePacketsTable", BindingFlags.Instance | BindingFlags.NonPublic);
                var previous = method.NetworkTracePacketsTable;
                tableProperty.SetValue(method, active.TableByName(NetworkTraceCaptureMethod.TableName));
                try
                {
                    Complete(model.CaptureFileLoads.LoadDesktopAsync(path));
                    var context = model.CaptureDeviceContexts.Single();
                    var trace = (NetworkTraceCaptureController)context.CaptureController;
                    CaptureInvestigationTestData.AddPacket(active, DateTime.UtcNow);
                    WpfTestHost.Pump();
                    Assert.Single(trace.NetworkTracePackets);
                    trace.ClearAllCaptures();
                    Assert.Empty(trace.NetworkTracePackets);
                    Assert.Single(active.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                }
                finally
                {
                    tableProperty.SetValue(method, previous);
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void NativeDalQuotesSchemaIdentifiersAndBindsDeviceNamesWithoutChangingTheirText()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "quoted.xmatcap");
            const string table = "odd\"; table";
            const string field = "select\"field";
            const string value = "double\" and 'single'";
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"odd\"\"; table\" (id INTEGER PRIMARY KEY AUTOINCREMENT, \"select\"\"field\" TEXT);" +
                    "INSERT INTO \"odd\"\"; table\" (\"select\"\"field\") VALUES ($value)";
                command.Parameters.AddWithValue("$value", value);
                command.ExecuteNonQuery();
            }
            using var loaded = new SqLtReadonlyDatabase(path);
            Assert.Equal(value, Assert.Single(loaded.TableByName(table).Dataset.DataRecordsWhen(
                new FieldValue<string>(field, value))).Str(field));
        }

        [Theory]
        [InlineData("CREATE TABLE CaptureDevices (DeviceType INTEGER, DeviceName TEXT, CaptureType INTEGER)")]
        [InlineData("CREATE TABLE CaptureDevices (id INTEGER PRIMARY KEY, DeviceType INTEGER, DeviceName TEXT, CaptureType INTEGER, Generated TEXT AS (printf('%s','computed')))")]
        [InlineData("CREATE TABLE CaptureDevices (id INTEGER, DeviceType INTEGER, DeviceName TEXT, CaptureType INTEGER, PRIMARY KEY(id,DeviceName))")]
        public void InvalidNativeSchemaIsRejectedWithoutEvaluatingRowsOrLeakingTheConnection(string schema)
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "invalid.xmatcap");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = schema;
                command.ExecuteNonQuery();
            }
            Assert.Throws<InvalidDataException>(() => new SqLtReadonlyDatabase(path));
            File.Delete(path);
            Assert.False(File.Exists(path));
        }

        internal static void WriteCapture(
            string path, (string Name, CaptureType Type)[] devices,
            bool invalidPacket = false, Action<SqLtDatabase> modify = null)
        {
            using var database = CaptureInvestigationTestData.WebDatabase();
            database.CreateTable("CaptureDevices", new Field<long>("DeviceType"),
                new Field<string>("DeviceName"), new Field<long>("CaptureType"));
            database.CreateTable(NetworkTraceCaptureMethod.TableName,
                new Field<string>("DeviceName"), new Field<int>("ProcessId"), new Field<int>("ThreadId"),
                new Field<string>("Timestamp"), new Field<string>("MediaType"), new Field<int>("StartPacket"),
                new Field<int>("EndPacket"), new Field<int>("Fragment"), new Field<int>("Send"),
                new Field<int>("Receive"), new Field<string>("Payload"));
            foreach (var device in devices)
            {
                database.TableByName("CaptureDevices").AddRow((long)DeviceType.Unknown, device.Name, (long)device.Type);
                if (device.Type == CaptureType.WebProxy)
                    CaptureInvestigationTestData.AddWeb(database, 1, device: device.Name);
                else
                    database.TableByName(NetworkTraceCaptureMethod.TableName).AddRow(device.Name, 1, 2,
                        "2026-01-01T00:00:00Z", "ethernet", 1, 1, 0, 1, 0, invalidPacket ? "!!!" : "AQI=");
            }
            modify?.Invoke(database);
            database.SaveToFile(path);
        }
    }
}
