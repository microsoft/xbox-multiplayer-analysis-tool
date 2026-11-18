// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Operations
{
    // How an operation ended. The command line maps each value to one exit code; the GUI maps it to its
    // existing messages. CleanupFailed overrides every other outcome.
    internal enum OperationOutcome
    {
        Succeeded,
        InvalidInput,
        NoAnalyzableData,
        PrerequisiteMissing,
        Conflict,
        Failed,
        CleanupFailed,
        Cancelled,
        InternalError,
    }
}
