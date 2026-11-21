// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using XMAT.ApplicationServices;
using XMAT.NetworkTrace;
using XMAT.Operations.Captures;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureReadonlySourceTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData(".xmatcap")]
        [InlineData(".saz")]
        [InlineData(".etl")]
        public void SupportedFilesOpenIsolatedWithoutChangingActiveRowsSelectionDevicesOrRunningCapture(string extension)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var target = setup.Created.Single();
                var model = setup.Model;
                model.SelectedDeviceContext = target;
                CaptureInvestigationTestData.AddWeb(setup.Database, 7, device: target.DeviceName);
                var active = setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.Single();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture" + extension);
                if (extension == ".xmatcap")
                    CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                else if (extension == ".saz")
                    CaptureSazImportTests.WriteSaz(path);
                else
                    File.WriteAllText(path, "frozen event input");
                NetworkShapingMetadata expectedShaping = null;
                if (extension == ".saz")
                {
                    var shaping = new NetworkShapingController();
                    shaping.Apply(NetworkShapingProfile.Preset("min"));
                    expectedShaping = NetworkShapingMetadata.From(shaping, 1800);
                    using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update);
                    using Stream stream = archive.CreateEntry(NetworkShapingMetadata.ArchiveEntry).Open();
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(NetworkShapingMetadata.Serialize(expectedShaping)));
                    model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer());
                }
                var original = File.ReadAllBytes(path);
                var sources = new CaptureReadonlySources(model.CaptureFileInputs, model.CaptureFileLoads, (_, append, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    append(NetworkTraceTestData.Event(1));
                    return Task.CompletedTask;
                });
                Finish(model.CaptureOperations.SetRunningAsync(target, true));
                try
                {
                    var input = Complete(model.CaptureFileInputs.RegisterAsync(path, 1024 * 1024));
                    var state = Complete(model.CaptureFileLoads.GetStateAsync());
                    var receipt = Complete(sources.OpenInputAsync(input.Id, state, 1024 * 1024, _ => Task.CompletedTask));
                    Assert.True(receipt.Published);
                    Assert.False(receipt.ActiveDataChanged);
                    Assert.NotSame(setup.Database, model.LoadedDatabase);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    Assert.Same(target, model.SelectedDeviceContext);
                    Assert.True(target.CaptureController.IsRunning);
                    Assert.Single(model.CaptureDevicesTable.Dataset.DataRecords);
                    Assert.Equal(active.RowId, setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                    var loaded = Assert.Single(model.CaptureDeviceContexts, context => context.IsReadOnly);
                    Assert.False(loaded.CaptureController.IsRunning);
                    if (expectedShaping != null)
                    {
                        Assert.Equal(
                            NetworkShapingMetadata.Serialize(expectedShaping),
                            model.LoadedDatabase.TableByName("CaptureDevices").Dataset.DataRecords
                                .Single().Str(NetworkShapingMetadata.DeviceField));
                        var controller = Assert.IsType<WebServiceDeviceCaptureController>(loaded.CaptureController);
                        Assert.Equal(
                            NetworkShapingMetadata.Serialize(expectedShaping),
                            NetworkShapingMetadata.Serialize(controller.RecordedShaping));
                    }
                    var table = extension == ".etl" ? NetworkTraceCaptureMethod.TableName : WebServiceCaptureMethod.TableName;
                    var snapshot = Complete(model.CaptureQueries.CreateSessionAsync(receipt.SourceId));
                    try
                    {
                        var result = Complete(snapshot.ExecuteAsync($"SELECT count(*) FROM \"{table}\"", new(TimeSpan.FromSeconds(10), 65536)));
                        try
                        { Assert.Equal(1L, Complete(result.ReadPageAsync(0, 1, 8192)).Rows[0].Cells[0].Value); }
                        finally { Finish(result.DisposeAsync().AsTask()); }
                    }
                    finally { Finish(snapshot.DisposeAsync().AsTask()); }
                    if (extension == ".saz")
                    {
                        Finish(model.CaptureOperations.SetRunningAsync(target, false));
                        var analysis = Complete(model.CaptureInvestigation.AnalyzeAsync(
                            receipt.SourceId, CaptureDataKind.WebProxy, null,
                            new QueryExecutionOptions(TimeSpan.FromSeconds(30), 4 * 1024 * 1024)));
                        try
                        {
                            var manifest = Complete(analysis.Details.GetReportManifestAsync(
                                4 * 1024 * 1024));
                            Assert.Contains(manifest, artifact => artifact.RelativePath == "network-shaping.json");
                        }
                        finally { Finish(analysis.DisposeAsync().AsTask()); }
                    }
                }
                finally
                {
                    Finish(model.CaptureOperations.SetRunningAsync(target, false));
                    Finish(sources.DisposeAsync().AsTask());
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedOrCancelledPreparationPreservesThePreviousLoadedSourceAndItsViews(bool cancelled)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var model = setup.Model;
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var native = Path.Combine(directory.Path, "original.xmatcap");
                CaptureFileLoadTests.WriteCapture(native, [("original", CaptureType.WebProxy)]);
                Complete(model.CaptureFileLoads.LoadDesktopAsync(native));
                var previous = model.LoadedDatabase;
                var previousContext = model.CaptureDeviceContexts.Single();
                var state = Complete(model.CaptureFileLoads.GetStateAsync());
                var path = Path.Combine(directory.Path, cancelled ? "cancel.etl" : "invalid.saz");
                File.WriteAllText(path, "not an archive");
                using var cancellation = new CancellationTokenSource();
                var sources = new CaptureReadonlySources(model.CaptureFileInputs, model.CaptureFileLoads, (_, append, _) =>
                {
                    append(NetworkTraceTestData.Event(1));
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
                try
                {
                    var input = Complete(model.CaptureFileInputs.RegisterAsync(path, 65536));
                    var open = sources.OpenInputAsync(input.Id, state, 65536, _ => Task.CompletedTask, cancellation.Token);
                    if (cancelled)
                        Assert.ThrowsAny<OperationCanceledException>(() => Complete(open));
                    else
                        Assert.Equal(CaptureFileFailure.InvalidCapture,
                            Assert.Throws<CaptureFileException>(() => Complete(open)).Failure);
                    Assert.Same(previous, model.LoadedDatabase);
                    Assert.Same(previousContext, Assert.Single(model.CaptureDeviceContexts));
                    Assert.Equal(state, Complete(model.CaptureFileLoads.GetStateAsync()));
                    Assert.False(model.IsCaptureDataOperationRunning);
                    Assert.False(model.IsCaptureFileLoadRunning);
                }
                finally
                {
                    Finish(sources.DisposeAsync().AsTask());
                    Finish(model.CaptureFileLoads.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void TypedOverwriteCannotReplaceTheOriginalLoadedInputOrItsHardlink()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "original.xmatcap");
                var alias = Path.Combine(directory.Path, "alias.xmatcap");
                CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                var original = File.ReadAllBytes(path);
                try
                {
                    Complete(setup.Model.CaptureFileLoads.LoadDesktopAsync(path));
                    Assert.True(CreateHardLink(alias, path, IntPtr.Zero));
                    foreach (var destination in new[] { path, alias })
                        Assert.Equal(CaptureFileFailure.ProtectedSource, Assert.Throws<CaptureFileException>(() =>
                            Complete(setup.Model.CaptureFileExports.PrepareDestinationAsync(destination, true))).Failure);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    Assert.Equal(original, File.ReadAllBytes(alias));
                }
                finally { Finish(setup.Model.CaptureFileLoads.DisposeAsync().AsTask()); }
            });
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string link, string existing, IntPtr security);
    }
}
