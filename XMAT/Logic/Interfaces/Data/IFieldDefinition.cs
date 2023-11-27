// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace XMAT.SharedInterfaces
{
    public enum FieldType : int
    {
        Key = 0,
        DataElement = 1,
        Undefined = 0x7fffffff
    }

    public enum FieldValueType : int
    {
        Integer = 0,
        String = 1,
        Undefined = 0x7fffffff
    }

    public interface IField
    {
        Type ValueType { get; }
        string Name { get; }
    }

    public interface IFieldValue
    {
        string Name { get; }
        object Value { get; }
    }

    public interface IFieldDefinition
    {
        FieldType FieldType { get; }
        int ColumnIndex { get; }
        FieldValueType ValueType { get; }
        string Name { get; }
    }

    public struct Field<T> : IField
    {
        public Type ValueType { get { return typeof(T); } }
        public string Name { get; private set; }

        public Field(string name)
        {
            Name = name;
        }
    }

    public struct FieldValue<T> : IFieldValue
    {
        public string Name { get; private set; }
        public object Value { get { return TypedValue; } }
        public T TypedValue { get; private set; }

        public FieldValue(string name, T value)
        {
            Name = name;
            TypedValue = value;
        }
    }
}
