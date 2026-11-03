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

namespace XMAT.AgentAccess
{
    internal sealed class AgentScriptMcpTools(
        AgentScriptApplication application, AgentSession session, AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal IEnumerable<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)ListAsync,
                "xmat_list_scripts", "AGENT_TOOL_SCRIPTS", true),
            AgentMcpTools.Tool((Func<Guid, string, CancellationToken, Task<CallToolResult>>)SymbolsAsync,
                "xmat_describe_scripting", "AGENT_TOOL_SCRIPT_SYMBOLS", true),
            AgentMcpTools.Tool((Func<Guid, Guid, long, long, int, CancellationToken, Task<CallToolResult>>)ReadAsync,
                "xmat_read_script", "AGENT_TOOL_SCRIPT_READ", true),
            AgentMcpTools.Tool((Func<Guid, Guid, long, string, string, string, CancellationToken, Task<CallToolResult>>)UpdateAsync,
                "xmat_update_script", "AGENT_TOOL_SCRIPT_UPDATE", false),
            AgentMcpTools.Tool((Func<Guid, Guid, long, string, string, CancellationToken, Task<CallToolResult>>)ValidateAsync,
                "xmat_validate_script", "AGENT_TOOL_SCRIPT_VALIDATE", true),
            AgentMcpTools.Tool((Func<Guid, Guid, long, string, bool, string, CancellationToken, Task<CallToolResult>>)SetEnabledAsync,
                "xmat_set_script_enabled", "AGENT_TOOL_SCRIPT_ENABLED", false),
            AgentMcpTools.Tool((Func<Guid, string, Task<CallToolResult>>)DiagnosticsAsync,
                "xmat_get_script_diagnostics", "AGENT_TOOL_SCRIPT_DIAGNOSTICS", true),
            AgentMcpTools.Tool((Func<Guid, Task<CallToolResult>>)ReleaseAsync,
                "xmat_release_script_validation", "AGENT_TOOL_SCRIPT_RELEASE", false),
            AgentMcpTools.Tool((Func<Guid, Guid, long, string, Guid, string, string, CancellationToken, Task<CallToolResult>>)SaveFileAsync,
                "xmat_save_script", "AGENT_TOOL_SCRIPT_SAVE_FILE", false),
            AgentMcpTools.Tool((Func<int, Task<CallToolResult>>)InputsAsync,
                "xmat_list_input_files", "AGENT_TOOL_INPUT_FILES", true),
            AgentMcpTools.Tool((Func<Guid, Guid, long, string, Guid, string, CancellationToken, Task<CallToolResult>>)LoadFileAsync,
                "xmat_load_script", "AGENT_TOOL_SCRIPT_LOAD_FILE", false)
        ];

        private Task<CallToolResult> ListAsync(Guid captureId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.ListAsync(session, captureId, cursor, cancellationToken));
        private Task<CallToolResult> SymbolsAsync(Guid captureId, string cursor = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.SymbolsAsync(session, captureId, cursor, cancellationToken));
        private Task<CallToolResult> ReadAsync(
            Guid captureId, Guid scriptId, long expectedRevision, long byteOffset = 0, int maximumBytes = 4096,
            CancellationToken cancellationToken = default) =>
            wrap(() => application.ReadAsync(session, captureId, scriptId, expectedRevision, byteOffset, maximumBytes, cancellationToken));

        private Task<CallToolResult> UpdateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, string source, string idempotencyKey,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await application.UpdateAsync(session, captureId, scriptId,
                    expectedRevision, expectedSourceHash, source, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ValidateAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, string idempotencyKey,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await application.ValidateAsync(session, captureId, scriptId,
                    expectedRevision, expectedSourceHash, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> SetEnabledAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash, bool enabled, string idempotencyKey,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await application.SetEnabledAsync(session, captureId, scriptId,
                    expectedRevision, expectedSourceHash, enabled, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> DiagnosticsAsync(Guid validationId, string cursor = null) =>
            wrap(() => Task.FromResult(application.Diagnostics(session, validationId, cursor)));
        private Task<CallToolResult> ReleaseAsync(Guid validationId) => wrap(() => application.ReleaseValidationAsync(session, validationId));

        private Task<CallToolResult> SaveFileAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            Guid folderId, string fileName, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await application.SaveFileAsync(
                session, captureId, scriptId, expectedRevision, expectedSourceHash, folderId, fileName, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> InputsAsync(int offset = 0) =>
            wrap(() => application.InputsAsync(session, offset));

        private Task<CallToolResult> LoadFileAsync(
            Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            Guid inputId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await application.LoadFileAsync(
                session, captureId, scriptId, expectedRevision, expectedSourceHash, inputId, idempotencyKey, cancellationToken))));
    }
}
