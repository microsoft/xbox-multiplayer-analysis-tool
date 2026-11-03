// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Windows.Controls;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ApplicationPreferenceOperationsTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData("first")]
        [InlineData("last")]
        [InlineData("threshold")]
        [InlineData("mtu")]
        [InlineData("window")]
        [InlineData("language")]
        [InlineData("theme")]
        public void InvalidTypedUpdatesHaveNoEffects(string field)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var before = Complete(setup.Preferences.InspectAsync());
                var values = field switch
                {
                    "first" => before.Values with { FirstProxyPort = 0 },
                    "last" => before.Values with { LastProxyPort = before.Values.FirstProxyPort - 1 },
                    "threshold" => before.Values with { PacketsPerSecondThreshold = -1 },
                    "mtu" => before.Values with { MaximumMtu = 65536 },
                    "window" => before.Values with { DuplicatePacketWindowMilliseconds = -1 },
                    "language" => before.Values with { Language = "..\\en-us" },
                    _ => before.Values with { Theme = "Unknown" }
                };
                Assert.Equal(AdministrationFailure.InvalidRequest, Assert.Throws<AdministrationException>(() =>
                    Complete(setup.Preferences.PrepareAsync(before.Revision, values))).Failure);
                var after = Complete(setup.Preferences.InspectAsync());
                Assert.Equal(before.Revision, after.Revision);
                Assert.Equal(before.Values, after.Values);
                Assert.Equal(0, setup.SaveCount);
                Assert.Empty(setup.AppliedThemes);
            });
        }

        [Fact]
        public void TypedCommitUsesDispatcherPreservesReservationsAppliesThemeAndPersistsExactValues()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                using var directory = new AdministrationTestData.DirectoryFixture();
                using var reserved = setup.Ports.ReservePort(8880, "human-owned listener");
                var path = Path.Combine(directory.Path, "settings.json");
                setup.Save = () =>
                {
                    Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                    CaptureAppSettings.WriteSettingsFile(path,
                        JsonSerializer.Serialize(ApplicationPreferences.ReadValues(setup.Model.PreferencesModel)));
                    return Task.CompletedTask;
                };
                var before = Complete(setup.Preferences.InspectAsync());
                var values = new ApplicationPreferenceValues(9100, 9110, false, 42, 1400, 2000, "fr-fr", "Dark");
                var plan = Complete(Task.Run(() => setup.Preferences.PrepareAsync(before.Revision, values)));
                Assert.Equal(8, plan.Differences.Count);
                Assert.True(plan.LanguageRestartRequired);
                var notifiedOnDispatcher = false;
                setup.Model.PreferencesModel.PropertyChanged += (_, _) =>
                    notifiedOnDispatcher = System.Windows.Application.Current.Dispatcher.CheckAccess();
                var result = Complete(Task.Run(() => setup.Preferences.ExecuteAsync(plan)));
                Assert.True(result.Changed && result.Committed && result.Persisted);
                Assert.True(notifiedOnDispatcher);
                Assert.True(result.LanguageRestartRequired);
                Assert.Equal(values, Complete(setup.Preferences.InspectAsync()).Values);
                Assert.Equal(values, JsonSerializer.Deserialize<ApplicationPreferenceValues>(File.ReadAllText(path)));
                Assert.Equal(new[] { "Dark" }, setup.AppliedThemes);
                Assert.Equal(new[] { 8880 }, setup.Ports.GetReservedPorts());
                Assert.Throws<InvalidOperationException>(() => setup.Ports.ReservePort(8880, "another listener"));
                using var next = setup.Ports.ObtainPort("new listener");
                Assert.Equal(9100, next.Port);
                Assert.Single(Directory.GetFiles(directory.Path));
            });
        }

        [Fact]
        public void EditAwayAndBackOrReplacingPreferenceModelsInvalidatesOldRevision()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var before = Complete(setup.Preferences.InspectAsync());
                var proxy = (WebServiceCapture.Models.PreferencesModel)setup.Model.PreferencesModel.WebProxyCapturePreferences;
                proxy.PromptToDisableOnClose = !before.Values.PromptToDisableProxyOnClose;
                proxy.PromptToDisableOnClose = before.Values.PromptToDisableProxyOnClose;
                Assert.Equal(AdministrationFailure.SourceChanged, Assert.Throws<AdministrationException>(() =>
                    Complete(setup.Preferences.PrepareAsync(before.Revision, before.Values))).Failure);
                var current = Complete(setup.Preferences.InspectAsync());
                setup.Model.PreferencesModel = ApplicationPreferences.CreateDraft(current.Values);
                Assert.Equal(AdministrationFailure.SourceChanged, Assert.Throws<AdministrationException>(() =>
                    Complete(setup.Preferences.PrepareAsync(current.Revision, current.Values))).Failure);
                Assert.Equal(0, setup.SaveCount);
            });
        }

        [Fact]
        public void CommitReceiptIsStampedBeforeHumanNotificationChangesAndPersistenceFailureNeverClaimsRollback()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Preferences);
                setup.Save = () => throw new IOException("private-path-and-source-do-not-disclose");
                var before = Complete(setup.Preferences.InspectAsync());
                var reentered = false;
                setup.Model.PreferencesModel.PropertyChanged += (_, _) =>
                {
                    if (reentered)
                        return;
                    reentered = true;
                    setup.Model.PreferencesModel.Language = "fr-fr";
                };
                var operation = Complete(setup.Agent.UpdatePreferencesAsync(setup.Session, before.Revision,
                    before.Values with { Theme = "Dark" }, "save-fails", default));
                Assert.Equal(AgentOperationState.AwaitingApproval, setup.Runtime.GetOperation(setup.Session, operation).State);
                Assert.Equal(0, setup.SaveCount);
                setup.Approve(operation);
                var status = setup.Runtime.GetOperation(setup.Session, operation);
                Assert.Equal(AgentAccessError.PersistenceFailed, status.Error);
                Assert.True(status.EffectsMayHaveOccurred);
                var result = setup.Runtime.GetResult(setup.Session, operation);
                Assert.True(result.GetProperty("committed").GetBoolean());
                Assert.False(result.GetProperty("persisted").GetBoolean());
                var after = Complete(setup.Preferences.InspectAsync());
                Assert.Equal("Dark", after.Values.Theme);
                Assert.Equal("fr-fr", after.Values.Language);
                Assert.True(after.Revision > result.GetProperty("revision").GetInt64());
                Assert.DoesNotContain("private-path-and-source", result.GetRawText());
                Assert.DoesNotContain("private-path-and-source", JsonSerializer.Serialize(status));
                Assert.Equal(operation, Complete(setup.Agent.UpdatePreferencesAsync(setup.Session, before.Revision,
                    before.Values with { Theme = "Dark" }, "save-fails", default)));
                Assert.Equal(1, setup.SaveCount);
            });
        }

        [Fact]
        public void RevocationAfterCommitDrainsPersistenceWithoutUndoingCommittedPreferences()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Preferences);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                setup.Save = () => release.Task;
                var before = Complete(setup.Preferences.InspectAsync());
                var operation = Complete(setup.Agent.UpdatePreferencesAsync(setup.Session, before.Revision,
                    before.Values with { MaximumMtu = 1500 }, "drain", default));
                Assert.True(setup.Runtime.Approve(operation));
                PumpUntil(() => setup.SaveCount == 1);
                var revoke = setup.Runtime.SetPermissionsAsync(setup.Session, []);
                Assert.False(revoke.IsCompleted);
                var preferenceDrain = setup.Preferences.DrainAsync();
                Assert.False(preferenceDrain.IsCompleted);
                Assert.Equal(1500, Complete(setup.Preferences.InspectAsync()).Values.MaximumMtu);
                release.TrySetResult();
                Finish(revoke);
                Finish(preferenceDrain);
                Assert.Equal(1500, Complete(setup.Preferences.InspectAsync()).Values.MaximumMtu);
                Assert.Equal(AgentAccessError.PermissionDenied,
                    Assert.Throws<AgentAccessException>(() => setup.Runtime.GetResult(setup.Session, operation)).Error);
            });
        }

        [Fact]
        public void ThemeFailureBeforeCommitRestoresOldThemeAndDoesNotMutatePreferences()
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                var attempts = new List<string>();
                var preferences = new ApplicationPreferences(setup.Model,
                    System.Windows.Threading.Dispatcher.CurrentDispatcher, setup.Ports,
                    () => throw new InvalidOperationException("saving must not run"),
                    theme =>
                    {
                        attempts.Add(theme);
                        if (theme == "Dark")
                            throw new IOException("private theme resource path");
                    }, () => ["en-us"]);
                var before = Complete(preferences.InspectAsync());
                var plan = Complete(preferences.PrepareAsync(before.Revision,
                    before.Values with { Theme = "Dark", FirstProxyPort = 9000, LastProxyPort = 9010 }));
                Assert.Throws<IOException>(() => Complete(preferences.ExecuteAsync(plan)));
                Assert.Equal(before.Values, Complete(preferences.InspectAsync()).Values);
                Assert.Equal(new[] { "Dark", "Light" }, attempts);
                Assert.Equal(8880, setup.Ports.FirstPort);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DesktopApplyTracksPreparationAndDrainsWithoutUiThreadFileReadsOrLateCancelledCommit(bool cancelBeforeCommit)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                using var release = new ManualResetEventSlim();
                using var cancellation = new CancellationTokenSource();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pause = false;
                var preferences = new ApplicationPreferences(setup.Model,
                    System.Windows.Threading.Dispatcher.CurrentDispatcher, setup.Ports,
                    () => Task.CompletedTask, _ => { }, () =>
                    {
                        Assert.False(System.Windows.Application.Current.Dispatcher.CheckAccess());
                        if (pause)
                        {
                            entered.TrySetResult();
                            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                        }
                        return ["en-us"];
                    });
                var before = Complete(preferences.InspectAsync());
                pause = true;
                var applying = preferences.ApplyAsync(before.Revision, before.Values with { MaximumMtu = 1450 }, cancellation.Token);
                try
                {
                    PumpUntil(() => entered.Task.IsCompleted);
                    var draining = preferences.DrainAsync();
                    Assert.False(draining.IsCompleted);
                    if (cancelBeforeCommit)
                        cancellation.Cancel();
                    release.Set();
                    if (cancelBeforeCommit)
                        Assert.ThrowsAny<OperationCanceledException>(() => Complete(applying));
                    else
                        Assert.True(Complete(applying).Persisted);
                    Finish(draining);
                    Assert.Equal(cancelBeforeCommit ? before.Values.MaximumMtu : 1450,
                        ApplicationPreferences.ReadValues(setup.Model.PreferencesModel).MaximumMtu);
                }
                finally { release.Set(); }
            });
        }

        [Fact]
        public void PreferencesWindowEditsDraftAndSavesThroughSameValidatedService()
        {
            application.Run(owner =>
            {
                using var setup = new AdministrationTestData();
                WpfTestHost.Show(owner, new Grid());
                var before = Complete(setup.Preferences.InspectAsync());
                var window = new PreferencesWindow(setup.Model, setup.Preferences) { Owner = owner };
                try
                {
                    window.Show();
                    PumpUntil(() => window.DataContext is AppPreferencesModel draft &&
                        !ReferenceEquals(draft, setup.Model.PreferencesModel));
                    var proxy = (CaptureOptionsFull)window.FindName("ProxyOptions");
                    ((TextBox)proxy.FindName("StartingPort")).Text = "9200";
                    ((TextBox)proxy.FindName("EndingPort")).Text = "9205";
                    ((CheckBox)proxy.FindName("DisablePromptLabel")).IsChecked = false;
                    Assert.Equal(before.Values, Complete(setup.Preferences.InspectAsync()).Values);
                    var analysis = (NetworkTraceCaptureAnalysis.NetworkTraceAnalysisOptions)window.FindName("TraceOptions");
                    ((TextBox)analysis.FindName("MaximumMtuValue")).Text = "1480";
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedItem = "Dark";
                    ((ComboBox)window.FindName("LanguageChoice")).SelectedItem = "fr-fr";
                    var saved = Complete(window.SaveDraftAsync());
                    Assert.True(saved.Committed && saved.Persisted);
                    var after = Complete(setup.Preferences.InspectAsync());
                    Assert.Equal(9200, after.Values.FirstProxyPort);
                    Assert.Equal(9205, after.Values.LastProxyPort);
                    Assert.False(after.Values.PromptToDisableProxyOnClose);
                    Assert.Equal(1480, after.Values.MaximumMtu);
                    Assert.Equal("Dark", after.Values.Theme);
                    Assert.Equal("fr-fr", after.Values.Language);
                    Assert.Equal(1, setup.SaveCount);
                    ((TextBox)proxy.FindName("StartingPort")).Text = "9300";
                    window.Close();
                    Assert.Equal(9200, Complete(setup.Preferences.InspectAsync()).Values.FirstProxyPort);
                }
                finally { window.Close(); }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StaleOrChangedGrantsCannotApplyAnApprovedPreferenceDiff(bool changeGrants)
        {
            application.Run(_ =>
            {
                using var setup = new AdministrationTestData();
                setup.Grant(AgentCapability.Preferences);
                var before = Complete(setup.Preferences.InspectAsync());
                var operation = Complete(setup.Agent.UpdatePreferencesAsync(setup.Session, before.Revision,
                    before.Values with { MaximumMtu = 1460 }, "exact", default));
                if (changeGrants)
                {
                    setup.Grant(AgentCapability.Preferences, AgentCapability.Navigation);
                    Assert.False(setup.Runtime.Approve(operation));
                    Assert.Equal(AgentOperationState.Denied, setup.Runtime.GetOperation(setup.Session, operation).State);
                }
                else
                {
                    setup.Model.PreferencesModel.Theme = "Dark";
                    setup.Approve(operation);
                    Assert.Equal(AgentOperationState.SourceChanged, setup.Runtime.GetOperation(setup.Session, operation).State);
                }
                Assert.Equal(before.Values.MaximumMtu, Complete(setup.Preferences.InspectAsync()).Values.MaximumMtu);
                Assert.Equal(0, setup.SaveCount);
            });
        }
    }
}
