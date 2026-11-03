// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentMcpTools(
        AgentRuntime runtime,
        AgentSession session,
        AgentCaptureApplication application,
        AgentQueryApplication queries,
        AgentDataApplication data,
        AgentScriptApplication scripts,
        AgentDeviceApplication devices,
        AgentProxyApplication proxy,
        AgentCaptureFileApplication captureFiles,
        AgentClearApplication clear,
        AgentAdministrationApplication administration,
        Func<string> clientName)
    {
        internal McpServerPrimitiveCollection<McpServerTool> Create()
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>
            {
                Tool((Func<Task<CallToolResult>>)SessionAsync, "xmat_get_session", "AGENT_TOOL_SESSION", true),
                Tool((Func<string, CancellationToken, Task<CallToolResult>>)ListAsync,
                    "xmat_list_captures", "AGENT_TOOL_LIST", true),
                Tool((Func<Guid, long, bool, string, CancellationToken, Task<CallToolResult>>)SetRunningAsync,
                    "xmat_set_capture_running", "AGENT_TOOL_CAPTURE", false),
                Tool((Func<Guid, bool, CancellationToken, Task<CallToolResult>>)AvailabilityAsync,
                    "xmat_get_capture_availability", "AGENT_TOOL_CAPTURE_AVAILABILITY", true),
                Tool((Func<Guid, long, string, CancellationToken, Task<CallToolResult>>)AcknowledgeRecoveryAsync,
                    "xmat_acknowledge_trace_recovery", "AGENT_TOOL_TRACE_ACK", false),
                Tool((Func<Guid, Task<CallToolResult>>)OperationAsync, "xmat_get_operation", "AGENT_TOOL_OPERATION", true),
                Tool((Func<Guid, Task<CallToolResult>>)ResultAsync, "xmat_get_result", "AGENT_TOOL_RESULT", true),
                Tool((Func<Guid, Task<CallToolResult>>)CancelAsync, "xmat_cancel_operation", "AGENT_TOOL_CANCEL", false)
            };
            tools.Add(Tool((Func<Guid, long, string, CancellationToken, Task<CallToolResult>>)ClearAsync,
                "xmat_clear_capture", "ETL_TOOL_CLEAR", false));
            foreach (var tool in new AgentQueryMcpTools(queries, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentDataMcpTools(data, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentScriptMcpTools(scripts, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentDeviceMcpTools(devices, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentProxyMcpTools(proxy, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentCaptureFileMcpTools(captureFiles, session, runtime, Wrap).Create())
            {
                tools.Add(tool);
            }
            foreach (var tool in new AgentAdministrationMcpTools(administration, session, runtime, Wrap).Create())
                tools.Add(tool);
            return tools;
        }

        internal static McpServerTool Tool(Delegate method, string name, string description, bool readOnly) =>
            McpServerTool.Create(method, new McpServerToolCreateOptions
            {
                Name = name,
                Description = Localization.GetLocalizedString(description),
                ReadOnly = readOnly,
                Destructive = !readOnly,
                Idempotent = readOnly || name is "xmat_set_capture_running" or "xmat_update_script" or "xmat_set_script_enabled" or
                    "xmat_acknowledge_trace_recovery" or "xmat_add_device" or "xmat_remove_device" or "xmat_set_pending_proxy_port" or
                    "xmat_set_device_proxy" or "xmat_apply_proxy_port" or "xmat_save_query_result" or "xmat_save_capture" or
                    "xmat_save_script" or "xmat_load_script" or "xmat_load_capture" or "xmat_import_saz" or "xmat_import_etl" or "xmat_clear_capture" or
                    "xmat_update_host_policy" or "xmat_update_preferences" or "xmat_export_diagnostics" or
                    "xmat_reveal_desktop_target" or "xmat_request_desktop_lifecycle" or "xmat_export_analysis_report",
                OpenWorld = !readOnly,
                SerializerOptions = AgentJson.Options
            });

        private Task<CallToolResult> SessionAsync() => Wrap(() =>
        {
            runtime.SetDeclaredClientName(session, clientName());
            return Task.FromResult(AgentJson.Encode(new
            {
                enabled = runtime.IsEnabled,
                session = runtime.GetSession(session),
                supported = new[] { "capture-metadata", "web-proxy-start-stop", "network-trace-start-stop", "capture-prerequisites", "operation-status", "source-schema",
                    "read-only-sql", "query-csv-json-download", "source-summary-search", "structured-analysis",
                    "script-source", "compile-only-validation", "approved-script-activation",
                    "device-creation-removal", "device-configuration-inspection", "pending-proxy-port",
                    "approved-device-proxy", "approved-proxy-port-application", "approved-query-file-export",
                    "approved-source-capture-file-export", "approved-script-source-file-export",
                    "approved-script-input-files", "approved-native-capture-loading", "approved-saz-import", "approved-etl-import",
                    "host-policy", "typed-preferences", "diagnostics-public-certificate", "explicit-desktop-navigation",
                    "approved-normal-close", "operator-elevated-restart-request", "approved-capture-clear", "read-only-view-close",
                    "snapshot-bound-raw-records-findings", "session-websocket-inspection", "completed-report-manifests-export",
                    "snapshot-evidence-navigation" }
            }));
        });

        private Task<CallToolResult> ListAsync(string cursor = null, CancellationToken cancellationToken = default) =>
            Wrap(() => application.ListCapturesAsync(session, cursor, cancellationToken));

        private Task<CallToolResult> SetRunningAsync(
            Guid captureId, long expectedRevision, bool running, string idempotencyKey,
            CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            var id = await application.SetRunningAsync(
                session, captureId, expectedRevision, running, idempotencyKey, cancellationToken);
            return AgentJson.Encode(runtime.GetOperation(session, id));
        });

        private Task<CallToolResult> ClearAsync(
            Guid captureId, long expectedRevision, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Wrap(async () => AgentJson.Encode(runtime.GetOperation(session,
                await clear.ClearAsync(session, captureId, expectedRevision, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> AvailabilityAsync(
            Guid captureId, bool running, CancellationToken cancellationToken = default) =>
            Wrap(() => application.AvailabilityAsync(session, captureId, running, cancellationToken));

        private Task<CallToolResult> AcknowledgeRecoveryAsync(
            Guid captureId, long expectedRevision, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Wrap(async () => AgentJson.Encode(runtime.GetOperation(session, await application.AcknowledgeRecoveryAsync(
                session, captureId, expectedRevision, idempotencyKey, cancellationToken))));

        private Task<CallToolResult> OperationAsync(Guid operationId) =>
            Wrap(() => Task.FromResult(AgentJson.Encode(runtime.GetOperation(session, operationId))));

        private Task<CallToolResult> ResultAsync(Guid operationId) =>
            Wrap(() => Task.FromResult(runtime.GetResult(session, operationId)));

        private Task<CallToolResult> CancelAsync(Guid operationId) => Wrap(async () =>
            AgentJson.Encode(new { cancellationRequested = await runtime.CancelAsync(session, operationId) }));

        private async Task<CallToolResult> Wrap(Func<Task<JsonElement>> action)
        {
            try
            {
                runtime.SetDeclaredClientName(session, clientName());
                var content = await action();
                var result = new CallToolResult
                {
                    StructuredContent = content,
                    Content = [new TextContentBlock { Text = "XMAT_RESULT" }]
                };
                return Bound(result);
            }
            catch (AgentAccessException exception)
            {
                return Bound(exception.ProtectedResult.HasValue ? new CallToolResult
                {
                    IsError = true,
                    StructuredContent = exception.ProtectedResult.Value.Clone(),
                    Content = [new TextContentBlock { Text = exception.Error.ToString() }]
                } : Error(exception.Error));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                return Error(AgentAccessError.ExecutionFailed);
            }
        }

        private CallToolResult Bound(CallToolResult result)
        {
            try
            {
                return JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length >
                    runtime.GetSession(session).Limits.ResponseBytes ? Error(AgentAccessError.LimitExceeded) : result;
            }
            catch (AgentAccessException)
            {
                return Error(AgentAccessError.SessionClosed);
            }
        }

        private static CallToolResult Error(AgentAccessError error) => new()
        {
            IsError = true,
            StructuredContent = AgentJson.Encode(new { error }),
            Content = [new TextContentBlock { Text = error.ToString() }]
        };
    }
}
