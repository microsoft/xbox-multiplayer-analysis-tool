// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtDataTable : IDataTable, IImportedCaptureTable, IVersionedCaptureTable, IExactCaptureRowDelete
    {
        public event Action<IDataTable, Int64> RowAdded;
        public event Action<IDataTable, Int64> RowUpdated;
        public event Action<IDataTable, Int64> RowsRemoved;
        private readonly object _lockObj = new object();

        public string Name { get { return _name; } }

        public IDataset Dataset { get { return GetDataset(); } }

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

                string fieldNames = String.Join(",", _fieldDefinitions.Values.Select(x => x.Name));
                _commandBuilder.Append(fieldNames);

                _commandBuilder.Append(") VALUES(");

                var valueEnumerator = fieldValueArgs.GetEnumerator();
                string fieldValues = String.Join(",", _fieldDefinitions.Values.Select(
                    (x) =>
                    {
                        valueEnumerator.MoveNext();
                        return (x as FieldDefinition).FieldValueAsString(valueEnumerator.Current);
                    }));
                _commandBuilder.Append(fieldValues);

                _commandBuilder.Append(")");

                _command.CommandText = _commandBuilder.ToString();

                int rowsModified = 0;

                if (_command.Connection.State == ConnectionState.Open)
                {
                    rowsModified = _command.ExecuteNonQuery();

                    if (rowsModified > 0)
                    {
                        _command.CommandText = "SELECT last_insert_rowid()";
                        lastRowId = (Int64)_command.ExecuteScalar();
                        _rowVersions[lastRowId] = 1;
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

                string updateValues = String.Join(",", fieldNamesAndValues.Select(
                    (x) =>
                    {
                        var fieldDef = _fieldDefinitions[x.Name] as FieldDefinition;
                        var fieldName = fieldDef.Name;
                        var fieldValue = fieldDef.FieldValueAsString(x.Value);
                        return string.Format(" {0} = {1}", fieldName, fieldValue);
                    }));
                _commandBuilder.Append(updateValues);

                _commandBuilder.Append($" WHERE id = {rowId}");

                _command.CommandText = _commandBuilder.ToString();

                var rowsModified = _command.ExecuteNonQuery();

                if (rowsModified > 0)
                {
                    if (fieldNamesAndValues.Any(field => !IsProcessField(field.Name)))
                    {
                        _rowVersions[rowId] = VersionOfCore(rowId) + 1;
                    }
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

                    string whereValues = String.Join(" AND ", fieldNamesAndValues.Select(
                        (x) =>
                        {
                            var fieldDef = _fieldDefinitions[x.Name] as FieldDefinition;
                            var fieldName = fieldDef.Name;
                            var fieldValue = fieldDef.FieldValueAsString(x.Value);
                            return string.Format(" {0} = {1}", fieldName, fieldValue);
                        }));
                    _commandBuilder.Append(whereValues);
                }

                _command.CommandText = _commandBuilder.ToString();
                var rowsModified = _command.ExecuteNonQuery();

                if (rowsModified > 0)
                {
                    _rowVersions.Clear();
                    RowsRemoved?.Invoke(this, rowsModified);
                }
            }
        }

        public IReadOnlyList<long> DeleteRows(
            string deviceName,
            IReadOnlyList<long> rowIds) =>
            throw new NotSupportedException();

        public IDisposable EnterRead()
        {
            Monitor.Enter(_lockObj);
            return new ReadLease(_lockObj);
        }

        public long VersionOf(long rowId)
        {
            lock (_lockObj)
            {
                return VersionOfCore(rowId);
            }
        }

        public IReadOnlyList<long> ReplaceImportedRows(
            IFieldValue capture,
            IEnumerable<object[]> rows)
        {
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
                        ImportedFieldValueAsParameter(captureField, capture.Value));
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
                            ImportedFieldValueAsParameter(fields[index], row[index]);
                    }

                    insertCommand.ExecuteNonQuery();
                    rowIds.Add((long)rowIdCommand.ExecuteScalar());
                }

                transaction.Commit();
                return rowIds;
            }
        }

        private static object ImportedFieldValueAsParameter(
            FieldDefinition field,
            object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (field.ValueType != FieldValueType.String)
            {
                return value;
            }

            // Preserve FieldValueAsString's stored text: doubled single quotes
            // were SQL syntax, while doubled double quotes became data.
            return value.ToString().Replace("\"", "\"\"");
        }

        private IDataset GetDataset()
        {
            lock (_lockObj)
            {
                return new SqLtDataset(_connection, Name, _fieldDefinitions, _columns);
            }
        }

        private IDataset GetSubset(params string[] fieldNames)
        {
            lock (_lockObj)
            {
                Dictionary<string, IFieldDefinition> fields = _fieldDefinitions
                    .Where(item => fieldNames.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value);
                return new SqLtDataset(
                    _connection,
                    Name,
                    fields,
                    _columns.Where(column => fields.ContainsKey(column.Name)).ToList());
            }
        }

        internal SqLtDataTable(
            SqliteConnection connection,
            string name,
            IEnumerable<IField> fields)
        {
            _name = name;

            var columnIndex = 1;
            foreach (var field in fields)
            {
                _columns.Add(new SqLtSchemaColumn(
                    field.Name,
                    field.ValueType == typeof(long) ? SqLtDeclaredType.Integer : SqLtDeclaredType.Text));
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

        private long VersionOfCore(long rowId) =>
            _rowVersions.TryGetValue(rowId, out long version) ? version : 0;

        private static bool IsProcessField(string name) =>
            name == WebProxyCaptureSchema.ClientProcessName ||
            name == WebProxyCaptureSchema.ClientProcessId ||
            name == WebProxyCaptureSchema.ClientProcessStartTime ||
            name == WebProxyCaptureSchema.ClientProcessStatus;

        private sealed class ReadLease : IDisposable
        {
            private object _gate;

            internal ReadLease(object gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                object gate = Interlocked.Exchange(ref _gate, null);
                if (gate != null)
                {
                    Monitor.Exit(gate);
                }
            }
        }

        private string _name;
        private readonly Dictionary<string, IFieldDefinition> _fieldDefinitions =
            new Dictionary<string, IFieldDefinition>();
        private readonly List<SqLtSchemaColumn> _columns = new();
        private readonly Dictionary<long, long> _rowVersions = new();
        private SqliteConnection _connection;
        private SqliteCommand _command;
        private StringBuilder _commandBuilder;
    }
}
