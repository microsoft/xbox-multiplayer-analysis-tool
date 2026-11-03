// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ApplicationOperationsTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public ApplicationOperationsTests(CaptureUiApplicationFixture application) =>
            _application = application;

        [Fact]
        public void SetRunning_UsesExplicitTargetAndDesiredState()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var selected = new Context();
                var target = new Context();
                model.CaptureDeviceContexts.Add(selected);
                model.CaptureDeviceContexts.Add(target);
                model.SelectedDeviceContext = selected;
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);

                Assert.True(Complete(operations.SetRunningAsync(target, true)).Changed);
                Assert.False(Complete(operations.SetRunningAsync(target, true)).Changed);
                Assert.False(selected.Controller.IsRunning);
                Assert.Same(selected, model.SelectedDeviceContext);
                Assert.Equal(1, adapter.StartCount);

                Assert.True(Complete(operations.SetRunningAsync(target, false)).Changed);
                Assert.False(Complete(operations.SetRunningAsync(target, false)).Changed);
                Assert.Equal(1, adapter.StopCount);
                Assert.False(model.IsCaptureDataOperationRunning);
            });
        }

        [Fact]
        public void SetRunning_RejectsDetachedAndReadOnlyTargets()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);

                Assert.Equal(CaptureOperationFailure.TargetNotFound,
                    Complete(operations.SetRunningAsync(target, true)).Failure);
                model.CaptureDeviceContexts.Add(target);
                target.IsReadOnly = true;
                Assert.Equal(CaptureOperationFailure.ReadOnlyCapture,
                    Complete(operations.SetRunningAsync(target, true)).Failure);
                Assert.Equal(0, adapter.StartCount);
            });
        }

        [Fact]
        public void SetRunning_UnsupportedControllerHasNoEffects()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);

                Assert.Equal(CaptureOperationFailure.UnsupportedCapture,
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)).Failure);
                Assert.False(target.Controller.IsRunning);
                Assert.False(model.IsCaptureDataOperationRunning);
            });
        }

        [Fact]
        public void ProxyAdapter_FailedStartReleasesGuardsAndSubsequentStopReportsCompletion()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var proxy = new RecordingProxy { StartFailure = new IOException("test listener failure") };
                var controller = new WebServiceDeviceCaptureController(
                    "Test manual proxy", DeviceType.GenericProxyDevice, false,
                    new ProxyPortPool(), proxyFactory: () => proxy);
                var target = new LiveContext(controller, CaptureType.WebProxy, DeviceType.GenericProxyDevice);
                model.CaptureDeviceContexts.Add(target);
                controller.Initialize();

                Assert.Throws<IOException>(() =>
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)));
                Assert.False(controller.IsRunning);
                Assert.False(controller.IsTransitioning);
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Equal(1, proxy.StopCount);

                proxy.StartFailure = null;
                try
                {
                    Assert.True(Complete(model.CaptureOperations.SetRunningAsync(target, true)).Changed);
                    Assert.True(controller.IsRunning);
                    Assert.False(controller.IsTransitioning);
                }
                finally
                {
                    var progress = new List<WebProxyStopProgress>();
                    var result = Complete(model.CaptureOperations.SetRunningAsync(
                        target, false, new ProgressRecorder(progress.Add)));
                    Assert.True(result.Succeeded);
                    Assert.False(result.IsRunning);
                    Assert.Equal(WebProxyStopPhase.Complete, progress[^1].Phase);
                }

                Assert.False(controller.IsTransitioning);
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Equal(2, proxy.StartCount);
                Assert.Equal(2, proxy.StopCount);
            });
        }

        [Fact]
        public void TraceAdapter_RejectsTransitioningOrReadOnlyCaptureBeforeEngineWork()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var controller = new NetworkTraceCaptureController("Test trace", DeviceType.LocalPC, new FakeTraceEngineFactory());
                controller.Initialize(database.TableByName(NetworkTraceCaptureMethod.TableName));
                var target = new LiveContext(controller, CaptureType.NetworkTrace, DeviceType.LocalPC);
                model.CaptureDeviceContexts.Add(target);
                Assert.Equal(CaptureOperationFailure.None,
                    Complete(model.CaptureOperations.GetAvailabilityAsync(target, true)));

                controller.IsCapturing = true;
                controller.DeviceStatus = DeviceStatusEnum.Connecting;
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(model.CaptureOperations.SetRunningAsync(target, true)).Failure);
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(model.CaptureOperations.SetRunningAsync(target, false)).Failure);

                target.IsReadOnly = true;
                Assert.Equal(CaptureOperationFailure.ReadOnlyCapture,
                    Complete(model.CaptureOperations.SetRunningAsync(target, false)).Failure);
                Assert.Null(controller._networkTraceEngine);
            });
        }

        [Fact]
        public void SetRunning_EnforcesAnalysisAndAdapterGuardsWithoutEffects()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);
                Assert.True(model.TryBeginAnalysis(target, out var lease));

                using (lease)
                {
                    Assert.Equal(CaptureOperationFailure.CaptureBusy,
                        Complete(operations.SetRunningAsync(target, true)).Failure);
                }

                adapter.Failure = CaptureOperationFailure.PendingPortChange;
                Assert.Equal(CaptureOperationFailure.PendingPortChange,
                    Complete(operations.GetAvailabilityAsync(target, true)));
                Assert.Equal(CaptureOperationFailure.PendingPortChange,
                    Complete(operations.SetRunningAsync(target, true)).Failure);
                Assert.Equal(0, adapter.StartCount);
                Assert.Equal(0, adapter.TransitionCount);
            });
        }

        [Fact]
        public void SetRunning_HoldsAndReleasesLeaseAcrossAsynchronousStart()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                var other = new Context();
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(other);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var adapter = new Adapter(target.Controller) { StartAction = () => release.Task };
                var operations = Create(model, adapter);

                var starting = operations.SetRunningAsync(target, true);
                Assert.True(model.IsCaptureDataOperationRunning);
                Assert.False(starting.IsCompleted);
                Assert.Equal(CaptureOperationFailure.CaptureBusy,
                    Complete(operations.SetRunningAsync(other, true)).Failure);

                release.SetResult();
                Assert.True(Complete(starting).Succeeded);
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Equal(1, adapter.CompletedTransitions);
            });
        }

        [Fact]
        public void SetRunning_FailureReleasesLeaseAndTransitionForRetry()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller)
                {
                    StartAction = () => Task.FromException(new IOException("test start failure"))
                };
                var operations = Create(model, adapter);

                Assert.Throws<IOException>(() => Complete(operations.SetRunningAsync(target, true)));
                Assert.False(model.IsCaptureDataOperationRunning);
                Assert.Equal(1, adapter.CompletedTransitions);

                adapter.StartAction = () => Task.CompletedTask;
                Assert.True(Complete(operations.SetRunningAsync(target, true)).Succeeded);
                Assert.Equal(2, adapter.CompletedTransitions);
            });
        }

        [Fact]
        public void SetRunning_CancellationBeforeStartHasNoEffectsButStopDrainsAfterCommit()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                Assert.ThrowsAny<OperationCanceledException>(() =>
                    Complete(operations.SetRunningAsync(target, true, cancellationToken: cancelled.Token)));
                Assert.Equal(0, adapter.StartCount);
                Assert.False(model.IsCaptureDataOperationRunning);

                Complete(operations.SetRunningAsync(target, true));
                using var stoppingCancellation = new CancellationTokenSource();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                adapter.StopAction = () => release.Task;
                var stopping = operations.SetRunningAsync(
                    target, false, cancellationToken: stoppingCancellation.Token);
                stoppingCancellation.Cancel();
                Assert.False(stopping.IsCompleted);
                Assert.True(model.IsCaptureDataOperationRunning);

                release.SetResult();
                Assert.True(Complete(stopping).Succeeded);
                Assert.False(target.Controller.IsRunning);
                Assert.False(model.IsCaptureDataOperationRunning);
            });
        }

        [Fact]
        public void SetRunning_MarshalsBackgroundCallerAndRevalidatesRemovedTarget()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);

                Assert.True(Complete(Task.Run(() => operations.SetRunningAsync(target, true))).Succeeded);
                Assert.True(adapter.RanOnDispatcher);

                var stopping = Task.Run(() => operations.SetRunningAsync(target, false));
                model.CaptureDeviceContexts.Remove(target);
                Assert.Equal(CaptureOperationFailure.TargetNotFound, Complete(stopping).Failure);
                Assert.Equal(0, adapter.StopCount);
            });
        }

        [Fact]
        public void StopWithoutGlobalDataLease_RemainsAvailableDuringOtherCaptureOperation()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                target.Controller.SetRunning(true);
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller) { StopRequiresDataOperation = false };
                var operations = Create(model, adapter);
                Assert.True(model.TryBeginCaptureDataOperation(out var lease));

                using (lease)
                {
                    Assert.True(Complete(operations.SetRunningAsync(target, false)).Succeeded);
                    Assert.True(model.IsCaptureDataOperationRunning);
                }
                Assert.False(target.Controller.IsRunning);
            });
        }

        [Fact]
        public void ExplicitAnalysisLease_DoesNotRequireOrChangeSelectedContext()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                Assert.Null(model.SelectedDeviceContext);

                Assert.True(model.TryBeginAnalysis(target, out var lease));
                using (lease)
                {
                    Assert.True(model.IsAnalysisRunning);
                    Assert.Null(model.SelectedDeviceContext);
                }

                model.CaptureDeviceContexts.Remove(target);
                Assert.False(model.TryBeginAnalysis(target, out var removedLease));
                Assert.Null(removedLease);
            });
        }

        [Fact]
        public void AgentDisconnect_DrainsCommittedStartThroughSharedServiceAndStopsOnlyOwnedCapture()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                var human = new Context();
                human.Controller.SetRunning(true);
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var adapter = new Adapter(target.Controller)
                {
                    StartAction = () =>
                    {
                        starting.SetResult();
                        return release.Task;
                    }
                };
                var operations = Create(model, adapter);
                var runtime = new AgentRuntime();
                runtime.Enable();
                var session = runtime.Connect("client");
                var targetId = Guid.NewGuid();
                var permission = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(targetId));
                Finish(runtime.SetPermissionsAsync(session, [permission]));
                var descriptor = new AgentOperationDescriptor(
                    "capture.start", [permission], AgentOperationEffects.StateChange,
                    AgentOperationDescriptor.Hash(new { targetId }));
                var id = runtime.Submit(session, descriptor, "start", async context =>
                {
                    var result = await operations.SetRunningAsync(
                        target, true, cancellationToken: context.CancellationToken);
                    if (result.Changed)
                    {
                        context.OwnResource(new AgentResourceKey(AgentResourceKind.Capture, targetId),
                            "started-generation", async () =>
                            {
                                var stopped = await operations.SetRunningAsync(target, false);
                                return stopped.Succeeded ? AgentCleanupOutcome.Released : AgentCleanupOutcome.RecoveryRequired;
                            });
                    }
                    return JsonSerializer.SerializeToElement(result);
                }, cancellationToken => Task.FromResult(true));

                try
                {
                    Finish(starting.Task);
                    Assert.True(model.IsCaptureDataOperationRunning);
                    var ending = runtime.EndSessionAsync(session);
                    Assert.False(ending.IsCompleted);
                    release.SetResult();
                    Finish(ending);
                    Finish(runtime.WaitForCompletionAsync(session, id));

                    Assert.False(target.Controller.IsRunning);
                    Assert.True(human.Controller.IsRunning);
                    Assert.Same(human, model.SelectedDeviceContext);
                    Assert.Equal(1, adapter.StartCount);
                    Assert.Equal(1, adapter.StopCount);
                    Assert.Empty(runtime.GetOwnedResources());
                    Assert.False(model.IsCaptureDataOperationRunning);
                }
                finally
                {
                    release.TrySetResult();
                    Finish(runtime.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void TransitionReceiptAndFailureSnapshotRetainTheirOwnEpochAfterHumanRestart()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var initial = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);
                var receipt = Complete(operations.SetRunningAsync(target, true));

                target.Controller.SetRunning(false);
                target.Controller.SetRunning(true);
                var afterHuman = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                Assert.True(receipt.CaptureEpoch > initial.CaptureEpoch);
                Assert.True(afterHuman.CaptureEpoch > receipt.CaptureEpoch);

                Complete(operations.SetRunningAsync(target, false));
                adapter.StartAction = () =>
                {
                    target.Controller.SetRunning(true);
                    return Task.FromException(new IOException("partial startup"));
                };
                var error = Assert.Throws<IOException>(() => Complete(operations.SetRunningAsync(target, true)));
                var failure = CaptureTransitionFailure.Get(error);
                Assert.True(failure.IsRunning);
                Assert.True(Complete(model.CaptureCatalog.GetCapturesAsync()).Single().RecoveryRequired);
                target.Controller.SetRunning(false);
                target.Controller.SetRunning(true);
                Assert.True(Complete(model.CaptureCatalog.GetCapturesAsync()).Single().CaptureEpoch > failure.CaptureEpoch);
            });
        }

        [Fact]
        public void FailedProxyRestoreRetainsRecoveryEvenAfterTheListenerHasStopped()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context();
                model.CaptureDeviceContexts.Add(target);
                var adapter = new Adapter(target.Controller);
                var operations = Create(model, adapter);
                var runtime = new AgentRuntime();
                runtime.Enable();
                try
                {
                    var capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                    var session = runtime.Connect("client");
                    Finish(runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var application = new AgentCaptureApplication(runtime, model.CaptureCatalog, operations, () => true);
                    var starting = Complete(application.SetRunningAsync(session, capture.Id, capture.Revision, true, "start"));
                    Assert.True(runtime.Approve(starting));
                    Finish(runtime.WaitForCompletionAsync(session, starting));
                    Assert.True(target.Controller.IsRunning);
                    adapter.StopAction = () =>
                    {
                        target.Controller.SetRunning(false);
                        return Task.FromException(new IOException("proxy restore unconfirmed"));
                    };
                    capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                    var stopping = Complete(application.SetRunningAsync(session, capture.Id, capture.Revision, false, "stop"));
                    Assert.True(runtime.Approve(stopping));
                    Finish(runtime.WaitForCompletionAsync(session, stopping));
                    Assert.Equal(AgentOperationState.RecoveryRequired, runtime.GetOperation(session, stopping).State);
                    Assert.False(target.Controller.IsRunning);
                    Finish(runtime.EndSessionAsync(session));
                    Assert.Equal(AgentResourceState.RecoveryRequired, runtime.GetOwnedResources().Single().State);
                    Assert.True(Complete(model.CaptureCatalog.GetCapturesAsync()).Single().RecoveryRequired);
                }
                finally { Finish(runtime.DisposeAsync().AsTask()); }
            });
        }

        private static CaptureOperations Create(CaptureAppModel model, Adapter adapter) =>
            new(model, Dispatcher.CurrentDispatcher, _ => adapter);

        private static T Complete<T>(Task<T> task)
        {
            var timeout = Stopwatch.StartNew();
            while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The application operation did not complete.");
            return task.GetAwaiter().GetResult();
        }

        private static void Finish(Task task) => Complete(Observe(task));

        private static async Task<bool> Observe(Task task)
        {
            await task;
            return true;
        }

        private sealed class Context : ICaptureDeviceContext
        {
            internal Controller Controller { get; } = new();
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Test capture";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => Controller;
        }

        private sealed class LiveContext(
            IDeviceCaptureController controller,
            CaptureType captureType,
            DeviceType deviceType) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => deviceType;
            public CaptureType CaptureType => captureType;
            public string DeviceName => "Test capture";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }

        private sealed class Controller : IDeviceCaptureController
        {
            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;
            internal void SetRunning(bool value)
            {
                IsRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            }
            public void Initialize() { }
            public void LoadCaptures(IEnumerable<IDataset> tables) { }
            public void ClearAllCaptures() { }
            public Task<bool> CanCloseAsync() => Task.FromResult(true);
            public Task CloseAsync() => Task.CompletedTask;
        }

        private sealed class Adapter(Controller controller) : ICaptureLifecycleAdapter
        {
            public bool StopRequiresDataOperation { get; set; } = true;
            internal CaptureOperationFailure Failure { get; set; }
            internal Func<Task> StartAction { get; set; } = () => Task.CompletedTask;
            internal Func<Task> StopAction { get; set; } = () => Task.CompletedTask;
            internal int StartCount { get; private set; }
            internal int StopCount { get; private set; }
            internal int TransitionCount { get; private set; }
            internal int CompletedTransitions { get; private set; }
            internal bool RanOnDispatcher { get; private set; }
            public CaptureOperationFailure GetAvailability(bool running) => Failure;
            public IDisposable BeginTransition(bool running)
            {
                TransitionCount++;
                return new Transition(() => CompletedTransitions++);
            }
            public async Task StartAsync()
            {
                RanOnDispatcher = System.Windows.Application.Current.Dispatcher.CheckAccess();
                StartCount++;
                await StartAction();
                controller.SetRunning(true);
            }
            public async Task StopAsync(IProgress<WebProxyStopProgress> progress)
            {
                StopCount++;
                await StopAction();
                controller.SetRunning(false);
            }
        }

        private sealed class Transition(Action complete) : IDisposable
        {
            public void Dispose() => complete();
        }

        private sealed class ProgressRecorder(Action<WebProxyStopProgress> report)
            : IProgress<WebProxyStopProgress>
        {
            public void Report(WebProxyStopProgress value) => report(value);
        }

        private sealed class RecordingProxy : IWebServiceProxy
        {
#pragma warning disable CS0067
            public event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
            public event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
            public event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
            public event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
            public event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
            public event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
            public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
            public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
            public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
            public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
            public event EventHandler ProxyStopped;
#pragma warning restore CS0067
            internal Exception StartFailure { get; set; }
            internal int StartCount { get; private set; }
            internal int StopCount { get; private set; }
            public bool IsProxyEnabled { get; private set; }
            public BypassListModel BypassList { get; set; }
            public Func<SslConnectionRequestEventArgs, CancellationToken, Task<bool>> SslConnectionRequestPolicyAsync { get; set; }
            public Func<HttpRequestEventArgs, CancellationToken, Task<bool>> WebRequestPolicyAsync { get; set; }
            public Func<HttpResponseEventArgs, CancellationToken, Task<bool>> WebResponsePolicyAsync { get; set; }
            public Task StartProxyAsync(WebServiceProxyOptions options, CancellationToken cancellationToken = default)
            {
                StartCount++;
                if (StartFailure != null)
                {
                    return Task.FromException(StartFailure);
                }
                IsProxyEnabled = true;
                return Task.CompletedTask;
            }
            public Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null)
            {
                StopCount++;
                IsProxyEnabled = false;
                return Task.CompletedTask;
            }
            public void Reset() { }
        }
    }
}
