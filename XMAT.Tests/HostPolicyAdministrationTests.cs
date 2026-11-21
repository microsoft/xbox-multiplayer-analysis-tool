// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class HostPolicyAdministrationTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void HostChangesRequireGrantApprovalExactRevisionAndRetryWithoutRepeating()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var request = new HostPolicyRequest(snapshot.Revision, HostPolicyList.Block, HostPolicyAction.Add, " [0:0::1] ");
                Assert.Equal(AgentAccessError.PermissionDenied, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.UpdateHostsAsync(setup.Session, request, "once", default))).Error);
                setup.Grant(AgentCapability.HostPolicy);
                var operation = Complete(setup.Agent.UpdateHostsAsync(setup.Session, request, "once", default));
                Assert.Equal(AgentOperationState.AwaitingApproval, setup.Runtime.GetOperation(setup.Session, operation).State);
                Assert.Empty(setup.Model.HostListPolicy.BlockList.BlockedUrls);
                Assert.Contains("Normalized pattern: ::1", setup.Runtime.GetApprovalDetails(operation));
                Assert.Contains("retained after disconnect", setup.Runtime.GetApprovalDetails(operation));
                setup.Approve(operation);
                var result = setup.Runtime.GetResult(setup.Session, operation);
                Assert.True(result.GetProperty("changed").GetBoolean());
                Assert.True(setup.Model.HostListPolicy.BlockList.Contains("[::1]"));
                Assert.Equal(operation, Complete(setup.Agent.UpdateHostsAsync(setup.Session, request, "once", default)));
                Assert.Single(setup.Model.HostListPolicy.BlockList.BlockedUrls);
                Assert.Equal(AgentAccessError.IdempotencyConflict, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.UpdateHostsAsync(setup.Session, request with { Pattern = "different.example" }, "once", default))).Error);
                Finish(setup.Runtime.EndSessionAsync(setup.Session));
                Assert.True(setup.Model.HostListPolicy.BlockList.IsBlocked("::1"));
                Assert.Empty(setup.Runtime.GetOwnedResources());
            });
        }

        [Fact]
        public void DesktopEditsInvalidateApprovalAndNoOpNeverAdoptsHumanEntries()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.HostPolicy);
                var snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var pending = Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                    new(snapshot.Revision, HostPolicyList.Bypass, HostPolicyAction.Add, "agent.example"), "stale", default));
                Assert.True(setup.Model.HostListPolicy.BypassList.Add("human.example"));
                setup.Approve(pending);
                Assert.Equal(AgentOperationState.SourceChanged, setup.Runtime.GetOperation(setup.Session, pending).State);
                Assert.False(setup.Model.HostListPolicy.BypassList.Contains("agent.example"));
                snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var human = Assert.Single(snapshot.Entries);
                var noOp = Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                    new(snapshot.Revision, HostPolicyList.Bypass, HostPolicyAction.Add, "HUMAN.EXAMPLE"), "noop", default));
                setup.Approve(noOp);
                Assert.False(setup.Runtime.GetResult(setup.Session, noOp).GetProperty("changed").GetBoolean());
                var after = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                Assert.Equal(snapshot.Revision, after.Revision);
                Assert.Equal(human, Assert.Single(after.Entries));
                Assert.Equal(HostPolicyOwnership.Desktop, human.Ownership);
            });
        }

        [Fact]
        public void RemovedReaddedAndDesktopAdoptedEntriesCannotBeRemovedUsingOldEpochs()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.HostPolicy);
                var snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var add = Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                    new(snapshot.Revision, HostPolicyList.Block, HostPolicyAction.Add, "kept.example"), "add", default));
                setup.Approve(add);
                snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var owned = Assert.Single(snapshot.Entries);
                Assert.Equal(HostPolicyOwnership.ThisSession, owned.Ownership);
                var remove = Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                    new(snapshot.Revision, owned.List, HostPolicyAction.Remove, owned.Pattern, owned.Id, owned.Epoch), "remove", default));
                setup.Model.HostListPolicy.BlockList.BlockedUrls[0] = owned.Pattern;
                setup.Approve(remove);
                Assert.Equal(AgentOperationState.SourceChanged, setup.Runtime.GetOperation(setup.Session, remove).State);
                var adopted = Assert.Single(Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id)).Entries);
                Assert.Equal(owned.Id, adopted.Id);
                Assert.True(adopted.Epoch > owned.Epoch);
                Assert.Equal(HostPolicyOwnership.Desktop, adopted.Ownership);
                setup.Model.HostListPolicy.BlockList.Remove(owned.Pattern);
                setup.Model.HostListPolicy.BlockList.Add(owned.Pattern);
                var readded = Assert.Single(Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id)).Entries);
                Assert.NotEqual(adopted.Id, readded.Id);
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session, []));
                Assert.Equal(readded, Assert.Single(Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id)).Entries));
            });
        }

        [Fact]
        public void CommittedEntryEpochExistsBeforeNotificationsAndReceiptCannotAdoptReentrantHumanAdd()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var observed = new List<HostPolicyEntryDescriptor>();
                var reentered = false;
                ((INotifyPropertyChanged)setup.Model.HostListPolicy.BlockList.BlockedUrls).PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != "Count" || reentered)
                        return;
                    reentered = true;
                    observed.Add(Assert.Single(Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id)).Entries));
                    setup.Model.HostListPolicy.BlockList.Remove("epoch.example");
                    setup.Model.HostListPolicy.BlockList.Add("epoch.example");
                };
                var snapshot = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var plan = Complete(setup.Model.HostPolicyOperations.PrepareAsync(
                    new(snapshot.Revision, HostPolicyList.Block, HostPolicyAction.Add, "epoch.example"), setup.Session.Id));
                var change = Complete(Task.Run(() => setup.Model.HostPolicyOperations.ExecuteAsync(plan, setup.Session.Id)));
                Assert.Equal(change.Entry, Assert.Single(observed));
                var current = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                Assert.NotEqual(change.Entry.Id, Assert.Single(current.Entries).Id);
                Assert.True(current.Revision > change.Revision);
                Assert.Equal(HostPolicyOwnership.Desktop, current.Entries[0].Ownership);
            });
        }

        [Fact]
        public void BoundedPagesKeepExactRevisionAndDenyAccessAfterGrantChange()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Model.HostListPolicy.BlockList.Add("first.example");
                setup.Model.HostListPolicy.BypassList.Add("second.example");
                var permission = new AgentPermission(AgentCapability.HostPolicy, AgentScope.Application);
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [permission], new(responseRows: 1, responseBytes: 1024)));
                var first = Complete(setup.Agent.HostsAsync(setup.Session, 0, null, default));
                Assert.Single(first.GetProperty("entries").EnumerateArray());
                Assert.True(Encoding.UTF8.GetByteCount(first.GetRawText()) <= 1024);
                var revision = first.GetProperty("revision").GetInt64();
                var second = Complete(setup.Agent.HostsAsync(setup.Session, 1, revision, default));
                Assert.Single(second.GetProperty("entries").EnumerateArray());
                setup.Model.HostListPolicy.BlockList.Clear();
                Assert.Equal(AgentAccessError.SourceChanged, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.HostsAsync(setup.Session, 1, revision, default))).Error);
                setup.Model.HostListPolicy.BlockList.Add(new string('x', 1024));
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session, [permission], new(responseBytes: 256)));
                Assert.Equal(AgentAccessError.LimitExceeded, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.HostsAsync(setup.Session, 0, null, default))).Error);
                setup.Grant();
                Assert.Equal(AgentAccessError.PermissionDenied, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.HostsAsync(setup.Session, 0, null, default))).Error);
            });
        }

        [Fact]
        public void ClearTargetsOnlyOneWholeListAtTheExactApprovedRevisionAndEmptyClearIsANoOp()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.HostPolicy);
                setup.Model.HostListPolicy.BlockList.Add("one.example");
                setup.Model.HostListPolicy.BlockList.Add("two.example");
                setup.Model.HostListPolicy.BypassList.Add("kept.example");
                var before = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var request = new HostPolicyRequest(before.Revision, HostPolicyList.Block, HostPolicyAction.Clear);
                var operation = Complete(setup.Agent.UpdateHostsAsync(setup.Session, request, "clear-block", default));
                Assert.Contains("Affected entries: 2", setup.Runtime.GetApprovalDetails(operation));
                Assert.Equal(2, setup.Model.HostListPolicy.BlockList.BlockedUrls.Count);
                setup.Approve(operation);
                Assert.Empty(setup.Model.HostListPolicy.BlockList.BlockedUrls);
                Assert.True(setup.Model.HostListPolicy.BypassList.Contains("kept.example"));
                Assert.Equal(2, setup.Runtime.GetResult(setup.Session, operation).GetProperty("affectedEntries").GetInt32());
                Assert.Equal(operation, Complete(setup.Agent.UpdateHostsAsync(setup.Session, request, "clear-block", default)));
                var after = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                var noOp = Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                    request with { ExpectedRevision = after.Revision }, "empty-clear", default));
                setup.Approve(noOp);
                Assert.False(setup.Runtime.GetResult(setup.Session, noOp).GetProperty("changed").GetBoolean());
                Assert.Equal(after.Revision, Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id)).Revision);
            });
        }

        [Fact]
        public void TooSmallResponseBudgetRejectsAnUpdateBeforeCreatingApprovalOrChangingState()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                Finish(setup.Runtime.SetPermissionsAsync(setup.Session,
                    [new(AgentCapability.HostPolicy, AgentScope.Application)], new(responseBytes: 256)));
                var before = Complete(setup.Model.HostPolicyOperations.InspectAsync(setup.Session.Id));
                Assert.Equal(AgentAccessError.LimitExceeded, Assert.Throws<AgentAccessException>(() =>
                    Complete(setup.Agent.UpdateHostsAsync(setup.Session,
                        new(before.Revision, HostPolicyList.Block, HostPolicyAction.Add, "not-committed.example"), "budget", default))).Error);
                Assert.Empty(setup.Runtime.GetOperations());
                Assert.Empty(setup.Model.HostListPolicy.BlockList.BlockedUrls);
            });
        }
    }
}
