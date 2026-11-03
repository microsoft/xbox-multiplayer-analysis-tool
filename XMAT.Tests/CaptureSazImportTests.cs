// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureSazImportTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CompleteImportsPreserveBoundSelectionAndUnrelatedDataAndReplaceWithoutAppending(bool humanSelected)
        {
            application.Run(owner =>
            {
                using var setup = new CaptureDeviceTestData();
                var model = setup.Model;
                if (humanSelected)
                {
                    setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                    model.SelectedDeviceContext = setup.Created.Single();
                }
                var selection = model.SelectedDeviceContext;
                var tabs = new TabControl { ItemsSource = model.CaptureDeviceContexts, IsSynchronizedWithCurrentItem = true };
                tabs.SetBinding(TabControl.SelectedItemProperty, new Binding(nameof(CaptureAppModel.SelectedDeviceContext))
                { Source = model, Mode = BindingMode.TwoWay });
                WpfTestHost.Show(owner, tabs);
                CaptureInvestigationTestData.AddWeb(setup.Database, 9, device: "unrelated");
                var table = setup.Database.TableByName(WebServiceCaptureMethod.TableName);
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                WriteSaz(path);
                var original = File.ReadAllBytes(path);
                try
                {
                    var receipt = Complete(model.CaptureSazImports.ImportDesktopAsync(path));
                    Assert.True(receipt.Published);
                    Assert.True(receipt.ViewsNotified);
                    Assert.True(receipt.AddedContext);
                    Assert.Equal(1, receipt.RecordCount);
                    Assert.Same(selection, model.SelectedDeviceContext);
                    Assert.Same(selection, tabs.SelectedItem);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    var context = model.CaptureDeviceContexts.Single(item => item.IsReadOnly);
                    var controller = (WebServiceDeviceCaptureController)context.CaptureController;
                    Assert.False(controller.HasPortReservation);
                    Assert.Empty(controller.Scripts.ActiveScripts);
                    Assert.Equal(Secret, Encoding.UTF8.GetString(Assert.Single(controller.ProxyConnections).RequestBody));
                    Assert.Single(controller.GetCaptureRecords());
                    Assert.Equal(humanSelected ? 1 : 0, setup.Model.CaptureDevicesTable.Dataset.DataRecords.Count());
                    WriteSaz(path, "replacement.example");
                    var next = Complete(model.CaptureSazImports.ImportDesktopAsync(path));
                    Assert.False(next.AddedContext);
                    Assert.Equal(receipt.CaptureId, next.CaptureId);
                    Assert.Equal("replacement.example", Assert.Single(controller.ProxyConnections).Host);
                    Assert.Equal("replacement.example", Assert.Single(controller.GetCaptureRecords()).Str("RequestHost"));
                    Assert.Equal(2, table.Dataset.DataRecords.Count());
                    using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
                    { }
                    var empty = Complete(model.CaptureSazImports.ImportDesktopAsync(path));
                    Assert.Equal(0, empty.RecordCount);
                    Assert.Empty(controller.ProxyConnections);
                    Assert.Equal("unrelated", Assert.Single(table.Dataset.DataRecords).Str("DeviceName"));
                }
                finally { Finish(model.CaptureSazImports.DisposeAsync().AsTask()); }
            });
        }

        [Theory]
        [InlineData("archive")]
        [InlineData("transaction")]
        [InlineData("stored-limit")]
        public void InvalidOrFailedImportsPreserveExactOldRowsViewsAndSelection(string failure)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                WriteSaz(path);
                Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                var context = setup.Model.CaptureDeviceContexts.Single();
                setup.Model.SelectedDeviceContext = context;
                var controller = (WebServiceDeviceCaptureController)context.CaptureController;
                var original = controller.ProxyConnections;
                var table = setup.Database.TableByName(WebServiceCaptureMethod.TableName);
                var rows = Snapshot(table);
                try
                {
                    if (failure == "archive")
                        File.WriteAllText(path, "not an archive");
                    else
                        WriteSaz(path, "replacement.example", failure == "stored-limit" ? new byte[12000] : null);
                    if (failure == "transaction")
                        ExecuteSql(setup.Database, "CREATE TRIGGER FailImport AFTER INSERT ON WebProxyConnections " +
                            "BEGIN SELECT RAISE(ABORT,'PRIVATE persistence failure'); END;");
                    if (failure == "stored-limit")
                    {
                        var input = Complete(setup.Model.CaptureFileInputs.RegisterAsync(path, 16384));
                        var target = Complete(setup.Model.CaptureSazImports.GetTargetAsync(input.FileName));
                        Assert.Equal(QueryError.ResultLimit, Assert.Throws<QueryException>(() =>
                            Complete(setup.Model.CaptureSazImports.ImportInputAsync(input.Id, target, 16384,
                                _ => Task.CompletedTask))).Error);
                    }
                    else
                        Assert.Equal(CaptureFileFailure.InvalidCapture, Assert.Throws<CaptureFileException>(() =>
                            Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path))).Failure);
                    Assert.Same(context, Assert.Single(setup.Model.CaptureDeviceContexts));
                    Assert.Same(context, setup.Model.SelectedDeviceContext);
                    Assert.Same(original, controller.ProxyConnections);
                    Assert.Equal(rows, Snapshot(table));
                    Assert.False(setup.Model.IsCaptureDataOperationRunning);
                    Assert.False(setup.Model.IsCaptureFileLoadRunning);
                }
                finally { Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void CancellationAfterTheLastRowStillRollsBackTheWholeTransaction()
        {
            using var database = CaptureInvestigationTestData.WebDatabase();
            CaptureInvestigationTestData.AddWeb(database, 1, device: "imported");
            var table = database.TableByName(WebServiceCaptureMethod.TableName);
            var original = Snapshot(table);
            using var cancellation = new CancellationTokenSource();
            // Materialize the replacement before the delete begins.
            var values = new ProxyConnectionModel(Assert.Single(table.Dataset.DataRecords)).ToDataTableValues("imported");
            IEnumerable<object[]> Replacement()
            {
                yield return values;
                cancellation.Cancel();
            }
            Assert.ThrowsAny<OperationCanceledException>(() => ((IImportedCaptureTable)table).ReplaceImportedRows(
                new FieldValue<string>("DeviceName", "imported"), Replacement(), cancellation.Token));
            Assert.Equal(original, Snapshot(table));
        }

        [Fact]
        public void ACommittedTransactionPublishesItsViewEvenWhenCancellationArrivesBeforeTheProviderReturns()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                WriteSaz(path);
                using var committed = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var cancellation = new CancellationTokenSource();
                var observed = new ObservedTable(setup.Database.TableByName(WebServiceCaptureMethod.TableName), () =>
                {
                    committed.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
                });
                var imports = new CaptureSazImports(setup.Model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)setup.Model.CaptureQueries, setup.Model.CaptureFileInputs, () => observed);
                try
                {
                    var task = imports.ImportDesktopAsync(path, cancellation.Token);
                    PumpUntil(() => committed.IsSet);
                    Assert.Empty(setup.Model.CaptureDeviceContexts);
                    Assert.True(setup.Model.IsCaptureDataOperationRunning);
                    Assert.False(Complete(setup.Model.CanCloseAsync()));
                    cancellation.Cancel();
                    release.Set();
                    var receipt = Complete(task);
                    Assert.True(receipt.Published);
                    var context = Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.Single(((WebServiceDeviceCaptureController)context.CaptureController).GetCaptureRecords());
                    Assert.Single(observed.Dataset.DataRecords);
                    Assert.False(setup.Model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    release.Set();
                    Finish(imports.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void NotificationsObserveCompleteCommittedDataAndFailuresAreReportedAsPublishedWarnings()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                WriteSaz(path);
                var seen = 0;
                setup.Model.CaptureDeviceContexts.CollectionChanged += (_, _) =>
                {
                    var context = Assert.Single(setup.Model.CaptureDeviceContexts);
                    Assert.Single(((WebServiceDeviceCaptureController)context.CaptureController).GetCaptureRecords());
                    Assert.Single(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    seen++;
                    throw new InvalidOperationException("PRIVATE UI failure");
                };
                try
                {
                    var receipt = Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                    Assert.True(receipt.Published);
                    Assert.False(receipt.ViewsNotified);
                    Assert.Equal(1, seen);
                }
                finally { Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void RunningCapturesAndWritableNameCollisionsCannotBeOverwrittenByImports()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                WriteSaz(path);
                var human = setup.Created.Single();
                Finish(setup.Model.CaptureOperations.SetRunningAsync(human, true));
                Assert.Equal(QueryError.SourceBusy, Assert.Throws<QueryException>(() =>
                    Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path))).Error);
                Assert.True(human.CaptureController.IsRunning);
                Finish(setup.Model.CaptureOperations.SetRunningAsync(human, false));
                var collision = new CaptureDeviceTestData.DeviceContext(DeviceType.Unknown, "Fiddler - capture.saz",
                    CaptureType.WebProxy, setup.Pool, setup.Database, setup.Model);
                setup.Model.CaptureDeviceContexts.Add(collision);
                try
                {
                    Assert.Equal(QueryError.SourceBusy, Assert.Throws<QueryException>(() =>
                        Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path))).Error);
                    Assert.DoesNotContain(setup.Model.CaptureDeviceContexts, context => context.IsReadOnly);
                    Assert.Empty(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                }
                finally
                {
                    Finish(collision.Proxy.CloseAsync());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void RebindingANativeViewToImportedActiveRowsReleasesOnlyItsUnusedOldDataset()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var native = Path.Combine(directory.Path, "native.xmatcap");
                var other = Path.Combine(directory.Path, "other.xmatcap");
                var saz = Path.Combine(directory.Path, "capture.saz");
                CaptureFileLoadTests.WriteCapture(native, [("Fiddler - capture.saz", CaptureType.WebProxy)]);
                CaptureFileLoadTests.WriteCapture(other, [("unrelated", CaptureType.WebProxy)]);
                WriteSaz(saz);
                var paths = new List<string>();
                var loads = new CaptureFileLoads(setup.Model, Dispatcher.CurrentDispatcher,
                    (CaptureQueries)setup.Model.CaptureQueries, setup.Model.CaptureFileInputs, path =>
                    {
                        paths.Add(path);
                        return DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile(path);
                    });
                try
                {
                    Complete(loads.LoadDesktopAsync(native));
                    var original = setup.Model.CaptureDeviceContexts.Single();
                    Complete(loads.LoadDesktopAsync(other));
                    Assert.True(File.Exists(paths[0]));
                    var receipt = Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(saz));
                    Assert.False(receipt.AddedContext);
                    PumpUntil(() => !File.Exists(paths[0]));
                    Assert.True(File.Exists(paths[1]));
                    Assert.Equal("import.example", Assert.Single(
                        ((WebServiceDeviceCaptureController)original.CaptureController).GetCaptureRecords()).Str("RequestHost"));
                }
                finally
                {
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                    Finish(loads.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("expanded")]
        [InlineData("entries")]
        [InlineData("xml")]
        public void ArchiveExpansionMetadataAndCancellationAreBoundedBeforePublication(string kind)
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "capture.saz");
            WriteSaz(path, requestBody: kind == "expanded" ? new byte[200000] : null);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                if (kind == "entries")
                    for (var index = 0; index < 70; index++)
                        archive.CreateEntry($"unused/{index}");
                else if (kind == "xml")
                {
                    var metadata = archive.Entries.Single(entry => entry.Name.EndsWith("_m.xml", StringComparison.Ordinal));
                    metadata.Delete();
                    using var writer = new StreamWriter(archive.CreateEntry("raw/1_m.xml").Open());
                    writer.Write("<!DOCTYPE Session [<!ENTITY x 'private'>]><Session>&x;</Session>");
                }
            }
            if (kind == "xml")
                Assert.Throws<System.Xml.XmlException>(() => FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            else
                Assert.Equal(QueryError.ResultLimit, Assert.Throws<QueryException>(() =>
                    FiddlerSazHandler.ImportSazFile(path, 16384)).Error);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => FiddlerSazHandler.ImportSazFile(path, canceled.Token));
        }

        internal const string Secret = "PRIVATE_IMPORTED_BODY";
        internal static void WriteSaz(string path, string host = "import.example", byte[] requestBody = null)
        {
            var connection = new ProxyConnectionModel(1, "200", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(20), "https", host + ":443", "GET", "/items",
                $"GET https://{host}/items HTTP/1.1\r\nHost: {host}\r\nAuthorization: {Secret}\r\n\r\n",
                requestBody ?? Encoding.UTF8.GetBytes(Secret),
                "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n\r\n",
                Encoding.UTF8.GetBytes("private response"), "10.0.0.1");
            FiddlerSazHandler.CreateSazFile(path, new[] { connection });
        }

        private static string[] Snapshot(IDataTable table) => table.Dataset.DataRecords.Select(record =>
            string.Join("|", new[] { record.RowId.ToString(), record.Str("DeviceName"), record.Str("RequestHost"),
                record.Str("RequestBody"), record.Str("ResponseBody") })).ToArray();

        private static void ExecuteSql(SqLtDatabase database, string sql)
        {
            var connection = (SqliteConnection)typeof(SqLtDatabase).GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(database);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private sealed class ObservedTable(IDataTable inner, Action afterCommit) : IDataTable, IImportedCaptureTable
        {
            public string Name => inner.Name;
            public IDataset Dataset => inner.Dataset;
            public event Action<IDataTable, long> RowAdded { add => inner.RowAdded += value; remove => inner.RowAdded -= value; }
            public event Action<IDataTable, long> RowUpdated { add => inner.RowUpdated += value; remove => inner.RowUpdated -= value; }
            public event Action<IDataTable, long> RowsRemoved { add => inner.RowsRemoved += value; remove => inner.RowsRemoved -= value; }
            public IReadOnlyList<long> ReplaceImportedRows(
                IFieldValue capture, IEnumerable<object[]> rows, CancellationToken cancellationToken = default)
            {
                var ids = ((IImportedCaptureTable)inner).ReplaceImportedRows(capture, rows, cancellationToken);
                afterCommit();
                return ids;
            }
            public IDataset Subset(params string[] fieldNames) => inner.Subset(fieldNames);
            public long AddRow(params object[] values) => inner.AddRow(values);
            public void UpdateRow(long rowId, params IFieldValue[] values) => inner.UpdateRow(rowId, values);
            public void RemoveRowsWhere(params IFieldValue[] values) => inner.RemoveRowsWhere(values);
        }
    }
}
