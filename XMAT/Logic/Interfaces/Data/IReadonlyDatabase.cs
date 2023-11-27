// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    public interface IReadonlyDatabase
    {
        IEnumerable<string> TableNames();

        // notes:
        // - returns null if the table does not exist
        IReadonlyDataTable TableByName(string name);
    }
}
