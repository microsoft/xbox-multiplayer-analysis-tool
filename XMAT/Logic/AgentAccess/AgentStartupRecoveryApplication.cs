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
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentStartupRecoveryApplication(AgentRuntime runtime, ApplicationStartupRecovery recovery)
    {
        private static readonly AgentPermission Read = new(AgentCapability.SummaryRead, AgentScope.Application);
        private static readonly AgentPermission Control = new(AgentCapability.Lifecycle, AgentScope.Application);

        internal IEnumerable<McpServerTool> Create(
            AgentSession session, Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap) =>
        [
            AgentMcpTools.Tool((Func<CancellationToken, Task<CallToolResult>>)(token => wrap(async () =>
            {
                runtime.Demand(session, Read);
                return AgentJson.Encode(await recovery.InspectAsync(token));
            })), "xmat_get_startup_recovery", "AGENT_TOOL_STARTUP_RECOVERY", true),
            AgentMcpTools.Tool((Func<string, bool, bool, string, CancellationToken, Task<CallToolResult>>)
                ((expectedRevision, disableAbandonedLoopback, acknowledgeXboxNotice, idempotencyKey, token) => wrap(async () =>
                    AgentJson.Encode(runtime.GetOperation(session, await RequestAsync(session, expectedRevision,
                        disableAbandonedLoopback, acknowledgeXboxNotice, idempotencyKey, token))))),
                "xmat_resolve_startup_recovery", "AGENT_TOOL_RESOLVE_STARTUP_RECOVERY", false)
        ];

        private async Task<Guid> RequestAsync(
            AgentSession session, string expectedRevision, bool disableLoopback, bool acknowledgeXbox,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            runtime.Demand(session, Control);
            if (acknowledgeXbox && disableLoopback)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var hash = AgentOperationDescriptor.Hash(new { expectedRevision, disableLoopback, acknowledgeXbox });
            var replay = runtime.FindReplay(session, idempotencyKey, "recovery.startup", hash);
            if (replay.HasValue)
                return replay.Value;
            var snapshot = await recovery.InspectAsync(cancellationToken);
            if (snapshot.Revision != expectedRevision)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            var descriptor = new AgentOperationDescriptor("recovery.startup", [Control],
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource |
                    (acknowledgeXbox ? AgentOperationEffects.None : AgentOperationEffects.NetworkConfiguration),
                hash, Localization.GetLocalizedString("AGENT_STARTUP_RECOVERY_WARNING",
                    snapshot.PcProxyServer ?? "-", disableLoopback, acknowledgeXbox,
                    string.Join(", ", snapshot.AbandonedXboxConsoles)),
                confirmationRequired: !acknowledgeXbox);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                runtime.Demand(session, Control);
                return AgentJson.Encode(await recovery.ExecuteAsync(expectedRevision, disableLoopback, acknowledgeXbox,
                    context.CancellationToken));
            }, async token => (await recovery.InspectAsync(token)).Revision == expectedRevision);
        }
    }
}
