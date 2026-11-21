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
        internal McpServerPrimitiveCollection<McpServerTool> Create()
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>
            {
            runtime.Policy == null
                ? AgentMcpTools.Tool((Func<Guid, CaptureArchiveFormat, Guid, string, string, string, CancellationToken, Task<CallToolResult>>)SaveAsync,
                    "xmat_save_capture", "AGENT_TOOL_CAPTURE_FILE", false)
                : AgentMcpTools.Tool((Func<Guid, CaptureArchiveFormat, string, string, string, bool, CancellationToken, Task<CallToolResult>>)SavePathAsync,
                    "xmat_save_capture", "AGENT_TOOL_CAPTURE_FILE", false),
            runtime.Policy == null
                ? AgentMcpTools.Tool((Func<Guid, string, Guid?, CancellationToken, Task<CallToolResult>>)LoadAsync,
                    "xmat_load_capture", "AGENT_TOOL_CAPTURE_LOAD", false)
                : AgentMcpTools.Tool((Func<string, string, Guid?, CancellationToken, Task<CallToolResult>>)LoadPathAsync,
                    "xmat_load_capture", "AGENT_TOOL_CAPTURE_LOAD", false),
            runtime.Policy == null
                ? AgentMcpTools.Tool((Func<Guid, Guid, string, CancellationToken, Task<CallToolResult>>)ImportSazAsync,
                    "xmat_import_saz", "AGENT_TOOL_SAZ_IMPORT", false)
                : AgentMcpTools.Tool((Func<string, Guid, string, CancellationToken, Task<CallToolResult>>)ImportSazPathAsync,
                    "xmat_import_saz", "AGENT_TOOL_SAZ_IMPORT", false),
            runtime.Policy == null
                ? AgentMcpTools.Tool((Func<Guid, Guid, string, CancellationToken, Task<CallToolResult>>)ImportEtlAsync,
                    "xmat_import_etl", "ETL_TOOL_IMPORT", false)
                : AgentMcpTools.Tool((Func<string, Guid, string, CancellationToken, Task<CallToolResult>>)ImportEtlPathAsync,
                    "xmat_import_etl", "ETL_TOOL_IMPORT", false)
            };
            if (runtime.Policy != null)
                tools.Add(AgentMcpTools.Tool((Func<string, string, Guid?, CancellationToken, Task<CallToolResult>>)OpenAsync,
                    "xmat_open_capture", "AGENT_TOOL_OPEN_READONLY", false));
            return tools;
        }

        private Task<CallToolResult> OpenAsync(
            string path, string idempotencyKey, Guid? expectedLoadedSourceId = null, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.OpenPathAsync(
                session, path, expectedLoadedSourceId, idempotencyKey, cancellationToken))));

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

        private Task<CallToolResult> SavePathAsync(
            Guid sourceId, CaptureArchiveFormat format, string path, string idempotencyKey,
            string deviceName = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.SavePathAsync(
                session, sourceId, format, path, idempotencyKey, deviceName, overwrite, cancellationToken))));

        private Task<CallToolResult> LoadPathAsync(
            string path, string idempotencyKey, Guid? expectedLoadedSourceId = null, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.LoadPathAsync(
                session, path, expectedLoadedSourceId, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ImportSazPathAsync(
            string path, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.ImportPathAsync(
                session, path, sourceId, idempotencyKey, false, cancellationToken))));

        private Task<CallToolResult> ImportEtlPathAsync(
            string path, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await files.ImportPathAsync(
                session, path, sourceId, idempotencyKey, true, cancellationToken))));
    }
}
