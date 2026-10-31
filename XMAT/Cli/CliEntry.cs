// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;

namespace XMAT.Cli
{
    // The only public entry point of the command line. xmat.exe (project XMAT.Cli) calls it from Main and returns its
    // result as the process exit code. It never creates the WPF application, a window or a dispatcher.
    public static class CliEntry
    {
        public static Task<int> RunAsync(string[] args)
        {
            return CliApplication.RunAsync(
                args ?? Array.Empty<string>(),
                new CliConsole(Console.OpenStandardOutput(), Console.Error));
        }
    }
}
