// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal interface INullableDataRecord
    {
        bool IsNull(string fieldName);
    }

    internal class SqLtDataRecord :
        IDataRecord,
        IOptionalTypedDataRecord,
        INullableDataRecord
    {
        public int RowId { get; private set; }

        public Int64 Int(string fieldName)
        {
            IFieldDefinition fieldDef;
            if (_fieldDefs.TryGetValue(fieldName, out fieldDef))
            {
                object value =
                    _fieldValues[fieldDef.ColumnIndex];
                return value == null
                    ? default
                    : (Int64)value;
            }
            else
            {
                return default(Int64);
            }
        }

        // Only an extension column can hold NULL; reading one yields null text or a zero integer.
        public bool IsNull(string fieldName)
        {
            return _fieldDefs.TryGetValue(
                    fieldName,
                    out IFieldDefinition fieldDef) &&
                _fieldValues[fieldDef.ColumnIndex] == null;
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

        public bool TryReadString(string name, out string value)
        {
            value = null;
            if (!_fieldDefs.TryGetValue(name, out IFieldDefinition fieldDefinition) ||
                fieldDefinition.ValueType != FieldValueType.String)
            {
                return false;
            }

            value = _fieldValues[fieldDefinition.ColumnIndex] as string;
            return value != null;
        }

        public bool TryReadInt64(string name, out long value)
        {
            value = default;
            if (!_fieldDefs.TryGetValue(name, out IFieldDefinition fieldDefinition) ||
                fieldDefinition.ValueType != FieldValueType.Integer ||
                _fieldValues[fieldDefinition.ColumnIndex] is not long stored)
            {
                return false;
            }

            value = stored;
            return true;
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
                    fieldDef.Value);
            }
        }

        private IDictionary<string, IFieldDefinition> _fieldDefs;
        private IDictionary<int, object> _fieldValues;
    }
}
