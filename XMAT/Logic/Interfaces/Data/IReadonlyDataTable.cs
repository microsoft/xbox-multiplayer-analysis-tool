// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.SharedInterfaces
{
    public interface IReadonlyDataTable
    {
        string Name { get; }
        IDataset Dataset { get; }

        // should throw exception if:
        // - no field names are provided
        // - a non-existent field is provided
        // - a null or empty field is provided
        IDataset Subset(params string[] fieldNames);
    }
}
