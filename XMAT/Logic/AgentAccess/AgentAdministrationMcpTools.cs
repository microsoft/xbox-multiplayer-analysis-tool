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
    internal sealed class AgentAdministrationMcpTools(
        AgentAdministrationApplication application, AgentSession session, AgentRuntime runtime,
        Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create() =>
        [
            AgentMcpTools.Tool((Func<int, long?, CancellationToken, Task<CallToolResult>>)HostsAsync,
                "xmat_get_host_policy", "CTRL_TOOL_HOSTS", true),
            AgentMcpTools.Tool((Func<HostPolicyRequest, string, CancellationToken, Task<CallToolResult>>)UpdateHostsAsync,
                "xmat_update_host_policy", "CTRL_TOOL_UPDATE_HOSTS", false),
            AgentMcpTools.Tool((Func<int, CancellationToken, Task<CallToolResult>>)PreferencesAsync,
                "xmat_get_preferences", "CTRL_TOOL_PREFERENCES", true),
            AgentMcpTools.Tool((Func<long, ApplicationPreferenceValues, string, CancellationToken, Task<CallToolResult>>)UpdatePreferencesAsync,
                "xmat_update_preferences", "CTRL_TOOL_UPDATE_PREFERENCES", false),
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)DiagnosticsAsync,
                "xmat_get_diagnostics", "CTRL_TOOL_DIAGNOSTICS", true),
            AgentMcpTools.Tool((Func<string, AdministrationArtifactKind, Guid, string, string, CancellationToken, Task<CallToolResult>>)ExportDiagnosticsAsync,
                "xmat_export_diagnostics", "CTRL_TOOL_EXPORT_DIAGNOSTICS", false),
            AgentMcpTools.Tool((Func<int, CancellationToken, Task<CallToolResult>>)NavigationTargetsAsync,
                "xmat_list_navigation_targets", "CTRL_TOOL_NAVIGATION", true),
            AgentMcpTools.Tool((Func<DesktopNavigationRequest, string, CancellationToken, Task<CallToolResult>>)RevealAsync,
                "xmat_reveal_desktop_target", "CTRL_TOOL_REVEAL", false),
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)LifecycleAsync,
                "xmat_get_desktop_lifecycle", "CTRL_TOOL_LIFECYCLE", true),
            AgentMcpTools.Tool((Func<long, DesktopLifecycleAction, string, CancellationToken, Task<CallToolResult>>)RequestLifecycleAsync,
                "xmat_request_desktop_lifecycle", "CTRL_TOOL_REQUEST_LIFECYCLE", false)
        ];

        private Task<CallToolResult> HostsAsync(int offset = 0, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
            wrap(() => application.HostsAsync(session, offset, expectedRevision, cancellationToken));
        private Task<CallToolResult> UpdateHostsAsync(HostPolicyRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.UpdateHostsAsync(session, request, idempotencyKey, cancellationToken))));
        private Task<CallToolResult> PreferencesAsync(int reservedPortOffset = 0, CancellationToken cancellationToken = default) =>
            wrap(() => application.PreferencesAsync(session, reservedPortOffset, cancellationToken));
        private Task<CallToolResult> UpdatePreferencesAsync(
            long expectedRevision, ApplicationPreferenceValues values, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.UpdatePreferencesAsync(session, expectedRevision, values, idempotencyKey, cancellationToken))));
        private Task<CallToolResult> DiagnosticsAsync(CancellationToken cancellationToken = default) =>
            wrap(() => application.DiagnosticsAsync(session, cancellationToken));
        private Task<CallToolResult> ExportDiagnosticsAsync(
            string expectedRevision, AdministrationArtifactKind kind, Guid folderId, string fileName,
            string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.ExportDiagnosticsAsync(session, expectedRevision, kind, folderId, fileName, idempotencyKey, cancellationToken))));
        private Task<CallToolResult> NavigationTargetsAsync(int offset = 0, CancellationToken cancellationToken = default) =>
            wrap(() => application.NavigationTargetsAsync(session, offset, cancellationToken));
        private Task<CallToolResult> RevealAsync(
            DesktopNavigationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.RevealAsync(session, request, idempotencyKey, cancellationToken))));
        private Task<CallToolResult> LifecycleAsync(CancellationToken cancellationToken = default) =>
            wrap(() => application.LifecycleAsync(session, cancellationToken));
        private Task<CallToolResult> RequestLifecycleAsync(
            long expectedRevision, DesktopLifecycleAction action, string idempotencyKey, CancellationToken cancellationToken = default) =>
            wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await application.RequestLifecycleAsync(session, expectedRevision, action, idempotencyKey, cancellationToken))));
    }
}
