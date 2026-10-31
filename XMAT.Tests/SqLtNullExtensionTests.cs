// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
            using SqliteConnection connection =
                OpenMemoryDatabase();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT 1, 'value', 7";
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
                "value",
                record.Str("TextExtension"));
            Assert.Equal(
                7,
                record.Int("IntegerExtension"));
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
