// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Operations
{
    // Phase is a stable, data-free name; Fraction is 0..1 when the operation can estimate it.
    internal sealed record OperationProgress(string Phase, double? Fraction = null);
}
