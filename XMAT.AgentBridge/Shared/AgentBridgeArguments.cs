// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;

namespace XMAT.AgentConnection
{
    internal sealed record AgentBridgeArguments(
        Guid? InstanceId, int ProcessId, long StartedUtcTicks, AgentLaunchMode Launch)
    {
        internal static AgentBridgeArguments Parse(string[] args)
        {
            if (args.Length == 6 &&
                args[0] == "--instance" && Guid.TryParseExact(args[1], "D", out var instance) &&
                args[2] == "--pid" && int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                args[4] == "--started" && long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var started) &&
                instance != Guid.Empty && pid > 0 && started > 0)
                return new(instance, pid, started, AgentLaunchMode.ConnectOnly);
            if (args.Length == 1 && args[0] == "--discover")
                return new(null, 0, 0, AgentLaunchMode.ConnectOnly);
            if (args.Length == 3 && args[0] == "--discover" && args[1] == "--launch")
            {
                if (args[2] == "headless")
                    return new(null, 0, 0, AgentLaunchMode.Headless);
                if (args[2] == "desktop")
                    return new(null, 0, 0, AgentLaunchMode.Desktop);
            }
            return null;
        }
    }
}
