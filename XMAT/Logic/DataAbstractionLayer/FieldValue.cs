// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    // NOTE: see IFieldDefinition.cs, FieldValueType
    internal struct FieldValue
    {
        // NOTE: as other FieldType(s) are added, then be sure to add
        // those other primitive members here.

        internal static object GetFieldValue(
            SqliteDataReader reader,
            object stored,
            IFieldDefinition fieldDefinition)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(fieldDefinition);
            if (stored == null)
            {
                if (!fieldDefinition.IsExtension)
                {
                    throw new InvalidOperationException(
                        "A required capture field is NULL.");
                }
                return null;
            }
            if (fieldDefinition.IsExtension)
            {
                return fieldDefinition.ValueType switch
                {
                    FieldValueType.Integer when stored is long => stored,
                    FieldValueType.String when stored is string => stored,
                    _ => null,
                };
            }

            switch (fieldDefinition.ValueType)
            {
                case FieldValueType.Integer:
                    return stored is long ? stored : reader.GetInt64(fieldDefinition.ColumnIndex);

                case FieldValueType.String:
                    return stored is string ? stored : reader.GetString(fieldDefinition.ColumnIndex);

                default:
                    throw new InvalidOperationException("need value type method for unsupported type.");
            }
        }
    }
}
