// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.Operations.Capabilities;

namespace XMAT.Cli
{
    internal sealed record CliCommandHelp(
        string Name,
        string Status,
        IReadOnlyList<string> Usage,
        IReadOnlyList<string> Capabilities)
    {
        internal const string SupportedStatus = "supported";
    }

    // xmat help [COMMAND] and --help, generated from the capability registry. The text goes to standard error and the
    // same information to the JSON document on standard output.
    internal static class CliHelp
    {
        internal static CliReport Create(string runId, string topic)
        {
            var report = new CliReport(CliCommands.Help, runId);
            report.SetExitCode(CliExitCodes.Success);
            if (topic == null)
            {
                report.Commands = CliCatalog.Commands().Select(Describe).ToList();
                report.HumanLines.AddRange(Overview(report.Commands));
            }
            else
            {
                CliCommandHelp command = Describe(topic);
                report.Commands = new[] { command };
                report.HumanLines.AddRange(Details(command));
            }

            return report;
        }

        internal static CliCommandHelp Describe(string command)
        {
            IReadOnlyList<Capability> capabilities = CliCatalog.CapabilitiesOf(command);
            return new CliCommandHelp(
                command,
                CliCommandHelp.SupportedStatus,
                capabilities.Select(capability => capability.CliRoute).Distinct(StringComparer.Ordinal).ToList(),
                capabilities.Select(capability => capability.Id).ToList());
        }

        private static IEnumerable<string> Overview(IReadOnlyList<CliCommandHelp> commands)
        {
            yield return $"xmat {CliReport.ToolVersion}: Xbox Multiplayer Analysis Tool without the GUI.";
            yield return string.Empty;
            yield return "Usage:";
            foreach (string usage in commands.SelectMany(command => command.Usage))
            {
                yield return "  xmat " + usage;
            }

            yield return "  xmat help [COMMAND]";
            yield return "  xmat --version";
            yield return string.Empty;
            yield return "Standard output carries one JSON document (schema xmat-cli/1); diagnostics go to standard error.";
            yield return "Exit codes: 0 success, 2 usage error, 3 invalid input, 4 missing prerequisite, 5 conflict, 6 failed,";
            yield return "7 cleanup failed, 10 findings found (--fail-on findings), 70 internal error, 130 cancelled.";
        }

        private static IEnumerable<string> Details(CliCommandHelp command)
        {
            string titles = string.Join("; ", CliCatalog.CapabilitiesOf(command.Name).Select(item => item.Title));
            yield return $"xmat {command.Name}: {titles}.";
            yield return string.Empty;
            yield return "Usage:";
            foreach (string usage in command.Usage)
            {
                yield return "  xmat " + usage;
            }
        }
    }
}
