// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtDataset : IDataset
    {
        public IEnumerable<IFieldDefinition> FieldDefs { get { return GetFieldDefs(); } }

        public IEnumerable<IDataRecord> DataRecords { get { return GetDataRecords(); } }

        public IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId)
        {
            return DataRecordsFrom(firstRowId, Int32.MaxValue);
        }

        public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] fieldNamesAndValues)
        {
            lock (_gate)
            {
                SqliteDataReader dataReader = null;

                _commandBuilder.Clear();
                _commandBuilder.Append("SELECT DISTINCT id,");

                string fields = string.Join(",", _fieldDefinitions.Select(x => SqLtQuerySupport.Identifier(x.Key)));
                _commandBuilder.Append(fields);

                _commandBuilder.Append($" FROM {SqLtQuerySupport.Identifier(_sourceTableName)}");
                _commandBuilder.Append(@" WHERE");

                _commandInterface.Parameters.Clear();
                var index = 0;
                string whereValues = string.Join(" AND ", fieldNamesAndValues.Select(
                    (x) =>
                    {
                        var fieldDef = _fieldDefinitions[x.Name] as FieldDefinition;
                        var parameter = $"$p{index++}";
                        _commandInterface.Parameters.AddWithValue(parameter, x.Value ?? DBNull.Value);
                        return $" {SqLtQuerySupport.Identifier(fieldDef.Name)} = {parameter}";
                    }));
                _commandBuilder.Append(whereValues);

                _commandInterface.CommandText = _commandBuilder.ToString();

                if (_commandInterface.Connection.State == System.Data.ConnectionState.Open)
                {
                    dataReader = _commandInterface.ExecuteReader();
                }

                return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null, _gate);
            }
        }

        public IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId, int limit)
        {
            lock (_gate)
            {
                _commandBuilder.Clear();
                _commandBuilder.Append("SELECT DISTINCT id,");

                string fields = string.Join(",", _fieldDefinitions.Select(x => SqLtQuerySupport.Identifier(x.Key)));
                _commandBuilder.Append(fields);

                _commandBuilder.Append($" FROM {SqLtQuerySupport.Identifier(_sourceTableName)}");
                _commandBuilder.Append($" WHERE id >= {firstRowId} LIMIT {limit}");

                _commandInterface.CommandText = _commandBuilder.ToString();
                _commandInterface.Parameters.Clear();
                var dataReader = _commandInterface.ExecuteReader();

                return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null, _gate);
            }
        }

        internal SqLtDataset(
            SqliteConnection connection,
            string sourceTableName,
            IDictionary<string, IFieldDefinition> fieldsToInclude)
        {
            _gate = SqLtConnectionGate.For(connection);
            _sourceTableName = sourceTableName;
            _commandInterface = new SqliteCommand();
            _commandInterface.Connection = connection;
            _commandBuilder = new StringBuilder();
            _fieldDefinitions = fieldsToInclude;
        }

        private IEnumerable<IFieldDefinition> GetFieldDefs()
        {
            return _fieldDefinitions.Values;
        }

        private IEnumerable<IDataRecord> GetDataRecords()
        {
            lock (_gate)
            {
                _commandBuilder.Clear();
                _commandBuilder.Append("SELECT DISTINCT id,");

                string fields = string.Join(",", _fieldDefinitions.Select(x => SqLtQuerySupport.Identifier(x.Key)));
                _commandBuilder.Append(fields);

                _commandBuilder.Append($" FROM {SqLtQuerySupport.Identifier(_sourceTableName)}");

                _commandInterface.CommandText = _commandBuilder.ToString();
                _commandInterface.Parameters.Clear();
                var dataReader = _commandInterface.ExecuteReader();

                return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null, _gate);
            }
        }

        private string _sourceTableName;
        private SqliteCommand _commandInterface;
        private StringBuilder _commandBuilder;
        private IDictionary<string, IFieldDefinition> _fieldDefinitions;
        private readonly object _gate;
    }
}
