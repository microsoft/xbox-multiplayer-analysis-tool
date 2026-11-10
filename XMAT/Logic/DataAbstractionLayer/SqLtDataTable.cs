// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtDataTable :
        IDataTable,
        IImportedCaptureTable,
        ICaptureTextSourceProvider
    {
        public event Action<IDataTable, Int64> RowAdded;
        public event Action<IDataTable, Int64> RowUpdated;
        public event Action<IDataTable, Int64> RowsRemoved;
        private readonly object _lockObj = new object();

        public string Name { get { return _name; } }

        public IDataset Dataset { get { return GetDataset(); } }

        public CaptureTextSource CaptureTextSource { get; }

        public IDataset Subset(params string[] fieldNames)
        {
            return GetSubset(fieldNames);
        }

        public Int64 AddRow(params object[] fieldValueArgs)
        {
            lock (_lockObj)
            {
                Int64 lastRowId = -1;

                if (fieldValueArgs.Length != _fieldDefinitions.Count())
                {
                    throw new ArgumentException("inequal number of values to table fields.");
                }

                _commandBuilder.Clear();
                _commandBuilder.Append("INSERT INTO ");
                _commandBuilder.Append(_name);
                _commandBuilder.Append("(");

                FieldDefinition[] fields =
                    _fieldDefinitions.Values
                        .Cast<FieldDefinition>()
                        .ToArray();
                string fieldNames = String.Join(
                    ",",
                    fields.Select(x => x.Name));
                _commandBuilder.Append(fieldNames);

                _commandBuilder.Append(") VALUES(");
                string[] parameters = fields.Select(
                    (_, index) => $"$value{index}").ToArray();
                _commandBuilder.Append(
                    string.Join(",", parameters));

                _commandBuilder.Append(")");

                _command.Parameters.Clear();
                _command.CommandText = _commandBuilder.ToString();
                for (int index = 0; index < fields.Length; index++)
                {
                    _command.Parameters.AddWithValue(
                        parameters[index],
                        fields[index].ValueAsParameter(
                            fieldValueArgs[index]));
                }

                int rowsModified = 0;

                if (_command.Connection.State == ConnectionState.Open)
                {
                    rowsModified = _command.ExecuteNonQuery();

                    if (rowsModified > 0)
                    {
                        _command.Parameters.Clear();
                        _command.CommandText = "SELECT last_insert_rowid()";
                        lastRowId = (Int64)_command.ExecuteScalar();
                        RowAdded?.Invoke(this, lastRowId);
                    }
                    else
                    {
                        throw new ApplicationException("Add row returned with 0 rows modified");
                    }
                }
                return lastRowId;
            }
        }

        public void UpdateRow(Int64 rowId, params IFieldValue[] fieldNamesAndValues)
        {
            lock (_lockObj)
            {
                _commandBuilder.Clear();

                _commandBuilder.Append($"UPDATE {_name} SET");

                FieldDefinition[] fields = fieldNamesAndValues.Select(
                    value =>
                        (FieldDefinition)_fieldDefinitions[value.Name])
                    .ToArray();
                string[] parameters = fields.Select(
                    (_, index) => $"$value{index}").ToArray();
                string updateValues = String.Join(
                    ",",
                    fields.Select(
                        (field, index) =>
                            $" {field.Name} = {parameters[index]}"));
                _commandBuilder.Append(updateValues);

                _commandBuilder.Append(" WHERE id = $rowId");

                _command.Parameters.Clear();
                _command.CommandText = _commandBuilder.ToString();
                for (int index = 0; index < fields.Length; index++)
                {
                    _command.Parameters.AddWithValue(
                        parameters[index],
                        fields[index].ValueAsParameter(
                            fieldNamesAndValues[index].Value));
                }
                _command.Parameters.AddWithValue(
                    "$rowId",
                    rowId);

                var rowsModified = _command.ExecuteNonQuery();

                if (rowsModified > 0)
                {
                    RowUpdated?.Invoke(this, rowsModified);
                }
            }
        }

        public void RemoveRowsWhere(params IFieldValue[] fieldNamesAndValues)
        {
            lock (_lockObj)
            {
                _commandBuilder.Clear();

                _commandBuilder.Append($"DELETE from {_name}");

                if (fieldNamesAndValues.Count() > 0)
                {
                    _commandBuilder.Append(@" WHERE");

                    FieldDefinition[] fields =
                        fieldNamesAndValues.Select(
                            value =>
                                (FieldDefinition)_fieldDefinitions[value.Name])
                            .ToArray();
                    string[] parameters = fields.Select(
                        (_, index) => $"$value{index}").ToArray();
                    string whereValues = String.Join(
                        " AND ",
                        fields.Select(
                            (field, index) =>
                                $" {field.Name} = {parameters[index]}"));
                    _commandBuilder.Append(whereValues);

                    _command.Parameters.Clear();
                    for (int index = 0; index < fields.Length; index++)
                    {
                        _command.Parameters.AddWithValue(
                            parameters[index],
                            fields[index].ValueAsParameter(
                                fieldNamesAndValues[index].Value));
                    }
                }
                else
                {
                    _command.Parameters.Clear();
                }

                _command.CommandText = _commandBuilder.ToString();
                var rowsModified = _command.ExecuteNonQuery();

                if (rowsModified > 0)
                {
                    RowsRemoved?.Invoke(this, rowsModified);
                }
            }
        }

        public IReadOnlyList<long> ReplaceImportedRows(
            CaptureTextSource source,
            IFieldValue capture,
            IEnumerable<object[]> rows)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Semantics !=
                CaptureTextSemantics.Literal)
            {
                throw new InvalidOperationException(
                    "Ambiguous capture text cannot be copied into a marked capture.");
            }

            lock (_lockObj)
            {
                var captureField = (FieldDefinition)_fieldDefinitions[capture.Name];
                var fields = _fieldDefinitions.Values.Cast<FieldDefinition>().ToArray();
                string fieldNames = string.Join(",", fields.Select(field => field.Name));
                string[] parameterNames = fields.Select(
                    (_, index) => $"$value{index}").ToArray();
                var rowIds = new List<long>();

                using var transaction = _connection.BeginTransaction();
                using (var deleteCommand = _connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText =
                        $"DELETE FROM {_name} WHERE {captureField.Name} = $capture";
                    deleteCommand.Parameters.AddWithValue(
                        "$capture",
                        captureField.ValueAsParameter(
                            capture.Value));
                    deleteCommand.ExecuteNonQuery();
                }

                using var insertCommand = _connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    $"INSERT INTO {_name}({fieldNames}) VALUES({string.Join(",", parameterNames)})";
                var parameters = parameterNames.Select(name =>
                {
                    var parameter = insertCommand.CreateParameter();
                    parameter.ParameterName = name;
                    insertCommand.Parameters.Add(parameter);
                    return parameter;
                }).ToArray();

                using var rowIdCommand = _connection.CreateCommand();
                rowIdCommand.Transaction = transaction;
                rowIdCommand.CommandText = "SELECT last_insert_rowid()";

                foreach (object[] row in rows)
                {
                    if (row.Length != fields.Length)
                    {
                        throw new ArgumentException("Inequal number of values to table fields.", nameof(rows));
                    }

                    for (int index = 0; index < fields.Length; index++)
                    {
                        parameters[index].Value =
                            fields[index].ValueAsParameter(
                                row[index]);
                    }

                    insertCommand.ExecuteNonQuery();
                    rowIds.Add((long)rowIdCommand.ExecuteScalar());
                }

                transaction.Commit();
                return rowIds;
            }
        }

        private IDataset GetDataset()
        {
            lock (_lockObj)
            {
                return new SqLtDataset(
                    _connection,
                    Name,
                    _fieldDefinitions,
                    CaptureTextSource);
            }
        }

        internal T ReadForSazExport<T>(
            Func<SqliteConnection, string, T> read)
        {
            ArgumentNullException.ThrowIfNull(read);
            lock (_lockObj)
            {
                return read(
                    _connection,
                    _name);
            }
        }

        private IDataset GetSubset(params string[] fieldNames)
        {
            lock (_lockObj)
            {
                return new SqLtDataset(_connection, Name,
                    _fieldDefinitions.Where(
                        (x) =>
                        {
                            return fieldNames.Contains(x.Key);
                        }
                    ).ToDictionary(dict => dict.Key, dict => dict.Value),
                    CaptureTextSource);
            }
        }

        internal SqLtDataTable(
            SqliteConnection connection,
            string name,
            IEnumerable<IField> fields)
            : this(
                connection,
                name,
                fields,
                global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal)
        {
        }

        internal SqLtDataTable(
            SqliteConnection connection,
            string name,
            IEnumerable<IField> fields,
            CaptureTextSource captureTextSource)
        {
            ArgumentNullException.ThrowIfNull(captureTextSource);
            _name = name;
            CaptureTextSource = captureTextSource;

            var columnIndex = 1;
            foreach (var field in fields)
            {
                _fieldDefinitions.Add(
                    field.Name,
                    FieldDefinition.CreateFromSchema(field.Name, field.ValueType, columnIndex));
                columnIndex++;
            }

            _connection = connection;
            _command = new SqliteCommand();
            _command.Connection = connection;
            _commandBuilder = new StringBuilder();

            InitializeNewTable();
        }

        private void InitializeNewTable()
        {
            _commandBuilder.Clear();
            _commandBuilder.Append("CREATE TABLE ");
            _commandBuilder.Append(Name);
            _commandBuilder.Append("(id INTEGER PRIMARY KEY,");

            string joinedFields = String.Join(",", _fieldDefinitions.Values.Select(x => (x as FieldDefinition).AsFieldCreate()));
            _commandBuilder.Append(joinedFields);

            _commandBuilder.Append(")");

            _command.CommandText = _commandBuilder.ToString();
            _command.ExecuteNonQuery();
        }

        private string _name;
        private readonly Dictionary<string, IFieldDefinition> _fieldDefinitions =
            new Dictionary<string, IFieldDefinition>();
        private SqliteConnection _connection;
        private SqliteCommand _command;
        private StringBuilder _commandBuilder;
    }
}
