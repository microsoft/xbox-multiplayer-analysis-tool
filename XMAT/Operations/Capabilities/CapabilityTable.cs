// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XMAT.Operations.Capabilities
{
    // Renders the registry as the Markdown table that docs/cli.md keeps between its xmat-capabilities markers. Only
    // Supported capabilities have a command line.
    internal static class CapabilityTable
    {
        internal const string BeginMarker = "<!-- xmat-capabilities:begin -->";
        internal const string EndMarker = "<!-- xmat-capabilities:end -->";

        internal static string RenderMarkdown(IEnumerable<Capability> capabilities)
        {
            var builder = new StringBuilder();
            builder.Append("| Capability | Command line | Headless |\n");
            builder.Append("|---|---|---|\n");
            foreach (Capability capability in capabilities.OrderBy(capability => capability.Id, StringComparer.Ordinal))
            {
                builder.Append("| ").Append(Escape(capability.Title)).Append(" (`").Append(capability.Id).Append("`)");
                builder.Append(" | ").Append(CommandLine(capability));
                builder.Append(" | ").Append(Escape(Status(capability))).Append(" |\n");
            }

            return builder.ToString();
        }

        internal static string Status(Capability capability) => capability.Status switch
        {
            CapabilityStatus.Supported => $"Supported ({capability.Slice})",
            CapabilityStatus.Planned => $"Planned ({capability.Slice}, {capability.Owner})",
            _ => "GUI only. " + capability.Reason,
        };

        private static string CommandLine(Capability capability) => capability.Status switch
        {
            CapabilityStatus.Supported => "`xmat " + Escape(capability.CliRoute) + "`",
            CapabilityStatus.Planned => "(not available yet)",
            _ => "(none)",
        };

        // A literal '|' would end the Markdown table cell.
        private static string Escape(string text) => (text ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    }
}
