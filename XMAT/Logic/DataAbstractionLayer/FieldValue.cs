// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    // NOTE: see IFieldDefinition.cs, FieldValueType
    internal struct FieldValue
    {
        // NOTE: as other FieldType(s) are added, then be sure to add
        // those other primitive members here.

        internal static object GetFieldValue(
            object stored,
            IFieldDefinition fieldDefinition)
        {
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
                    return stored is long ? stored : Convert.ToInt64(stored, CultureInfo.InvariantCulture);

                case FieldValueType.String:
                    return stored is string ? stored : Convert.ToString(stored, CultureInfo.InvariantCulture);

                default:
                    throw new InvalidOperationException("need value type method for unsupported type.");
            }
        }
    }
}
