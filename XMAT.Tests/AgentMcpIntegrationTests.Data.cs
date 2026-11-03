// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void SourceSummarySearchIsFrozenBoundedConnectionOwnedAndDoesNotAuthorizeRawSql()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                for (var index = 1; index <= 6; index++)
                    CaptureInvestigationTestData.AddWeb(database, index);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                McpClient other = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    var applicationSummary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(owner, [applicationSummary]));
                    var args = new Dictionary<string, object>
                    {
                        ["sourceId"] = source,
                        ["kind"] = "WebProxy",
                        ["idempotencyKey"] = "search",
                        ["filter"] = new { host = "API.XBOXLIVE.COM", statusCode = 429 }
                    };
                    var denied = Payload(Complete(client.CallToolAsync("xmat_search_captures", args).AsTask()));
                    Assert.Equal("PermissionDenied", denied.GetProperty("error").GetString());
                    var permission = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source));
                    Finish(host.Runtime.SetPermissionsAsync(owner, [applicationSummary, permission],
                        new AgentLimits(responseRows: 2, responseBytes: 4096, maximumQueryResults: 1)));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_search_captures", args).AsTask())).GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, operation).State);
                    var receipt = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask()));
                    var result = receipt.GetProperty("resultId").GetGuid();
                    Assert.Equal(6, receipt.GetProperty("recordCount").GetInt64());
                    var replay = Payload(Complete(client.CallToolAsync("xmat_search_captures", args).AsTask()));
                    Assert.Equal(operation, replay.GetProperty("id").GetGuid());
                    CaptureInvestigationTestData.AddWeb(database, 7);
                    var page = DataPage(client, result);
                    Assert.Equal(6, page.GetProperty("totalCount").GetInt64());
                    Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
                    Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 4096);
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, page.GetRawText());
                    Assert.DoesNotContain("requestPath", page.GetRawText());
                    var cursor = page.GetProperty("nextCursor").GetString();
                    var next = DataPage(client, result, cursor: cursor);
                    Assert.Equal(2, next.GetProperty("offset").GetInt64());
                    var wrongSection = Payload(Complete(client.CallToolAsync("xmat_get_data_page",
                        new Dictionary<string, object> { ["resultId"] = result, ["section"] = "Metrics", ["cursor"] = cursor }).AsTask()));
                    Assert.Equal("OperationNotFound", wrongSection.GetProperty("error").GetString());
                    var raw = Payload(Complete(client.CallToolAsync("xmat_run_query", new Dictionary<string, object>
                    {
                        ["sourceId"] = source,
                        ["sql"] = "SELECT * FROM WebProxyConnections",
                        ["idempotencyKey"] = "raw"
                    }).AsTask()));
                    Assert.Equal("PermissionDenied", raw.GetProperty("error").GetString());
                    other = Complete(Connect(host, "summary borrower"));
                    Complete(other.CallToolAsync("xmat_get_session").AsTask());
                    var borrower = host.FindSession(host.Runtime.GetSessions().Single(item => item.DeclaredClientName == "summary borrower").Id);
                    Finish(host.Runtime.SetPermissionsAsync(borrower, [permission]));
                    var borrowed = Payload(Complete(other.CallToolAsync("xmat_get_data_page",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask()));
                    Assert.Equal("OperationNotFound", borrowed.GetProperty("error").GetString());
                    var overflow = Payload(Complete(client.CallToolAsync("xmat_search_captures",
                        new Dictionary<string, object> { ["sourceId"] = source, ["kind"] = "WebProxy", ["idempotencyKey"] = "overflow" }).AsTask()))
                        .GetProperty("id").GetGuid();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, overflow));
                    Assert.Equal(AgentAccessError.LimitExceeded, host.Runtime.GetOperation(owner, overflow).Error);
                    Complete(client.CallToolAsync("xmat_release_data_result", new Dictionary<string, object> { ["resultId"] = result }).AsTask());
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    var latest = RunData(client, host, owner, source, "WebProxy", false, "latest");
                    Assert.Equal(7, DataPage(client, latest).GetProperty("totalCount").GetInt64());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [applicationSummary]));
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    Finish(host.Runtime.SetPermissionsAsync(owner, [permission]));
                    var lost = Payload(Complete(client.CallToolAsync("xmat_get_data_page",
                        new Dictionary<string, object> { ["resultId"] = latest }).AsTask()));
                    Assert.Equal("OperationNotFound", lost.GetProperty("error").GetString());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    if (other != null)
                        Finish(other.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void AgentAnalysisProjectsEveryConsoleAndSnapshotEvidenceWithoutFreeFormRuleData()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                CaptureInvestigationTestData.AddWeb(database, 2, console: "10.0.0.2");
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer(new CaptureInvestigationTestData.SensitiveRule()));
                var unrelated = new Context("unrelated selected capture");
                model.CaptureDeviceContexts.Add(unrelated);
                model.SelectedDeviceContext = unrelated;
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application),
                         new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source))]));
                    var analyzers = Payload(Complete(client.CallToolAsync("xmat_list_analyzers").AsTask()));
                    Assert.True(analyzers.GetProperty("analyzers").EnumerateArray()
                        .Single(analyzer => analyzer.GetProperty("kind").GetString() == "WebProxy").GetProperty("available").GetBoolean());
                    var result = RunData(client, host, owner, source, "WebProxy", true, "analysis");
                    var page = DataPage(client, result, "Findings");
                    Assert.DoesNotContain(CaptureInvestigationTestData.Secret, page.GetRawText());
                    Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" },
                        page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("console").GetString()).Order());
                    foreach (var finding in page.GetProperty("rows").EnumerateArray())
                    {
                        Assert.Equal("Error", finding.GetProperty("severity").GetString());
                        Assert.Equal(1, finding.GetProperty("affectedRecords").GetInt64());
                        Assert.Equal(0, finding.GetProperty("unmappedCalls").GetInt64());
                        var evidence = Payload(Complete(client.CallToolAsync("xmat_get_finding_evidence", new Dictionary<string, object>
                        {
                            ["resultId"] = result,
                            ["findingId"] = finding.GetProperty("id").GetInt64()
                        }).AsTask()));
                        Assert.Equal(page.GetProperty("snapshotId").GetGuid(), evidence.GetProperty("snapshotId").GetGuid());
                        Assert.Single(evidence.GetProperty("rows").EnumerateArray());
                        Assert.InRange(evidence.GetProperty("rows")[0].GetProperty("recordId").GetInt64(), 1, 2);
                        Assert.DoesNotContain(CaptureInvestigationTestData.Secret, evidence.GetRawText());
                    }
                    var generic = Payload(Complete(client.CallToolAsync("xmat_get_query_page",
                        new Dictionary<string, object> { ["resultId"] = result }).AsTask()));
                    Assert.Equal("OperationNotFound", generic.GetProperty("error").GetString());
                    Assert.Same(unrelated, model.SelectedDeviceContext);
                    Assert.False(model.IsAnalysisRunning);
                    Assert.Empty(model.AnalysisRuns);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void NetworkAnalysisWorksWithoutARenderedPacketGridAndDoesNotClaimUnmappedEvidence()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.NetworkDatabase();
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start.AddMilliseconds(10));
                CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start.AddSeconds(1), shortPacket: true);
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(NetworkTraceCaptureAnalyzer.Analyzer);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source))]));
                    var result = RunData(client, host, owner, source, "NetworkTrace", true, "packet-analysis");
                    var records = DataPage(client, result);
                    Assert.Equal(3, records.GetProperty("rows").GetArrayLength());
                    Assert.DoesNotContain("payload", records.GetRawText(), StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(17, records.GetProperty("rows")[0].GetProperty("protocol").GetInt32());
                    Assert.Equal(-1, records.GetProperty("rows")[2].GetProperty("protocol").GetInt32());
                    var findings = DataPage(client, result, "Findings");
                    var matching = findings.GetProperty("rows").EnumerateArray()
                        .Single(finding => finding.GetProperty("ruleId").GetString() == "packet.matching-payload-pairs");
                    Assert.Equal(1, matching.GetProperty("violationCount").GetInt64());
                    Assert.Equal(0, matching.GetProperty("affectedRecords").GetInt64());
                    var evidence = Payload(Complete(client.CallToolAsync("xmat_get_finding_evidence", new Dictionary<string, object>
                    {
                        ["resultId"] = result,
                        ["findingId"] = matching.GetProperty("id").GetInt64()
                    }).AsTask()));
                    Assert.Empty(evidence.GetProperty("rows").EnumerateArray());
                    Assert.Empty(model.CaptureDeviceContexts);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DesktopCanGrantSourceSummariesWithoutGrantingRawDataOrExport()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                var model = new CaptureAppModel { ActiveDatabase = database };
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                AgentAccessWindow manager = null;
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)manager.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    ((CheckBox)manager.FindName("SourceSummaryGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    Assert.Equal(new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source)),
                        Assert.Single(session.Permissions));
                    Assert.False(((CheckBox)manager.FindName("RawQueryGrant")).IsChecked);
                    Assert.False(((CheckBox)manager.FindName("QueryExportGrant")).IsChecked);
                    RunData(client, host, host.FindSession(session.Id), source, "WebProxy", false, "desktop-summary");
                }
                finally
                {
                    manager?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void CancellingAnUninterruptibleRuleDrainsItBeforeReleasingTheSharedAnalysisLease()
        {
            _application.Run(_ =>
            {
                using var database = CaptureInvestigationTestData.WebDatabase();
                CaptureInvestigationTestData.AddWeb(database, 1);
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var model = new CaptureAppModel { ActiveDatabase = database };
                model.CaptureAnalyzers.Add(CaptureInvestigationTestData.Analyzer(
                    new CaptureInvestigationTestData.GatedRule(started, release) { Endpoint = "*" }));
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var owner = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var source = Complete(host.GetSourcesAsync()).Single().Id;
                    Finish(host.Runtime.SetPermissionsAsync(owner,
                        [new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source))]));
                    var operation = Payload(Complete(client.CallToolAsync("xmat_run_analysis", new Dictionary<string, object>
                    {
                        ["sourceId"] = source,
                        ["kind"] = "WebProxy",
                        ["idempotencyKey"] = "drain-rule"
                    }).AsTask())).GetProperty("id").GetGuid();
                    Assert.True(Complete(Task.Run(() => started.Wait(TimeSpan.FromSeconds(10)))));
                    Complete(client.CallToolAsync("xmat_cancel_operation",
                        new Dictionary<string, object> { ["operationId"] = operation }).AsTask());
                    Assert.Equal(AgentOperationState.CancellationRequested, host.Runtime.GetOperation(owner, operation).State);
                    Assert.True(model.IsAnalysisRunning);
                    Assert.False(model.TryBeginCaptureDataOperation(out var deniedLease));
                    Assert.Null(deniedLease);
                    release.Set();
                    Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
                    Assert.Equal(AgentOperationState.Cancelled, host.Runtime.GetOperation(owner, operation).State);
                    Assert.False(model.IsAnalysisRunning);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                    Assert.Single(database.TableByName(XMAT.WebServiceCapture.WebServiceCaptureMethod.TableName).Dataset.DataRecords);
                }
                finally
                {
                    release.Set();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        private static Guid RunData(
            McpClient client, AgentAccessHost host, AgentSession owner, Guid sourceId, string kind, bool analyze, string key)
        {
            var response = Complete(client.CallToolAsync(analyze ? "xmat_run_analysis" : "xmat_search_captures",
                new Dictionary<string, object> { ["sourceId"] = sourceId, ["kind"] = kind, ["idempotencyKey"] = key }).AsTask());
            Assert.False(response.IsError ?? false, Payload(response).GetRawText());
            var operation = Payload(response).GetProperty("id").GetGuid();
            Finish(host.Runtime.WaitForCompletionAsync(owner, operation));
            Assert.Equal(AgentOperationState.Succeeded, host.Runtime.GetOperation(owner, operation).State);
            return Payload(Complete(client.CallToolAsync("xmat_get_result",
                new Dictionary<string, object> { ["operationId"] = operation }).AsTask())).GetProperty("resultId").GetGuid();
        }

        private static JsonElement DataPage(McpClient client, Guid resultId, string section = "Records", string cursor = null)
        {
            var args = new Dictionary<string, object> { ["resultId"] = resultId, ["section"] = section };
            if (cursor != null)
                args.Add("cursor", cursor);
            var response = Complete(client.CallToolAsync("xmat_get_data_page", args).AsTask());
            var page = Payload(response);
            Assert.False(response.IsError ?? false, page.GetRawText());
            return page;
        }
    }
}
