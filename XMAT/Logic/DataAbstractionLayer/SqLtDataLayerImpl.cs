// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal static class DataAbstractionLayerImpl
    {
        internal static IDatabase GetDatabase()
        {
            if (_database == null)
            {
                _database = new SqLtDatabase();
            }

            return _database;
        }

        internal static IReadonlyDatabase LoadDatabaseFromFile(string filepath)
        {
            return new SqLtReadonlyDatabase(filepath);
        }

        private static SqLtDatabase _database;
    }
}
