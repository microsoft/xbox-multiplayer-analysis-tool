// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ApplicationShutdownTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData(ShutdownDocumentAction.Save)]
        [InlineData(ShutdownDocumentAction.Discard)]
        [InlineData(ShutdownDocumentAction.Cancel)]
        public void SqlCloseDecisionsAreExplicitAndUseTheTypedSaveWorkflow(ShutdownDocumentAction action)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                var exits = 0;
                var blocked = false;
                shutdown.Configure(() => exits++, value => blocked = value);
                var document = model.SqlQueryWorkspace.NewDocument();
                document.Text = "SELECT 1 AS [original];";
                var snapshot = Complete(shutdown.InspectAsync());
                var path = Path.Combine(directory.Path, "query.sql");
                var choices = new ApplicationShutdownChoices
                {
                    Documents = [new(document.Id, document.TextRevision, action, path)]
                };
                if (action == ShutdownDocumentAction.Cancel)
                {
                    Assert.Equal(AgentAccessError.ShutdownDecisionRequired,
                        Assert.Throws<AgentAccessException>(() => Complete(shutdown.PrepareAsync(snapshot.Revision, choices, default))).Error);
                    Assert.False(blocked);
                    Assert.False(model.SqlQueryWorkspace.IsClosing);
                    Assert.False(File.Exists(path));
                }
                else
                {
                    var plan = Complete(shutdown.PrepareAsync(snapshot.Revision, choices, default));
                    var receipt = Complete(shutdown.ExecuteAsync(plan, default));
                    Assert.True(blocked);
                    Assert.True(model.SqlQueryWorkspace.IsClosing);
                    Assert.Equal(action == ShutdownDocumentAction.Save ? 1 : 0, receipt.SavedSqlDocuments);
                    if (action == ShutdownDocumentAction.Save)
                        Assert.Equal(document.Text, File.ReadAllText(path));
                    else
                        Assert.False(File.Exists(path));
                    Finish(shutdown.RequestExitAsync(receipt.PreparedRevision, _ => true, default));
                    Assert.Equal(1, exits);
                    shutdown.CancelPrepared();
                    Assert.False(blocked);
                    Assert.True(document.CanEditFiles);
                }
                Finish(model.DisposeSqlWorkspaceAsync());
            });
        }

        [Fact]
        public void NewerSqlEditsInvalidateThePreparedCloseWithoutDiscardingTheDocument()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                shutdown.Configure(() => Assert.Fail("A stale close must not exit."));
                var document = model.SqlQueryWorkspace.NewDocument();
                document.Text = "SELECT 1";
                var before = Complete(shutdown.InspectAsync());
                var plan = Complete(shutdown.PrepareAsync(before.Revision, new()
                {
                    Documents = [new(document.Id, document.TextRevision, ShutdownDocumentAction.Discard)]
                }, default));
                document.Text = "SELECT 2";
                Assert.Equal(AgentAccessError.SourceChanged,
                    Assert.Throws<AgentAccessException>(() => Complete(shutdown.ExecuteAsync(plan, default))).Error);
                Assert.True(document.CanEditFiles);
                Assert.True(document.IsDirty);
                Assert.Equal("SELECT 2", document.Text);
                Finish(model.DisposeSqlWorkspaceAsync());
            });
        }

        [Fact]
        public void FailedSettingsPersistenceResumesSqlAndReportsAlreadyPublishedFiles()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher,
                    () => Task.FromException(new IOException("fixture")));
                var blocked = false;
                shutdown.Configure(() => Assert.Fail("A failed close must not exit."), value => blocked = value);
                var document = model.SqlQueryWorkspace.NewDocument();
                document.Text = "SELECT 1";
                var snapshot = Complete(shutdown.InspectAsync());
                var path = Path.Combine(directory.Path, "saved.sql");
                var plan = Complete(shutdown.PrepareAsync(snapshot.Revision, new()
                {
                    Documents = [new(document.Id, document.TextRevision, ShutdownDocumentAction.Save, path)]
                }, default));
                var failure = Assert.Throws<AgentAccessException>(() => Complete(shutdown.ExecuteAsync(plan, default)));
                Assert.True(failure.ProtectedResult.Value.GetProperty("effectsMayHaveOccurred").GetBoolean());
                Assert.Equal(1, failure.ProtectedResult.Value.GetProperty("savedSqlDocuments").GetInt32());
                Assert.Equal(document.Text, File.ReadAllText(path));
                Assert.False(blocked);
                Assert.False(shutdown.IsClosing);
                Assert.True(document.CanEditFiles);
                Finish(model.DisposeSqlWorkspaceAsync());
            });
        }

        [Fact]
        public void ANewFileAfterSqlClosePreflightIsNeverOverwrittenAndItsTypedFailureRemainsVisible()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var model = new CaptureAppModel { ActiveDatabase = database };
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                shutdown.Configure(() => Assert.Fail("A failed SQL save must not close."));
                var document = model.SqlQueryWorkspace.NewDocument();
                document.Text = "SELECT 1";
                var path = Path.Combine(directory.Path, "query.sql");
                var snapshot = Complete(shutdown.InspectAsync());
                var plan = Complete(shutdown.PrepareAsync(snapshot.Revision, new()
                {
                    Documents = [new(document.Id, document.TextRevision, ShutdownDocumentAction.Save, path)]
                }, default));
                File.WriteAllText(path, "another writer");
                var failure = Assert.Throws<AgentAccessException>(() => Complete(shutdown.ExecuteAsync(plan, default)));
                Assert.Equal(AgentAccessError.DestinationExists, failure.Error);
                Assert.Equal("another writer", File.ReadAllText(path));
                Assert.True(document.IsDirty);
                Assert.True(document.CanEditFiles);
                Assert.False(shutdown.IsClosing);
                Finish(model.DisposeSqlWorkspaceAsync());
            });
        }

        [Fact]
        public void CaptureDiscardRequiresTheFinalBackupAndFailedBackupLeavesTheDatabaseAlive()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var row = CaptureInvestigationTestData.AddPacket(database, DateTime.UtcNow);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                shutdown.Configure(() => Assert.Fail("A failed backup must not exit."));
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, AutomaticBackups = true }, 0));
                    var snapshot = Complete(shutdown.InspectAsync());
                    Assert.True(snapshot.HasCapturedData);
                    Assert.Equal(AgentAccessError.ShutdownDecisionRequired, Assert.Throws<AgentAccessException>(() =>
                        Complete(shutdown.PrepareAsync(snapshot.Revision, new(), default))).Error);
                    var unavailable = Path.Combine(directory.Path, "Backups");
                    File.WriteAllText(unavailable, "not a directory");
                    using var protection = new AgentMutationProtection(Guid.NewGuid(), policy, new(unavailable));
                    var plan = Complete(shutdown.PrepareAsync(snapshot.Revision, new() { DiscardCapturedData = true }, default));
                    Assert.Equal(AgentAccessError.BackupFailed,
                        Assert.Throws<AgentAccessException>(() => Complete(shutdown.ExecuteAsync(plan, default))).Error);
                    Assert.False(shutdown.IsClosing);
                    Assert.Equal(row, database.TableByName(NetworkTrace.NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single().RowId);
                }
                finally { Finish(policy.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void InspectorHousekeepingRechecksOtherClientsAtDeferredExit()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.ConfigureAgentPolicy(policy, headless: true);
                var runtime = model.AgentAccess.Runtime;
                var exited = false;
                var resumed = false;
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                shutdown.Configure(() => exited = true, blocked =>
                {
                    if (blocked)
                        runtime.Connect("arrived during stop");
                });
                var lifecycle = new AgentHostApplication(runtime, shutdown, () => { }, () => resumed = true);
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true }, 0));
                    Finish(runtime.ApplyPolicyAsync());
                    runtime.Enable();
                    var session = runtime.Connect("inspector");
                    var state = Complete(shutdown.InspectAsync());
                    var id = Complete(lifecycle.RequestAsync(session, state.Revision, new(), "idle stop", default));
                    var confirmation = runtime.GetConfirmation(session, id);
                    if (confirmation != null)
                        runtime.Confirm(session, id, confirmation.ConfirmationToken, true);
                    Finish(runtime.WaitForCompletionAsync(session, id));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, id).State);
                    Finish(Task.Delay(400));
                    Assert.False(exited);
                    Assert.True(resumed);
                    Assert.False(shutdown.IsClosing);
                }
                finally
                {
                    Finish(lifecycle.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CleanDesktopCloseDoesNotConfirmButRechecksLateClientInterference(bool anotherClientArrives)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.ConfigureAgentPolicy(policy);
                var runtime = model.AgentAccess.Runtime;
                var exited = false;
                var resumed = false;
                var shutdown = new ApplicationShutdown(model, Dispatcher.CurrentDispatcher, () => Task.CompletedTask);
                shutdown.Configure(() => exited = true, blocked =>
                {
                    if (blocked && anotherClientArrives)
                        runtime.Connect("arrived after preparation");
                });
                var lifecycle = new AgentHostApplication(runtime, shutdown, () => { }, () => resumed = true);
                try
                {
                    Complete(policy.SaveAsync(new() { Enabled = true, Actions = AgentActionAccess.FullControl }, 0));
                    Finish(runtime.ApplyPolicyAsync());
                    runtime.Enable();
                    var session = runtime.Connect("desktop closer");
                    var state = Complete(shutdown.InspectAsync());
                    var operation = Complete(lifecycle.RequestAsync(session, state.Revision, new(), "clean close", default));
                    Assert.Null(runtime.GetConfirmation(session, operation));
                    Finish(runtime.WaitForCompletionAsync(session, operation));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, operation).State);
                    Finish(Task.Delay(400));
                    Assert.Equal(!anotherClientArrives, exited);
                    Assert.Equal(anotherClientArrives, resumed);
                    if (anotherClientArrives)
                        Assert.False(shutdown.IsClosing);
                }
                finally
                {
                    shutdown.CancelPrepared();
                    Finish(lifecycle.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                }
            });
        }
    }
}
