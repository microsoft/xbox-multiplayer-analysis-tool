// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.Operations
{
    internal sealed record OperationResult<T>(
        OperationOutcome Outcome, T Value, IReadOnlyList<OperationDiagnostic> Diagnostics)
    {
        // Never null, so adapters can enumerate it without checks.
        public IReadOnlyList<OperationDiagnostic> Diagnostics { get; init; } =
            Diagnostics ?? Array.Empty<OperationDiagnostic>();

        internal bool Succeeded => Outcome == OperationOutcome.Succeeded;

        internal static OperationResult<T> Success(T value, IReadOnlyList<OperationDiagnostic> diagnostics = null) =>
            new(OperationOutcome.Succeeded, value, diagnostics);

        internal static OperationResult<T> Failure(OperationOutcome outcome, params OperationDiagnostic[] diagnostics)
        {
            if (outcome == OperationOutcome.Succeeded)
            {
                throw new ArgumentOutOfRangeException(nameof(outcome), "A failure needs an outcome other than Succeeded.");
            }

            return new(outcome, default, diagnostics);
        }
    }
}
