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
    internal sealed class AgentCaptureFileMcpTools(
        AgentCaptureFileApplication files, AgentSession session, AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<Guid, CaptureArchiveFormat, Guid, string, string, string, CancellationToken, Task<CallToolResult>>)SaveAsync,
                "xmat_save_capture", "AGENT_TOOL_CAPTURE_FILE", false),
            AgentMcpTools.Tool((Func<Guid, string, Guid?, CancellationToken, Task<CallToolResult>>)LoadAsync,
                "xmat_load_capture", "AGENT_TOOL_CAPTURE_LOAD", false),
            AgentMcpTools.Tool((Func<Guid, Guid, string, CancellationToken, Task<CallToolResult>>)ImportSazAsync,
                "xmat_import_saz", "AGENT_TOOL_SAZ_IMPORT", false),
            AgentMcpTools.Tool((Func<Guid, Guid, string, CancellationToken, Task<CallToolResult>>)ImportEtlAsync,
                "xmat_import_etl", "ETL_TOOL_IMPORT", false)
        ];

        private Task<CallToolResult> SaveAsync(
            Guid sourceId, CaptureArchiveFormat format, Guid folderId, string fileName,
            string idempotencyKey, string deviceName = null, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.SaveAsync(
                session, sourceId, format, folderId, fileName, idempotencyKey, deviceName, cancellationToken))));

        private Task<CallToolResult> LoadAsync(
            Guid inputId, string idempotencyKey, Guid? expectedLoadedSourceId = null, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.LoadAsync(
                session, inputId, expectedLoadedSourceId, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ImportSazAsync(
            Guid inputId, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.ImportSazAsync(
                session, inputId, sourceId, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ImportEtlAsync(
            Guid inputId, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.ImportEtlAsync(
                session, inputId, sourceId, idempotencyKey, cancellationToken))));
    }
}
