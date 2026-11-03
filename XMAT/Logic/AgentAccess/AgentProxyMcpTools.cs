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
    internal sealed class AgentProxyMcpTools(
        AgentProxyApplication proxy, AgentSession session, AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<Guid, int, CancellationToken, Task<CallToolResult>>)OptionsAsync,
                "xmat_get_proxy_options", "AGENT_TOOL_PROXY_OPTIONS", true),
            AgentMcpTools.Tool((Func<Guid, long, bool, string, string, CancellationToken, Task<CallToolResult>>)SetEnabledAsync,
                "xmat_set_device_proxy", "AGENT_TOOL_PROXY_STATE", false),
            AgentMcpTools.Tool((Func<Guid, long, string, string, CancellationToken, Task<CallToolResult>>)ApplyAsync,
                "xmat_apply_proxy_port", "AGENT_TOOL_PROXY_APPLY", false)
        ];

        private Task<CallToolResult> OptionsAsync(
            Guid captureId, int addressOffset = 0, CancellationToken cancellationToken = default) =>
            wrap(() => proxy.OptionsAsync(session, captureId, addressOffset, cancellationToken));

        private Task<CallToolResult> SetEnabledAsync(
            Guid captureId, long expectedRevision, bool enabled, string idempotencyKey,
            string sourceAddress = null, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await proxy.SubmitAsync(
                session, captureId, expectedRevision, enabled ? CaptureProxyAction.Enable : CaptureProxyAction.Disable,
                sourceAddress, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> ApplyAsync(
            Guid captureId, long expectedRevision, string sourceAddress, string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await proxy.SubmitAsync(
                session, captureId, expectedRevision, CaptureProxyAction.ApplyPendingPort,
                sourceAddress, idempotencyKey, cancellationToken))));
    }
}
