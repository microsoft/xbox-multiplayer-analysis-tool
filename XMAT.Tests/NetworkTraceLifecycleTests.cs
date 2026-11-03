// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows;
using NetworkTraceCaptureControls;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkTraceLifecycleTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void RepeatedTraceSessionsPublishOnlyTheirOwnCompleteDataAndPreserveFullTimestamps()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                var factory = Factory(out var first);
                var second = new FakeTraceEngine();
                factory.Engines.Enqueue(second);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                Assert.Single(target.Controller.NetworkTracePackets);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);
                Assert.Empty(target.Controller.NetworkTracePackets);
                Assert.True(target.Controller.IsRunning);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
                Assert.Equal(2026, target.Controller.NetworkTracePackets[0].Timestamp.Year);
                Assert.Contains("2026-10-30", database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.First().Str("Timestamp"));
                Assert.Equal(0, first.Subscribers);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Succeeded);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
                Assert.Equal(2, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Count());
                Assert.Equal(1, first.StopCount);
                Assert.Equal(1, second.StopCount);
                Assert.Equal(0, second.Subscribers);
                Assert.False(target.Controller.IsTransitioning);
                Assert.False(target.Controller.RecoveryRequired);
            });
        }

        [Fact]
        public void MissingElevationOrConnectionFailureDoesNotClearOldPackets()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                var factory = Factory(out var engine);
                factory.Prerequisite = CaptureOperationFailure.ElevationRequired;
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var denied = Complete(model.CaptureOperations.SetRunningAsync(target, true));
                Assert.Equal(CaptureOperationFailure.ElevationRequired, denied.Failure);
                Assert.Equal(0, factory.ConnectCount);
                Assert.Single(target.Controller.NetworkTracePackets);
                Assert.Single(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                factory.Prerequisite = CaptureOperationFailure.None;
                factory.BeforeConnect = () => Task.FromException(new IOException("test connection failure"));
                Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, true)));
                Assert.Single(target.Controller.NetworkTracePackets);
                Assert.False(target.Controller.IsRunning);
                Assert.False(target.Controller.RecoveryRequired);
                Assert.Equal(0, engine.StartCount);
                Assert.False(model.IsCaptureDataOperationRunning);
            });
        }

        [Fact]
        public void UncertainStartRetainsItsSessionAndRequiresStopRecoveryWithoutStartingAgain()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                engine.Start = () => Task.FromException(new IOException("unknown start outcome"));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var failure = Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, true)));
                Assert.True(CaptureTransitionFailure.Get(failure).IsRunning);
                Assert.True(target.Controller.MayBeTracing);
                Assert.Equal(CaptureTraceRecoveryPhase.Stop, target.Controller.RecoveryPhase);
                Assert.Equal(0, factory.DisconnectCount);
                Assert.Equal(CaptureOperationFailure.RecoveryRequired,
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)).Failure);
                Assert.Throws<InvalidOperationException>(target.Controller.ClearAllCaptures);
                Assert.False(Complete(target.Controller.CanCloseAsync()));
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(1, engine.StartCount);
                Assert.Equal(1, engine.StopCount);
                Assert.False(target.Controller.IsRunning);
                Assert.True(Complete(target.Controller.CanCloseAsync()));
            });
        }

        [Fact]
        public void DefinitivelyRejectedStartDisconnectsWithoutClaimingAnActiveTrace()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                engine.Start = () => Task.FromException(new NetworkTraceEngineException("rejected", false));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                Assert.Throws<NetworkTraceEngineException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, true)));
                Assert.False(target.Controller.IsRunning);
                Assert.False(target.Controller.RecoveryRequired);
                Assert.Equal(1, factory.DisconnectCount);
                Assert.Equal(0, engine.StopCount);
                Assert.Equal(DeviceStatusEnum.Idle, target.Controller.DeviceStatus);
            });
        }

        [Fact]
        public void PartialDownloadIsNeverPublishedAndRetryDoesNotStopOrDuplicateRecords()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                engine.Download = source =>
                {
                    source.Emit(NetworkTraceTestData.Event(1));
                    throw new IOException("test partial download");
                };
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                Complete(model.CaptureOperations.SetRunningAsync(target, true));
                Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, false)));
                Assert.True(target.Controller.IsRunning);
                Assert.False(target.Controller.MayBeTracing);
                Assert.Equal(CaptureTraceRecoveryPhase.Download, target.Controller.RecoveryPhase);
                Assert.Empty(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
                Assert.Empty(target.Controller.NetworkTracePackets);
                Assert.Equal(0, engine.Subscribers);
                engine.Download = source =>
                {
                    source.Emit(NetworkTraceTestData.Event(1));
                    source.Emit(NetworkTraceTestData.Event(2));
                    return Task.CompletedTask;
                };
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(1, engine.StopCount);
                Assert.Equal(2, engine.DownloadCount);
                Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
                Assert.Equal(2, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Count());
            });
        }

        [Fact]
        public void DisconnectRetryPreservesPublishedRowsAndNeverDownloadsAgain()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                factory.BeforeDisconnect = () => Task.FromException(new IOException("test disconnect failure"));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                Complete(model.CaptureOperations.SetRunningAsync(target, true));
                Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, false)));
                Assert.Equal(CaptureTraceRecoveryPhase.Disconnect, target.Controller.RecoveryPhase);
                var ids = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Select(row => row.RowId).ToArray();
                factory.BeforeDisconnect = () => Task.CompletedTask;
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(ids, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Select(row => row.RowId));
                Assert.Equal(1, engine.StopCount);
                Assert.Equal(1, engine.DownloadCount);
                Assert.False(target.Controller.RecoveryRequired);
            });
        }

        [Fact]
        public void PublicationRetryDoesNotReplaceCommittedRowsOrDownloadAgain()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                var failed = false;
                target.Controller.PropertyChanged += (_, args) =>
                {
                    if (!failed && args.PropertyName == nameof(NetworkTraceCaptureController.NetworkTracePackets))
                    {
                        failed = true;
                        throw new IOException("test view publication failure");
                    }
                };
                Complete(model.CaptureOperations.SetRunningAsync(target, true));
                Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, false)));
                Assert.Equal(CaptureTraceRecoveryPhase.Publish, target.Controller.RecoveryPhase);
                var ids = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Select(row => row.RowId).ToArray();
                Assert.Equal(2, ids.Length);
                Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, false)).Succeeded);
                Assert.Equal(ids, database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Select(row => row.RowId));
                Assert.Equal(1, engine.DownloadCount);
                Assert.Equal(1, engine.StopCount);
                Assert.Equal(2, target.Controller.NetworkTracePackets.Count);
            });
        }

        [Fact]
        public void RecoveryRemainsVisibleAndStoppableInTheExistingDesktopView()
        {
            application.Run(owner =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var factory = Factory(out var engine);
                engine.Start = () => Task.FromException(new IOException("uncertain start"));
                var model = new CaptureAppModel { ActiveDatabase = database };
                var target = Target(model, factory, database.TableByName(NetworkTraceCaptureMethod.TableName));
                Assert.Throws<IOException>(() => Complete(model.CaptureOperations.SetRunningAsync(target, true)));
                var view = new NetworkTraceCaptureView { DataContext = target };
                WpfTestHost.Show(owner, view);
                Assert.True(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                Assert.True(NetworkTraceCommands.AcknowledgeRecovery.CanExecute(null, view));
                Assert.Equal(Visibility.Visible, view.AcknowledgeRecoveryButton.Visibility);
                Assert.Contains("Stop", target.Controller.RecoveryStatusText);
                Assert.False(target.Controller.CanClearCaptures);
            });
        }

        private static FakeTraceEngineFactory Factory(out FakeTraceEngine engine)
        {
            var factory = new FakeTraceEngineFactory();
            engine = new FakeTraceEngine();
            factory.Engines.Enqueue(engine);
            return factory;
        }

        private static TraceCaptureContext Target(CaptureAppModel model, FakeTraceEngineFactory factory, IDataTable table)
        {
            var target = new TraceCaptureContext(factory);
            target.Controller.Initialize(table);
            target.Controller.LoadCaptures([table.Dataset]);
            model.CaptureDeviceContexts.Add(target);
            return target;
        }

        private static T Complete<T>(Task<T> task)
        {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The trace lifecycle did not complete.");
            return task.GetAwaiter().GetResult();
        }
    }
}
