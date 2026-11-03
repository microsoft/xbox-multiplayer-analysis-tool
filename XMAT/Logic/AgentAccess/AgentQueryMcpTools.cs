// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentQueryMcpTools(
        AgentQueryApplication application,
        AgentSession session,
        AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create() => new()
        {
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)SourcesAsync,
                "xmat_list_sources", "AGENT_TOOL_SOURCES", true),
            AgentMcpTools.Tool((Func<Guid, string, string, CancellationToken, Task<CallToolResult>>)SchemaAsync,
                "xmat_describe_schema", "AGENT_TOOL_SCHEMA", true),
            AgentMcpTools.Tool((Func<Guid, string, string, int?, long?, CancellationToken, Task<CallToolResult>>)RunAsync,
                "xmat_run_query", "AGENT_TOOL_QUERY", true),
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)PageAsync,
                "xmat_get_query_page", "AGENT_TOOL_QUERY_PAGE", true),
            AgentMcpTools.Tool((Func<Guid, long, int, long, int, CancellationToken, Task<CallToolResult>>)ValueAsync,
                "xmat_get_query_value", "AGENT_TOOL_QUERY_VALUE", true),
            AgentMcpTools.Tool((Func<Guid, Task<CallToolResult>>)ReleaseAsync,
                "xmat_release_query_result", "AGENT_TOOL_QUERY_RELEASE", false),
            AgentMcpTools.Tool((Func<Guid, QueryExportFormat, string, Task<CallToolResult>>)ExportAsync,
                "xmat_export_query_result", "AGENT_TOOL_QUERY_EXPORT", true),
            AgentMcpTools.Tool((Func<Guid, long, int, CancellationToken, Task<CallToolResult>>)ExportChunkAsync,
                "xmat_get_query_export_chunk", "AGENT_TOOL_QUERY_EXPORT_CHUNK", true),
            AgentMcpTools.Tool((Func<Guid, Task<CallToolResult>>)ReleaseExportAsync,
                "xmat_release_query_export", "AGENT_TOOL_QUERY_EXPORT_RELEASE", false),
            AgentMcpTools.Tool((Func<int, Task<CallToolResult>>)FoldersAsync,
                "xmat_list_export_folders", "AGENT_TOOL_EXPORT_FOLDERS", true),
            AgentMcpTools.Tool((Func<Guid, Guid, string, QueryExportFormat, string, CancellationToken, Task<CallToolResult>>)SaveFileAsync,
                "xmat_save_query_result", "AGENT_TOOL_QUERY_SAVE_FILE", false)
        };

        private Task<CallToolResult> SourcesAsync(CancellationToken cancellationToken) =>
            wrap(() => application.ListSourcesAsync(session, cancellationToken));

        private Task<CallToolResult> SchemaAsync(
            Guid sourceId, string tableName = null, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.DescribeSchemaAsync(session, sourceId, tableName, cursor, cancellationToken));

        private Task<CallToolResult> RunAsync(
            Guid sourceId, string sql, string idempotencyKey,
            int? timeoutSeconds = null, long? maximumResultBytes = null, CancellationToken cancellationToken = default) =>
            wrap(async () =>
            {
                var id = await application.RunAsync(session, sourceId, sql, idempotencyKey,
                    timeoutSeconds, maximumResultBytes, cancellationToken);
                return AgentJson.Encode(runtime.GetOperation(session, id));
            });

        private Task<CallToolResult> PageAsync(
            Guid resultId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadPageAsync(session, resultId, cursor, cancellationToken));

        private Task<CallToolResult> ValueAsync(
            Guid resultId, long row, int column, long byteOffset = 0, int maximumBytes = 4096,
            CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadValueAsync(session, resultId, row, column, byteOffset, maximumBytes, cancellationToken));

        private Task<CallToolResult> ReleaseAsync(Guid resultId) => wrap(() => application.ReleaseAsync(session, resultId));

        private Task<CallToolResult> ExportAsync(Guid resultId, QueryExportFormat format, string idempotencyKey) =>
            wrap(async () =>
            {
                var id = await application.ExportAsync(session, resultId, format, idempotencyKey);
                return AgentJson.Encode(runtime.GetOperation(session, id));
            });

        private Task<CallToolResult> ExportChunkAsync(
            Guid artifactId, long byteOffset = 0, int maximumBytes = 4096, CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadExportAsync(session, artifactId, byteOffset, maximumBytes, cancellationToken));

        private Task<CallToolResult> ReleaseExportAsync(Guid artifactId) => wrap(() => application.ReleaseExportAsync(session, artifactId));

        private Task<CallToolResult> FoldersAsync(int offset = 0) => wrap(() => application.ExportFoldersAsync(session, offset));

        private Task<CallToolResult> SaveFileAsync(
            Guid resultId, Guid folderId, string fileName, QueryExportFormat format, string idempotencyKey,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await application.SaveFileAsync(
                    session, resultId, folderId, fileName, format, idempotencyKey, cancellationToken))));
    }
}
