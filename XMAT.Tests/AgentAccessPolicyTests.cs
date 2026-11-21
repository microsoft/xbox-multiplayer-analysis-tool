// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentAccessPolicyTests
    {
        [Theory]
        [InlineData(AgentCapability.CaptureControl)]
        [InlineData(AgentCapability.ScriptEdit)]
        [InlineData(AgentCapability.ScriptExecute)]
        [InlineData(AgentCapability.DeviceConfiguration)]
        [InlineData(AgentCapability.CaptureImport)]
        [InlineData(AgentCapability.HostPolicy)]
        [InlineData(AgentCapability.Preferences)]
        [InlineData(AgentCapability.Lifecycle)]
        public void InspectorDoesNotAuthorizeControlCapabilities(AgentCapability capability)
        {
            var scope = capability switch
            {
                AgentCapability.CaptureControl or AgentCapability.ScriptEdit or AgentCapability.ScriptExecute or
                    AgentCapability.DeviceConfiguration => AgentScope.Capture(Guid.NewGuid()),
                AgentCapability.CaptureImport => AgentScope.Source(Guid.NewGuid()),
                _ => AgentScope.Application
            };
            var permission = new AgentPermission(capability, scope);
            var inspector = new AgentAccessSettings { Enabled = true };
            Assert.False(inspector.Allows(permission));
            Assert.True((inspector with { Actions = AgentActionAccess.FullControl }).Allows(permission));
        }

        [Fact]
        public void GlobalInspectionCoversDifferentAndNewSourceIdentitiesButNotHiddenDestructiveEffects()
        {
            var settings = new AgentAccessSettings { Enabled = true };
            for (var index = 0; index < 3; index++)
            {
                var read = new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(Guid.NewGuid()));
                Assert.True(settings.Allows(read));
            }
            var output = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(Guid.NewGuid()));
            Assert.True(settings.Allows(new AgentOperationDescriptor("export", [output],
                AgentOperationEffects.FileOverwrite | AgentOperationEffects.SensitiveDataExport, "output")));
            Assert.False(settings.Allows(new AgentOperationDescriptor("misclassified", [output],
                AgentOperationEffects.DataDeletion, "output")));
        }

        [Fact]
        public void RestrictiveIntersectionNeverPublishesAnUnpersistedPrivilegeIncrease()
        {
            var current = new AgentAccessSettings
            {
                Enabled = true,
                Actions = AgentActionAccess.InspectAndExport,
                DataSharing = AgentDataSharing.Complete
            };
            var proposed = current with
            {
                Actions = AgentActionAccess.FullControl,
                DataSharing = AgentDataSharing.Redacted,
                AutomaticBackups = true
            };
            var effective = current.RestrictTo(proposed);
            Assert.Equal(AgentActionAccess.InspectAndExport, effective.Actions);
            Assert.Equal(AgentDataSharing.Redacted, effective.DataSharing);
            Assert.True(effective.AutomaticBackups);
            Assert.False(effective.RestrictTo(proposed with { Enabled = false }).Enabled);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"enabled\":true,\"version\":2}")]
        [InlineData("{\"enabled\":true,\"actions\":\"Unknown\"}")]
        [InlineData("{\"enabled\":true,\"dataSharing\":1}")]
        [InlineData("{\"enabled\":true,\"revision\":-1}")]
        [InlineData("{\"enabled\":true,\"unexpected\":true}")]
        public void InvalidSavedPolicyFailsClosed(string json)
        {
            using var folder = new PolicyFolder();
            File.WriteAllText(folder.Path, json);
            var read = new AgentAccessSettingsStore(folder.Path).Read();
            Assert.Equal(AgentPolicyReadStatus.Invalid, read.Status);
            Assert.False(read.Settings.Enabled);
        }

        [Fact]
        public async Task StoreUsesAtomicRevisionCheckedWritesAndDoesNotTouchOtherPreferences()
        {
            using var folder = new PolicyFolder();
            var preferences = System.IO.Path.Combine(folder.Directory, "app-settings.json");
            File.WriteAllText(preferences, "unchanged device preferences");
            var first = new AgentAccessSettingsStore(folder.Path);
            var second = new AgentAccessSettingsStore(folder.Path);
            Assert.Equal(AgentPolicyReadStatus.Missing, first.Read().Status);
            var desired = new AgentAccessSettings
            {
                Enabled = true,
                Actions = AgentActionAccess.FullControl,
                AutomaticBackups = true
            };
            var committed = await first.SaveAsync(desired, 0);
            Assert.Equal(1, committed.Revision);
            Assert.Equal(committed, second.Read().Settings);
            var changed = await Assert.ThrowsAsync<AgentPolicyException>(() => second.SaveAsync(desired, 0));
            Assert.Equal(AgentPolicyFailure.PolicyChanged, changed.Failure);
            Assert.Equal("unchanged device preferences", File.ReadAllText(preferences));
            Assert.Empty(System.IO.Directory.GetFiles(folder.Directory, "*.tmp"));
        }

        [Fact]
        public async Task ConcurrentStoresCannotOverwriteTheSameExpectedPolicyRevision()
        {
            using var folder = new PolicyFolder();
            var stores = new[] { new AgentAccessSettingsStore(folder.Path), new AgentAccessSettingsStore(folder.Path) };
            var writes = stores.Select(async store =>
            {
                try
                { await store.SaveAsync(new() { Enabled = true }, 0); return true; }
                catch (AgentPolicyException exception) when (exception.Failure == AgentPolicyFailure.PolicyChanged)
                { return false; }
            });
            Assert.Equal(1, (await Task.WhenAll(writes)).Count(succeeded => succeeded));
            Assert.Equal(1, stores[0].Read().Settings.Revision);
        }

        [Fact]
        public async Task CancelledWritePreservesPreviouslySavedPolicy()
        {
            using var folder = new PolicyFolder();
            var store = new AgentAccessSettingsStore(folder.Path);
            var committed = await store.SaveAsync(new() { Enabled = true }, 0);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.SaveAsync(committed with { Enabled = false }, committed.Revision, cancellation.Token));
            Assert.Equal(committed, store.Read().Settings);
        }

        [Fact]
        public async Task FailedSaveKeepsRestrictionsButNeverPublishesBroaderAccess()
        {
            using var folder = new PolicyFolder();
            var store = new AgentAccessSettingsStore(folder.Path);
            await store.SaveAsync(new()
            {
                Enabled = true,
                Actions = AgentActionAccess.InspectAndExport,
                DataSharing = AgentDataSharing.Complete
            }, 0);
            await using var service = new AgentAccessPolicyService(store);
            using (var locked = new FileStream(folder.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<AgentPolicyException>(() => service.SaveAsync(new()
                {
                    Enabled = true,
                    Actions = AgentActionAccess.FullControl,
                    DataSharing = AgentDataSharing.Redacted,
                    AutomaticBackups = true
                }, 1));
            }
            service.Reload();
            Assert.True(service.Current.HasUnsavedRestriction);
            Assert.Equal(AgentActionAccess.InspectAndExport, service.Current.Settings.Actions);
            Assert.Equal(AgentDataSharing.Redacted, service.Current.Settings.DataSharing);
            Assert.True(service.Current.Settings.AutomaticBackups);
            var saved = await service.SaveAsync(service.Current.Settings, 1);
            Assert.False(saved.HasUnsavedRestriction);
            Assert.Null(saved.SaveFailure);
            Assert.Equal(2, saved.Settings.Revision);
        }

        [Fact]
        public async Task BackupChangesDoNotInvalidateDataViewsButDataChangesDo()
        {
            using var folder = new PolicyFolder();
            await using var service = new AgentAccessPolicyService(new(folder.Path));
            var first = await service.SaveAsync(new() { Enabled = true }, 0);
            var backup = await service.SaveAsync(first.Settings with { AutomaticBackups = true }, first.Settings.Revision);
            Assert.Equal(first.DataRevision, backup.DataRevision);
            Assert.Equal(first.AccessRevision, backup.AccessRevision);
            var raw = await service.SaveAsync(backup.Settings with { DataSharing = AgentDataSharing.Complete },
                backup.Settings.Revision);
            Assert.True(raw.DataRevision > backup.DataRevision);
            Assert.True(raw.AccessRevision > backup.AccessRevision);
        }

        [Fact]
        public async Task ReloadCannotUndoARestrictionWhileItsSaveIsPending()
        {
            using var folder = new PolicyFolder();
            var store = new AgentAccessSettingsStore(folder.Path);
            await store.SaveAsync(new() { Enabled = true, DataSharing = AgentDataSharing.Complete }, 0);
            await using var service = new AgentAccessPolicyService(store);
            var observed = new List<bool>();
            service.Changed += (_, _) =>
            {
                if (!service.Current.HasUnsavedRestriction)
                    return;
                service.Reload();
                observed.Add(service.Current.Settings.DataSharing == AgentDataSharing.Redacted);
            };
            await service.SaveAsync(service.Current.Settings with { DataSharing = AgentDataSharing.Redacted }, 1);
            Assert.NotEmpty(observed);
            Assert.All(observed, Assert.True);
        }

        [Fact]
        public async Task ASecondStoreUpdatesEveryWatchingPolicyServiceWithoutOpeningTheDesktop()
        {
            using var folder = new PolicyFolder();
            var store = new AgentAccessSettingsStore(folder.Path);
            await using var first = new AgentAccessPolicyService(store);
            await using var second = new AgentAccessPolicyService(new(folder.Path));
            first.StartWatching();
            second.StartWatching();
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Changed += (_, _) =>
            {
                if (second.Current.Settings.Enabled)
                    changed.TrySetResult();
            };
            await first.SaveAsync(new() { Enabled = true }, 0);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(first.Current.Settings, second.Current.Settings);
            XMAT.ApplicationServices.AtomicSettingsFile.Write(folder.Path, "{\"enabled\":true,\"version\":100}");
            second.Reload();
            Assert.Equal(AgentPolicyReadStatus.Invalid, second.Current.ReadStatus);
            Assert.False(second.Current.Settings.Enabled);
        }

        private sealed class PolicyFolder : IDisposable
        {
            internal string Directory { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "XMAT.PolicyTests." + Guid.NewGuid().ToString("N"));
            internal string Path => System.IO.Path.Combine(Directory, "agent-access.json");
            internal PolicyFolder() => System.IO.Directory.CreateDirectory(Directory);
            public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
