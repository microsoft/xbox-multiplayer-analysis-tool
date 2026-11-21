// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AgentMutationProtectionTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void RealClearUsesTheAgentOriginAcrossDispatcherHopsAndBlocksOnBackupFailure(bool enabled, bool fail)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                database.CreateTable("CaptureDevices", new Field<long>("DeviceType"),
                    new Field<string>("DeviceName"), new Field<long>("CaptureType")).AddRow(0L, "test-device", 1L);
                var original = CaptureInvestigationTestData.AddPacket(database, DateTime.UtcNow);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var controller = new NetworkTraceCaptureController("test-device", DeviceType.LocalPC);
                controller.Initialize(database.TableByName(NetworkTraceCaptureMethod.TableName));
                var target = new Target(controller);
                model.CaptureDeviceContexts.Add(target);
                var state = model.CaptureCatalog.DescribeTarget(target);
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                var root = Path.Combine(directory.Path, "Backups");
                if (fail)
                    File.WriteAllText(root, "unavailable");
                var runtime = new AgentRuntime(policy, new AgentBackupStore(root));
                try
                {
                    Complete(policy.SaveAsync(new()
                    {
                        Enabled = true,
                        Actions = AgentActionAccess.FullControl,
                        AutomaticBackups = enabled
                    }, 0));
                    Finish(runtime.ApplyPolicyAsync());
                    runtime.Enable();
                    var owner = runtime.Connect("clear protection");
                    var operation = runtime.Submit(owner, new("capture.clear",
                        [new(AgentCapability.CaptureControl, AgentScope.Capture(state.Id))],
                        AgentOperationEffects.DataDeletion, "clear"),
                        "clear", async context => AgentJson.Encode(await model.CaptureDataOperations.ClearAsync(
                            state.Id, state.Revision, context.CancellationToken)), _ => Task.FromResult(true));
                    var confirmation = runtime.GetConfirmation(owner, operation);
                    runtime.Confirm(owner, operation, confirmation.ConfirmationToken, true);
                    Finish(runtime.WaitForCompletionAsync(owner, operation));
                    Assert.False(model.IsCaptureDataOperationRunning);
                    if (fail)
                    {
                        Assert.Equal(AgentAccessError.BackupFailed, runtime.GetOperation(owner, operation).Error);
                        Assert.Equal(original, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                        Assert.False(runtime.GetResult(owner, operation).GetProperty("protectedChangeStarted").GetBoolean());
                    }
                    else
                    {
                        Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, operation).State);
                        Assert.Empty(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                        if (enabled)
                        {
                            var receipt = runtime.GetResult(owner, operation).GetProperty("backups")[0];
                            var path = Path.Combine(receipt.GetProperty("directory").GetString(), "capture.xmatcap");
                            using var restored = new SqLtReadonlyDatabase(path);
                            Assert.Equal(original, restored.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                            Assert.Equal("Committed", receipt.GetProperty("actionStatus").GetString());
                        }
                        else
                            Assert.False(Directory.Exists(root));
                    }
                }
                finally
                {
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void StandaloneDesktopClearDoesNotCreateAgentBackups()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, DateTime.UtcNow);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var controller = new NetworkTraceCaptureController("test-device", DeviceType.LocalPC);
                controller.Initialize(database.TableByName(NetworkTraceCaptureMethod.TableName));
                var target = new Target(controller);
                model.CaptureDeviceContexts.Add(target);
                var state = model.CaptureCatalog.DescribeTarget(target);
                Assert.Null(AgentMutationProtection.Current);
                Complete(model.CaptureDataOperations.ClearAsync(state.Id, state.Revision));
                Assert.Empty(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
            });
        }

        [Fact]
        public void TraceImplicitResetBacksUpOldRowsAndFinalShutdownBacksUpPostStopPackets()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                database.CreateTable("CaptureDevices", new Field<long>("DeviceType"),
                    new Field<string>("DeviceName"), new Field<long>("CaptureType")).AddRow(0L, "test-device", 1L);
                var previous = CaptureInvestigationTestData.AddPacket(database, DateTime.UtcNow);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var factory = new FakeTraceEngineFactory();
                factory.Engines.Enqueue(new());
                var controller = new NetworkTraceCaptureController("test-device", DeviceType.LocalPC, factory);
                controller.Initialize(database.TableByName(NetworkTraceCaptureMethod.TableName));
                var target = new Target(controller);
                model.CaptureDeviceContexts.Add(target);
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, AutomaticBackups = true }, 0));
                    using (var startOrigin = new AgentMutationProtection(Guid.NewGuid(), policy, new(Path.Combine(directory.Path, "Backups"))))
                    {
                        Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);
                        Assert.Empty(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                        var backup = Assert.Single(startOrigin.Backups);
                        using var old = new SqLtReadonlyDatabase(Path.Combine(backup.Directory, "capture.xmatcap"));
                        Assert.Equal(previous, old.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                    }
                    var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                    shutdown.Configure(() => { });
                    using var stopOrigin = new AgentMutationProtection(Guid.NewGuid(), policy, new(Path.Combine(directory.Path, "Backups")));
                    var snapshot = Complete(shutdown.InspectAsync());
                    var plan = Complete(shutdown.PrepareAsync(snapshot.Revision, new() { DiscardCapturedData = true }, default));
                    var receipt = Complete(shutdown.ExecuteAsync(plan, default));
                    Assert.Equal(1, receipt.StoppedCaptures);
                    Assert.False(controller.IsRunning);
                    var final = Assert.Single(stopOrigin.Backups);
                    using (var restored = new SqLtReadonlyDatabase(Path.Combine(final.Directory, "capture.xmatcap")))
                        Assert.Equal(2, restored.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Count());
                    database.Shutdown();
                    Finish(shutdown.DatabaseDisposedAsync());
                    Assert.Equal("Committed", Assert.Single(stopOrigin.Backups).ActionStatus);
                }
                finally
                {
                    if (controller.IsRunning)
                        Complete(model.CaptureOperations.SetRunningAsync(target, false));
                    controller.Dispose();
                    Finish(policy.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InspectorCanCloseIsolatedViewsButActiveImportedRowsNeedFullControlAndBackup(bool native)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, native ? "capture.xmatcap" : "capture.saz");
                if (native)
                {
                    CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
                    Complete(setup.Model.CaptureFileLoads.LoadDesktopAsync(path));
                }
                else
                {
                    CaptureSazImportTests.WriteSaz(path);
                    Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                }
                var original = File.ReadAllBytes(path);
                var target = Assert.Single(setup.Model.CaptureDeviceContexts);
                var capture = setup.Model.CaptureCatalog.DescribeTarget(target);
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                var runtime = new AgentRuntime(policy, new AgentBackupStore(Path.Combine(directory.Path, "Backups")));
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, AutomaticBackups = true }, 0));
                    Finish(runtime.ApplyPolicyAsync());
                    runtime.Enable();
                    var session = runtime.Connect("view removal");
                    var adapter = new AgentDeviceApplication(runtime, setup.Devices, setup.Model.CaptureCatalog, () => true);
                    if (!native)
                    {
                        Assert.Equal(AgentAccessError.PermissionDenied, Assert.Throws<AgentAccessException>(() =>
                            Complete(adapter.RemoveAsync(session, capture.Id, capture.Revision, "inspect denied", default))).Error);
                        Assert.Single(setup.Database.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                        Complete(policy.SaveAsync(policy.Current.Settings with { Actions = AgentActionAccess.FullControl },
                            policy.Current.Settings.Revision));
                        Finish(runtime.ApplyPolicyAsync());
                    }
                    var id = Complete(adapter.RemoveAsync(session, capture.Id, capture.Revision, "close view", default));
                    var confirmation = runtime.GetConfirmation(session, id);
                    if (native)
                        Assert.Null(confirmation);
                    else
                        runtime.Confirm(session, id, Assert.IsType<AgentConfirmation>(confirmation).ConfirmationToken, true);
                    Finish(runtime.WaitForCompletionAsync(session, id));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, id).State);
                    Assert.Empty(setup.Model.CaptureDeviceContexts);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    if (native)
                        Assert.False(Directory.Exists(Path.Combine(directory.Path, "Backups")));
                    else
                    {
                        var receipt = runtime.GetResult(session, id).GetProperty("backups")[0];
                        using var old = new SqLtReadonlyDatabase(Path.Combine(receipt.GetProperty("directory").GetString(), "capture.xmatcap"));
                        Assert.Single(old.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                        Assert.Empty(setup.Database.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                    }
                }
                finally
                {
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureFileLoads.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void ReplacingAnImportedScopeBacksUpThePriorRowsBeforePublishingTheReplacement()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "capture.saz");
                CaptureSazImportTests.WriteSaz(path, "old.example");
                Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, AutomaticBackups = true }, 0));
                    using var protection = new AgentMutationProtection(Guid.NewGuid(), policy, new(Path.Combine(directory.Path, "Backups")));
                    CaptureSazImportTests.WriteSaz(path, "new.example");
                    Complete(setup.Model.CaptureSazImports.ImportDesktopAsync(path));
                    var receipt = Assert.Single(protection.Backups);
                    using var old = new SqLtReadonlyDatabase(Path.Combine(receipt.Directory, "capture.xmatcap"));
                    Assert.Equal("old.example", Assert.Single(old.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords).Str("RequestHost"));
                    Assert.Equal("new.example", Assert.Single(setup.Database.TableByName(WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords).Str("RequestHost"));
                    Assert.Equal("Committed", receipt.ActionStatus);
                }
                finally
                {
                    Finish(policy.DisposeAsync().AsTask());
                    Finish(setup.Model.CaptureSazImports.DisposeAsync().AsTask());
                }
            });
        }

        private sealed class Target(NetworkTraceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.NetworkTrace;
            public string DeviceName => "test-device";
            public bool IsReadOnly { get; set; }
            public bool IsSelected { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }
    }
}
