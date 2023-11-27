// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace XMAT.SharedInterfaces
{
    public interface IDatabase
    {
        void Initialize();
        void Shutdown();

        void SaveToFile(string filepath);

        IEnumerable<string> TableNames();

        // notes:
        // - returns null if the table does not exist
        IDataTable TableByName(string name);

        // should throw an exception if:
        // - the name is null or empty
        // - the name contains non-alphanumeric characters
        // - fields is null
        // - fields has a length of 0
        // - fields has a field with a null or empty name
        // - fields has a field with a name that has non-alphanumeric characters
        // - fields has fields with duplicate names
        IDataTable CreateTable(string name, params IField[] fields);
    }
}
