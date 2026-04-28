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
            int columnIndex,
            FieldValueType fieldValueType)
        {
            switch (fieldValueType)
            {
                case FieldValueType.Integer:
                    return reader.GetInt64(columnIndex);

                case FieldValueType.String:
                    return reader.GetString(columnIndex);

                default:
                    throw new InvalidOperationException("need value type method for unsupported type.");
            }
        }
    }
}
