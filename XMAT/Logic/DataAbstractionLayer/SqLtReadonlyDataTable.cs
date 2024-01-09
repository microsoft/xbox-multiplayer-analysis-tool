// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtReadonlyDataTable : IReadonlyDataTable
    {
        public string Name { get { return _name; } }

        public IDataset Dataset { get { return GetDataset(); } }

        public IDataset Subset(params string[] fieldNames)
        {
            return GetSubset(fieldNames);
        }

        private IDataset GetDataset()
        {
            return new SqLtDataset(_connection, Name, _fieldDefinitions);
        }

        private IDataset GetSubset(params string[] fieldNames)
        {
            return new SqLtDataset(_connection, Name,
                _fieldDefinitions.Where(
                    (x) =>
                    {
                        return fieldNames.Contains(x.Key);
                    }
                ).ToDictionary(dict => dict.Key, dict => dict.Value));
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
            _command = new SqliteCommand();
            _command.Connection = connection;
        }

        private string _name;
        private readonly Dictionary<string, IFieldDefinition> _fieldDefinitions = 
            new Dictionary<string, IFieldDefinition>();
        private SqliteConnection _connection;
        private SqliteCommand _command;
    }
}
