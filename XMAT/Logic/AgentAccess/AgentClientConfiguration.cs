// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using XMAT.AgentConnection;

namespace XMAT.AgentAccess
{
    internal enum AgentClientKind { Copilot, Claude, Codex, Gemini }
    internal sealed record AgentClientSetup(string Command, string Configuration, string ConfigurationFile, string ManagementCommand);

    internal static class AgentClientConfiguration
    {
        internal static AgentClientSetup Create(
            string installationDirectory, AgentClientKind client, AgentLaunchMode launch = AgentLaunchMode.Headless,
            AgentInstanceRecord instance = null)
        {
            if (!Enum.IsDefined(client) || !Enum.IsDefined(launch))
                throw new ArgumentOutOfRangeException(nameof(client));
            var command = Path.Combine(Path.GetFullPath(installationDirectory), "AgentBridge", "XMAT.AgentBridge.exe");
            var args = instance != null
                ? new[]
                {
                    "--instance", instance.InstanceId.ToString("D"),
                    "--pid", instance.ProcessId.ToString(CultureInfo.InvariantCulture),
                    "--started", instance.StartedUtcTicks.ToString(CultureInfo.InvariantCulture)
                }
                : launch == AgentLaunchMode.ConnectOnly ? ["--discover"] :
                    new[] { "--discover", "--launch", launch == AgentLaunchMode.Headless ? "headless" : "desktop" };
            if (client == AgentClientKind.Codex)
            {
                var toml = "[mcp_servers.xmat]\r\ncommand = " + JsonSerializer.Serialize(command) +
                    "\r\nargs = " + JsonSerializer.Serialize(args) + "\r\n";
                return new(null, toml, @"%USERPROFILE%\.codex\config.toml", "/mcp");
            }
            object server = client switch
            {
                AgentClientKind.Copilot => new { type = "stdio", command, args, tools = new[] { "*" } },
                AgentClientKind.Claude => new { type = "stdio", command, args },
                _ => new { command, args }
            };
            var json = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object> { ["xmat"] = server }
            }, new JsonSerializerOptions { WriteIndented = true });
            var suffix = string.Join(" ", args.Select(QuotePowerShell));
            var registration = client switch
            {
                AgentClientKind.Copilot => $"copilot mcp add xmat -- {QuotePowerShell(command)} {suffix}",
                AgentClientKind.Claude => $"claude mcp add --transport stdio --scope user xmat -- {QuotePowerShell(command)} {suffix}",
                _ => $"gemini mcp add --scope user --transport stdio xmat {QuotePowerShell(command)} -- {suffix}"
            };
            var location = client switch
            {
                AgentClientKind.Copilot => @"$COPILOT_HOME\mcp-config.json (%USERPROFILE%\.copilot\mcp-config.json)",
                AgentClientKind.Claude => @"%USERPROFILE%\.claude.json",
                _ => @"%USERPROFILE%\.gemini\settings.json"
            };
            return new(registration, json, location, client == AgentClientKind.Copilot ? "/mcp list" : "/mcp");
        }

        private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
