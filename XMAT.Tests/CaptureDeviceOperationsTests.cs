// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureDeviceOperationsTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void Add_NormalizesAndDeduplicatesWithoutSelectingStartingOrConfiguring()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var human = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var selected = setup.Model.CaptureDeviceContexts.Single();
                setup.Model.SelectedDeviceContext = selected;
                var first = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "CONSOLE.example."));
                var duplicate = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, " console.EXAMPLE "));
                Assert.True(first.Changed);
                Assert.False(duplicate.Changed);
                Assert.Equal(first.CaptureId, duplicate.CaptureId);
                Assert.Equal(2, setup.Created.Count);
                Assert.Equal("console.example", setup.Created[1].DeviceName);
                Assert.Equal(2, setup.Model.CaptureDevicesTable.Dataset.DataRecords.Count());
                Assert.Same(selected, setup.Model.SelectedDeviceContext);
                Assert.All(setup.Created, context => Assert.Equal(0, context.Listener.StartCount));
                Assert.Equal(0, setup.ConfigurationWrites);
                Assert.Same(setup.Model.HostListPolicy, setup.Created[1].Proxy.HostListPolicy);
                Assert.Equal(2, setup.SaveCount);
                Assert.NotEqual(human.CaptureId, first.CaptureId);
            });
        }

        [Fact]
        public void Add_DefaultConsoleApprovalCannotFollowAChangedSelection()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var request = new CaptureDeviceRequest(CaptureDeviceKind.DefaultXboxConsole, CaptureType.NetworkTrace);
                var creation = Complete(setup.Devices.PrepareAddAsync(request));
                setup.DefaultConsole = () => Task.FromResult("192.0.2.2");
                Assert.Equal(CaptureDeviceFailure.SourceChanged,
                    Assert.Throws<CaptureDeviceException>(() => Complete(setup.Devices.AddAsync(creation))).Failure);
                Assert.Empty(setup.Created);
                setup.DefaultConsole = () => Task.FromResult<string>(null);
                Assert.Equal(CaptureDeviceFailure.NoDefaultConsole,
                    Assert.Throws<CaptureDeviceException>(() => Complete(setup.Devices.PrepareAddAsync(request))).Failure);
                setup.GdkInstalled = false;
                Assert.Equal(CaptureDeviceFailure.GdkRequired,
                    Assert.Throws<CaptureDeviceException>(() => Complete(setup.Devices.PrepareAddAsync(request))).Failure);
            });
        }

        [Theory]
        [InlineData(CaptureDeviceKind.GenericProxyDevice, CaptureType.NetworkTrace, null, CaptureDeviceFailure.UnsupportedCapture)]
        [InlineData(CaptureDeviceKind.WindowsHandheldOrRemoteDevice, CaptureType.NetworkTrace, null, CaptureDeviceFailure.UnsupportedCapture)]
        [InlineData(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, null, CaptureDeviceFailure.InvalidRequest)]
        [InlineData(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "http://console.example", CaptureDeviceFailure.InvalidRequest)]
        [InlineData(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "console/path", CaptureDeviceFailure.InvalidRequest)]
        [InlineData(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "::1", CaptureDeviceFailure.InvalidRequest)]
        [InlineData(CaptureDeviceKind.LocalPC, CaptureType.WebProxy, "unexpected", CaptureDeviceFailure.InvalidRequest)]
        [InlineData((CaptureDeviceKind)999, CaptureType.WebProxy, null, CaptureDeviceFailure.InvalidRequest)]
        [InlineData(CaptureDeviceKind.LocalPC, (CaptureType)999, null, CaptureDeviceFailure.InvalidRequest)]
        public void Add_InvalidRequestsHaveNoEffects(
            CaptureDeviceKind kind, CaptureType captureType, string address, CaptureDeviceFailure expected)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                Assert.Equal(expected, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.PrepareAddAsync(new(kind, captureType, address)))).Failure);
                Assert.Empty(setup.Created);
                Assert.Empty(setup.Model.CaptureDevicesTable.Dataset.DataRecords);
                Assert.Equal(0, setup.SaveCount);
            });
        }

        [Fact]
        public void Add_CancellingDefaultResolutionDoesNotAcquireTheDataLeaseOrCreate()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var resolution = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                setup.DefaultConsole = () => resolution.Task;
                using var cancellation = new CancellationTokenSource();
                var preparing = setup.Devices.PrepareAddAsync(
                    new(CaptureDeviceKind.DefaultXboxConsole, CaptureType.WebProxy), cancellation.Token);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Complete(preparing));
                resolution.SetResult("192.0.2.1");
                WpfTestHost.Pump();
                Assert.Empty(setup.Created);
            });
        }

        [Fact]
        public void Add_PersistenceFailureKeepsTheCommittedContextAndReportsIt()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData(() => throw new IOException("private file path"));
                var result = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.WebProxy));
                Assert.True(result.Changed);
                Assert.False(result.Persisted);
                Assert.Equal(CaptureDeviceFailure.PersistenceFailed, result.Failure);
                Assert.Single(setup.Model.CaptureDeviceContexts);
                Assert.Single(setup.Model.CaptureDevicesTable.Dataset.DataRecords);
                Assert.True(Complete(setup.Devices.GetDeviceAsync(result.CaptureId)).IsProxyPortReserved);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }

        [Fact]
        public void PendingPort_SaveRollbackRetainsBothOriginalReservationsAndReleasesOnlyTheReplacement()
        {
            application.Run(_ =>
            {
                var fail = false;
                using var setup = new CaptureDeviceTestData(() => fail
                    ? Task.FromException(new IOException("private save failure")) : Task.CompletedTask);
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var context = setup.Created.Single();
                var initialPort = context.Proxy.Port;
                var queued = Complete(setup.Devices.SetPendingProxyPortAsync(added.CaptureId, added.Revision, 9001));
                Assert.True(queued.Changed);
                fail = true;
                Assert.Equal(CaptureDeviceFailure.PersistenceFailed, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.SetPendingProxyPortAsync(added.CaptureId, queued.Revision, 9002))).Failure);
                Assert.Equal(initialPort, context.Proxy.Port);
                Assert.Equal(9001, context.Proxy.PendingPort);
                Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(initialPort, "other"));
                Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(9001, "other"));
                using var freedReplacement = setup.Pool.ReservePort(9002, "other");
                var current = Complete(setup.Devices.GetDeviceAsync(added.CaptureId)).Capture;
                Assert.Equal(CaptureDeviceFailure.PersistenceFailed, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.SetPendingProxyPortAsync(current.Id, current.Revision, null))).Failure);
                Assert.Equal(9001, context.Proxy.PendingPort);
                fail = false;
                current = Complete(setup.Devices.GetDeviceAsync(added.CaptureId)).Capture;
                var discarded = Complete(setup.Devices.SetPendingProxyPortAsync(current.Id, current.Revision, initialPort));
                Assert.True(discarded.Changed);
                Assert.False(context.Proxy.HasPendingPort);
                using var released = setup.Pool.ReservePort(9001, "other");
                Assert.Equal(0, context.Listener.StartCount);
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }

        [Fact]
        public void PendingPort_AsyncSaveHoldsGuardsAndDrainsAfterCancellation()
        {
            application.Run(_ =>
            {
                Task save = Task.CompletedTask;
                using var setup = new CaptureDeviceTestData(() => save);
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var context = setup.Created.Single();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                save = release.Task;
                using var cancellation = new CancellationTokenSource();
                var changing = setup.Devices.SetPendingProxyPortAsync(
                    added.CaptureId, added.Revision, 9001, cancellation.Token);
                Assert.True(context.Proxy.IsTransitioning);
                Assert.True(setup.Model.IsCaptureDataOperationRunning);
                cancellation.Cancel();
                Assert.False(changing.IsCompleted);
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(setup.Model.CaptureOperations.GetAvailabilityAsync(context, true)));
                release.SetResult();
                var result = Complete(changing);
                Assert.True(result.Changed);
                Assert.True(result.Persisted);
                Assert.Equal(9001, context.Proxy.PendingPort);
                Assert.False(context.Proxy.IsTransitioning);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
            });
        }

        [Fact]
        public void Remove_UsesExistingCloseAndDALWithoutDeletingOtherContextsOrSelectingTheTarget()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var first = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var second = setup.Add(new(CaptureDeviceKind.WindowsHandheldOrRemoteDevice, CaptureType.WebProxy));
                var target = setup.Created[0];
                var human = setup.Created[1];
                setup.Model.SelectedDeviceContext = human;
                CaptureInvestigationTestData.AddWeb(setup.Database, 1, device: target.DeviceName);
                CaptureInvestigationTestData.AddWeb(setup.Database, 2, device: human.DeviceName);
                var queued = Complete(setup.Devices.SetPendingProxyPortAsync(first.CaptureId, first.Revision, 9001));
                var port = target.Proxy.Port;
                var result = Complete(setup.Devices.RemoveAsync(first.CaptureId, queued.Revision));
                Assert.True(result.Removed);
                Assert.True(result.Persisted);
                Assert.True(result.CaptureEpoch > first.CaptureEpoch);
                Assert.Same(human, Assert.Single(setup.Model.CaptureDeviceContexts));
                Assert.Same(human, setup.Model.SelectedDeviceContext);
                Assert.Equal(human.DeviceName, Assert.Single(setup.Model.CaptureDevicesTable.Dataset.DataRecords).Str("DeviceName"));
                Assert.Equal(human.DeviceName,
                    Assert.Single(setup.Database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords)
                        .Str(WebServiceCaptureMethod.FieldKey_DeviceName));
                using var released = setup.Pool.ReservePort(port, "other");
                using var pendingReleased = setup.Pool.ReservePort(9001, "other");
                Assert.Equal(0, setup.ConfigurationWrites);
                Assert.Equal(CaptureDeviceFailure.TargetNotFound, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.GetDeviceAsync(first.CaptureId))).Failure);
                Assert.NotEqual(first.CaptureId, second.CaptureId);
            });
        }

        [Theory]
        [InlineData((int)ProxyConfigurationState.Enabled, CaptureDeviceFailure.ProxyEndpointInUse)]
        [InlineData((int)ProxyConfigurationState.Unknown, CaptureDeviceFailure.ProxyStateUnknown)]
        [InlineData((int)ProxyConfigurationState.Unreachable, CaptureDeviceFailure.ProxyStateUnknown)]
        public void Remove_OwnedUnknownOrUnreachableProxyKeepsDataAndReservations(int state, CaptureDeviceFailure expected)
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.WebProxy));
                var target = setup.Created.Single();
                target.Configuration.Current = new((ProxyConfigurationState)state, new("127.0.0.1", target.Proxy.Port),
                    "PRIVATE_CONFIGURATION_DO_NOT_RETURN");
                var inspected = Complete(setup.Devices.PrepareRemoveAsync(added.CaptureId, added.Revision));
                Assert.Equal(expected, inspected.RemovalFailure);
                Assert.DoesNotContain("PRIVATE_CONFIGURATION_DO_NOT_RETURN", JsonSerializer.Serialize(inspected));
                Assert.Equal(expected, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.RemoveAsync(added.CaptureId, added.Revision))).Failure);
                Assert.Single(setup.Model.CaptureDeviceContexts);
                Assert.Throws<InvalidOperationException>(() => setup.Pool.ReservePort(target.Proxy.Port, "other"));
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }

        [Fact]
        public void Remove_ExternalReadDoesNotBlockOtherStopsAndRechecksTheRevisionAfterIt()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.WebProxy));
                var other = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var target = setup.Created[0];
                var human = setup.Created[1];
                Finish(setup.Model.CaptureOperations.SetRunningAsync(human, true));
                var reading = new TaskCompletionSource<ProxyConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
                target.Configuration.Read = () => reading.Task;
                var removing = setup.Devices.RemoveAsync(added.CaptureId, added.Revision);
                Assert.False(setup.Model.IsCaptureDataOperationRunning);
                Assert.True(Complete(setup.Model.CaptureOperations.SetRunningAsync(human, false)).Changed);
                target.Proxy.QueuePortChange(9001, () => { });
                reading.SetResult(new(ProxyConfigurationState.Disabled));
                Assert.Equal(CaptureDeviceFailure.SourceChanged,
                    Assert.Throws<CaptureDeviceException>(() => Complete(removing)).Failure);
                Assert.Equal(2, setup.Model.CaptureDeviceContexts.Count);
                Assert.Equal(9001, target.Proxy.PendingPort);
                Assert.NotEqual(other.CaptureId, added.CaptureId);
            });
        }

        [Fact]
        public void Remove_TraceRowsAreRetainedAndPersistenceFailureIsExplicit()
        {
            application.Run(_ =>
            {
                var fail = false;
                using var setup = new CaptureDeviceTestData(() => fail
                    ? Task.FromException(new IOException("private settings path")) : Task.CompletedTask);
                var added = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.NetworkTrace));
                var target = setup.Created.Single();
                setup.Model.SelectedDeviceContext = target;
                CaptureInvestigationTestData.AddPacket(setup.Database, DateTime.UtcNow);
                fail = true;
                var removed = Complete(setup.Devices.RemoveAsync(added.CaptureId, added.Revision));
                Assert.True(removed.Removed);
                Assert.False(removed.Persisted);
                Assert.Equal(CaptureDeviceFailure.PersistenceFailed, removed.Failure);
                Assert.Null(setup.Model.SelectedDeviceContext);
                Assert.Empty(setup.Model.CaptureDeviceContexts);
                Assert.Empty(setup.Model.CaptureDevicesTable.Dataset.DataRecords);
                Assert.Single(setup.Database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords);
            });
        }

        [Fact]
        public void DeviceInspection_IsBoundedAndNeverTreatsTimeoutAsDisabled()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData(inspectionTimeout: TimeSpan.FromMilliseconds(100));
                var added = setup.Add(new(CaptureDeviceKind.XboxConsole, CaptureType.WebProxy, "192.0.2.1"));
                var target = setup.Created.Single();
                var reading = new TaskCompletionSource<ProxyConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
                target.Configuration.Read = () => reading.Task;
                var inspected = Complete(setup.Devices.GetDeviceAsync(added.CaptureId, true));
                Assert.Equal(CaptureDeviceProxyState.Unknown, inspected.ProxyState);
                Assert.Equal(CaptureDeviceFailure.ProxyStateUnknown, inspected.RemovalFailure);
                reading.SetResult(new(ProxyConfigurationState.Disabled));
                WpfTestHost.Pump();
                Assert.Single(setup.Model.CaptureDeviceContexts);
                Assert.Equal(0, setup.ConfigurationWrites);
            });
        }

        [Fact]
        public void DesktopCloseCallbackUsesTheSharedRemovalAndPersistenceWorkflow()
        {
            application.Run(owner =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var target = setup.Created.Single();
                var callbackCalled = false;
                var cancelled = Complete(setup.Devices.RemoveDesktopAsync(target, () =>
                {
                    callbackCalled = true;
                    Assert.True(setup.Model.IsCaptureDataOperationRunning);
                    return Task.FromResult(false);
                }));
                Assert.True(callbackCalled);
                Assert.Equal(CaptureDeviceFailure.Cancelled, cancelled.Failure);
                Assert.Single(setup.Model.CaptureDeviceContexts);
                var removed = Complete(setup.Devices.RemoveDesktopAsync(target, () => Task.FromResult(true)));
                Assert.True(removed.Removed);
                Assert.Equal(2, setup.SaveCount);
                Assert.True(removed.Revision > added.Revision);
            });
        }

        [Fact]
        public void DeviceInspection_EnabledWithoutAnEndpointIsUnknownAndCannotBeRemoved()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.LocalPC, CaptureType.WebProxy));
                setup.Created.Single().Configuration.Current = new(ProxyConfigurationState.Enabled);
                var state = Complete(setup.Devices.PrepareRemoveAsync(added.CaptureId, added.Revision));
                Assert.Equal(CaptureDeviceProxyState.Unknown, state.ProxyState);
                Assert.Equal(CaptureDeviceFailure.ProxyStateUnknown, state.RemovalFailure);
                Assert.Equal(CaptureDeviceFailure.ProxyStateUnknown, Assert.Throws<CaptureDeviceException>(() =>
                    Complete(setup.Devices.RemoveAsync(added.CaptureId, added.Revision))).Failure);
                Assert.Single(setup.Model.CaptureDeviceContexts);
            });
        }

        [Fact]
        public void PendingPort_AndRemovalPersistThroughTheExistingSettingsSerializer()
        {
            application.Run(_ =>
            {
                using var setup = new CaptureDeviceTestData();
                var added = setup.Add(new(CaptureDeviceKind.GenericProxyDevice, CaptureType.WebProxy));
                var path = Path.Combine(PublicUtilities.StorageDirectoryPath, "app-settings.json");
                var previous = File.Exists(path) ? File.ReadAllText(path) : null;
                try
                {
                    var devices = new CaptureDevices(setup.Model, Dispatcher.CurrentDispatcher);
                    var queued = Complete(devices.SetPendingProxyPortAsync(added.CaptureId, added.Revision, 9001));
                    Assert.True(queued.Persisted);
                    using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                    {
                        var device = document.RootElement.GetProperty("CaptureDevices")[0];
                        Assert.Equal(setup.Created.Single().DeviceName, device.GetProperty("DeviceName").GetString());
                        Assert.Equal(9001, device.GetProperty("Parameters").GetProperty("PendingPort").GetInt32());
                    }
                    Assert.True(Complete(devices.RemoveAsync(added.CaptureId, queued.Revision)).Persisted);
                    using var removed = JsonDocument.Parse(File.ReadAllText(path));
                    Assert.Empty(removed.RootElement.GetProperty("CaptureDevices").EnumerateArray());
                }
                finally
                {
                    if (previous == null)
                        File.Delete(path);
                    else
                        CaptureAppSettings.WriteSettingsFile(path, previous);
                }
            });
        }

        [Fact]
        public void AddDialogCollectsDefaultConsoleChoiceWithoutResolvingOrConfiguringIt()
        {
            application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                var selector = new AddDeviceWindow { Owner = owner };
                ((RadioButton)selector.FindName("DefaultConsole")).IsChecked = true;
                ((RadioButton)selector.FindName("NetworkTraceLabel")).IsChecked = true;
                selector.Dispatcher.BeginInvoke(() =>
                    ((Button)selector.FindName("OkButton")).RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent)));
                Assert.True(selector.ShowDialog());
                Assert.Equal(new CaptureDeviceRequest(CaptureDeviceKind.DefaultXboxConsole, CaptureType.NetworkTrace),
                    selector.Request);
            });
        }
    }
}
