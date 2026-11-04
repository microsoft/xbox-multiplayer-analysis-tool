// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    internal interface IImportedCaptureTable
    {
        // Imports publish their prepared view only after this atomic replacement succeeds.
        IReadOnlyList<long> ReplaceImportedRows(
            IFieldValue capture,
            IEnumerable<object[]> rows);
    }
}
