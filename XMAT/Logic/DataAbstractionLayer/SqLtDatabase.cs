// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
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

        public void SaveToFile(string filepath, long maximumBytes, CancellationToken cancellationToken) =>
            SqLtDatabaseBackup.SaveToFile(_connection, filepath, this,
                maximumBytes: maximumBytes, cancellationToken: cancellationToken);

        public void CreateSnapshotToFile(string filepath, long maximumBytes, CancellationToken cancellationToken) =>
            SqLtDatabaseBackup.CreateSnapshot(_connection, filepath, maximumBytes, cancellationToken);

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
