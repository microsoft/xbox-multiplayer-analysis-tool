// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.Operations.Capabilities
{
    internal enum CapabilityStatus
    {
        // Available from the command line through a shared operation.
        Supported,

        // Not headless yet; Slice and Owner say where it lands.
        Planned,

        // Presentation only (layout, themes, navigation inside a view); there is nothing to run headless.
        GuiOnly,
    }

    // A user-facing capability: how the GUI reaches it, the operation that implements it and its command lines.
    internal sealed record Capability(
        string Id,
        string Title,
        CapabilityStatus Status,
        string Slice,
        string Owner,
        string Reason,
        string Operation,
        string CliRoute,
        IReadOnlyList<string> GuiEntryPoints)
    {
        public IReadOnlyList<string> GuiEntryPoints { get; init; } = GuiEntryPoints ?? Array.Empty<string>();

        // Command lines of other commands that provide the same capability, such as export's process selectors.
        public IReadOnlyList<string> AdditionalCliRoutes { get; init; } = Array.Empty<string>();

        // Every command line: the primary one, then the additional ones.
        internal IEnumerable<string> CliRoutes =>
            CliRoute == null ? AdditionalCliRoutes : AdditionalCliRoutes.Prepend(CliRoute);

        internal static Capability Supported(
            string id, string title, string slice, string cli, string operation = null, string[] gui = null) =>
            new(id, title, CapabilityStatus.Supported, slice, null, null, operation, cli, gui);

        // Planned capabilities have no command line until they are Supported.
        internal static Capability Planned(string id, string title, string slice, string owner, string[] gui = null) =>
            new(id, title, CapabilityStatus.Planned, slice, owner, null, null, null, gui);

        internal static Capability GuiOnly(string id, string title, string reason, string[] gui = null) =>
            new(id, title, CapabilityStatus.GuiOnly, null, null, reason, null, null, gui);
    }
}
