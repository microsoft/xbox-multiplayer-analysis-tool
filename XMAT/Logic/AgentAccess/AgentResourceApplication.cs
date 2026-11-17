// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentResourceApplication(AgentRuntime runtime)
    {
        private static readonly AgentPermission Control = new(AgentCapability.Lifecycle, AgentScope.Application);

        internal IEnumerable<McpServerTool> Create(
            AgentSession session, Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap) =>
        [
            AgentMcpTools.Tool((Func<int, Task<CallToolResult>>)(offset => wrap(() =>
            {
                runtime.Demand(session, new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application));
                if (offset < 0)
                    throw new AgentAccessException(AgentAccessError.InvalidRequest);
                var all = runtime.GetOwnedResources().OrderBy(item => item.Resource.Id).ToArray();
                var limits = runtime.GetResponseLimits(session);
                var resources = all.Skip(offset).Take(Math.Min(limits.ResponseRows, Math.Max(1, limits.ResponseBytes / 512))).ToArray();
                var next = offset + resources.Length;
                return Task.FromResult(AgentJson.Encode(new
                {
                    resources, nextOffset = next < all.Length ? (int?)next : null, total = all.Length,
                    privateResultsTransferable = false
                }));
            })), "xmat_list_resources", "AGENT_TOOL_RESOURCES", true),
            ChangeTool(session, wrap, false),
            ChangeTool(session, wrap, true)
        ];

        private McpServerTool ChangeTool(
            AgentSession session, Func<Func<Task<JsonElement>>, Task<CallToolResult>> wrap, bool cleanup) =>
            AgentMcpTools.Tool((Func<AgentResourceKey, Guid, string, string, CancellationToken, Task<CallToolResult>>)
                ((resource, expectedOwnerSessionId, expectedRevision, idempotencyKey, cancellationToken) => wrap(async () =>
                    AgentJson.Encode(runtime.GetOperation(session, await RequestAsync(session, resource,
                        expectedOwnerSessionId, expectedRevision, cleanup, idempotencyKey, cancellationToken))))),
                cleanup ? "xmat_retry_resource_cleanup" : "xmat_take_over_resource",
                cleanup ? "AGENT_TOOL_RESOURCE_CLEANUP" : "AGENT_TOOL_RESOURCE_TAKEOVER", false);

        internal Task<Guid> RequestAsync(
            AgentSession session, AgentResourceKey resource, Guid owner, string revision,
            bool cleanup, string idempotencyKey, CancellationToken cancellationToken)
        {
            runtime.Demand(session, Control);
            if (!Enum.IsDefined(resource.Kind) || resource.Id == Guid.Empty || owner == Guid.Empty ||
                string.IsNullOrEmpty(revision) || revision.Length > 256)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var action = cleanup ? "resource.retryCleanup" : "resource.takeOver";
            var hash = AgentOperationDescriptor.Hash(new { resource, owner, revision });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return Task.FromResult(replay.Value);
            var current = runtime.GetOwnedResources().SingleOrDefault(item =>
                item.Resource == resource && item.SessionId == owner && item.Revision == revision);
            if (current == null || current.State == AgentResourceState.Cleaning)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            if (cleanup ? current.State != AgentResourceState.RecoveryRequired :
                resource.Kind is not (AgentResourceKind.Capture or AgentResourceKind.Script))
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var descriptor = new AgentOperationDescriptor(action, [Control],
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString(cleanup ? "AGENT_RESOURCE_CLEANUP_WARNING" : "AGENT_RESOURCE_TAKEOVER_WARNING",
                    resource.Kind, resource.Id, owner, revision),
                confirmationRequired: !cleanup || owner != session.Id &&
                    resource.Kind is AgentResourceKind.Capture or AgentResourceKind.Script);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(runtime.Submit(session, descriptor, idempotencyKey, async _ =>
            {
                runtime.Demand(session, Control);
                var changed = cleanup ? await runtime.RetryResourceCleanupAsync(resource, owner, revision) :
                    runtime.TakeOver(resource, owner, revision);
                if (!changed)
                    throw new AgentAccessException(cleanup ? AgentAccessError.RecoveryRequired : AgentAccessError.SourceChanged);
                return AgentJson.Encode(new
                {
                    resource,
                    changed = true,
                    cleanup,
                    privateResultsTransferred = false,
                    ownership = cleanup ? "released" : "operator",
                    stillRunningMayRequireExplicitStop = !cleanup
                });
            }, _ => Task.FromResult(runtime.MatchesResource(resource, owner, revision))));
        }
    }
}
