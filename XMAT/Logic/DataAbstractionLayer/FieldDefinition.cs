// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class FieldDefinition : IFieldDefinition
    {
        internal object ValueAsParameter(object value) =>
            value == null
                ? string.Empty
                : ValueType == FieldValueType.String
                    ? value.ToString()
                    : value;

        internal static FieldDefinition CreateFromSchema(
            string name,
            Type type,
            int columnIndex,
            bool isExtension = false)
        {
            var fieldValueType = FieldValueType.Undefined;
            if (type == typeof(string))
            {
                fieldValueType = FieldValueType.String;
            }
            else if (
                type == typeof(int) ||
                type == typeof(Int32) ||
                type == typeof(Int64))
            {
                fieldValueType = FieldValueType.Integer;
            }
            else
            {
                throw new InvalidOperationException();
            }

            return new FieldDefinition()
            {
                FieldType = FieldType.DataElement,
                ColumnIndex = columnIndex,
                ValueType = fieldValueType,
                Name = name,
                IsExtension = isExtension,
            };
        }

        private FieldDefinition() { }

        public FieldType FieldType { get; private set; }
        public int ColumnIndex { get; private set; }
        public FieldValueType ValueType { get; private set; }
        public string Name { get; private set; }
        public bool IsExtension { get; private set; }

        internal string AsFieldCreate()
        {
            return string.Format("{0} {1}",
                Name,
                ValueTypeAsString());
        }

        internal string ValueTypeAsString()
        {
            switch (ValueType)
            {
                case FieldValueType.Integer:
                    return @"INTEGER";

                case FieldValueType.String:
                    return @"TEXT";

                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
