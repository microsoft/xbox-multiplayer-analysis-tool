// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentDataMcpTools(
        AgentDataApplication application, AgentSession session, AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal IEnumerable<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)AnalyzersAsync,
                "xmat_list_analyzers", "AGENT_TOOL_ANALYZERS", true),
            AgentMcpTools.Tool((Func<Guid, CaptureDataKind, string, CaptureDataFilter, int?, long?, Task<CallToolResult>>)SearchAsync,
                "xmat_search_captures", "AGENT_TOOL_SEARCH", true),
            AgentMcpTools.Tool((Func<Guid, CaptureDataKind, string, CaptureDataFilter, int?, long?, Task<CallToolResult>>)AnalyzeAsync,
                "xmat_run_analysis", "AGENT_TOOL_ANALYZE", true),
            AgentMcpTools.Tool((Func<Guid, CaptureDataSection, string, Task<CallToolResult>>)PageAsync,
                "xmat_get_data_page", "AGENT_TOOL_DATA_PAGE", true),
            AgentMcpTools.Tool((Func<Guid, long, string, Task<CallToolResult>>)EvidenceAsync,
                "xmat_get_finding_evidence", "AGENT_TOOL_FINDING_EVIDENCE", true),
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)WebSocketsAsync,
                "xmat_list_websocket_captures", "DATA_TOOL_WEBSOCKETS", true),
            AgentMcpTools.Tool((Func<Guid, CaptureDetailKind, long, string, CancellationToken, Task<CallToolResult>>)DetailsAsync,
                "xmat_get_data_details", "DATA_TOOL_DETAILS", true),
            AgentMcpTools.Tool((Func<Guid, CaptureDetailKind, long, long, int, long, int, CancellationToken, Task<CallToolResult>>)DetailValueAsync,
                "xmat_get_data_detail_value", "DATA_TOOL_DETAIL_VALUE", true),
            AgentMcpTools.Tool((Func<string, CancellationToken, Task<CallToolResult>>)ReportsAsync,
                "xmat_list_completed_reports", "DATA_TOOL_REPORTS", true),
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)OpenReportAsync,
                "xmat_open_completed_report", "DATA_TOOL_REPORT_OPEN", true),
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)ManifestAsync,
                "xmat_get_report_manifest", "DATA_TOOL_REPORT_MANIFEST", true),
            AgentMcpTools.Tool((Func<Guid, Guid, string, string, CancellationToken, Task<CallToolResult>>)ExportReportAsync,
                "xmat_export_analysis_report", "DATA_TOOL_REPORT_EXPORT", false),
            AgentMcpTools.Tool((Func<Guid, CaptureDetailKind, long, CancellationToken, Task<CallToolResult>>)NavigationAsync,
                "xmat_prepare_data_navigation", "DATA_TOOL_NAVIGATION", true),
            AgentMcpTools.Tool((Func<Guid, Task<CallToolResult>>)ReleaseAsync,
                "xmat_release_data_result", "AGENT_TOOL_DATA_RELEASE", false)
        ];

        private Task<CallToolResult> AnalyzersAsync(CancellationToken cancellationToken = default) =>
            wrap(() => application.ListAnalyzersAsync(session, cancellationToken));

        private Task<CallToolResult> SearchAsync(
            Guid sourceId, CaptureDataKind kind, string idempotencyKey, CaptureDataFilter filter = null,
            int? timeoutSeconds = null, long? maximumResultBytes = null) =>
            RunAsync(sourceId, kind, idempotencyKey, filter, timeoutSeconds, maximumResultBytes, false);

        private Task<CallToolResult> AnalyzeAsync(
            Guid sourceId, CaptureDataKind kind, string idempotencyKey, CaptureDataFilter filter = null,
            int? timeoutSeconds = null, long? maximumResultBytes = null) =>
            RunAsync(sourceId, kind, idempotencyKey, filter, timeoutSeconds, maximumResultBytes, true);

        private Task<CallToolResult> RunAsync(
            Guid sourceId, CaptureDataKind kind, string key, CaptureDataFilter filter, int? seconds, long? bytes, bool analyze) =>
            wrap(() => Task.FromResult(AgentJson.Encode(runtime.GetOperation(session,
                application.Run(session, sourceId, kind, filter, key, analyze, seconds, bytes)))));

        private Task<CallToolResult> PageAsync(
            Guid resultId, CaptureDataSection section = CaptureDataSection.Records, string cursor = null) =>
            wrap(() => Task.FromResult(application.ReadPage(session, resultId, section, cursor)));

        private Task<CallToolResult> EvidenceAsync(Guid resultId, long findingId, string cursor = null) =>
            wrap(() => Task.FromResult(application.ReadEvidence(session, resultId, findingId, cursor)));

        private Task<CallToolResult> ReleaseAsync(Guid resultId) => wrap(() => application.ReleaseAsync(session, resultId));

        private Task<CallToolResult> WebSocketsAsync(Guid sourceId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ListWebSocketsAsync(session, sourceId, cursor, cancellationToken));

        private Task<CallToolResult> DetailsAsync(
            Guid resultId, CaptureDetailKind kind, long itemId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadDetailsAsync(session, resultId, kind, itemId, cursor, cancellationToken));

        private Task<CallToolResult> DetailValueAsync(
            Guid resultId, CaptureDetailKind kind, long itemId, long row, int column, long byteOffset = 0,
            int maximumBytes = 16 * 1024, CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadDetailValueAsync(session, resultId, kind, itemId, row, column, byteOffset, maximumBytes, cancellationToken));

        private Task<CallToolResult> ReportsAsync(string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ListReportsAsync(session, cursor, cancellationToken));

        private Task<CallToolResult> OpenReportAsync(Guid reportId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.OpenReportAsync(session, reportId, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ManifestAsync(Guid resultId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadReportManifestAsync(session, resultId, cursor, cancellationToken));

        private Task<CallToolResult> ExportReportAsync(
            Guid resultId, Guid folderId, string directoryName, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.ExportReportAsync(session, resultId, folderId, directoryName, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> NavigationAsync(
            Guid resultId, CaptureDetailKind kind, long itemId = 0, CancellationToken cancellationToken = default) =>
            wrap(() => application.PrepareNavigationAsync(session, resultId, kind, itemId, cancellationToken));
    }
}
