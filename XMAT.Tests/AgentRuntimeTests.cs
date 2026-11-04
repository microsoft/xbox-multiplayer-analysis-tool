// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentRuntimeTests
    {
        private static readonly AgentPermission Summary =
            new(AgentCapability.SummaryRead, AgentScope.Application);

        [Fact]
        public async Task DisabledAndUngrantedSessionsCannotExecute()
        {
            await using var runtime = new AgentRuntime();
            AssertError(AgentAccessError.AccessDisabled, () => runtime.Connect("client"));
            runtime.Enable();
            var session = runtime.Connect("client");
            var calls = 0;

            AssertError(AgentAccessError.PermissionDenied, () => runtime.Submit(
                session, Descriptor(Summary), "first",
                _ => { calls++; return Done(); }, Current));
            Assert.Empty(runtime.GetOperations());
            Assert.Equal(0, calls);
            Assert.Empty(runtime.GetSession(session).Permissions);
        }

        [Fact]
        public async Task GrantsAreExactSourceScopesAndSnapshotsCannotGrantPermissions()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var allowed = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            var other = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            var permissions = new[] { allowed };
            await runtime.SetPermissionsAsync(session, permissions);
            permissions[0] = other;

            runtime.Demand(session, allowed);
            AssertError(AgentAccessError.PermissionDenied, () => runtime.Demand(session, other));
            Assert.Throws<ArgumentException>(() =>
                new AgentPermission(AgentCapability.RawDataRead, AgentScope.Application));
            Assert.Throws<ArgumentException>(() => AgentScope.Capture(Guid.Empty));
            Assert.Equal(allowed, Assert.Single(runtime.GetSession(session).Permissions));
        }

        [Fact]
        public async Task ScopedGrantEditsPreserveOtherTargetsAndOnlyRevokeTheEditedScopes()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("multiple targets");
            var first = new[]
            {
                Control(Guid.NewGuid()),
                new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(Guid.NewGuid())),
                new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(Guid.NewGuid())),
                new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(Guid.NewGuid()))
            };
            var second = new[]
            {
                Control(Guid.NewGuid()),
                new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(Guid.NewGuid())),
                new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(Guid.NewGuid())),
                new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(Guid.NewGuid()))
            };
            await runtime.SetPermissionsAsync(session, first.Append(Summary));
            var cleaned = 0;
            var capture = new AgentResourceKey(AgentResourceKind.Capture, first[0].Scope.Id);
            var id = runtime.Submit(session, Descriptor(first[0], AgentOperationEffects.StateChange), "own first", context =>
            {
                context.OwnResource(capture, "first", () =>
                {
                    cleaned++;
                    return Task.FromResult(AgentCleanupOutcome.Released);
                });
                return Done();
            }, Current);
            await Wait(runtime, session, id);

            var secondScopes = second.Select(permission => permission.Scope).Append(AgentScope.Application).ToArray();
            await runtime.SetPermissionsAsync(session, second.Append(Summary), editedScopes: secondScopes);
            Assert.All(first.Concat(second).Append(Summary), permission => runtime.Demand(session, permission));
            Assert.Equal(0, cleaned);
            Assert.Equal(capture, Assert.Single(runtime.GetOwnedResources()).Resource);

            await runtime.SetPermissionsAsync(session, [Summary], editedScopes: secondScopes);
            Assert.All(first, permission => runtime.Demand(session, permission));
            Assert.DoesNotContain(runtime.GetSession(session).Permissions, permission => second.Contains(permission));
            Assert.Equal(0, cleaned);
            await runtime.SetPermissionsAsync(session, [Summary],
                editedScopes: first.Select(permission => permission.Scope).Append(AgentScope.Application));
            Assert.Equal(1, cleaned);
            Assert.Equal(Summary, Assert.Single(runtime.GetSession(session).Permissions));
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task ScopedGrantEditsCannotChangeAPermissionOutsideTheirSelectedScopes()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("scope validation");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var control = Control(Guid.NewGuid());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                runtime.SetPermissionsAsync(session, [control], editedScopes: [AgentScope.Application]));
            Assert.Equal(Summary, Assert.Single(runtime.GetSession(session).Permissions));
        }

        [Fact]
        public async Task TemporaryCleanupRetriesDoNotRetryExternalCaptureRecovery()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("temporary recovery");
            var control = Control(Guid.NewGuid());
            var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [control, raw]);
            var temporary = new AgentResourceKey(AgentResourceKind.QueryResult, Guid.NewGuid());
            var capture = new AgentResourceKey(AgentResourceKind.Capture, control.Scope.Id);
            var retryReady = false;
            var captureCleanups = 0;
            var id = runtime.Submit(session, new AgentOperationDescriptor("own", [control, raw],
                AgentOperationEffects.StateChange, "fingerprint", "reviewed"), "own", context =>
            {
                context.OwnResource(temporary, "query", () => Task.FromResult(retryReady
                    ? AgentCleanupOutcome.Released : AgentCleanupOutcome.RecoveryRequired));
                context.OwnResource(capture, "capture", () =>
                {
                    captureCleanups++;
                    return Task.FromResult(AgentCleanupOutcome.RecoveryRequired);
                });
                return Done();
            }, Current);
            await Wait(runtime, session, id);
            await runtime.EndSessionAsync(session);
            Assert.Equal(2, runtime.GetOwnedResources().Count);
            retryReady = true;
            await runtime.RetryTemporaryCleanupAsync();
            Assert.Equal(capture, Assert.Single(runtime.GetOwnedResources()).Resource);
            Assert.Equal(1, captureCleanups);
            Assert.True(runtime.TakeOver(capture));
        }

        [Theory]
        [InlineData(AgentResourceKind.Capture)]
        [InlineData(AgentResourceKind.Script)]
        public async Task UnrelatedGrantEditsAndDisconnectDoNotRetryExternalRecovery(AgentResourceKind kind)
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("external recovery policy");
            var control = new AgentPermission(kind == AgentResourceKind.Capture
                ? AgentCapability.CaptureControl : AgentCapability.ScriptExecute, AgentScope.Capture(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [control, Summary]);
            var capture = new AgentResourceKey(kind, control.Scope.Id);
            var attempts = 0;
            var id = runtime.Submit(session, Descriptor(control, AgentOperationEffects.StateChange), "own", context =>
            {
                context.OwnResource(capture, "epoch", () =>
                {
                    attempts++;
                    return Task.FromResult(AgentCleanupOutcome.RecoveryRequired);
                });
                return Done();
            }, Current);
            if (kind == AgentResourceKind.Script)
                Assert.True(runtime.Approve(id));
            await Wait(runtime, session, id);
            await runtime.SetPermissionsAsync(session, [Summary]);
            Assert.Equal(1, attempts);
            var other = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [Summary, other]);
            await runtime.EndSessionAsync(session);
            await runtime.RetryTemporaryCleanupAsync();
            Assert.Equal(1, attempts);
            Assert.Equal(AgentResourceState.RecoveryRequired, Assert.Single(runtime.GetOwnedResources()).State);
            Assert.True(runtime.TakeOver(capture));
        }

        [Fact]
        public async Task SessionSnapshotRoundTripPreservesScopeWithoutCreatingAuthority()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var copied = JsonSerializer.Deserialize<AgentSessionSnapshot>(
                JsonSerializer.Serialize(runtime.GetSession(session)));

            Assert.Equal(permission, Assert.Single(copied.Permissions));
            Assert.Throws<ArgumentException>(() => new AgentScope(AgentScopeKind.Application, Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => new AgentScope(AgentScopeKind.Source, Guid.Empty));
            var ungranted = runtime.Connect("client");
            AssertError(AgentAccessError.PermissionDenied, () => runtime.Demand(ungranted, copied.Permissions[0]));
        }

        [Fact]
        public async Task IdenticallyNamedClientsCannotBorrowSessionsOrResults()
        {
            await using var runtime = Enabled();
            await using var otherRuntime = Enabled();
            var first = runtime.Connect("same declared name");
            var second = runtime.Connect("same declared name");
            var foreign = otherRuntime.Connect("same declared name");
            await runtime.SetPermissionsAsync(first, [Summary]);
            await runtime.SetPermissionsAsync(second, [Summary]);
            var id = runtime.Submit(first, Descriptor(Summary), "first", _ => Done(), Current);
            await Wait(runtime, first, id);

            AssertError(AgentAccessError.SessionClosed, () => runtime.GetSession(foreign));
            AssertError(AgentAccessError.OperationNotFound, () => runtime.GetResult(second, id));
            AssertError(AgentAccessError.OperationNotFound, () => runtime.CancelAsync(second, id).GetAwaiter().GetResult());
            Assert.True(runtime.GetResult(first, id).GetProperty("ok").GetBoolean());
        }

        [Fact]
        public async Task SensitiveActionWaitsForDesktopApprovalAndRevalidatesSource()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var script = new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [script]);
            var calls = 0;
            var descriptor = new AgentOperationDescriptor(
                "script.enable", [script], AgentOperationEffects.None,
                AgentOperationDescriptor.Hash(new { sourceHash = "approved-revision" }),
                "private script source shown only by the desktop adapter");
            var id = runtime.Submit(session, descriptor, "enable",
                _ => { calls++; return Done(); }, _ => Task.FromResult(false));

            Assert.Equal(AgentOperationState.AwaitingApproval, runtime.GetOperation(session, id).State);
            Assert.Equal(0, calls);
            Assert.DoesNotContain("private script source", JsonSerializer.Serialize(runtime.GetOperation(session, id)));
            Assert.Contains("private script source", runtime.GetApprovalDetails(id));
            Assert.True(runtime.Approve(id));
            Assert.Null(runtime.GetApprovalDetails(id));
            await Wait(runtime, session, id);

            Assert.Equal(AgentOperationState.SourceChanged, runtime.GetOperation(session, id).State);
            Assert.False(runtime.GetOperation(session, id).ExecutionStarted);
            Assert.Equal(0, calls);
            Assert.False(runtime.Approve(id));
        }

        [Fact]
        public async Task ApprovalIsOneShotAndPendingRequestsAreBounded()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var descriptor = Descriptor(permission, AgentOperationEffects.DataDeletion);
            var id = runtime.Submit(session, descriptor, "one", _ => Done(), Current);

            AssertError(AgentAccessError.LimitExceeded, () =>
                runtime.Submit(session, descriptor, "two", _ => Done(), Current));
            Assert.True(runtime.Deny(id));
            Assert.False(runtime.Approve(id));
            Assert.Equal(AgentOperationState.Denied, runtime.GetOperation(session, id).State);

            var next = runtime.Submit(session, descriptor, "two", _ => Done(), Current);
            Assert.True(runtime.Approve(next));
            await Wait(runtime, session, next);
            Assert.False(runtime.Approve(next));
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, next).State);
        }

        [Fact]
        public async Task UnchangedGrantsPreservePendingApprovalButPendingWorkIsNotComplete()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.DataDeletion),
                "delete", _ => Done(), Current);

            AssertError(AgentAccessError.ApprovalRequired, () => runtime.WaitForCompletionAsync(session, id));
            await runtime.SetPermissionsAsync(session, [permission]);
            Assert.Equal(AgentOperationState.AwaitingApproval, runtime.GetOperation(session, id).State);
            Assert.True(runtime.Approve(id));
            await Wait(runtime, session, id);
            var completed = runtime.GetOperation(session, id);
            Assert.True(completed.CompletedAt >= completed.CreatedAt);
            Assert.Equal(AgentOperationEffects.DataDeletion, completed.Effects);
        }

        [Fact]
        public async Task RetriesShareAnOperationAndChangedArgumentsAreRejected()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var release = Gate();
            var started = Gate();
            var calls = 0;
            var descriptor = Descriptor(Summary);
            var first = runtime.Submit(session, descriptor, "same", async _ =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task;
                return Value();
            }, Current);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(first, runtime.Submit(session, descriptor, "same", _ => Done(), Current));
                AssertError(AgentAccessError.IdempotencyConflict, () => runtime.Submit(session,
                    Descriptor(Summary, argument: "different"), "same", _ => Done(), Current));
            }
            finally { release.TrySetResult(); }
            await Wait(runtime, session, first);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task CancellationIsRequestedWithoutPretendingCommittedWorkWasUndone()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var started = Gate();
            var release = Gate();
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange),
                "start", async _ =>
                {
                    started.SetResult();
                    await release.Task;
                    return Value();
                }, Current);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(await runtime.CancelAsync(session, id));
                Assert.Equal(AgentOperationState.CancellationRequested, runtime.GetOperation(session, id).State);
                Assert.False(runtime.WaitForCompletionAsync(session, id).IsCompleted);
            }
            finally { release.TrySetResult(); }
            await Wait(runtime, session, id);
            var completed = runtime.GetOperation(session, id);
            Assert.Equal(AgentOperationState.Succeeded, completed.State);
            Assert.True(completed.EffectsMayHaveOccurred);
        }

        [Fact]
        public async Task CooperativeCancellationFinishesAndKeepsFailureDetailsPrivate()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var started = Gate();
            var id = runtime.Submit(session, Descriptor(Summary), "cancel", async context =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return Value();
            }, Current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.CancelAsync(session, id);
            await Wait(runtime, session, id);
            Assert.Equal(AgentOperationState.Cancelled, runtime.GetOperation(session, id).State);

            var failure = runtime.Submit(session, Descriptor(Summary), "failure",
                _ => Task.FromException<JsonElement>(new IOException("credential in exception")), Current);
            await Wait(runtime, session, failure);
            var snapshot = runtime.GetOperation(session, failure);
            Assert.Equal(AgentAccessError.ExecutionFailed, snapshot.Error);
            Assert.DoesNotContain("credential", JsonSerializer.Serialize(snapshot));
        }

        [Fact]
        public async Task FatalFailureStopsAcceptingWorkAndPropagatesDuringDrain()
        {
            var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var id = runtime.Submit(session, Descriptor(Summary), "fatal",
                _ => Task.FromException<JsonElement>(new BadImageFormatException("fatal test failure")), Current);

            await Assert.ThrowsAsync<BadImageFormatException>(() => Wait(runtime, session, id));
            Assert.False(runtime.IsEnabled);
            AssertError(AgentAccessError.SessionClosed, () => runtime.Demand(session, Summary));
            AssertError(AgentAccessError.OperationUnavailable, runtime.Enable);
            await Assert.ThrowsAsync<BadImageFormatException>(() => runtime.DisposeAsync().AsTask());
        }

        [Theory]
        [InlineData(AgentAccessError.SourceChanged, AgentOperationState.SourceChanged)]
        [InlineData(AgentAccessError.RecoveryRequired, AgentOperationState.RecoveryRequired)]
        public async Task ApplicationCommitChecksReportStaleOrUncertainOutcomes(
            AgentAccessError error, AgentOperationState expected)
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange),
                "change", _ => Task.FromException<JsonElement>(new AgentAccessException(error)), Current);
            await Wait(runtime, session, id);

            var result = runtime.GetOperation(session, id);
            Assert.Equal(expected, result.State);
            Assert.True(result.EffectsMayHaveOccurred);
            Assert.False(result.HasResult);
        }

        [Fact]
        public async Task PermissionRevocationDrainsWorkAndDoesNotResurrectOldResults()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [permission]);
            var started = Gate();
            var release = Gate();
            var id = runtime.Submit(session, Descriptor(permission), "raw", async _ =>
            {
                started.SetResult();
                await release.Task;
                return JsonSerializer.SerializeToElement(new { secret = "private captured body" });
            }, Current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var changing = runtime.SetPermissionsAsync(session, []);
            try
            {
                Assert.True(runtime.GetSession(session).PermissionsUpdating);
                Assert.False(changing.IsCompleted);
                AssertError(AgentAccessError.PermissionsUpdating, () => runtime.GetResult(session, id));
            }
            finally { release.TrySetResult(); }
            await changing.WaitAsync(TimeSpan.FromSeconds(10));
            AssertError(AgentAccessError.PermissionDenied, () => runtime.GetResult(session, id));
            await runtime.SetPermissionsAsync(session, [permission]);
            AssertError(AgentAccessError.ResultUnavailable, () => runtime.GetResult(session, id));
        }

        [Fact]
        public async Task PermissionChangePreservesUnaffectedRunningWork()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [Summary, raw]);
            var summaryStarted = Gate();
            var rawStarted = Gate();
            var releaseSummary = Gate();
            var releaseRaw = Gate();
            var summaryId = runtime.Submit(session, Descriptor(Summary), "summary", async _ =>
            {
                summaryStarted.SetResult();
                await releaseSummary.Task;
                return Value();
            }, Current);
            var rawId = runtime.Submit(session, Descriptor(raw), "raw", async _ =>
            {
                rawStarted.SetResult();
                await releaseRaw.Task;
                return Value();
            }, Current);
            await Task.WhenAll(summaryStarted.Task, rawStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            var changing = runtime.SetPermissionsAsync(session, [Summary]);
            try
            {
                releaseSummary.SetResult();
                await Wait(runtime, session, summaryId);
                Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, summaryId).State);
                Assert.True(runtime.GetOperation(session, summaryId).HasResult);
                Assert.False(changing.IsCompleted);
            }
            finally
            {
                releaseSummary.TrySetResult();
                releaseRaw.TrySetResult();
            }
            await changing.WaitAsync(TimeSpan.FromSeconds(10));
            await Wait(runtime, session, rawId);
            Assert.True(runtime.GetResult(session, summaryId).GetProperty("ok").GetBoolean());
            Assert.False(runtime.GetOperation(session, rawId).HasResult);
        }

        [Fact]
        public async Task ReducedResponseBudgetAppliesToPreviouslyCompletedResults()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var id = runtime.Submit(session, Descriptor(Summary), "result",
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { text = new string('x', 300) })),
                Current);
            await Wait(runtime, session, id);
            Assert.Equal(300, runtime.GetResult(session, id).GetProperty("text").GetString().Length);

            await runtime.SetPermissionsAsync(session, [Summary], new AgentLimits(responseBytes: 256));
            AssertError(AgentAccessError.LimitExceeded, () => runtime.GetResult(session, id));
        }

        [Fact]
        public async Task DisconnectWaitsForLateOwnershipRegistrationBeforeCleanup()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            var started = Gate();
            var release = Gate();
            var cleanupCalls = 0;
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange),
                "start", async context =>
                {
                    started.SetResult();
                    await release.Task;
                    context.OwnResource(key, "capture-generation-1", () =>
                    {
                        Interlocked.Increment(ref cleanupCalls);
                        return Task.FromResult(AgentCleanupOutcome.Released);
                    });
                    return Value();
                }, Current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var ending = runtime.EndSessionAsync(session);
            try
            {
                Assert.False(ending.IsCompleted);
                AssertError(AgentAccessError.SessionClosed, () => runtime.Demand(session, permission));
            }
            finally { release.TrySetResult(); }
            await ending.WaitAsync(TimeSpan.FromSeconds(10));
            await Wait(runtime, session, id);

            Assert.Equal(1, cleanupCalls);
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Equal(AgentSessionState.Disconnected, runtime.GetSession(session).State);
            AssertError(AgentAccessError.SessionClosed, () => runtime.GetResult(session, id));
        }

        [Fact]
        public async Task DesktopTakeoverPreventsAgentCleanup()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            var cleanups = 0;
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange), "own", context =>
            {
                context.OwnResource(key, "one", () =>
                {
                    cleanups++;
                    return Task.FromResult(AgentCleanupOutcome.Released);
                });
                return Done();
            }, Current);
            await Wait(runtime, session, id);
            Assert.True(runtime.TakeOver(key));
            await runtime.EndSessionAsync(session, revoked: true);

            Assert.Equal(0, cleanups);
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task DesktopTakeoverCannotBeUndoneByReclaimingTheSameResourceRevision()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            Task<JsonElement> Own(AgentExecutionContext context)
            {
                context.OwnResource(key, "same-generation", () => Task.FromResult(AgentCleanupOutcome.Released));
                return Done();
            }
            var descriptor = Descriptor(permission, AgentOperationEffects.StateChange);
            var id = runtime.Submit(session, descriptor, "first", Own, Current);
            await Wait(runtime, session, id);
            Assert.True(runtime.TakeOver(key));

            var reclaiming = runtime.Submit(session, descriptor, "second", Own, Current);
            await Wait(runtime, session, reclaiming);
            Assert.Equal(AgentAccessError.OwnershipConflict, runtime.GetOperation(session, reclaiming).Error);
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task TakeoverCannotRaceACommittedCleanup()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            var cleaning = Gate();
            var release = Gate();
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange),
                "own", context =>
                {
                    context.OwnResource(key, "one", async () =>
                    {
                        cleaning.SetResult();
                        await release.Task;
                        return AgentCleanupOutcome.Released;
                    });
                    return Done();
                }, Current);
            await Wait(runtime, session, id);
            var ending = runtime.EndSessionAsync(session);
            try
            {
                await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(runtime.TakeOver(key));
                Assert.Equal(AgentResourceState.Cleaning, Assert.Single(runtime.GetOwnedResources()).State);
            }
            finally { release.TrySetResult(); }
            await ending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UncertainOrFailedCleanupRemainsVisibleUntilHumanTakeover(bool throws)
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(session, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            var id = runtime.Submit(session, Descriptor(permission, AgentOperationEffects.StateChange), "own", context =>
            {
                context.OwnResource(key, "one", () => throws
                    ? Task.FromException<AgentCleanupOutcome>(new IOException("private endpoint"))
                    : Task.FromResult(AgentCleanupOutcome.RecoveryRequired));
                return Done();
            }, Current);
            await Wait(runtime, session, id);
            await runtime.EndSessionAsync(session);

            Assert.Equal(AgentResourceState.RecoveryRequired, Assert.Single(runtime.GetOwnedResources()).State);
            runtime.TrimDisconnectedHistory(retain: 0);
            Assert.Equal(AgentSessionState.Disconnected, runtime.GetSession(session).State);
            Assert.True(runtime.TakeOver(key));
            Assert.Empty(runtime.GetOwnedResources());
            runtime.TrimDisconnectedHistory(retain: 0);
            AssertError(AgentAccessError.SessionClosed, () => runtime.GetSession(session));
        }

        [Fact]
        public async Task OneSessionCannotStealAnotherSessionsOwnedResource()
        {
            await using var runtime = Enabled();
            var first = runtime.Connect("first");
            var second = runtime.Connect("second");
            var permission = Control(Guid.NewGuid());
            await runtime.SetPermissionsAsync(first, [permission]);
            await runtime.SetPermissionsAsync(second, [permission]);
            var key = new AgentResourceKey(AgentResourceKind.Capture, permission.Scope.Id);
            Task<JsonElement> Own(AgentExecutionContext context)
            {
                context.OwnResource(key, "one", () => Task.FromResult(AgentCleanupOutcome.Released));
                return Done();
            }
            var descriptor = Descriptor(permission, AgentOperationEffects.StateChange);
            var owner = runtime.Submit(first, descriptor, "own", Own, Current);
            await Wait(runtime, first, owner);
            runtime.EnsureOwnershipAvailable(first, key);
            AssertError(AgentAccessError.OwnershipConflict, () => runtime.EnsureOwnershipAvailable(second, key));
            var stealing = runtime.Submit(second, descriptor, "steal", Own, Current);
            await Wait(runtime, second, stealing);

            Assert.Equal(AgentAccessError.OwnershipConflict, runtime.GetOperation(second, stealing).Error);
            Assert.Equal(first.Id, Assert.Single(runtime.GetOwnedResources()).SessionId);
            await runtime.EndSessionAsync(second);
            Assert.Single(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task ReadOnlyOperationCannotClaimAMutableResourceOrRegisterAfterCompletion()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            AgentExecutionContext saved = null;
            var key = new AgentResourceKey(AgentResourceKind.Capture, Guid.NewGuid());
            var id = runtime.Submit(session, Descriptor(Summary), "bad ownership", context =>
            {
                saved = context;
                context.OwnResource(key, "one", () => Task.FromResult(AgentCleanupOutcome.Released));
                return Done();
            }, Current);
            await Wait(runtime, session, id);

            Assert.Equal(AgentAccessError.PermissionDenied, runtime.GetOperation(session, id).Error);
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Throws<InvalidOperationException>(() =>
                saved.OwnResource(key, "two", () => Task.FromResult(AgentCleanupOutcome.Released)));
        }

        [Fact]
        public async Task ResultAndHistoryBudgetsFailExplicitlyWithoutReplayingOldKeys()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary],
                new AgentLimits(responseBytes: 256, maximumOperations: 1));
            var descriptor = Descriptor(Summary);
            var id = runtime.Submit(session, descriptor, "one",
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { value = new string('x', 300) })),
                Current);
            await Wait(runtime, session, id);

            Assert.Equal(AgentAccessError.LimitExceeded, runtime.GetOperation(session, id).Error);
            Assert.False(runtime.GetOperation(session, id).HasResult);
            Assert.Equal(id, runtime.Submit(session, descriptor, "one", _ => Done(), Current));
            AssertError(AgentAccessError.LimitExceeded, () =>
                runtime.Submit(session, descriptor, "two", _ => Done(), Current));
        }

        [Fact]
        public async Task ObserverFailuresCannotChangeSuccessfulOperationOrBlockStateReads()
        {
            await using var runtime = Enabled();
            runtime.Changed += (_, _) => runtime.GetSessions();
            runtime.Changed += (_, _) => throw new IOException("observer private detail");
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var id = runtime.Submit(session, Descriptor(Summary), "read", _ => Done(), Current);
            await Wait(runtime, session, id);
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, id).State);
        }

        [Fact]
        public async Task RepeatedDisposalWaitsForTheSameRunningWork()
        {
            var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var started = Gate();
            var release = Gate();
            runtime.Submit(session, Descriptor(Summary), "running", async _ =>
            {
                started.SetResult();
                await release.Task;
                return Value();
            }, Current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = runtime.DisposeAsync().AsTask();
            var second = runtime.DisposeAsync().AsTask();
            try
            {
                Assert.Same(first, second);
                Assert.False(second.IsCompleted);
            }
            finally { release.TrySetResult(); }
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<ObjectDisposedException>(runtime.Enable);
        }

        [Fact]
        public async Task DisableRejectsReenableUntilWorkDrainsAndDoesNotRestoreOldGrants()
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("client");
            await runtime.SetPermissionsAsync(session, [Summary]);
            var started = Gate();
            var release = Gate();
            runtime.Submit(session, Descriptor(Summary), "running", async _ =>
            {
                started.SetResult();
                await release.Task;
                return Value();
            }, Current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopping = runtime.DisableAsync();
            try
            {
                Assert.False(runtime.IsEnabled);
                Assert.Same(stopping, runtime.DisableAsync());
                AssertError(AgentAccessError.OperationUnavailable, runtime.Enable);
                AssertError(AgentAccessError.AccessDisabled, () => runtime.Connect("new client"));
            }
            finally { release.TrySetResult(); }
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            runtime.Enable();
            var next = runtime.Connect("client");
            Assert.Empty(runtime.GetSession(next).Permissions);
            AssertError(AgentAccessError.SessionClosed, () => runtime.Demand(session, Summary));
            AssertError(AgentAccessError.PermissionDenied, () => runtime.Demand(next, Summary));
        }

        [Theory]
        [InlineData(AgentResourceKind.QueryResult, false)]
        [InlineData(AgentResourceKind.QueryResult, true)]
        [InlineData(AgentResourceKind.Artifact, false)]
        [InlineData(AgentResourceKind.Artifact, true)]
        [InlineData(AgentResourceKind.ScriptValidation, false)]
        [InlineData(AgentResourceKind.ScriptValidation, true)]
        public async Task FailedJobsReleaseUnpublishedTemporaryResourcesIncludingOversizedReceipts(
            AgentResourceKind kind, bool throws)
        {
            await using var runtime = Enabled();
            var session = runtime.Connect("temporary output");
            var capability = kind switch
            {
                AgentResourceKind.QueryResult => AgentCapability.RawDataRead,
                AgentResourceKind.ScriptValidation => AgentCapability.ScriptRead,
                _ => AgentCapability.QueryResultExport
            };
            var permission = new AgentPermission(capability, kind == AgentResourceKind.ScriptValidation
                ? AgentScope.Capture(Guid.NewGuid()) : AgentScope.Source(Guid.NewGuid()));
            await runtime.SetPermissionsAsync(session, [permission], new AgentLimits(responseBytes: 256));
            var descriptor = Descriptor(permission, kind == AgentResourceKind.Artifact
                ? AgentOperationEffects.SensitiveDataExport : AgentOperationEffects.None);
            var cleanups = 0;
            var id = runtime.Submit(session, descriptor, "unpublished", context =>
            {
                context.OwnResource(new AgentResourceKey(kind, Guid.NewGuid()), "temporary", () =>
                {
                    Interlocked.Increment(ref cleanups);
                    return Task.FromResult(AgentCleanupOutcome.Released);
                });
                return throws ? Task.FromException<JsonElement>(new IOException("private failure")) :
                    Task.FromResult(JsonSerializer.SerializeToElement(new { body = new string('x', 1024) }));
            }, Current);
            if (descriptor.RequiresApproval)
                Assert.True(runtime.Approve(id));
            await Wait(runtime, session, id);
            Assert.Equal(AgentOperationState.Failed, runtime.GetOperation(session, id).State);
            Assert.Equal(throws ? AgentAccessError.ExecutionFailed : AgentAccessError.LimitExceeded,
                runtime.GetOperation(session, id).Error);
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Equal(1, cleanups);
            await runtime.EndSessionAsync(session);
            Assert.Equal(1, cleanups);
        }

        private static AgentRuntime Enabled()
        {
            var runtime = new AgentRuntime();
            runtime.Enable();
            return runtime;
        }

        private static AgentPermission Control(Guid id) =>
            new(AgentCapability.CaptureControl, AgentScope.Capture(id));

        private static AgentOperationDescriptor Descriptor(
            AgentPermission permission,
            AgentOperationEffects effects = AgentOperationEffects.None,
            string argument = "same") =>
            new("test.operation", [permission], effects, AgentOperationDescriptor.Hash(new { argument }));

        private static Task<bool> Current(CancellationToken cancellationToken) => Task.FromResult(true);
        private static JsonElement Value() => JsonSerializer.SerializeToElement(new { ok = true });
        private static Task<JsonElement> Done() => Task.FromResult(Value());
        private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static Task Wait(AgentRuntime runtime, AgentSession session, Guid id) =>
            runtime.WaitForCompletionAsync(session, id).WaitAsync(TimeSpan.FromSeconds(10));

        private static void AssertError(AgentAccessError error, Action action) =>
            Assert.Equal(error, Assert.Throws<AgentAccessException>(action).Error);
    }
}
