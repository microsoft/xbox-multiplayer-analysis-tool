// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    public interface IDataset
    {
        IEnumerable<IFieldDefinition> FieldDefs { get; }

        IEnumerable<IDataRecord> DataRecords { get; }
        IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId);
        IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId, int limit);
        IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] fieldNamesAndValues);
    }
}
