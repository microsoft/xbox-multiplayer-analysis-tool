// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    public static class DataAbstractionLayer
    {
        public static IDatabase Database 
        { 
            get { return DataAbstractionLayerImpl.GetDatabase(); }
        }

        public static IReadonlyDatabase LoadDatabaseFromFile(string filepath)
        {
            return DataAbstractionLayerImpl.LoadDatabaseFromFile(filepath);
        }
    }
}
