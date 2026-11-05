// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqLtNullExtensionTests
    {
        [Theory]
        [InlineData("Text")]
        [InlineData("Integer")]
        public void NullExtension_IsRetainedAsNoValue(
            string kind)
        {
            Type type = kind == "Text"
                ? typeof(string)
                : typeof(long);
            using SqliteConnection connection =
                OpenMemoryDatabase();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT 1, NULL";
            using SqliteDataReader reader =
                command.ExecuteReader();
            Assert.True(reader.Read());
            var fields =
                new Dictionary<
                    string,
                    IFieldDefinition>
                {
                    ["Extension"] =
                        FieldDefinition.CreateFromSchema(
                            "Extension",
                            type,
                            1,
                            isExtension: true),
                };

            var record =
                new SqLtDataRecord(
                    reader,
                    fields);

            Assert.True(
                record.IsNull("Extension"));
            if (type == typeof(string))
            {
                Assert.Null(
                    record.Str("Extension"));
            }
            else
            {
                Assert.Equal(
                    0,
                    record.Int("Extension"));
            }
        }

        [Theory]
        [InlineData("Text")]
        [InlineData("Integer")]
        public void NullCoreField_RemainsInvalid(
            string kind)
        {
            Type type = kind == "Text"
                ? typeof(string)
                : typeof(long);
            using SqliteConnection connection =
                OpenMemoryDatabase();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT 1, NULL";
            using SqliteDataReader reader =
                command.ExecuteReader();
            Assert.True(reader.Read());
            var fields =
                new Dictionary<
                    string,
                    IFieldDefinition>
                {
                    ["Core"] =
                        FieldDefinition.CreateFromSchema(
                            "Core",
                            type,
                            1),
                };

            Assert.Throws<InvalidOperationException>(
                () => new SqLtDataRecord(
                    reader,
                    fields));
        }

        [Fact]
        public void NonNullExtensions_KeepTheirValues()
        {
            string text = new('x', 1024 * 1024);
            using SqliteConnection connection =
                OpenMemoryDatabase();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT 1, $text, 7, 7";
            command.Parameters.AddWithValue("$text", text);
            using SqliteDataReader reader =
                command.ExecuteReader();
            Assert.True(reader.Read());
            var fields =
                new Dictionary<
                    string,
                    IFieldDefinition>
                {
                    ["TextExtension"] =
                        FieldDefinition.CreateFromSchema(
                            "TextExtension",
                            typeof(string),
                            1,
                            isExtension: true),
                    ["IntegerExtension"] =
                        FieldDefinition.CreateFromSchema(
                            "IntegerExtension",
                            typeof(long),
                            2,
                            isExtension: true),
                    ["Mismatch"] =
                        FieldDefinition.CreateFromSchema(
                            "Mismatch",
                            typeof(string),
                            3,
                            isExtension: true),
                };

            var record =
                new SqLtDataRecord(
                    reader,
                    fields);

            Assert.False(
                record.IsNull("TextExtension"));
            Assert.False(
                record.IsNull("IntegerExtension"));
            Assert.Equal(
                text,
                record.Str("TextExtension"));
            Assert.Equal(
                7,
                record.Int("IntegerExtension"));
            Assert.True(((IRawDataRecord)record).TryReadRaw(
                "TextExtension",
                out object raw));
            Assert.Same(raw, record.Str("TextExtension"));
            Assert.True(((IRawDataRecord)record).TryReadRaw("Mismatch", out object mismatch));
            Assert.Equal(7L, mismatch);
            Assert.Null(record.Str("Mismatch"));
            var rawOverrides = (IReadOnlyDictionary<string, object>)typeof(SqLtDataRecord)
                .GetField("_rawValues", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(record);
            Assert.Equal(new[] { "Mismatch" }, rawOverrides.Keys);
        }

        [Fact]
        public void CoreFields_PreserveSqliteCoercionForMismatchedStorage()
        {
            using SqliteConnection connection = OpenMemoryDatabase();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1, '12abc', 1.5, 42, X'74657874'";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var fields = new Dictionary<string, IFieldDefinition>
            {
                ["TextToInteger"] =
                    FieldDefinition.CreateFromSchema("TextToInteger", typeof(long), 1),
                ["RealToInteger"] =
                    FieldDefinition.CreateFromSchema("RealToInteger", typeof(long), 2),
                ["IntegerToText"] =
                    FieldDefinition.CreateFromSchema("IntegerToText", typeof(string), 3),
                ["BlobToText"] =
                    FieldDefinition.CreateFromSchema("BlobToText", typeof(string), 4),
            };

            var record = new SqLtDataRecord(reader, fields);

            Assert.Equal(12L, record.Int("TextToInteger"));
            Assert.Equal(1L, record.Int("RealToInteger"));
            Assert.Equal("42", record.Str("IntegerToText"));
            Assert.Equal("text", record.Str("BlobToText"));
        }

        private static SqliteConnection OpenMemoryDatabase()
        {
            var connection =
                new SqliteConnection(
                    "Data Source=:memory:");
            connection.Open();
            return connection;
        }
    }
}
