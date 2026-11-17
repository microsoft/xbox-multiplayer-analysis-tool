// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.CompilerServices;
using XMAT.Hosting;
using XMAT.WebServiceCapture;

namespace XMAT
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Dispatched before any WPF, certificate or database code runs.
            if (ProxyGuard.IsGuardCommand(args))
            {
                return ProxyGuard.Run(args);
            }
            if (AgentHostArguments.IsHostCommand(args))
            {
                if (!AgentHostArguments.TryParse(args, out var host))
                    return 2;
                return host.Mode == ApplicationHostMode.Headless ? RunHeadless(host) : RunApplication(host);
            }
            return RunApplication();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunHeadless(AgentHostArguments host) => StatefulAgentHost.Run(host);

        // Kept out of Main so that the guard process never loads WPF.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunApplication(AgentHostArguments host = null)
        {
            var app = host == null ? new App() : new App(host.InstanceId, true);
            app.InitializeComponent();
            return app.Run();
        }
    }
}
