// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Hosting
{
    internal sealed record AgentHostArguments(Guid InstanceId, ApplicationHostMode Mode)
    {
        internal static bool IsHostCommand(string[] args) => args is { Length: > 0 } && args[0] == "--agent-host";
        internal static bool TryParse(string[] args, out AgentHostArguments options)
        {
            options = null;
            if (args is not { Length: 5 } || args[0] != "--agent-host" || args[1] != "--instance" ||
                !Guid.TryParseExact(args[2], "D", out var id) || id == Guid.Empty || args[3] != "--mode")
                return false;
            var mode = args[4] switch
            {
                "headless" => ApplicationHostMode.Headless,
                "desktop" => ApplicationHostMode.Desktop,
                _ => (ApplicationHostMode)(-1)
            };
            if (!Enum.IsDefined(mode))
                return false;
            options = new(id, mode);
            return true;
        }
    }
}
