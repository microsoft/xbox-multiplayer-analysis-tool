// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.Operations.Capabilities;

namespace XMAT.Cli
{
    // What the command line knows about its commands comes from the capability registry, so help, docs/cli.md and the
    // accepted commands cannot drift apart. Only Supported capabilities have command lines: any other command, option,
    // value or doctor target, including one planned for a later release, is a usage error (exit code 2).
    internal static class CliCatalog
    {
        // The command of a registry command line: "analyze" for "analyze INPUT --out DIR".
        internal static string CommandOf(string cliRoute) =>
            string.IsNullOrWhiteSpace(cliRoute) ? null : cliRoute.Trim().Split(' ', 2)[0];

        internal static bool IsImplemented(string command) =>
            command != null && CliCommands.Implemented.Contains(command, StringComparer.Ordinal);

        // The Supported capabilities that a command provides, by id.
        internal static IReadOnlyList<Capability> CapabilitiesOf(string command) =>
            CapabilityRegistry.All
                .Where(capability => capability.Status == CapabilityStatus.Supported && CommandOf(capability.CliRoute) == command)
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToList();

        // Every command that a Supported capability names, alphabetically.
        internal static IReadOnlyList<string> Commands() =>
            CapabilityRegistry.All
                .Where(capability => capability.Status == CapabilityStatus.Supported)
                .Select(capability => CommandOf(capability.CliRoute))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(command => command, StringComparer.Ordinal)
                .ToList();

        // xmat doctor --for TARGET checks what a command needs from its installation; only analyze needs anything.
        internal static bool IsDoctorTarget(string target) => target == CliCommands.Analyze;
    }
}
