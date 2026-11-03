// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class DesktopAdministrationTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void CaptureNavigationIsExplicitApprovedAndRejectsChangedEpochWithoutReturningContent()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var human = new AdministrationTestData.Context("human");
                var target = new AdministrationTestData.Context("explicit");
                setup.Model.CaptureDeviceContexts.Add(human);
                setup.Model.CaptureDeviceContexts.Add(target);
                setup.Model.SelectedDeviceContext = human;
                ICaptureDeviceContext revealed = null;
                setup.Model.DesktopActions.Configure((context, _) =>
                {
                    Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                    revealed = context;
                    return Task.FromResult(true);
                }, null, null);
                setup.Grant(AgentCapability.Navigation);
                var descriptor = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single(capture => capture.DeviceName == "explicit");
                var request = new DesktopNavigationRequest(descriptor.Id, descriptor.Revision, descriptor.CaptureEpoch);
                var operation = Complete(setup.Agent.RevealAsync(setup.Session, request, "reveal-once", default));
                Assert.Null(revealed);
                Assert.Equal(AgentOperationState.AwaitingApproval, setup.Runtime.GetOperation(setup.Session, operation).State);
                setup.Model.SelectedDeviceContext = target;
                setup.Model.SelectedDeviceContext = human;
                setup.Approve(operation);
                Assert.Same(target, revealed);
                Assert.Same(human, setup.Model.SelectedDeviceContext);
                var result = setup.Runtime.GetResult(setup.Session, operation);
                Assert.True(result.GetProperty("revealed").GetBoolean());
                Assert.DoesNotContain("human", result.GetRawText());
                Assert.DoesNotContain("explicit", result.GetRawText());
                Assert.Equal(operation, Complete(setup.Agent.RevealAsync(setup.Session, request, "reveal-once", default)));
                descriptor = Complete(setup.Model.CaptureCatalog.GetCapturesAsync()).Single(capture => capture.Id == descriptor.Id);
                target.Controller.SetRunning(true);
                target.Controller.SetRunning(false);
                Assert.Equal(AgentAccessError.SourceChanged, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.RevealAsync(setup.Session, request, "stale", default))).Error);
            });
        }

        [Theory]
        [InlineData(DesktopNavigationKind.Record)]
        [InlineData(DesktopNavigationKind.Report)]
        public void RegisteredRecordAndReportProvenanceCannotBeForgedOrReusedAfterReplacement(DesktopNavigationKind kind)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Navigation);
                const string content = "private-record-body-and-report-source";
                var source = Guid.NewGuid();
                var snapshot = Guid.NewGuid();
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session,
                    [new(AgentCapability.Navigation, AgentScope.Application),
                        new(AgentCapability.SummaryRead, AgentScope.Source(source))]));
                var provenance = new DesktopTargetProvenance(
                    SourceId: source, SnapshotId: snapshot, RecordId: kind == DesktopNavigationKind.Record ? 42 : null,
                    ReportId: kind == DesktopNavigationKind.Report ? Guid.NewGuid() : null);
                var current = true;
                var reveals = 0;
                Task<bool> Reveal(CancellationToken _)
                {
                    Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                    Assert.NotEmpty(content);
                    reveals++;
                    return Task.FromResult(true);
                }
                var target = kind == DesktopNavigationKind.Record
                    ? setup.Model.DesktopActions.RegisterRecord(provenance, 7, () => current, Reveal)
                    : setup.Model.DesktopActions.RegisterReport(provenance, 7, () => current, Reveal);
                var listing = Complete(setup.Agent.NavigationTargetsAsync(setup.Session, 0, default));
                Assert.DoesNotContain(content, listing.GetRawText());
                Assert.Equal(target.Id, listing.GetProperty("targets")[0].GetProperty("id").GetGuid());
                var request = new DesktopNavigationRequest(target.Id, target.Revision, target.Epoch);
                var pending = Complete(setup.Agent.RevealAsync(setup.Session, request, "stale-registered", default));
                current = false;
                setup.Approve(pending);
                Assert.Equal(AgentOperationState.SourceChanged, setup.Runtime.GetOperation(setup.Session, pending).State);
                Assert.Equal(0, reveals);
                current = true;
                var operation = Complete(setup.Agent.RevealAsync(setup.Session, request, "registered", default));
                setup.Approve(operation);
                Assert.Equal(1, reveals);
                Assert.DoesNotContain(content, setup.Runtime.GetResult(setup.Session, operation).GetRawText());
                Assert.True(setup.Model.DesktopActions.Unregister(target.Id));
                Assert.Empty(Complete(setup.Model.DesktopActions.GetNavigationTargetsAsync()));
                Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.RevealAsync(setup.Session, request, "released", default)));
                Assert.Throws<AdministrationException>(() => setup.Model.DesktopActions.RegisterRecord(
                    new(SourceId: source, RecordId: 42), 1, () => true, Reveal));
            });
        }

        [Fact]
        public void LifecycleCallbackRunsOnlyAfterTheOperationCompletesAndCannotSelfDeadlockDrain()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Lifecycle);
                Guid operation = default;
                var callbacks = 0;
                Task drain = null;
                setup.Model.DesktopActions.Configure(null, () =>
                {
                    Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                    Assert.Equal(AgentOperationState.Succeeded, setup.Runtime.GetOperation(setup.Session, operation).State);
                    Assert.True(setup.Runtime.WaitForCompletionAsync(setup.Session, operation).IsCompleted);
                    callbacks++;
                    drain = setup.Runtime.DisableAsync();
                }, null);
                var before = Complete(setup.Model.DesktopActions.InspectLifecycleAsync());
                operation = Complete(setup.Agent.RequestLifecycleAsync(setup.Session,
                    before.Revision, DesktopLifecycleAction.Close, "normal-close", default));
                Assert.Equal(0, callbacks);
                Assert.True(setup.Runtime.Approve(operation));
                Finish(setup.Runtime.WaitForCompletionAsync(setup.Session, operation));
                PumpUntil(() => callbacks == 1);
                Finish(drain);
                Finish(setup.Agent.DrainAsync());
                Assert.Equal(1, callbacks);
            });
        }

        [Fact]
        public void RecordNavigationRequiresExactOriginalSourceSummaryOnDiscoveryApprovalAndDispatch()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Navigation);
                var source = Guid.NewGuid();
                var reveals = 0;
                var target = setup.Model.DesktopActions.RegisterRecord(
                    new(SourceId: source, SnapshotId: Guid.NewGuid(), RecordId: 99), 1, () => true,
                    _ => { reveals++; return Task.FromResult(true); });
                Assert.Empty(Complete(setup.Agent.NavigationTargetsAsync(setup.Session, 0, default))
                    .GetProperty("targets").EnumerateArray());
                var request = new DesktopNavigationRequest(target.Id, target.Revision, target.Epoch);
                Assert.Equal(AgentAccessError.PermissionDenied, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.RevealAsync(setup.Session, request, "no-source", default))).Error);
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session,
                    [new(AgentCapability.Navigation, AgentScope.Application),
                        new(AgentCapability.SummaryRead, AgentScope.Source(Guid.NewGuid()))]));
                Assert.Empty(Complete(setup.Agent.NavigationTargetsAsync(setup.Session, 0, default))
                    .GetProperty("targets").EnumerateArray());
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session,
                    [new(AgentCapability.Navigation, AgentScope.Application),
                        new(AgentCapability.SummaryRead, AgentScope.Source(source))]));
                var operation = Complete(setup.Agent.RevealAsync(setup.Session, request, "exact-source", default));
                Assert.Contains(new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source)),
                    setup.Runtime.GetOperation(setup.Session, operation).Requirements);
                setup.Grant(AgentCapability.Navigation);
                Assert.False(setup.Runtime.Approve(operation));
                Assert.Equal(AgentOperationState.Cancelled, setup.Runtime.GetOperation(setup.Session, operation).State);
                Assert.Equal(0, reveals);
            });
        }

        [Fact]
        public void AnotherConnectionCannotListOrRevealAnOwnedResultNavigationHandle()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var source = Guid.NewGuid();
                var permissions = new[] { new AgentPermission(AgentCapability.Navigation, AgentScope.Application),
                    new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source)) };
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session, permissions));
                var other = setup.Runtime.Connect("another navigation client");
                Finish(setup.Runtime.SetPermissionsAsync(other, permissions));
                var target = setup.Model.DesktopActions.RegisterRecord(
                    new(SourceId: source, SnapshotId: Guid.NewGuid(), RecordId: 99), 1, () => true,
                    _ => Task.FromResult(true), ownerSessionId: setup.Session.Id);
                var own = Complete(setup.Agent.NavigationTargetsAsync(setup.Session, 0, default));
                Assert.Single(own.GetProperty("targets").EnumerateArray());
                Assert.DoesNotContain("ownerSessionId", own.GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.Empty(Complete(setup.Agent.NavigationTargetsAsync(other, 0, default)).GetProperty("targets").EnumerateArray());
                Assert.Equal(AgentAccessError.OperationNotFound, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.RevealAsync(other,
                        new(target.Id, target.Revision, target.Epoch), "copied-target", default))).Error);
                Assert.Empty(setup.Runtime.GetOperations());
            });
        }

        [Theory]
        [InlineData("deny")]
        [InlineData("cancel")]
        [InlineData("permissions")]
        [InlineData("stale")]
        public void DeniedCancelledChangedPermissionAndStaleLifecycleRequestsNeverInvokeDesktop(string change)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Lifecycle);
                var callbacks = 0;
                var allowed = true;
                setup.Model.DesktopActions.Configure(null, () => callbacks++, null, () => allowed);
                var snapshot = Complete(setup.Model.DesktopActions.InspectLifecycleAsync());
                var operation = Complete(setup.Agent.RequestLifecycleAsync(setup.Session,
                    snapshot.Revision, DesktopLifecycleAction.Close, "guarded", default));
                if (change == "deny")
                    Assert.True(setup.Runtime.Deny(operation));
                else if (change == "cancel")
                    Assert.True(Complete(setup.Runtime.CancelAsync(setup.Session, operation)));
                else if (change == "permissions")
                    setup.Grant(AgentCapability.Lifecycle, AgentCapability.Navigation);
                else
                {
                    allowed = false;
                    setup.Model.DesktopActions.NotifyLifecycleChanged();
                    setup.Approve(operation);
                }
                Finish(setup.Agent.DrainAsync());
                WpfTestHost.Pump();
                Assert.Equal(0, callbacks);
                Assert.False(setup.Runtime.Approve(operation));
            });
        }

        [Fact]
        public void ElevationIsOnlyAnApprovedTrustedDesktopRequestAndRetriesDoNotRepeatIt()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Lifecycle);
                var prompts = 0;
                setup.Model.DesktopActions.Configure(null, null, () => prompts++);
                var snapshot = Complete(setup.Model.DesktopActions.InspectLifecycleAsync());
                Assert.False(snapshot.CanRequestClose);
                Assert.True(snapshot.CanRequestElevatedRestart);
                var operation = Complete(setup.Agent.RequestLifecycleAsync(setup.Session, snapshot.Revision,
                    DesktopLifecycleAction.RequestElevatedRestart, "elevation-prompt", default));
                Assert.Equal(0, prompts);
                Assert.Contains("No silent elevation", setup.Runtime.GetApprovalDetails(operation));
                setup.Approve(operation);
                PumpUntil(() => prompts == 1);
                Finish(setup.Agent.DrainAsync());
                var result = setup.Runtime.GetResult(setup.Session, operation);
                Assert.True(result.GetProperty("restartSuccessIsNotConfirmed").GetBoolean());
                Assert.True(result.GetProperty("normalCloseChecksRemainAuthoritative").GetBoolean());
                Assert.False(result.TryGetProperty("restarted", out var ignoredRestart));
                Assert.Equal(operation, Complete(setup.Agent.RequestLifecycleAsync(setup.Session, snapshot.Revision,
                    DesktopLifecycleAction.RequestElevatedRestart, "elevation-prompt", default)));
                Assert.Equal(1, prompts);
            });
        }

        [Fact]
        public void RevokeBetweenCompletedJobAndQueuedDesktopCallbackIsCheckedAtActualDispatch()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Lifecycle);
                var prompts = 0;
                setup.Model.DesktopActions.Configure(null, () => prompts++, null);
                var paused = new PausedDesktop(setup.Model.DesktopActions);
                var agent = new AgentAdministrationApplication(setup.Runtime, setup.Model.HostPolicyOperations,
                    setup.Preferences, setup.Diagnostics, paused, () => true);
                try
                {
                    var snapshot = Complete(paused.InspectLifecycleAsync());
                    var operation = Complete(agent.RequestLifecycleAsync(setup.Session, snapshot.Revision,
                        DesktopLifecycleAction.Close, "queued-close", default));
                    setup.Approve(operation);
                    PumpUntil(() => paused.Entered.Task.IsCompleted);
                    Finish(setup.Runtime.SetPermissionsAsync(setup.Session, []));
                    paused.Continue.TrySetResult();
                    Finish(agent.DrainAsync());
                    Assert.Equal(0, prompts);
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    Finish(agent.DisposeAsync().AsTask());
                }
            });
        }

        private sealed class PausedDesktop(IDesktopActions inner) : IDesktopActions
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<IReadOnlyList<DesktopNavigationTarget>> GetNavigationTargetsAsync(CancellationToken cancellationToken = default) =>
                inner.GetNavigationTargetsAsync(cancellationToken);
            public Task<DesktopNavigationTarget> PrepareNavigationAsync(DesktopNavigationRequest request, CancellationToken cancellationToken = default) =>
                inner.PrepareNavigationAsync(request, cancellationToken);
            public Task<DesktopNavigationReceipt> RevealAsync(DesktopNavigationRequest request, CancellationToken cancellationToken = default) =>
                inner.RevealAsync(request, cancellationToken);
            public Task<DesktopLifecycleSnapshot> InspectLifecycleAsync(CancellationToken cancellationToken = default) =>
                inner.InspectLifecycleAsync(cancellationToken);
            public Task<DesktopLifecyclePlan> PrepareLifecycleAsync(
                long expectedRevision, DesktopLifecycleAction action, CancellationToken cancellationToken = default) =>
                inner.PrepareLifecycleAsync(expectedRevision, action, cancellationToken);
            public async Task RequestLifecycleAsync(DesktopLifecyclePlan plan, Func<bool> stillAuthorized, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                await inner.RequestLifecycleAsync(plan, stillAuthorized, cancellationToken);
            }
        }
    }
}
