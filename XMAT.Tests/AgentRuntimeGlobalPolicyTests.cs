// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentRuntimeGlobalPolicyTests
    {
        [Fact]
        public async Task CurrentAndFutureConnectionsUseOnePolicyForNewTargetsWithoutClientGrants()
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.InspectAndExport);
            var first = fixture.Runtime.Connect("first");
            var second = fixture.Runtime.Connect("second");
            var read = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            fixture.Runtime.Demand(first, read);
            fixture.Runtime.Demand(second, read);
            fixture.Runtime.Demand(second,
                new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid())));
            var control = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(Guid.NewGuid()));
            Assert.Throws<AgentAccessException>(() => fixture.Runtime.Demand(first, control));
            var grant = await Assert.ThrowsAsync<AgentAccessException>(() =>
                fixture.Runtime.SetPermissionsAsync(first, [control]));
            Assert.Equal(AgentAccessError.UnsupportedOperation, grant.Error);
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with { Actions = AgentActionAccess.FullControl });
            fixture.Runtime.Demand(first, control);
            fixture.Runtime.Demand(second, control);
            fixture.Runtime.Demand(fixture.Runtime.Connect("later"), control);
        }

        [Theory]
        [InlineData(AgentCapability.DeviceCreation, AgentOperationEffects.StateChange)]
        [InlineData(AgentCapability.DeviceConfiguration, AgentOperationEffects.StateChange)]
        [InlineData(AgentCapability.Navigation, AgentOperationEffects.HumanOwnedResource)]
        [InlineData(AgentCapability.FileWrite, AgentOperationEffects.SensitiveDataExport)]
        [InlineData(AgentCapability.CaptureLoad, AgentOperationEffects.HumanOwnedResource)]
        [InlineData(AgentCapability.Preferences, AgentOperationEffects.HumanOwnedResource)]
        [InlineData(AgentCapability.ScriptEdit, AgentOperationEffects.HumanOwnedResource)]
        public async Task RoutineCapabilitiesDoNotRequireAnotherConfirmation(
            AgentCapability capability, AgentOperationEffects effects)
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl);
            var session = fixture.Runtime.Connect("routine client");
            var scope = capability switch
            {
                AgentCapability.DeviceConfiguration or AgentCapability.ScriptEdit => AgentScope.Capture(Guid.NewGuid()),
                AgentCapability.FileWrite => AgentScope.ExportFolder(Guid.NewGuid()),
                _ => AgentScope.Application
            };
            var executions = 0;
            var operation = fixture.Runtime.Submit(session,
                new("routine", [new(capability, scope)], effects, "routine"), "routine", _ =>
                {
                    executions++;
                    return Task.FromResult(AgentJson.Encode(new { completed = true }));
                }, _ => Task.FromResult(true));
            Assert.Null(fixture.Runtime.GetConfirmation(session, operation));
            await fixture.Runtime.WaitForCompletionAsync(session, operation);
            Assert.Equal(AgentOperationState.Succeeded, fixture.Runtime.GetOperation(session, operation).State);
            Assert.Equal(1, executions);
        }

        [Theory]
        [InlineData(AgentOperationEffects.DataDeletion)]
        [InlineData(AgentOperationEffects.FileOverwrite)]
        [InlineData(AgentOperationEffects.ConsoleRestart)]
        [InlineData(AgentOperationEffects.TrustedCodeExecution)]
        public async Task ConsequentialEffectsStillWaitAndDecliningNeverExecutes(AgentOperationEffects effects)
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl);
            var session = fixture.Runtime.Connect("consequential client");
            var executions = 0;
            var operation = fixture.Runtime.Submit(session,
                new("consequential", [new(AgentCapability.CaptureControl, AgentScope.Capture(Guid.NewGuid()))],
                    effects, "consequential"), "consequential", _ =>
                {
                    executions++;
                    return Task.FromResult(AgentJson.Encode(new { completed = true }));
                }, _ => Task.FromResult(true));
            Assert.Equal(AgentOperationState.AwaitingApproval, fixture.Runtime.GetOperation(session, operation).State);
            var confirmation = fixture.Runtime.GetConfirmation(session, operation);
            fixture.Runtime.Confirm(session, operation, confirmation.ConfirmationToken, false);
            await fixture.Runtime.WaitForCompletionAsync(session, operation);
            Assert.Equal(AgentOperationState.Denied, fixture.Runtime.GetOperation(session, operation).State);
            Assert.Equal(0, executions);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RawLocalExportChecksTheCurrentSharingPolicyAtSubmission(bool redacted)
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl, AgentDataSharing.Complete);
            var client = fixture.Runtime.Connect("raw output");
            var descriptor = new AgentOperationDescriptor("file.export",
                [new(AgentCapability.FileWrite, AgentScope.ExportFolder(Guid.NewGuid()))],
                AgentOperationEffects.SensitiveDataExport, "raw", rawLocalOutput: true);
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with
            { DataSharing = redacted ? AgentDataSharing.Redacted : AgentDataSharing.Complete });
            var operation = fixture.Runtime.Submit(client, descriptor, "raw",
                _ => Task.FromResult(AgentJson.Encode(new { published = true })), _ => Task.FromResult(true));
            if (redacted)
            {
                var confirmation = fixture.Runtime.GetConfirmation(client, operation);
                Assert.NotNull(confirmation);
                Assert.Equal(AgentOperationState.AwaitingApproval, fixture.Runtime.GetOperation(client, operation).State);
            }
            else
            {
                Assert.Null(fixture.Runtime.GetConfirmation(client, operation));
                await fixture.Runtime.WaitForCompletionAsync(client, operation);
                Assert.Equal(AgentOperationState.Succeeded, fixture.Runtime.GetOperation(client, operation).State);
            }
        }

        [Fact]
        public async Task AcceptedScriptVersionIsSharedAcrossClientsButNotChangedCodeOrNewPolicy()
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl);
            var first = fixture.Runtime.Connect("first");
            var second = fixture.Runtime.Connect("second");
            var permission = new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(Guid.NewGuid()));
            var version = new AgentScriptVersion(Guid.NewGuid(), new string('A', 64));
            AgentOperationDescriptor Code(AgentScriptVersion current) => new(
                "script.enable", [permission], AgentOperationEffects.StateChange, current.SourceHash,
                scriptVersions: [current]);
            Guid Submit(AgentSession client, AgentScriptVersion current, string key) =>
                fixture.Runtime.Submit(client, Code(current), key,
                    _ => Task.FromResult(AgentJson.Encode(new { enabled = true })), _ => Task.FromResult(true));
            var initial = Submit(first, version, "initial");
            fixture.Runtime.Confirm(first, initial, fixture.Runtime.GetConfirmation(first, initial).ConfirmationToken, true);
            await fixture.Runtime.WaitForCompletionAsync(first, initial);
            var same = Submit(second, version, "same version");
            Assert.Null(fixture.Runtime.GetConfirmation(second, same));
            await fixture.Runtime.WaitForCompletionAsync(second, same);
            var changed = Submit(second, version with { SourceHash = new string('B', 64) }, "changed");
            Assert.NotNull(fixture.Runtime.GetConfirmation(second, changed));
            fixture.Runtime.Confirm(second, changed, fixture.Runtime.GetConfirmation(second, changed).ConfirmationToken, false);
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with { Actions = AgentActionAccess.InspectAndExport });
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with { Actions = AgentActionAccess.FullControl });
            var afterPolicy = Submit(first, version, "new policy");
            Assert.NotNull(fixture.Runtime.GetConfirmation(first, afterPolicy));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StaleOrFailedActivationDoesNotRememberCodeConsent(bool stale)
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl);
            var client = fixture.Runtime.Connect("script client");
            var descriptor = new AgentOperationDescriptor("script.enable",
                [new(AgentCapability.ScriptExecute, AgentScope.Capture(Guid.NewGuid()))],
                AgentOperationEffects.StateChange, "version",
                scriptVersions: [new(Guid.NewGuid(), new string('A', 64))]);
            var failed = fixture.Runtime.Submit(client, descriptor, "failed",
                _ => Task.FromException<System.Text.Json.JsonElement>(new InvalidOperationException()),
                _ => Task.FromResult(!stale));
            fixture.Runtime.Confirm(client, failed, fixture.Runtime.GetConfirmation(client, failed).ConfirmationToken, true);
            await fixture.Runtime.WaitForCompletionAsync(client, failed);
            Assert.Equal(stale ? AgentOperationState.SourceChanged : AgentOperationState.Failed,
                fixture.Runtime.GetOperation(client, failed).State);
            var retry = fixture.Runtime.Submit(client, descriptor, "retry",
                _ => Task.FromResult(AgentJson.Encode(new { enabled = true })), _ => Task.FromResult(true));
            Assert.NotNull(fixture.Runtime.GetConfirmation(client, retry));
        }

        [Fact]
        public async Task DataModeChangeDrainsReadersAndCannotPublishOrResurrectOldResults()
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl, AgentDataSharing.Complete);
            var session = fixture.Runtime.Connect("reader");
            var permission = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            var descriptor = new AgentOperationDescriptor("read", [permission], AgentOperationEffects.None, "read");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = fixture.Runtime.Submit(session, descriptor, "first", async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return AgentJson.Encode(new { value = "original secret" });
            }, _ => Task.FromResult(true));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await fixture.Policy.SaveAsync(
                    fixture.Policy.Current.Settings with { DataSharing = AgentDataSharing.Redacted },
                    fixture.Policy.Current.Settings.Revision);
                var updating = Assert.Throws<AgentAccessException>(() => fixture.Runtime.Demand(session, permission));
                Assert.Equal(AgentAccessError.PermissionsUpdating, updating.Error);
            }
            finally { release.TrySetResult(); }
            await fixture.Runtime.ApplyPolicyAsync();
            var discarded = Assert.Throws<AgentAccessException>(() => fixture.Runtime.GetResult(session, operation));
            Assert.Equal(AgentAccessError.ResultUnavailable, discarded.Error);
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with { DataSharing = AgentDataSharing.Complete });
            Assert.Throws<AgentAccessException>(() => fixture.Runtime.GetResult(session, operation));
            Assert.Equal(operation, fixture.Runtime.Submit(session, descriptor, "first",
                _ => throw new InvalidOperationException("Replay must not execute."), _ => Task.FromResult(true)));
        }

        [Fact]
        public async Task ActionDowngradeCleansOwnedControlAcrossClientsButKeepsPermittedReadResults()
        {
            await using var fixture = new PolicyRuntime();
            await fixture.EnableAsync(AgentActionAccess.FullControl);
            var clients = new[] { fixture.Runtime.Connect("one"), fixture.Runtime.Connect("two") };
            var cleaned = 0;
            foreach (var client in clients)
            {
                var captureId = Guid.NewGuid();
                var permission = new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(captureId));
                var descriptor = new AgentOperationDescriptor("capture.start", [permission],
                    AgentOperationEffects.StateChange, captureId.ToString());
                var operation = fixture.Runtime.Submit(client, descriptor, "capture", context =>
                {
                    context.OwnResource(new(AgentResourceKind.Capture, captureId), "1", () =>
                    {
                        Interlocked.Increment(ref cleaned);
                        return Task.FromResult(AgentCleanupOutcome.Released);
                    });
                    return Task.FromResult(AgentJson.Encode(new { running = true }));
                }, _ => Task.FromResult(true));
                await fixture.Runtime.WaitForCompletionAsync(client, operation);
            }
            var raw = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
            var read = fixture.Runtime.Submit(clients[0],
                new("read", [raw], AgentOperationEffects.None, "read"), "read",
                _ => Task.FromResult(AgentJson.Encode(new { value = "permitted" })), _ => Task.FromResult(true));
            await fixture.Runtime.WaitForCompletionAsync(clients[0], read);
            await fixture.ChangeAsync(fixture.Policy.Current.Settings with { Actions = AgentActionAccess.InspectAndExport });
            Assert.Equal(2, cleaned);
            Assert.Empty(fixture.Runtime.GetOwnedResources());
            Assert.Equal("permitted", fixture.Runtime.GetResult(clients[0], read).GetProperty("value").GetString());
        }

        private sealed class PolicyRuntime : IAsyncDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "XMAT.GlobalPolicy." + Guid.NewGuid().ToString("N"));
            internal AgentAccessPolicyService Policy { get; }
            internal AgentRuntime Runtime { get; }

            internal PolicyRuntime()
            {
                Directory.CreateDirectory(_directory);
                Policy = new(new(Path.Combine(_directory, "policy.json")));
                Runtime = new(Policy);
            }

            internal async Task EnableAsync(AgentActionAccess actions, AgentDataSharing data = AgentDataSharing.Redacted)
            {
                await ChangeAsync(new() { Enabled = true, Actions = actions, DataSharing = data });
                Runtime.Enable();
            }

            internal async Task ChangeAsync(AgentAccessSettings settings)
            {
                await Policy.SaveAsync(settings, Policy.Current.Settings.Revision);
                await Runtime.ApplyPolicyAsync();
            }

            public async ValueTask DisposeAsync()
            {
                await Runtime.DisposeAsync();
                await Policy.DisposeAsync();
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
