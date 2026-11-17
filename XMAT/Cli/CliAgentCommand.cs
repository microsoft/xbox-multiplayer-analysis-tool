// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using XMAT.AgentAccess;
using XMAT.AgentConnection;
using XMAT.SharedInterfaces;

namespace XMAT.Cli
{
    internal static class CliAgentCommand
    {
        internal const string Disclosure =
            "Global Agent Access applies to every eligible local MCP client, including existing connections. " +
            "XMAT -> local MCP client -> configured model/provider, if cloud-backed. " +
            "Returned queries, records, details, reports, scripts, diagnostics, downloaded exports and identifying metadata " +
            "may enter model context or client/provider histories. Complete data may include credentials, tokens, cookies, " +
            "user/device identifiers, IPs/hosts, URLs/query strings, headers, bodies, packet/WebSocket payloads, scripts and logs. " +
            "XMAT cannot control onward forwarding or provider retention. Best-effort redaction is not a confidentiality " +
            "guarantee or anonymity. Full control includes trusted in-process C#; scripts and other agent filesystem/tools " +
            "can bypass output filtering. Client confirmations are advisory and may be accepted automatically, not proof " +
            "of a human click. Optional backups are local, unredacted native captures/file preimages, latest 10 operation " +
            "bundles up to 2 GiB. Backup failure blocks protected destruction. Backups exclude memory-only WebSocket " +
            "messages, unsaved scripts/SQL and device/network changes; they are not complete undo. Local raw saved files " +
            "and backups may be readable through the agent's other filesystem tools. Obtain the user's consent before " +
            "enabling or broadening access. Launch/connect alone never changes these settings.";

        internal static async Task<CliReport> RunAsync(
            CliAgentRequest request, string runId, CancellationToken cancellationToken,
            string installation = null, AgentAccessSettingsStore settings = null, AgentInstanceDirectory instances = null,
            AgentHostLauncher launcher = null)
        {
            var report = new CliReport(CliCommands.Agent, runId);
            var root = Path.GetFullPath(installation ?? AppContext.BaseDirectory);
            settings ??= new();
            instances ??= new(Path.Combine(root, "XboxMultiplayerAnalysisTool.exe"));
            try
            {
                if (request.Action == "setup")
                    await SetupAsync(request, report, settings, root, cancellationToken);
                else if (request.Action == "config")
                {
                    var instance = request.InstanceId.HasValue
                        ? instances.Read().SingleOrDefault(item => item.InstanceId == request.InstanceId) : null;
                    if (request.InstanceId.HasValue && instance == null)
                        throw new AgentLaunchException("agent.NotRunning");
                    var setup = AgentClientConfiguration.Create(root, request.Client, request.Mode, instance);
                    report.Agent = AgentJson.Encode(new
                    {
                        action = "config",
                        setup.Command,
                        setup.Configuration,
                        setup.ConfigurationFile,
                        setup.ManagementCommand,
                        guide = Path.Combine(root, "AGENTS.md"),
                        policyChanged = false
                    });
                    report.HumanLines.Add(setup.Command ?? "Add the returned TOML to the client configuration.");
                    report.HumanLines.Add("Configuration is generated only; no external client files were changed.");
                }
                else if (request.Action == "status")
                {
                    report.Agent = AgentJson.Encode(new
                    {
                        action = "status",
                        policy = settings.Read(),
                        instances = instances.Read().Where(item => !request.InstanceId.HasValue || item.InstanceId == request.InstanceId)
                            .Select(Presence).ToArray(),
                        guide = Path.Combine(root, "AGENTS.md"),
                        supportedModes = new[] { "headless", "desktop" }
                    });
                }
                else if (request.Action == "start")
                {
                    var store = settings;
                    launcher ??= new(root, instances, () => store.Read().Settings.Enabled,
                        (instance, token) => AgentHostConnection.HandshakeAsync(root, instance, token));
                    var started = await launcher.StartAsync(request.Mode, cancellationToken);
                    report.Agent = AgentJson.Encode(new
                    {
                        action = "start",
                        started.Instance.InstanceId,
                        started.Instance.ProcessId,
                        started.Instance.Mode,
                        started.Instance.State,
                        started.Launched,
                        handshakeConfirmed = true,
                        persistent = true,
                        policyChanged = false
                    });
                }
                else
                    await StopAsync(request, report, root, instances, cancellationToken);
            }
            catch (AgentLaunchException exception)
            {
                report.Agent = AgentJson.Encode(new
                {
                    action = request.Action,
                    instance = exception.Instance == null ? null : Presence(exception.Instance),
                    stopped = false,
                    policyChanged = false
                });
                report.Fail(CliExitCodes.Prerequisite, exception.Code,
                    exception.Code == "agent.ExternalLaunchRequired"
                        ? "This job/sandbox does not permit an independent host. Run xmat agent start from an external Windows terminal; do not bypass containment."
                        : "Agent access is unavailable; inspect agent status and the installed AGENTS.md. Disabled access requires explicit local setup.");
            }
            catch (AgentPolicyException exception)
            {
                report.Fail(exception.Failure == AgentPolicyFailure.PolicyChanged ? CliExitCodes.Conflict : CliExitCodes.Failed,
                    "agent.policy." + exception.Failure, "The global policy could not be saved. Read the current policy before retrying.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                report.Fail(CliExitCodes.Cancelled, "agent.cancelled",
                    "The request was cancelled. A launched host or already committed action may still exist; inspect agent status before retrying.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                Win32Exception or McpException or OperationCanceledException)
            {
                report.Fail(CliExitCodes.Prerequisite, "agent.unavailable",
                    "The local host or installation is unavailable. No process was forcibly stopped; inspect agent status before retrying.");
            }
            return report;
        }

        private static async Task SetupAsync(
            CliAgentRequest request, CliReport report, AgentAccessSettingsStore store, string root, CancellationToken cancellationToken)
        {
            await using var policy = new AgentAccessPolicyService(store);
            var current = policy.Current;
            var desired = current.Settings with
            {
                Enabled = request.Enabled ?? true,
                DataSharing = request.DataSharing ?? current.Settings.DataSharing,
                Actions = request.Actions ?? current.Settings.Actions,
                AutomaticBackups = request.Backups ?? current.Settings.AutomaticBackups
            };
            report.HumanLines.Add(Disclosure);
            if (!request.Accept)
            {
                report.Agent = AgentJson.Encode(new
                {
                    action = "setup",
                    confirmationRequired = true,
                    current = current.Settings,
                    proposed = desired,
                    disclosure = Disclosure,
                    policyChanged = false,
                    guide = Path.Combine(root, "AGENTS.md"),
                    instruction = "Review these global choices with the user, then repeat the exact setup options with --accept."
                });
                report.Fail(CliExitCodes.Prerequisite, "agent.confirmation-required", "No policy was changed. Explicit --accept is required.");
                return;
            }
            var result = await policy.ExecuteAsync(desired, new(policy.SettingsDirectory), cancellationToken);
            if (!result.Succeeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new AgentPolicyException(result.Value.SaveFailure ?? AgentPolicyFailure.SaveFailed);
            }
            var saved = result.Value;
            report.Agent = AgentJson.Encode(new
            {
                action = "setup",
                policy = saved.Settings,
                policyChanged = true,
                clientAcknowledged = true,
                humanAttestation = false,
                guide = Path.Combine(root, "AGENTS.md")
            });
        }

        private static async Task StopAsync(
            CliAgentRequest request, CliReport report, string root, AgentInstanceDirectory instances, CancellationToken cancellationToken)
        {
            var selection = instances.Select(request.InstanceId);
            if (selection.Failure != AgentDiscoveryFailure.None)
                throw new AgentLaunchException("agent." + selection.Failure);
            var instance = selection.Instance;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await AgentHostConnection.ConnectAsync(root, instance, timeout.Token,
                request.Accept ? (prompt, _) =>
                {
                    report.HumanLines.Add(prompt.Message);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { ["accept"] = AgentJson.Encode(true) }
                    });
                }
            : null);
            var inspected = await client.CallToolAsync("xmat_get_host_lifecycle", cancellationToken: timeout.Token);
            var state = Payload(inspected).Deserialize<ApplicationShutdownSnapshot>(AgentJson.Options);
            if (!request.Accept || state.HasCapturedData && !request.DiscardData && request.SaveCapture == null ||
                state.UnsavedSqlDocuments.Count > 0 && !request.DiscardSql || state.ScriptDrafts.Count > 0 && !request.DiscardScripts)
            {
                report.Agent = AgentJson.Encode(new
                {
                    action = "stop",
                    instance.InstanceId,
                    confirmationRequired = !request.Accept,
                    shutdown = state,
                    stopped = false,
                    disclosure = Disclosure,
                    instruction = "Use explicit save/discard options and --accept. Save individual SQL/script drafts through the desktop or MCP before retrying if they must be retained."
                });
                report.Fail(CliExitCodes.Prerequisite, "agent.shutdown-decisions-required",
                    "The host remains alive. Review the shutdown effects and provide explicit choices.");
                return;
            }
            var choices = new ApplicationShutdownChoices
            {
                DiscardCapturedData = request.DiscardData,
                SaveCapturePath = request.SaveCapture,
                OverwriteCapture = request.Overwrite,
                DiscardScriptDrafts = request.DiscardScripts,
                DisableXboxProxies = request.DisableXboxProxies,
                Documents = state.UnsavedSqlDocuments.Select(document =>
                    new ShutdownDocumentChoice(document.Id, document.Revision, ShutdownDocumentAction.Discard)).ToArray()
            };
            var result = Payload(await client.CallToolAsync("xmat_stop_host", new Dictionary<string, object>
            {
                ["expectedRevision"] = state.Revision,
                ["choices"] = AgentJson.Encode(choices),
                ["idempotencyKey"] = "cli-stop-" + Guid.NewGuid().ToString("N")
            }, cancellationToken: timeout.Token));
            var operationId = result.GetProperty("id").GetGuid();
            if (result.TryGetProperty("confirmation", out var confirmation))
                Payload(await client.CallToolAsync("xmat_confirm_operation", new Dictionary<string, object>
                {
                    ["operationId"] = operationId,
                    ["confirmationToken"] = confirmation.GetProperty("confirmationToken").GetString(),
                    ["accept"] = true
                }, cancellationToken: timeout.Token));
            JsonElement? shutdownReceipt = null;
            while (IsAlive(instance))
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    var operation = Payload(await client.CallToolAsync("xmat_get_operation",
                        new Dictionary<string, object> { ["operationId"] = operationId }, cancellationToken: timeout.Token));
                    var status = operation.GetProperty("state").GetString();
                    if (status is not ("Queued" or "Running" or "CancellationRequested" or "Succeeded"))
                    {
                        report.Agent = AgentJson.Encode(new { action = "stop", instance.InstanceId, stopped = false, operation });
                        report.Fail(CliExitCodes.Conflict, "agent.shutdown-blocked",
                            "Shutdown was not completed. The host remains alive; inspect recovery and unsaved state.");
                        return;
                    }
                    if (status == "Succeeded" && !shutdownReceipt.HasValue)
                        shutdownReceipt = Payload(await client.CallToolAsync("xmat_get_result",
                            new Dictionary<string, object> { ["operationId"] = operationId }, cancellationToken: timeout.Token));
                }
                catch (Exception exception) when (exception is McpException or IOException)
                {
                    // Closing the transport is not proof of process exit.
                }
                await Task.Delay(100, timeout.Token);
            }
            report.Agent = AgentJson.Encode(new
            {
                action = "stop",
                instance.InstanceId,
                operationId,
                requested = true,
                stopped = true,
                processExitConfirmed = true,
                shutdownReceipt
            });
        }

        private static JsonElement Payload(CallToolResult result)
        {
            if (result.IsError == true || !result.StructuredContent.HasValue)
            {
                var code = result.StructuredContent.HasValue && result.StructuredContent.Value.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String ? error.GetString() : "RequestFailed";
                throw new AgentLaunchException("agent." + code);
            }
            return result.StructuredContent.Value;
        }

        private static bool IsAlive(AgentInstanceRecord instance)
        {
            try
            {
                using var process = Process.GetProcessById(instance.ProcessId);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == instance.StartedUtcTicks;
            }
            catch (ArgumentException) { return false; }
        }

        private static object Presence(AgentInstanceRecord instance) => new
        {
            instance.InstanceId,
            instance.ProcessId,
            instance.StartedUtcTicks,
            instance.Mode,
            instance.State,
            instance.Enabled,
            instance.Elevated,
            protocol = "mcp-stdio-local-pipe/1"
        };

        internal static IReadOnlyList<CliCheck> CheckInstallation(string root) =>
        [
            CheckFile(root, "agent.desktop-host", "XboxMultiplayerAnalysisTool.exe"),
            CheckFile(root, "agent.bridge", Path.Combine("AgentBridge", "XMAT.AgentBridge.exe")),
            CheckFile(root, "agent.instructions", "AGENTS.md"),
            new("runtime.64-bit", Environment.Is64BitProcess, Environment.Is64BitProcess ? null : "runtime.not-64-bit")
        ];

        private static CliCheck CheckFile(string root, string id, string relative) =>
            new(id, File.Exists(Path.Combine(root, relative)), "agent.installation.missing");
    }
}
