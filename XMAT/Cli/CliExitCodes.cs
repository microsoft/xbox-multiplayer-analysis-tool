// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;

namespace XMAT.Cli
{
    // Exit codes of xmat.exe, a public contract documented in docs/cli.md: add codes, never renumber or reuse them.
    internal static class CliExitCodes
    {
        internal const int Success = 0;
        internal const int Usage = 2;
        internal const int InvalidInput = 3;
        internal const int Prerequisite = 4;
        internal const int Conflict = 5;
        internal const int Failed = 6;
        internal const int CleanupFailed = 7;
        internal const int FindingsFound = 10;
        internal const int Internal = 70;
        internal const int Cancelled = 130;

        internal static int FromOutcome(OperationOutcome outcome) => outcome switch
        {
            OperationOutcome.Succeeded => Success,
            OperationOutcome.InvalidInput => InvalidInput,
            OperationOutcome.NoAnalyzableData => InvalidInput,
            OperationOutcome.PrerequisiteMissing => Prerequisite,
            OperationOutcome.Conflict => Conflict,
            OperationOutcome.Failed => Failed,
            OperationOutcome.CleanupFailed => CleanupFailed,
            OperationOutcome.Cancelled => Cancelled,
            _ => Internal,
        };

        // The "status" of the JSON document for an exit code.
        internal static string Status(int exitCode) => exitCode switch
        {
            Success => "succeeded",
            FindingsFound => "findingsFound",
            Cancelled => "cancelled",
            _ => "failed",
        };
    }
}
