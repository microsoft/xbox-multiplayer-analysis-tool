// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureArchiveTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void NativeArchiveContainsAllSourceTablesAndDoesNotFollowLaterCaptureChanges()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                database.CreateTable("CaptureDevices", new Field<long>("DeviceType"),
                    new Field<string>("DeviceName"), new Field<long>("CaptureType"));
                database.TableByName("CaptureDevices").AddRow(0L, "first", 0L);
                CaptureInvestigationTestData.AddWeb(database, 1, device: "first");
                var model = new CaptureAppModel { ActiveDatabase = database };
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single();
                var archive = Complete(model.CaptureFiles.CreateArchiveAsync(
                    source.Id, CaptureArchiveFormat.CaptureDatabase, 2 * 1024 * 1024));
                try
                {
                    CaptureInvestigationTestData.AddWeb(database, 2, device: "later");
                    var folder = Complete(model.CaptureFileExports.RegisterFolderAsync(directory.Path));
                    var length = Complete(model.CaptureFileExports.SaveCaptureAsync(archive,
                        new(folder.Id, "capture.xmatcap", CaptureArchiveFormat.CaptureDatabase),
                        2 * 1024 * 1024, _ => Task.CompletedTask));
                    using var loaded = new SqLtReadonlyDatabase(Path.Combine(directory.Path, "capture.xmatcap"));
                    Assert.Single(loaded.TableByName("CaptureDevices").Dataset.DataRecords);
                    Assert.Single(loaded.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    Assert.Equal(archive.Receipt.Length, length);
                    Assert.False(model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    Finish(archive.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void LoadedNativeBackupUsesTheReadonlyDalAndPreservesTheOriginalCapture()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.WebDatabase();
                database.CreateTable("CaptureDevices", new Field<long>("DeviceType"),
                    new Field<string>("DeviceName"), new Field<long>("CaptureType"));
                database.TableByName("CaptureDevices").AddRow(0L, "loaded-device", 0L);
                CaptureInvestigationTestData.AddWeb(database, 1, device: "loaded-device");
                var original = Path.Combine(directory.Path, "source.xmatcap");
                database.SaveToFile(original);
                var originalBytes = File.ReadAllBytes(original);
                using var loaded = new SqLtReadonlyDatabase(original);
                CaptureInvestigationTestData.AddWeb(database, 2, device: "active-only");
                var model = new CaptureAppModel { ActiveDatabase = database, LoadedDatabase = loaded };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single(item => item.Kind == QuerySourceKind.Loaded);
                var archive = Complete(model.CaptureFiles.CreateArchiveAsync(
                    source.Id, CaptureArchiveFormat.CaptureDatabase, 2 * 1024 * 1024));
                try
                {
                    var folder = Complete(model.CaptureFileExports.RegisterFolderAsync(directory.Path));
                    Complete(model.CaptureFileExports.SaveCaptureAsync(archive,
                        new(folder.Id, "loaded-copy.xmatcap", CaptureArchiveFormat.CaptureDatabase),
                        2 * 1024 * 1024, _ => Task.CompletedTask));
                    using var saved = new SqLtReadonlyDatabase(Path.Combine(directory.Path, "loaded-copy.xmatcap"));
                    Assert.Equal("loaded-device", Assert.Single(
                        saved.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords)
                        .Str(WebServiceCaptureMethod.FieldKey_DeviceName));
                    Assert.Single(saved.TableByName("CaptureDevices").Dataset.DataRecords);
                    Assert.Equal(originalBytes, File.ReadAllBytes(original));
                    Assert.Throws<ArgumentException>(() => loaded.SaveToFile(original));
                    Assert.Equal(originalBytes, File.ReadAllBytes(original));
                    Assert.Null(model.SelectedDeviceContext);
                    Assert.False(model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    Finish(archive.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void RawSazIncludesEveryActiveDeviceAndKeepsUnsanitizedPayloads()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, device: "first");
                CaptureInvestigationTestData.AddWeb(database, 2, device: "second");
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single();
                var archive = Complete(model.CaptureFiles.CreateArchiveAsync(source.Id, CaptureArchiveFormat.FiddlerSaz, 1024 * 1024));
                try
                {
                    using var output = new MemoryStream();
                    Finish(archive.CopyToAsync(output, 1024 * 1024));
                    output.Position = 0;
                    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
                    Assert.Equal(6, zip.Entries.Count);
                    var request = zip.Entries.First(entry => entry.Name.EndsWith("_c.txt", StringComparison.Ordinal));
                    using var reader = new StreamReader(request.Open());
                    Assert.Contains(CaptureInvestigationTestData.Secret, reader.ReadToEnd());
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally
                {
                    Finish(archive.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SanitizedSazUsesExactSourceAndDeviceFilterWithoutReadingTheSelectedTab(bool loadedSource)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1, device: "first");
                CaptureInvestigationTestData.AddWeb(database, 2, device: "second");
                var model = new CaptureAppModel { ActiveDatabase = database };
                SqLtReadonlyDatabase loaded = null;
                if (loadedSource)
                {
                    var path = Path.Combine(directory.Path, "source.xmatcap");
                    database.SaveToFile(path);
                    loaded = new SqLtReadonlyDatabase(path);
                    model.LoadedDatabase = loaded;
                    CaptureInvestigationTestData.AddWeb(database, 3, device: "first");
                }
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single(item =>
                    item.Kind == (loadedSource ? QuerySourceKind.Loaded : QuerySourceKind.Active));
                var archive = Complete(model.CaptureFiles.CreateSazArchiveAsync(
                    source.Id, CaptureArchiveFormat.SanitizedFiddlerSaz, "first", 1048576));
                try
                {
                    Assert.Equal("first", archive.Receipt.DeviceName);
                    Assert.Equal(1, archive.Receipt.Sanitization.Sessions);
                    Assert.True(archive.Receipt.Sanitization.RedactedValues > 0);
                    using var output = new MemoryStream();
                    Finish(archive.CopyToAsync(output, 1048576));
                    output.Position = 0;
                    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
                    Assert.Equal(3, zip.Entries.Count);
                    foreach (var entry in zip.Entries)
                    {
                        using var reader = new StreamReader(entry.Open());
                        var text = reader.ReadToEnd();
                        Assert.DoesNotContain(CaptureInvestigationTestData.Secret, text);
                        Assert.DoesNotContain("10.0.0.1", text);
                    }
                    Assert.Null(model.SelectedDeviceContext);
                }
                finally
                {
                    Finish(archive.DisposeAsync().AsTask());
                    loaded?.Dispose();
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void SazDeviceFilterDoesNotApplyToNativeWholeDatabaseArchives()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single();
                try
                {
                    Assert.Equal(CaptureFileFailure.InvalidPath, Assert.Throws<CaptureFileException>(() => Complete(
                        model.CaptureFiles.CreateSazArchiveAsync(source.Id, CaptureArchiveFormat.CaptureDatabase,
                            "first", 1048576))).Failure);
                }
                finally { Finish(model.AgentAccess.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void RunningCaptureAndReplacedSourcesCannotBeArchived()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var source = Complete(setup.Model.CaptureQueries.GetSourcesAsync()).Single();
                Finish(setup.Model.CaptureOperations.SetRunningAsync(setup.Created.Single(), true));
                Assert.Equal(QueryError.SourceBusy, Assert.Throws<QueryException>(() => Complete(
                    setup.Model.CaptureFiles.CreateArchiveAsync(source.Id, CaptureArchiveFormat.CaptureDatabase, 1048576))).Error);
                Finish(setup.Model.CaptureOperations.SetRunningAsync(setup.Created.Single(), false));
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                setup.Model.ActiveDatabase = replacement;
                Assert.Equal(QueryError.SourceChanged, Assert.Throws<QueryException>(() => Complete(
                    setup.Model.CaptureFiles.CreateArchiveAsync(source.Id, CaptureArchiveFormat.CaptureDatabase, 1048576))).Error);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
                setup.Model.ActiveDatabase = setup.Database;
            });
        }

        [Fact]
        public void ArchiveQuotaFailureAndCancelledDesktopSavePreserveExistingDestination()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.xmatcap");
                File.WriteAllText(path, "human");
                try
                {
                    Assert.Equal(QueryError.ResultLimit, Assert.Throws<QueryException>(() => Complete(
                        model.CaptureFiles.CreateArchiveAsync(source.Id, CaptureArchiveFormat.CaptureDatabase, 1))).Error);
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => Finish(
                        model.CaptureFiles.SaveDesktopAsync(source.Id, CaptureArchiveFormat.CaptureDatabase, path, cancellation.Token)));
                    Assert.Equal("human", File.ReadAllText(path));
                    Assert.Single(Directory.GetFiles(directory.Path));
                }
                finally { Finish(model.AgentAccess.DisposeAsync().AsTask()); }
            });
        }
    }
}
