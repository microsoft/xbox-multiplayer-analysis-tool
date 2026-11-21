// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.AgentAccess;
using XMAT.AgentConnection;

namespace XMAT.Cli
{
    internal sealed record CliAgentRequest(
        string Action, AgentLaunchMode Mode = AgentLaunchMode.Headless, AgentClientKind Client = AgentClientKind.Copilot,
        Guid? InstanceId = null, bool Accept = false, bool? Enabled = null, AgentDataSharing? DataSharing = null,
        AgentActionAccess? Actions = null, bool? Backups = null, string SaveCapture = null,
        bool Overwrite = false, bool DiscardData = false, bool DiscardScripts = false, bool DiscardSql = false,
        bool DisableXboxProxies = false);

    internal static class CliAgentCommandLine
    {
        private static readonly IReadOnlyDictionary<string, string[]> Options = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["setup"] = ["--enable", "--data", "--actions", "--backups", "--accept"],
            ["start"] = ["--mode"],
            ["status"] = ["--instance"],
            ["config"] = ["--client", "--launch", "--instance"],
            ["stop"] = ["--instance", "--accept", "--save-capture", "--overwrite", "--discard-data",
                "--discard-scripts", "--discard-sql", "--disable-xbox-proxies"]
        };
        private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
        {
            "--accept", "--overwrite", "--discard-data", "--discard-scripts", "--discard-sql", "--disable-xbox-proxies"
        };

        internal static CliParseResult Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 1 || args.Count == 2 && CliCommandLine.IsHelpSwitch(args[1]))
                return CliParseResult.Success(new(CliCommands.Help, Topic: CliCommands.Agent));
            if (!Options.TryGetValue(args[1] ?? string.Empty, out var allowed))
                return Error("Unknown agent command. Run xmat help agent.");
            var action = args[1];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 2; index < args.Count; index++)
            {
                var name = args[index] ?? string.Empty;
                if (CliCommandLine.IsHelpSwitch(name))
                    return CliParseResult.Success(new(CliCommands.Help, Topic: CliCommands.Agent));
                var separator = name.IndexOf('=');
                var value = separator < 0 ? null : name[(separator + 1)..];
                name = separator < 0 ? name : name[..separator];
                if (!allowed.Contains(name, StringComparer.Ordinal) || values.ContainsKey(name))
                    return Error("Unknown or repeated agent option. Run xmat help agent.");
                if (Flags.Contains(name))
                {
                    if (value != null)
                        return Error("Agent acknowledgment and discard flags take no value.");
                    value = "true";
                }
                else if (value == null)
                {
                    if (++index >= args.Count || string.IsNullOrEmpty(args[index]) || args[index].StartsWith('-'))
                        return Error("An agent option needs a value. Run xmat help agent.");
                    value = args[index];
                }
                values.Add(name, value);
            }
            var mode = AgentLaunchMode.Headless;
            if (values.TryGetValue(action == "config" ? "--launch" : "--mode", out var modeText))
            {
                mode = modeText switch
                {
                    "headless" => AgentLaunchMode.Headless,
                    "desktop" => AgentLaunchMode.Desktop,
                    "none" when action == "config" => AgentLaunchMode.ConnectOnly,
                    _ => (AgentLaunchMode)(-1)
                };
                if (!Enum.IsDefined(mode))
                    return Error("Launch mode must be headless or desktop; config also accepts none.");
            }
            var client = AgentClientKind.Copilot;
            if (action == "config" && (!values.TryGetValue("--client", out var clientText) ||
                !TryClient(clientText, out client)))
                return Error("config needs --client copilot, claude, codex or gemini.");
            Guid? instance = null;
            if (values.TryGetValue("--instance", out var instanceText))
            {
                if (!Guid.TryParseExact(instanceText, "D", out var id) || id == Guid.Empty)
                    return Error("--instance needs an instance id from agent status.");
                instance = id;
            }
            if (action == "stop" && !instance.HasValue)
                return Error("stop needs --instance from agent status.");
            bool? enabled = null, backups = null;
            if (values.TryGetValue("--enable", out var enabledText))
            {
                if (!TryBoolean(enabledText, out var result))
                    return Error("--enable takes true or false.");
                enabled = result;
            }
            if (values.TryGetValue("--backups", out var backupsText))
            {
                if (!TryBoolean(backupsText, out var result))
                    return Error("--backups takes true or false.");
                backups = result;
            }
            AgentDataSharing? data = null;
            if (values.TryGetValue("--data", out var dataText))
            {
                if (dataText is not ("redacted" or "complete"))
                    return Error("--data takes redacted or complete.");
                data = dataText == "redacted" ? AgentDataSharing.Redacted : AgentDataSharing.Complete;
            }
            AgentActionAccess? actions = null;
            if (values.TryGetValue("--actions", out var actionsText))
            {
                if (actionsText is not ("inspect" or "full"))
                    return Error("--actions takes inspect or full.");
                actions = actionsText == "inspect" ? AgentActionAccess.InspectAndExport : AgentActionAccess.FullControl;
            }
            values.TryGetValue("--save-capture", out var save);
            if (values.ContainsKey("--overwrite") && save == null)
                return Error("--overwrite requires --save-capture.");
            if (save != null && values.ContainsKey("--discard-data"))
                return Error("Choose --save-capture or --discard-data, not both.");
            return CliParseResult.Success(new(CliCommands.Agent, Agent: new(action, mode, client, instance,
                values.ContainsKey("--accept"), enabled, data, actions, backups, save,
                values.ContainsKey("--overwrite"), values.ContainsKey("--discard-data"), values.ContainsKey("--discard-scripts"),
                values.ContainsKey("--discard-sql"), values.ContainsKey("--disable-xbox-proxies"))));
        }

        private static bool TryClient(string value, out AgentClientKind client)
        {
            client = value switch
            {
                "copilot" => AgentClientKind.Copilot,
                "claude" => AgentClientKind.Claude,
                "codex" => AgentClientKind.Codex,
                "gemini" => AgentClientKind.Gemini,
                _ => (AgentClientKind)(-1)
            };
            return Enum.IsDefined(client);
        }

        private static bool TryBoolean(string text, out bool value)
        {
            value = text == "true";
            return text is "true" or "false";
        }

        private static CliParseResult Error(string message) =>
            CliParseResult.UsageError(CliCommands.Agent, "cli.usage.agent", message);
    }
}
