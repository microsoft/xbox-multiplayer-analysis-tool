// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDatabase : IDatabase, IDisposable
    {
        public void Initialize()
        {
            InitializeInternal();
        }

        public void Shutdown()
        {
            ShutdownInternal();
        }

        public void SaveToFile(string filepath)
        {
            SaveToFileInternal(filepath);
        }

        public IEnumerable<string> TableNames()
        {
            return GetTableNames();
        }

        public IDataTable TableByName(string name)
        {
            return GetTableByName(name);
        }

        public IDataTable CreateTable(string name, params IField[] fields)
        {
            return CreateTableInternal(name, fields);
        }

        public void Dispose()
        {
            DisposeInternal();
        }
    }
}
