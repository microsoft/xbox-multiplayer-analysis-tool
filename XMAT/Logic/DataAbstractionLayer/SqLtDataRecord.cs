// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtDataRecord : IDataRecord
    {
        public int RowId { get; private set; }

        public Int64 Int(string fieldName)
        {
            IFieldDefinition fieldDef;
            if (_fieldDefs.TryGetValue(fieldName, out fieldDef))
            {
                return (Int64)_fieldValues[fieldDef.ColumnIndex];
            }
            else
            {
                return default(Int64);
            }
        }

        public string Str(string fieldName)
        {
            IFieldDefinition fieldDef;
            if (_fieldDefs.TryGetValue(fieldName, out fieldDef))
            {
                return (string)_fieldValues[fieldDef.ColumnIndex];
            }
            else
            {
                return default(string);
            }
        }

        internal SqLtDataRecord(SqliteDataReader recordReader, IDictionary<string, IFieldDefinition> fieldDefs)
        {
            RowId = recordReader.GetInt32(0);

            _fieldDefs = fieldDefs;
            _fieldValues = new Dictionary<int, object>();

            foreach (var fieldDef in _fieldDefs)
            {
                _fieldValues[fieldDef.Value.ColumnIndex] = FieldValue.GetFieldValue(
                    recordReader,
                    fieldDef.Value.ColumnIndex,
                    fieldDef.Value.ValueType);
            }
        }

        private IDictionary<string, IFieldDefinition> _fieldDefs;
        private IDictionary<int, object> _fieldValues;
    }
}
