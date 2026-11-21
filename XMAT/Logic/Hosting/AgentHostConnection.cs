// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace XMAT.AgentConnection
{
    internal static class AgentHostConnection
    {
        internal static async Task<McpClient> ConnectAsync(
            string installation, AgentInstanceRecord instance, CancellationToken cancellationToken,
            Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>> elicitation = null)
        {
            var bridge = Path.Combine(Path.GetFullPath(installation), "AgentBridge", "XMAT.AgentBridge.exe");
            if (!File.Exists(bridge))
                throw new AgentLaunchException("agent.InstallationIncomplete", instance);
            var transport = new StdioClientTransport(new()
            {
                Command = bridge,
                Arguments = ["--instance", instance.InstanceId.ToString("D"),
                    "--pid", instance.ProcessId.ToString(CultureInfo.InvariantCulture),
                    "--started", instance.StartedUtcTicks.ToString(CultureInfo.InvariantCulture)],
                StandardErrorLines = _ => { }
            });
            var options = new McpClientOptions { ClientInfo = new() { Name = "XMAT CLI", Version = "1.0" } };
            if (elicitation != null)
            {
                options.Capabilities = new() { Elicitation = new() { Form = new() } };
                options.Handlers = new() { ElicitationHandler = (request, token) => elicitation(request, token) };
            }
            return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
        }

        internal static async Task HandshakeAsync(string installation, AgentInstanceRecord instance, CancellationToken cancellationToken)
        {
            await using var client = await ConnectAsync(installation, instance, cancellationToken);
            var result = await client.CallToolAsync("xmat_get_session", cancellationToken: cancellationToken);
            if (result.IsError == true || !result.StructuredContent.HasValue)
                throw new AgentLaunchException("agent.HandshakeFailed", instance);
        }
    }
}
