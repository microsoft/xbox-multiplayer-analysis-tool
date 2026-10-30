// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtReadonlyDataTable : IReadonlyDataTable, IDisposable
    {
        public string Name { get { return _name; } }

        public IDataset Dataset { get { return GetDataset(); } }

        public IDataset Subset(params string[] fieldNames)
        {
            return GetSubset(fieldNames);
        }

        private IDataset GetDataset()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
            var dataset =
                new SqLtDataset(
                    _connection,
                    Name,
                    _fieldDefinitions);
            _datasets.Add(dataset);
            return dataset;
        }

        private IDataset GetSubset(params string[] fieldNames)
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
            var dataset = new SqLtDataset(_connection, Name,
                _fieldDefinitions.Where(
                    (x) =>
                    {
                        return fieldNames.Contains(x.Key);
                    }
                ).ToDictionary(dict => dict.Key, dict => dict.Value));
            _datasets.Add(dataset);
            return dataset;
        }

        internal SqLtReadonlyDataTable(
            SqliteConnection connection,
            string name,
            IEnumerable<IFieldDefinition> fieldDefinitions)
        {
            _name = name;

            var columnIndex = 0;
            foreach (var fieldDef in fieldDefinitions)
            {
                _fieldDefinitions.Add(fieldDef.Name, fieldDef);
                columnIndex++;
            }

            _connection = connection;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (SqLtDataset dataset in
                _datasets)
            {
                dataset.Dispose();
            }
            _datasets.Clear();
            _connection = null;
        }

        private string _name;
        private readonly Dictionary<string, IFieldDefinition> _fieldDefinitions =
            new Dictionary<string, IFieldDefinition>();
        private readonly HashSet<SqLtDataset> _datasets =
            new(ReferenceEqualityComparer.Instance);
        private SqliteConnection _connection;
        private bool _disposed;
    }
}
