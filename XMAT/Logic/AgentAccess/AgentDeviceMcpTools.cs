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
    internal sealed class AgentDeviceMcpTools(
        AgentDeviceApplication devices,
        AgentSession session,
        AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<Task<CallToolResult>>)DescribeAsync,
                "xmat_describe_devices", "AGENT_TOOL_DEVICES_DESCRIBE", true),
            AgentMcpTools.Tool((Func<Guid, bool, CancellationToken, Task<CallToolResult>>)GetAsync,
                "xmat_get_device", "AGENT_TOOL_DEVICE_GET", true),
            AgentMcpTools.Tool((Func<CaptureDeviceKind, CaptureType, string, string, CancellationToken, Task<CallToolResult>>)AddAsync,
                "xmat_add_device", "AGENT_TOOL_DEVICE_ADD", false),
            AgentMcpTools.Tool((Func<Guid, long, string, CancellationToken, Task<CallToolResult>>)RemoveAsync,
                "xmat_remove_device", "AGENT_TOOL_DEVICE_REMOVE", false),
            AgentMcpTools.Tool((Func<Guid, long, string, ushort?, CancellationToken, Task<CallToolResult>>)SetPendingPortAsync,
                "xmat_set_pending_proxy_port", "AGENT_TOOL_DEVICE_PORT", false)
        ];

        private Task<CallToolResult> DescribeAsync() => wrap(() => devices.DescribeAsync(session));

        private Task<CallToolResult> GetAsync(
            Guid captureId, bool inspectProxy = false, CancellationToken cancellationToken = default) =>
            wrap(() => devices.GetDeviceAsync(session, captureId, inspectProxy, cancellationToken));

        private Task<CallToolResult> AddAsync(
            CaptureDeviceKind kind, CaptureType captureType, string idempotencyKey, string address = null,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await devices.AddAsync(
                    session, new(kind, captureType, address), idempotencyKey, cancellationToken))));

        private Task<CallToolResult> RemoveAsync(
            Guid captureId, long expectedRevision, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await devices.RemoveAsync(
                session, captureId, expectedRevision, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> SetPendingPortAsync(
            Guid captureId, long expectedRevision, string idempotencyKey, ushort? port = null,
            CancellationToken cancellationToken = default) => wrap(async () =>
                AgentJson.Encode(runtime.GetOperation(session, await devices.SetPendingPortAsync(
                    session, captureId, expectedRevision, port, idempotencyKey, cancellationToken))));
    }
}
