// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace XMAT.SharedInterfaces
{
    public interface IDataTable
    {
        // passes the rowId of the row that was added
        event Action<IDataTable, Int64> RowAdded;
        // passes the rowId of the row that was updated
        event Action<IDataTable, Int64> RowUpdated;
        // passes the number of rows removed
        event Action<IDataTable, Int64> RowsRemoved;

        string Name { get; }
        IDataset Dataset { get; }

        // should throw exception if:
        // - no field names are provided
        // - a non-existent field is provided
        // - a null or empty field is provided
        IDataset Subset(params string[] fieldNames);

        // should throw an exception if:
        // - fieldValueArgs is null
        // - fieldValueArgs has a length of 0
        // - fieldValueArgs length does not match then number of fields
        // - fieldValueArgs are parseable according to their field type
        Int64 AddRow(params object[] fieldValueArgs);

        // TODO: add an UpdateRowWhere() API method

        // should throw an exception if:
        // - rowId < 0
        // - fieldNamesAndValues is null
        // - fieldNamesAndValues has a length of 0
        // - any of fieldNamesAndValues fields is null or empty
        // - any of fieldNamesAndValues point to non-existent fields
        void UpdateRow(Int64 rowId, params IFieldValue[] fieldNamesAndValues);

        // notes:
        // - a null fieldNamesAndValues means remove all
        // - a fieldNamesAndValues with length of 0 means remove all
        // - any of fieldNamesAndValues that are null or empty are ignored
        // - any non-existent fields in fieldNamesAndValues are ignored
        void RemoveRowsWhere(params IFieldValue[] fieldNamesAndValues);
    }
}
