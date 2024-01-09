// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
            SqliteDataReader dataReader = null;

            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(",", _fieldDefinitions.Select(x => x.Key));
            _commandBuilder.Append(fields);

            _commandBuilder.Append($" FROM {_sourceTableName}");
            _commandBuilder.Append(@" WHERE");

            string whereValues = string.Join(" AND ", fieldNamesAndValues.Select(
                (x) =>
                {
                    var fieldDef = _fieldDefinitions[x.Name] as FieldDefinition;
                    var fieldName = fieldDef.Name;
                    var fieldValue = fieldDef.FieldValueAsString(x.Value);
                    return string.Format(" {0} = {1}", fieldName, fieldValue);
                }));
            _commandBuilder.Append(whereValues);

            _commandInterface.CommandText = _commandBuilder.ToString();

            if(_commandInterface.Connection.State == System.Data.ConnectionState.Open)
            {
                dataReader = _commandInterface.ExecuteReader();
            }

            return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null);
        }

        public IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId, int limit)
        {
            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(",", _fieldDefinitions.Select(x => x.Key));
            _commandBuilder.Append(fields);

            _commandBuilder.Append($" FROM {_sourceTableName}");
            _commandBuilder.Append($" WHERE id >= {firstRowId} LIMIT {limit}");

            _commandInterface.CommandText = _commandBuilder.ToString();
            var dataReader = _commandInterface.ExecuteReader();

            return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null);
        }

        internal SqLtDataset(
            SqliteConnection connection, 
            string sourceTableName, 
            IDictionary<string, IFieldDefinition> fieldsToInclude)
        {
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
            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(",", _fieldDefinitions.Select(x => x.Key));
            _commandBuilder.Append(fields);

            _commandBuilder.Append($" FROM {_sourceTableName}");

            _commandInterface.CommandText = _commandBuilder.ToString();
            var dataReader = _commandInterface.ExecuteReader();

            return new SqLtDataEnumerable(_fieldDefinitions, dataReader, null);
        }

        private string _sourceTableName;
        private SqliteCommand _commandInterface;
        private StringBuilder _commandBuilder;
        private IDictionary<string, IFieldDefinition> _fieldDefinitions;
    }
}
