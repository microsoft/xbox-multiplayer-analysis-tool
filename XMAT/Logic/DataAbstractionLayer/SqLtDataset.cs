// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal class SqLtDataset :
        IDataset,
        IRawCaptureDataset,
        IDisposable,
        ICaptureTextSourceProvider
    {
        public IEnumerable<IFieldDefinition> FieldDefs { get { return GetFieldDefs(); } }

        public IEnumerable<IDataRecord> DataRecords { get { return GetDataRecords(); } }

        public IReadOnlyList<SqLtSchemaColumn> Columns { get; }

        public CaptureTextSource CaptureTextSource { get; }

        public IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId)
        {
            return DataRecordsFrom(firstRowId, Int32.MaxValue);
        }

        public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] fieldNamesAndValues)
        {
            ThrowIfDisposed();
            SqliteDataReader dataReader = null;

            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(
                ",",
                _fieldDefinitions.Select(
                    field => QuoteIdentifier(field.Key)));
            _commandBuilder.Append(fields);

            _commandBuilder.Append(" FROM ")
                .Append(
                    QuoteIdentifier(
                        _sourceTableName));
            _commandBuilder.Append(@" WHERE");

            FieldDefinition[] filterFields =
                fieldNamesAndValues.Select(
                    value =>
                        (FieldDefinition)_fieldDefinitions[value.Name])
                    .ToArray();
            string[] parameters = filterFields.Select(
                (_, index) => $"$value{index}").ToArray();
            string whereValues = string.Join(
                " AND ",
                filterFields.Select(
                    (field, index) =>
                        " " +
                        QuoteIdentifier(field.Name) +
                        " = " +
                        parameters[index]));
            _commandBuilder.Append(whereValues);

            _commandInterface.Parameters.Clear();
            _commandInterface.CommandText = _commandBuilder.ToString();
            for (int index = 0; index < filterFields.Length; index++)
            {
                _commandInterface.Parameters.AddWithValue(
                    parameters[index],
                    filterFields[index].ValueAsParameter(
                        fieldNamesAndValues[index].Value));
            }

            if (_commandInterface.Connection.State == System.Data.ConnectionState.Open)
            {
                dataReader = _commandInterface.ExecuteReader();
            }

            return Track(dataReader);
        }

        public IEnumerable<IDataRecord> DataRecordsFrom(Int64 firstRowId, int limit)
        {
            ThrowIfDisposed();
            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(
                ",",
                _fieldDefinitions.Select(
                    field => QuoteIdentifier(field.Key)));
            _commandBuilder.Append(fields);

            _commandBuilder.Append(" FROM ")
                .Append(
                    QuoteIdentifier(
                        _sourceTableName));
            _commandBuilder.Append(
                " WHERE id >= $firstRowId LIMIT $limit");

            _commandInterface.Parameters.Clear();
            _commandInterface.CommandText = _commandBuilder.ToString();
            _commandInterface.Parameters.AddWithValue(
                "$firstRowId",
                firstRowId);
            _commandInterface.Parameters.AddWithValue(
                "$limit",
                limit);
            var dataReader = _commandInterface.ExecuteReader();

            return Track(dataReader);
        }

        internal SqLtDataset(
            SqliteConnection connection,
            string sourceTableName,
            IDictionary<string, IFieldDefinition> fieldsToInclude,
            IReadOnlyList<SqLtSchemaColumn> columns,
            CaptureTextSource captureTextSource)
        {
            ArgumentNullException.ThrowIfNull(captureTextSource);
            _sourceTableName = sourceTableName;
            _commandInterface = new SqliteCommand();
            _commandInterface.Connection = connection;
            _commandBuilder = new StringBuilder();
            _fieldDefinitions = fieldsToInclude
                .Select((field, index) => new
                {
                    field.Key,
                    Definition = FieldDefinition.CreateFromSchema(
                        field.Key,
                        field.Value.ValueType == FieldValueType.Integer
                            ? typeof(long)
                            : typeof(string),
                        index + 1,
                        field.Value.IsExtension),
                })
                .ToDictionary(item => item.Key, item => (IFieldDefinition)item.Definition, StringComparer.Ordinal);
            Columns = columns;
            _untypedColumns = columns
                .Where(column => column.DeclaredType == SqLtDeclaredType.None)
                .Select(column => column.Name)
                .ToHashSet(StringComparer.Ordinal);
            CaptureTextSource = captureTextSource;
        }

        private IEnumerable<IFieldDefinition> GetFieldDefs()
        {
            ThrowIfDisposed();
            return _fieldDefinitions.Values;
        }

        private IEnumerable<IDataRecord> GetDataRecords()
        {
            ThrowIfDisposed();
            _commandBuilder.Clear();
            _commandBuilder.Append("SELECT DISTINCT id,");

            string fields = string.Join(
                ",",
                _fieldDefinitions.Select(
                    field => QuoteIdentifier(field.Key)));
            _commandBuilder.Append(fields);

            _commandBuilder.Append(" FROM ")
                .Append(
                    QuoteIdentifier(
                        _sourceTableName));

            _commandInterface.Parameters.Clear();
            _commandInterface.CommandText = _commandBuilder.ToString();
            var dataReader = _commandInterface.ExecuteReader();

            return Track(dataReader);
        }

        public void Dispose()
        {
            SqLtDataEnumerable[] active;
            lock (_enumerablesLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                active =
                    _activeEnumerables.ToArray();
                _activeEnumerables.Clear();
            }
            var failures = new List<Exception>();
            try
            {
                foreach (SqLtDataEnumerable enumerable in
                    active)
                {
                    try
                    {
                        enumerable.DisposeFromOwner();
                    }
                    catch (Exception exception) when (
                        !ExceptionUtilities.IsFatal(
                            exception))
                    {
                        failures.Add(exception);
                    }
                }
            }
            finally
            {
                try
                {
                    _commandInterface.Dispose();
                }
                catch (Exception exception) when (
                    !ExceptionUtilities.IsFatal(
                        exception))
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(
                    failures[0]).Throw();
            }
            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }

        private SqLtDataEnumerable Track(
            SqliteDataReader dataReader)
        {
            var enumerable = new SqLtDataEnumerable(
                _fieldDefinitions,
                _untypedColumns,
                dataReader,
                null,
                CaptureTextSource,
                disposed =>
                {
                    lock (_enumerablesLock)
                    {
                        _activeEnumerables.Remove(
                            disposed);
                    }
                });
            bool ownerDisposed;
            lock (_enumerablesLock)
            {
                ownerDisposed = _disposed;
                if (!ownerDisposed)
                {
                    _activeEnumerables.Add(
                        enumerable);
                }
            }
            if (ownerDisposed)
            {
                enumerable.DisposeFromOwner();
                ObjectDisposedException.ThrowIf(
                    ownerDisposed,
                    this);
            }
            return enumerable;
        }

        private void ThrowIfDisposed()
        {
            lock (_enumerablesLock)
            {
                ObjectDisposedException.ThrowIf(
                    _disposed,
                    this);
            }
        }

        private string _sourceTableName;
        private SqliteCommand _commandInterface;
        private StringBuilder _commandBuilder;
        private IDictionary<string, IFieldDefinition> _fieldDefinitions;
        private readonly IReadOnlySet<string> _untypedColumns;
        private readonly HashSet<SqLtDataEnumerable> _activeEnumerables =
            new(ReferenceEqualityComparer.Instance);
        private readonly object _enumerablesLock =
            new();
        private bool _disposed;

        private static string QuoteIdentifier(string value) =>
            "\"" +
            value.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal) +
            "\"";
    }
}
