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
    internal class SqLtReadonlyDataTable :
        IReadonlyDataTable,
        IDisposable,
        ICaptureTextSourceProvider
    {
        public string Name { get { return _name; } }

        public IDataset Dataset { get { return GetDataset(); } }

        internal IReadOnlyList<SqLtSchemaColumn> Columns { get; }

        public CaptureTextSource CaptureTextSource { get; }

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
                    _fieldDefinitions,
                    Columns,
                    CaptureTextSource);
            _datasets.Add(dataset);
            return dataset;
        }

        private IDataset GetSubset(params string[] fieldNames)
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
            Dictionary<string, IFieldDefinition> fields = _fieldDefinitions.Where(
                item => fieldNames.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value);
            var dataset = new SqLtDataset(
                _connection,
                Name,
                fields,
                Columns.Where(column => fields.ContainsKey(column.Name)).ToList(),
                CaptureTextSource);
            _datasets.Add(dataset);
            return dataset;
        }

        internal SqLtReadonlyDataTable(
            SqliteConnection connection,
            string name,
            IEnumerable<IFieldDefinition> fieldDefinitions,
            IReadOnlyList<SqLtSchemaColumn> columns,
            CaptureTextSource captureTextSource)
        {
            ArgumentNullException.ThrowIfNull(captureTextSource);
            _name = name;
            Columns = columns;
            CaptureTextSource = captureTextSource;

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
