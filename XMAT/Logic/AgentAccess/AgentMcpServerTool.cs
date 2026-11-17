// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentMcpServerTool(
        McpServerTool inner, AgentRuntime runtime, AgentSession session) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var dataRevision = runtime.DataRevision;
            try
            {
                var result = runtime.DataPolicy.Redacts && ProtocolTool.Name == "xmat_get_data_detail_value"
                    ? new CallToolResult
                    {
                        StructuredContent = AgentJson.Encode(new
                        {
                            omitted = true,
                            reason = Localization.GetLocalizedString("AGENT_FILTERED_DETAIL_OMITTED")
                        })
                    }
                    : await base.InvokeAsync(request, cancellationToken);
                result = await ConfirmAsync(result, request, cancellationToken);
                if (dataRevision != runtime.DataRevision)
                    return Error(AgentAccessError.SourceChanged);
                if (result.StructuredContent is { } content)
                {
                    var projected = new AgentResponseProjection(runtime.DataPolicy, cancellationToken).Project(content, ProtocolTool.Name);
                    result.StructuredContent = projected;
                    result.Content = [new TextContentBlock { Text = projected.GetRawText() }];
                }
                else
                    result.Content = result.Content.OfType<TextContentBlock>().Select(block => new TextContentBlock
                    {
                        Text = runtime.DataPolicy.Redacts ? runtime.DataPolicy.Text(block.Text, cancellationToken) : block.Text
                    }).Cast<ContentBlock>().ToArray();
                return JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length >
                    runtime.GetSession(session).Limits.ResponseBytes ? Error(AgentAccessError.LimitExceeded) : result;
            }
            catch (OperationCanceledException) { throw; }
            catch (AgentAccessException exception) { return Error(exception.Error); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { return Error(AgentAccessError.ExecutionFailed); }
        }

        private async Task<CallToolResult> ConfirmAsync(
            CallToolResult result, RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            if (result.IsError == true || result.StructuredContent is not { ValueKind: JsonValueKind.Object } content ||
                !content.TryGetProperty("state", out var state) || state.GetString() != nameof(AgentOperationState.AwaitingApproval) ||
                !content.TryGetProperty("id", out var id) || !id.TryGetGuid(out var operationId))
                return result;
            var confirmation = runtime.GetConfirmation(session, operationId);
            if (confirmation == null)
                return new() { StructuredContent = AgentJson.Encode(runtime.GetOperation(session, operationId)) };
            if (ProtocolTool.Name is not "xmat_get_operation" and not "xmat_confirm_operation" &&
                request.Server.ClientCapabilities?.Elicitation?.Form != null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    var response = await request.Server.ElicitAsync(new ElicitRequestParams
                    {
                        Mode = "form",
                        Message = runtime.DataPolicy.Redacts
                            ? runtime.DataPolicy.Text(confirmation.Warning, timeout.Token) : confirmation.Warning,
                        RequestedSchema = new()
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["accept"] = new ElicitRequestParams.BooleanSchema
                                {
                                    Title = Localization.GetLocalizedString("AGENT_CONFIRM_ACCEPT"),
                                    Description = Localization.GetLocalizedString("AGENT_CONFIRM_ADVISORY"),
                                    Default = false
                                }
                            },
                            Required = ["accept"]
                        }
                    }, timeout.Token);
                    var accepted = response.IsAccepted && response.Content?.TryGetValue("accept", out var acceptedValue) == true &&
                        acceptedValue.ValueKind == JsonValueKind.True;
                    return new()
                    {
                        StructuredContent = AgentJson.Encode(runtime.Confirm(
                        session, operationId, confirmation.ConfirmationToken, accepted))
                    };
                }
                catch (OperationCanceledException)
                {
                    await runtime.CancelAsync(session, operationId);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new() { StructuredContent = AgentJson.Encode(runtime.GetOperation(session, operationId)) };
                }
                catch (McpException) { }
            }
            var pending = JsonSerializer.SerializeToNode(runtime.GetOperation(session, operationId), AgentJson.Options).AsObject();
            pending["confirmation"] = JsonSerializer.SerializeToNode(confirmation, AgentJson.Options);
            pending["continueWith"] = "xmat_confirm_operation";
            return new() { StructuredContent = AgentJson.Encode(pending) };
        }

        private static CallToolResult Error(AgentAccessError error) => new()
        {
            IsError = true,
            StructuredContent = AgentJson.Encode(new { error }),
            Content = [new TextContentBlock { Text = error.ToString() }]
        };
    }
}
