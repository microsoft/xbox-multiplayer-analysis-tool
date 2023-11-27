// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Text;

namespace XMAT.DataAbstractionLayer
{
    internal class FieldDefinition : IFieldDefinition
    {
        public static string EscapeText(string text)
        {
            StringBuilder literal = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\'': literal.Append('\'').Append('\''); break;
                    case '\"': literal.Append('\"').Append('\"'); break;
                    //case '\0': literal.Append('\\').Append('0'); break;
                    //case '\a': literal.Append('\\').Append('a'); break;
                    //case '\b': literal.Append('\\').Append('b'); break;
                    //case '\f': literal.Append('\\').Append('f'); break;
                    //case '\n': literal.Append('\\').Append('n'); break;
                    //case '\r': literal.Append('\\').Append('r'); break;
                    //case '\t': literal.Append('\\').Append('t'); break;
                    //case '\v': literal.Append('\\').Append('v'); break;
                    //case '\\': literal.Append('\\').Append('\\'); break;
                    default:
                        literal.Append(c);
                        // TODO: I don't think this is required...DB is in UTF8 mode, should be able to handle Unicode without encoding (?)
                        //// As ASCII printable character
                        //if (c >= 0x20 && c <= 0x7e)
                        //{
                        //    literal.Append(c);
                        //}
                        //// As UTF16 escaped character
                        //else
                        //{
                        //    literal.Append(@"\u");
                        //    literal.Append(((int)c).ToString("x4"));
                        //}
                        break;
                }
            }
            return literal.ToString();
        }

        internal string FieldValueAsString(object value)
        {
            if(value == null)
                return "''";

            var escapedValue = EscapeText(value.ToString());
            return ValueType == FieldValueType.String ?
                string.Format("'{0}'", escapedValue) :
                escapedValue;
        }

        internal static FieldDefinition CreateFromSchema(string name, Type type, int columnIndex)
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
                Name = name
            };
        }

        private FieldDefinition() { }

        public FieldType FieldType { get; private set; }
        public int ColumnIndex { get; private set; }
        public FieldValueType ValueType { get; private set; }
        public string Name { get; private set; }

        internal string AsFieldCreate()
        {
            return string.Format("{0} {1}",
                Name,
                ValueTypeAsString());
        }

        internal string ValueTypeAsString()
        {
            switch(ValueType)
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
