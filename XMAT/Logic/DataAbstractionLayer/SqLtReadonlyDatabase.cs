// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtReadonlyDatabase : IReadonlyDatabase, IDisposable
    {
        public IEnumerable<string> TableNames()
        {
            return _dataTables.Keys;
        }

        public IReadonlyDataTable TableByName(string name)
        {
            return _dataTables[name];
        }

        public void Dispose()
        {
            _connection.Close();
        }

        internal SqLtReadonlyDatabase(string filepath)
        {
            var connectionString = $"Data source={filepath}";
            _connection = new SqliteConnection(connectionString);
            _connection.Open();
            InitializeTables();
        }

        private void InitializeTables()
        {
            var commandText = "SELECT * from sqlite_master";
            var command = new SqliteCommand(commandText, _connection);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var fieldCount = reader.FieldCount;
                    if (fieldCount > 1)
                    {
                        if (reader.GetFieldValue<string>(0).ToLower().Equals(@"table"))
                        {
                            var tableName = reader.GetFieldValue<string>(1);
                            InitializeTableFields(tableName);
                        }
                    }
                }
            }
        }

        private void InitializeTableFields(string tableName)
        {
            // get a reader for the table

            var commandText = $"SELECT * from {tableName}";
            var command = new SqliteCommand(commandText, _connection);
            using (var reader = command.ExecuteReader())
            {
                // get the schema for the table

                var fieldCount = reader.FieldCount;
                if (fieldCount == 0)
                {
                    // TODO: this is an error condition, we need to handle it
                    return;
                }

                var numHiddenFields = 1;// the row id
                var numFieldsAfterRowId = fieldCount - numHiddenFields;
                var fieldDefinitions = new FieldDefinition[numFieldsAfterRowId];

                for (var fieldIndex = 0; fieldIndex < numFieldsAfterRowId; fieldIndex++)
                {
                    var absoluteFieldIndex = fieldIndex + numHiddenFields;
                    var fieldType = reader.GetFieldType(absoluteFieldIndex);
                    var fieldName = reader.GetName(absoluteFieldIndex);
                    fieldDefinitions[fieldIndex] = FieldDefinition.CreateFromSchema(fieldName, fieldType, absoluteFieldIndex);
                }

                // place the table in the table dictionary

                var readonlyTable = new SqLtReadonlyDataTable(_connection, tableName, fieldDefinitions);
                _dataTables[tableName] = readonlyTable;
            }
        }

        private SqliteConnection _connection;

        private readonly Dictionary<string, SqLtReadonlyDataTable> _dataTables = 
            new Dictionary<string, SqLtReadonlyDataTable>();
    }
}
