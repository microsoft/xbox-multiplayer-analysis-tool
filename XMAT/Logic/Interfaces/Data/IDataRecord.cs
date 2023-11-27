// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace XMAT.SharedInterfaces
{
    public interface IDataRecord
    {
        int RowId { get; }
        Int64 Int(string fieldName);
        string Str(string fieldName);
    }
}
