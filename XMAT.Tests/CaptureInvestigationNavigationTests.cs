// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureInvestigationNavigationTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void RecordNavigationUsesTheOriginalCaptureEpochAndIsBoundedOwnedAndReleasedWithoutImplicitSelection()
        {
            application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var context = new ScriptCaptureContext("test-device");
                var unrelated = new ScriptCaptureContext("unrelated");
                model.CaptureDeviceContexts.Add(context);
                model.CaptureDeviceContexts.Add(unrelated);
                model.SelectedDeviceContext = unrelated;
                var row = new ProxyConnectionModel(1, 99, 77);
                context.Controller.ProxyConnections.Add(row);
                var reveals = 0;
                model.DesktopActions.ConfigureSourceNavigation((provenance, token) => model.RevealInvestigationRecordAsync(provenance, target =>
                {
                    reveals++;
                    Assert.Same(context, target.Context);
                    Assert.Same(row, target.WebProxyRecord);
                    Assert.Null(target.NetworkPacket);
                    return true;
                }, token), (_, _) => Task.FromResult(true));
                var runtime = new AgentRuntime();
                var agent = new AgentDataApplication(runtime, model.CaptureInvestigation, () => true, model.CaptureFileExports, model);
                try
                {
                    runtime.Enable();
                    var owner = runtime.Connect("navigation owner");
                    var borrower = runtime.Connect("navigation borrower");
                    var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                    var grants = new[]
                    {
                        new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source)),
                        new AgentPermission(AgentCapability.Navigation, AgentScope.Application)
                    };
                    Finish(runtime.SetPermissionsAsync(owner, grants, new(responseRows: 1)));
                    Finish(runtime.SetPermissionsAsync(borrower, grants));
                    var result = Run(agent, runtime, owner, source, false);
                    var prepared = Complete(agent.PrepareNavigationAsync(owner, result, CaptureDetailKind.Record, 1, default));
                    Assert.DoesNotContain("ownerSessionId", prepared.GetRawText(), StringComparison.OrdinalIgnoreCase);
                    var id = prepared.GetProperty("id").GetGuid();
                    Assert.Equal(source, prepared.GetProperty("provenance").GetProperty("sourceId").GetGuid());
                    Assert.Equal(0, reveals);
                    Assert.Same(unrelated, model.SelectedDeviceContext);
                    Assert.Equal(id, Complete(agent.PrepareNavigationAsync(owner, result, CaptureDetailKind.Record, 1, default)).GetProperty("id").GetGuid());
                    Assert.Equal(AgentAccessError.OperationNotFound, Assert.Throws<AgentAccessException>(() =>
                        Complete(agent.PrepareNavigationAsync(borrower, result, CaptureDetailKind.Record, 1, default))).Error);
                    Assert.Equal(AgentAccessError.LimitExceeded, Assert.Throws<AgentAccessException>(() =>
                        Complete(agent.PrepareNavigationAsync(owner, result, CaptureDetailKind.Record, 2, default))).Error);
                    var request = new DesktopNavigationRequest(id, prepared.GetProperty("revision").GetInt64(), prepared.GetProperty("epoch").GetInt64());
                    Assert.True(Complete(model.DesktopActions.RevealAsync(request)).Revealed);
                    Assert.Equal(1, reveals);
                    context.Controller.IsRunning = true;
                    context.Controller.IsRunning = false;
                    Assert.Empty(Complete(model.DesktopActions.GetNavigationTargetsAsync()));
                    Assert.Equal(AdministrationFailure.SourceChanged, Assert.Throws<AdministrationException>(() =>
                        Complete(model.DesktopActions.PrepareNavigationAsync(request))).Failure);
                    Finish(agent.ReleaseAsync(owner, result));
                    Assert.Empty(Complete(model.DesktopActions.GetNavigationTargetsAsync()));
                    Assert.Same(unrelated, model.SelectedDeviceContext);
                }
                finally
                {
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(agent.DisposeResultsAsync());
                    Finish(model.DisposeInvestigationAsync());
                }
            });
        }

        [Fact]
        public void AgentOnlyCompletedReportNavigationOpensTheFrozenReportAfterSourceReplacementAndClosesItsSnapshot()
        {
            application.Run(ownerWindow =>
            {
                WpfTestHost.Show(ownerWindow, new Grid());
                using var database = CaptureInvestigationTestData.WebDatabase();
                using var replacement = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer());
                model.DesktopActions.ConfigureSourceNavigation((_, _) => Task.FromResult(false),
                    (provenance, token) => CaptureInvestigationReportWindow.ShowReportAsync(ownerWindow, model, provenance, token));
                var runtime = new AgentRuntime();
                var agent = new AgentDataApplication(runtime, model.CaptureInvestigation, () => true, model.CaptureFileExports, model);
                try
                {
                    runtime.Enable();
                    var owner = runtime.Connect("completed report navigation");
                    var source = Complete(model.CaptureQueries.GetSourcesAsync()).Single().Id;
                    Finish(runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source)),
                            new AgentPermission(AgentCapability.Navigation, AgentScope.Application)]));
                    var result = Run(agent, runtime, owner, source, true);
                    var target = Complete(agent.PrepareNavigationAsync(owner, result, CaptureDetailKind.Report, 0, default));
                    var provenance = target.GetProperty("provenance");
                    Assert.Equal(source, provenance.GetProperty("sourceId").GetGuid());
                    Assert.NotEqual(Guid.Empty, provenance.GetProperty("snapshotId").GetGuid());
                    model.ActiveDatabase = replacement;
                    var request = new DesktopNavigationRequest(target.GetProperty("id").GetGuid(),
                        target.GetProperty("revision").GetInt64(), target.GetProperty("epoch").GetInt64());
                    Assert.True(Complete(model.DesktopActions.RevealAsync(request)).Revealed);
                    WpfTestHost.Pump();
                    var window = Assert.Single(Application.Current.Windows.OfType<CaptureInvestigationReportWindow>());
                    Assert.Equal(4, Assert.IsType<TabControl>(Assert.IsType<DockPanel>(window.Content).Children[1]).Items.Count);
                    Assert.Null(model.SelectedDeviceContext);
                    Assert.Empty(model.AnalysisRuns);
                    Finish(agent.ReleaseAsync(owner, result));
                    Assert.Empty(Complete(model.DesktopActions.GetNavigationTargetsAsync()));
                    Assert.Contains(Complete(model.GetInvestigationSourcesAsync()), item => item.Id == source);
                    window.Close();
                    Finish(model.DisposeInvestigationAsync());
                    Assert.DoesNotContain(Complete(model.GetInvestigationSourcesAsync()), item => item.Id == source);
                }
                finally
                {
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(agent.DisposeResultsAsync());
                    Finish(model.DisposeInvestigationAsync());
                }
            });
        }

        private static Guid Run(AgentDataApplication agent, AgentRuntime runtime, AgentSession owner, Guid source, bool analyze)
        {
            var operation = agent.Run(owner, source, CaptureDataKind.WebProxy, null, Guid.NewGuid().ToString("N"), analyze, null, null);
            Finish(runtime.WaitForCompletionAsync(owner, operation));
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, operation).State);
            return runtime.GetResult(owner, operation).GetProperty("resultId").GetGuid();
        }
    }
}
