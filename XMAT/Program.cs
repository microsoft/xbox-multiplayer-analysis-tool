// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.CompilerServices;
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
            return RunApplication();
        }

        // Kept out of Main so that the guard process never loads WPF.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunApplication()
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
