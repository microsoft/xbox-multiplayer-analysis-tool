// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentResourceApplicationTests
    {
        [Fact]
        public async Task ExactOwnershipTakeoverDoesNotExposePrivateResultsOrStopTheResource()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0);
            await using var runtime = new AgentRuntime(policy);
            runtime.Enable();
            var owner = runtime.Connect("original");
            var receiver = runtime.Connect("operator client");
            var key = new AgentResourceKey(AgentResourceKind.Capture, Guid.NewGuid());
            var cleanups = 0;
            var running = runtime.Submit(owner, new("capture.fake", [new(AgentCapability.CaptureControl, AgentScope.Capture(key.Id))],
                AgentOperationEffects.StateChange, "start"), "start", context =>
                {
                    context.OwnResource(key, "7", () =>
                    {
                        Interlocked.Increment(ref cleanups);
                        return Task.FromResult(AgentCleanupOutcome.Released);
                    });
                    return Task.FromResult(AgentJson.Encode(new { privateValue = "not transferred" }));
                }, _ => Task.FromResult(true));
            await runtime.WaitForCompletionAsync(owner, running);
            var application = new AgentResourceApplication(runtime);
            Assert.Equal(AgentAccessError.SourceChanged, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.RequestAsync(receiver, key, owner.Id, "stale", false, "stale", default))).Error);
            var takeover = await application.RequestAsync(receiver, key, owner.Id, "7", false, "takeover", default);
            runtime.Confirm(receiver, takeover, runtime.GetConfirmation(receiver, takeover).ConfirmationToken, true);
            await runtime.WaitForCompletionAsync(receiver, takeover);
            await runtime.EndSessionAsync(owner);
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Equal(0, cleanups);
            Assert.False(runtime.GetResult(receiver, takeover).GetProperty("privateResultsTransferred").GetBoolean());
            Assert.Equal(AgentAccessError.OperationNotFound,
                Assert.Throws<AgentAccessException>(() => runtime.GetResult(receiver, running)).Error);
        }

        [Fact]
        public async Task RecoveryRetryUsesTheOriginalCleanupAndInspectorCannotInvokeIt()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0);
            await using var runtime = new AgentRuntime(policy);
            runtime.Enable();
            var owner = runtime.Connect("original");
            var key = new AgentResourceKey(AgentResourceKind.Capture, Guid.NewGuid());
            var attempts = 0;
            var operation = runtime.Submit(owner, new("capture.fake", [new(AgentCapability.CaptureControl, AgentScope.Capture(key.Id))],
                AgentOperationEffects.StateChange, "start"), "start", context =>
                {
                    context.OwnResource(key, "3", () => Task.FromResult(Interlocked.Increment(ref attempts) == 1
                        ? AgentCleanupOutcome.RecoveryRequired : AgentCleanupOutcome.Released));
                    return Task.FromResult(AgentJson.Encode(new { started = true }));
                }, _ => Task.FromResult(true));
            await runtime.WaitForCompletionAsync(owner, operation);
            await runtime.EndSessionAsync(owner);
            Assert.Equal(AgentResourceState.RecoveryRequired, Assert.Single(runtime.GetOwnedResources()).State);
            var recovery = runtime.Connect("recovery");
            var application = new AgentResourceApplication(runtime);
            await policy.SaveAsync(policy.Current.Settings with { Actions = AgentActionAccess.InspectAndExport },
                policy.Current.Settings.Revision);
            await runtime.ApplyPolicyAsync();
            Assert.Equal(AgentAccessError.PermissionDenied, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.RequestAsync(recovery, key, owner.Id, "3", true, "denied", default))).Error);
            await policy.SaveAsync(policy.Current.Settings with { Actions = AgentActionAccess.FullControl },
                policy.Current.Settings.Revision);
            await runtime.ApplyPolicyAsync();
            var retry = await application.RequestAsync(recovery, key, owner.Id, "3", true, "retry", default);
            runtime.Confirm(recovery, retry, runtime.GetConfirmation(recovery, retry).ConfirmationToken, true);
            await runtime.WaitForCompletionAsync(recovery, retry);
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task RetryingOrphanedTemporaryCleanupDoesNotNeedAnotherConfirmation()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0);
            await using var runtime = new AgentRuntime(policy);
            runtime.Enable();
            var owner = runtime.Connect("original reader");
            var key = new AgentResourceKey(AgentResourceKind.QueryResult, Guid.NewGuid());
            var attempts = 0;
            var operation = runtime.Submit(owner, new("query.fake",
                [new(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()))], AgentOperationEffects.None, "read"),
                "read", context =>
                {
                    context.OwnResource(key, "snapshot", () => Task.FromResult(++attempts == 1
                        ? AgentCleanupOutcome.RecoveryRequired : AgentCleanupOutcome.Released));
                    return Task.FromResult(AgentJson.Encode(new { read = true }));
                }, _ => Task.FromResult(true));
            await runtime.WaitForCompletionAsync(owner, operation);
            await runtime.EndSessionAsync(owner);
            var recovery = runtime.Connect("cleanup");
            var retry = await new AgentResourceApplication(runtime).RequestAsync(
                recovery, key, owner.Id, "snapshot", true, "retry", default);
            Assert.Null(runtime.GetConfirmation(recovery, retry));
            await runtime.WaitForCompletionAsync(recovery, retry);
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(recovery, retry).State);
            Assert.Equal(2, attempts);
            Assert.Empty(runtime.GetOwnedResources());
        }
    }
}
