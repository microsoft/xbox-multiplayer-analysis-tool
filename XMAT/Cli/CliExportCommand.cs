// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Cli
{
    // xmat export INPUT --out FILE: the command-line adapter of the shared export operation (the GUI's adapter is
    // CaptureExportWorkflow). It never replaces an existing file.
    internal static class CliExportCommand
    {
        internal static Task<CliReport> RunAsync(CliRequest request, string runId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
