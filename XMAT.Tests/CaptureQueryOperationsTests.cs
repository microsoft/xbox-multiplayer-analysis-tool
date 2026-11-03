// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows.Controls;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureQueryOperationsTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CaptureQueryOperationsTests(CaptureUiApplicationFixture application) =>
            _application = application;

        [Fact]
        public void SourceIdentityFollowsTheActualDatabaseNotSelectionOrLoadedFilename()
        {
            _application.Run(_ =>
            {
                using var first = Database("before");
                using var second = Database("after");
                var directory = Path.Combine(AppContext.BaseDirectory, $"CaptureQueryFixture-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                try
                {
                    var path = Path.Combine(directory, "saved.xmatcap");
                    first.SaveToFile(path);
                    using var loaded = new SqLtReadonlyDatabase(path);
                    using var reloaded = new SqLtReadonlyDatabase(path);
                    var model = new CaptureAppModel { ActiveDatabase = first, LoadedDatabase = loaded };
                    var queries = model.CaptureQueries;
                    var sources = Complete(queries.GetSourcesAsync());
                    Assert.Null(model.SelectedDeviceContext);
                    var activeId = sources.Single(item => item.Kind == QuerySourceKind.Active).Id;
                    var loadedId = sources.Single(item => item.Kind == QuerySourceKind.Loaded).Id;
                    Assert.NotEqual(activeId, loadedId);
                    var snapshot = Complete(Task.Run(() => queries.CreateSessionAsync(activeId)));
                    try
                    {
                        Assert.False(model.IsCaptureDataOperationRunning);
                        model.ActiveDatabase = second;
                        model.LoadedDatabase = reloaded;
                        var current = Complete(queries.GetSourcesAsync());
                        Assert.DoesNotContain(current, item => item.Id == activeId || item.Id == loadedId);
                        Assert.Equal(QueryError.SourceChanged,
                            Assert.Throws<QueryException>(() => Complete(queries.GetSchemaAsync(activeId))).Error);
                        Assert.Equal(QueryError.SourceChanged,
                            Assert.Throws<QueryException>(() => Complete(queries.CreateSessionAsync(loadedId))).Error);
                        var result = Complete(snapshot.ExecuteAsync("SELECT Value FROM Data",
                            new QueryExecutionOptions(TimeSpan.FromSeconds(5))));
                        try
                        {
                            Assert.Equal("before", Complete(result.ReadPageAsync(0, 1, 8192)).Rows[0].Cells[0].Value);
                        }
                        finally { Complete(result.DisposeAsync().AsTask()); }
                        model.ActiveDatabase = first;
                        Assert.Equal(activeId, Complete(queries.GetSourcesAsync()).Single(item => item.Kind == QuerySourceKind.Active).Id);
                        Assert.Equal("before", loaded.TableByName("Data").Dataset.DataRecords.Single().Str("Value"));
                    }
                    finally { Complete(snapshot.DisposeAsync().AsTask()); }
                }
                finally { Directory.Delete(directory, recursive: true); }
            });
        }

        [Fact]
        public void SnapshotUsesTheExistingDataLeaseButReleasesItBeforeSqlExecution()
        {
            _application.Run(_ =>
            {
                using var database = Database("value");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var queries = model.CaptureQueries;
                var source = Complete(queries.GetSourcesAsync()).Single().Id;
                Assert.True(model.TryBeginCaptureDataOperation(out var busy));
                using (busy)
                {
                    Assert.Equal(QueryError.SourceBusy,
                        Assert.Throws<QueryException>(() => Complete(queries.GetSchemaAsync(source))).Error);
                    Assert.Equal(QueryError.SourceBusy,
                        Assert.Throws<QueryException>(() => Complete(queries.CreateSessionAsync(source))).Error);
                    Assert.True(model.IsCaptureDataOperationRunning);
                }
                var snapshot = Complete(queries.CreateSessionAsync(source));
                try
                {
                    Assert.False(model.IsCaptureDataOperationRunning);
                    Assert.True(model.TryBeginCaptureDataOperation(out var next));
                    using (next)
                    {
                        var result = Complete(snapshot.ExecuteAsync("SELECT Value FROM Data",
                            new QueryExecutionOptions(TimeSpan.FromSeconds(5))));
                        try
                        {
                            Assert.Equal("value", Complete(result.ReadPageAsync(0, 1, 8192)).Rows[0].Cells[0].Value);
                        }
                        finally { Complete(result.DisposeAsync().AsTask()); }
                    }
                }
                finally { Complete(snapshot.DisposeAsync().AsTask()); }
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Null(model.SelectedDeviceContext);
            });
        }

        [Fact]
        public void DesktopSourceChoicesRefreshWhenTheApplicationReplacesItsDatabase()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var first = Database("before");
                using var second = Database("after");
                var model = new CaptureAppModel { ActiveDatabase = first };
                var host = model.AgentAccess;
                AgentAccessWindow manager = null;
                try
                {
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    WpfTestHost.Pump();
                    var sources = (ComboBox)manager.FindName("QuerySourceTarget");
                    var original = Assert.Single(sources.Items.Cast<QuerySourceDescriptor>()).Id;
                    model.ActiveDatabase = second;
                    WpfTestHost.Pump();
                    var replacement = Assert.Single(sources.Items.Cast<QuerySourceDescriptor>()).Id;
                    Assert.NotEqual(original, replacement);
                    Assert.Equal(replacement, ((QuerySourceDescriptor)sources.SelectedItem).Id);
                    model.ActiveDatabase = null;
                    WpfTestHost.Pump();
                    Assert.Empty(sources.Items);
                    Assert.False(((CheckBox)manager.FindName("RawQueryGrant")).IsEnabled);
                    Assert.False(((CheckBox)manager.FindName("QueryExportGrant")).IsEnabled);
                }
                finally
                {
                    manager?.Close();
                    Complete(host.DisposeAsync().AsTask());
                }
            });
        }

        private static SqLtDatabase Database(string value)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable("Data", new Field<string>("Value")).AddRow(value);
            return database;
        }

        private static T Complete<T>(Task<T> task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The query operation did not complete.");
            return task.GetAwaiter().GetResult();
        }

        private static void Complete(Task task) => Complete(Observe(task));
        private static async Task<bool> Observe(Task task) { await task; return true; }
    }
}
