// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;

namespace XMAT.Cli.Host
{
    // The entry point of xmat.exe. It does nothing but call the command line in the XMAT assembly.
    internal static class Program
    {
        private static Task<int> Main(string[] args) => CliEntry.RunAsync(args);
    }
}
