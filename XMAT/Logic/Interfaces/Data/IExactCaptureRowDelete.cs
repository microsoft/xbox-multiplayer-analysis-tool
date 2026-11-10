// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    internal interface IExactCaptureRowDelete
    {
        IReadOnlyList<long> DeleteRows(string deviceName, IReadOnlyList<long> rowIds);
    }
}
