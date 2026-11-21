// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureProxyOperationsTests(CaptureUiApplicationFixture application)
    {
        internal static CaptureProxyOperations Operations(
            CaptureDeviceTestData setup, Func<Task> save = null, TimeSpan? timeout = null,
            Func<DeviceType, string, Task<string>> resolve = null) =>
            new(setup.Model, () => setup.GdkInstalled, () => [IPAddress.Parse("192.0.2.10")],
                resolve ?? ((type, _) => Task.FromResult(type == DeviceType.LocalPC ? "LocalPC" : "192.0.2.1")),
                save ?? (() => Task.CompletedTask), timeout ?? TimeSpan.FromSeconds(3), _ => Task.CompletedTask);

        [Fact]
        public void EnableDisable_BindsApprovedEndpointAndUsesExistingListenerWithoutSelectingOrClearing()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                var record = CaptureTimelineTestData.Create();
                target.Proxy.ProxyConnections.Add(record);
                var operations = Operations(setup);
                var plan = Complete(operations.PrepareAsync(added.CaptureId, added.Revision,
                    CaptureProxyAction.Enable, "192.0.2.10"));
                Assert.True(plan.MayRestartConsole);
                Assert.Equal(0, target.Listener.StartCount);
                var enabled = Complete(operations.ExecuteAsync(plan));
                Assert.True(enabled.Succeeded);
                Assert.True(enabled.ConfigurationConfirmed);
                Assert.True(enabled.ConfigurationWriteRequested);
                Assert.True(enabled.IsRunning);
                Assert.Equal(new ProxyEndpoint("192.0.2.10", target.Proxy.Port), target.Configuration.Current.Endpoint);
                Assert.Same(record, Assert.Single(target.Proxy.ProxyConnections));
                Assert.Null(setup.Model.SelectedDeviceContext);
                var capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                var same = Complete(operations.PrepareAsync(capture.Id, capture.Revision,
                    CaptureProxyAction.Enable, "192.0.2.10"));
                Assert.False(same.MayRestartConsole);
                Assert.False(Complete(operations.ExecuteAsync(same)).ConfigurationWriteRequested);
                Assert.Equal(1, target.Listener.StartCount);
                capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                var disabled = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    capture.Id, capture.Revision, CaptureProxyAction.Disable))));
                Assert.True(disabled.ConfigurationConfirmed);
                Assert.False(disabled.IsRunning);
                Assert.Same(record, Assert.Single(target.Proxy.ProxyConnections));
                Assert.False(target.Proxy.IsTransitioning);
            });
        }

        [Theory]
        [InlineData("198.51.100.1", CaptureProxyFailure.NoLocalAddress)]
        [InlineData("proxy.example", CaptureProxyFailure.NoLocalAddress)]
        public void Enable_InvalidSourceHasNoListenerOrDeviceEffects(string host, CaptureProxyFailure failure)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var operations = Operations(setup);
                Assert.Equal(failure, Assert.Throws<CaptureProxyException>(() => Complete(operations.PrepareAsync(
                    added.CaptureId, added.Revision, CaptureProxyAction.Enable, host))).Failure);
                Assert.Equal(0, setup.Created.Single().Listener.StartCount);
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }

        [Fact]
        public void Enable_RechecksEndpointAndConsoleResolutionWithoutAdoptingAnotherTarget()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "console.example"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                var address = "192.0.2.1";
                var operations = Operations(setup, resolve: (_, _) => Task.FromResult(address));
                var plan = Complete(operations.PrepareAsync(added.CaptureId, added.Revision,
                    CaptureProxyAction.Enable, "192.0.2.10"));
                address = "192.0.2.2";
                Assert.Equal(CaptureProxyFailure.SourceChanged, Assert.Throws<CaptureProxyException>(() =>
                    Complete(operations.ExecuteAsync(plan))).Failure);
                address = "192.0.2.1";
                target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("other.example", 8080));
                Assert.Equal(CaptureProxyFailure.ForeignEndpoint, Assert.Throws<CaptureProxyException>(() =>
                    Complete(operations.ExecuteAsync(plan))).Failure);
                Assert.Equal(0, target.Listener.StartCount);
                Assert.Equal(0, target.Configuration.WriteCount);
            });
        }

        [Fact]
        public void Disable_WaitsForReadbackAndKeepsListenerOnUnknownOrUncertainWrite()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                target.Proxy.SetConfiguredProxyHost("192.0.2.10");
                target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("192.0.2.10", target.Proxy.Port));
                Finish(target.Proxy.StartProxyAsync(false));
                var capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                target.Configuration.Disable = _ =>
                {
                    target.Configuration.Current = new(ProxyConfigurationState.Unknown);
                    return Task.CompletedTask;
                };
                var operations = Operations(setup, timeout: TimeSpan.FromMilliseconds(100));
                var failed = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    capture.Id, capture.Revision, CaptureProxyAction.Disable))));
                Assert.Equal(CaptureProxyFailure.ConfigurationTimeout, failed.Failure);
                Assert.True(failed.ConfigurationWriteRequested);
                Assert.False(failed.ConfigurationConfirmed);
                Assert.True(failed.IsRunning);
                Assert.True(Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single().RecoveryRequired);
                target.Configuration.Disable = _ => throw new IOException("private SDK failure");
                capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                failed = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    capture.Id, capture.Revision, CaptureProxyAction.Disable))));
                Assert.Equal(CaptureProxyFailure.RecoveryRequired, failed.Failure);
                Assert.True(failed.IsRunning);
            });
        }

        [Fact]
        public void ApplyPort_UsesSavedCommitAndReleasesOldReservationOnlyAfterSave()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                target.Proxy.SetConfiguredProxyHost("192.0.2.10");
                var oldPort = target.Proxy.Port;
                target.Configuration.Current = new(ProxyConfigurationState.Enabled, new("192.0.2.10", oldPort));
                var queued = Complete(setup.Devices.SetPendingProxyPortAsync(added.CaptureId, added.Revision, 9001));
                var saved = false;
                var operations = Operations(setup, save: () =>
                {
                    Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(oldPort, "too early"));
                    Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(9001, "reserved"));
                    saved = true;
                    return Task.CompletedTask;
                });
                target.Configuration.Enable = endpoint =>
                {
                    Assert.True(saved);
                    target.Configuration.Current = new(ProxyConfigurationState.Enabled, endpoint);
                    return Task.CompletedTask;
                };
                var result = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    queued.CaptureId, queued.Revision, CaptureProxyAction.ApplyPendingPort, "192.0.2.10"))));
                Assert.True(result.Succeeded);
                Assert.True(result.PortCommitted);
                Assert.True(result.Persisted);
                Assert.True(result.ConfigurationConfirmed);
                Assert.True(result.IsRunning);
                Assert.Equal(9001, result.Port);
                Assert.Equal(0, result.PendingPort);
                using var released = setup.Pool.ReservePort(oldPort, "retired");
            });
        }

        [Fact]
        public void ApplyPort_FailedSaveRollsBackDraftAndFailedEnableKeepsCommittedListener()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                target.Configuration.AllowWrites = true;
                var oldPort = target.Proxy.Port;
                var queued = Complete(setup.Devices.SetPendingProxyPortAsync(added.CaptureId, added.Revision, 9001));
                var operations = Operations(setup, save: () => throw new IOException("private file"));
                var failed = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    queued.CaptureId, queued.Revision, CaptureProxyAction.ApplyPendingPort, "192.0.2.10"))));
                Assert.Equal(CaptureProxyFailure.PersistenceFailed, failed.Failure);
                Assert.False(failed.PortCommitted);
                Assert.False(failed.IsRunning);
                Assert.Equal(oldPort, target.Proxy.Port);
                Assert.Equal(9001, target.Proxy.PendingPort);
                Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(oldPort, "old"));
                var capture = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single();
                target.Configuration.Enable = _ => throw new IOException("possibly committed");
                operations = Operations(setup);
                failed = Complete(operations.ExecuteAsync(Complete(operations.PrepareAsync(
                    capture.Id, capture.Revision, CaptureProxyAction.ApplyPendingPort, "192.0.2.10"))));
                Assert.True(failed.PortCommitted);
                Assert.True(failed.IsRunning);
                Assert.Equal(9001, failed.Port);
                Assert.Equal(CaptureProxyFailure.RecoveryRequired, failed.Failure);
                Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(9001, "keep"));
            });
        }

        [Fact]
        public void SdkWrite_DrainsCancellationAndDoesNotBlockAnotherDeviceStop()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var target = setup.Created[0];
                var other = setup.Created[1];
                target.Configuration.AllowWrites = true;
                var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                target.Configuration.Enable = _ => write.Task;
                var operations = Operations(setup);
                var plan = Complete(operations.PrepareAsync(added.CaptureId, added.Revision,
                    CaptureProxyAction.Enable, "192.0.2.10"));
                using var cancellation = new CancellationTokenSource();
                var enabling = operations.ExecuteAsync(plan, cancellation.Token);
                PumpUntil(() => target.Configuration.WriteCount != 0);
                Assert.True(target.Proxy.IsTransitioning);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
                Finish(setup.Model.CaptureOperations.SetRunningAsync(other, true));
                Assert.True(Complete(setup.Model.CaptureOperations.SetRunningAsync(other, false)).Changed);
                cancellation.Cancel();
                Assert.False(enabling.IsCompleted);
                write.SetResult();
                var result = Complete(enabling);
                Assert.True(result.ConfigurationWriteRequested);
                Assert.True(result.IsRunning);
                Assert.False(result.ConfigurationConfirmed);
                Assert.Equal(CaptureProxyFailure.RecoveryRequired, result.Failure);
                Assert.False(target.Proxy.IsTransitioning);
            });
        }

        [Fact]
        public void ManualApplication_RequiresOperatorSetupAndNeverWritesDeviceConfiguration()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var queued = Complete(setup.Devices.SetPendingProxyPortAsync(added.CaptureId, added.Revision, 9001));
                var operations = Operations(setup);
                var plan = Complete(operations.PrepareAsync(queued.CaptureId, queued.Revision,
                    CaptureProxyAction.ApplyPendingPort, "192.0.2.10"));
                Assert.True(plan.RequiresManualSetup);
                var result = Complete(operations.ExecuteAsync(plan));
                Assert.True(result.RequiresManualSetup);
                Assert.True(result.PortCommitted);
                Assert.False(result.ConfigurationConfirmed);
                Assert.False(result.ConfigurationWriteRequested);
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }
    }
}
