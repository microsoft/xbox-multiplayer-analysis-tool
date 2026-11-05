// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Export
{
    internal sealed class XmatcapExportWriter : ICaptureExportWriter
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SqliteCommand _captureInsert;
        private readonly SqliteCommand _deviceInsert;
        private readonly IReadOnlyList<SqLtSchemaColumn> _captureFields;
        private readonly IReadOnlyList<SqLtSchemaColumn> _deviceFields;
        private readonly Dictionary<string, RawExportRow> _deferredDescriptors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _describedDevices = new(StringComparer.Ordinal);
        private readonly HashSet<string> _capturedDevices = new(StringComparer.Ordinal);
        private readonly bool _includeOnlyCapturedDevices;
        private bool _completed;
        private long _rowsWritten;

        internal XmatcapExportWriter(
            string path,
            IReadOnlyList<SqLtSchemaColumn> captureFields,
            IReadOnlyList<SqLtSchemaColumn> deviceFields,
            IReadOnlyDictionary<string, RawExportRow> deviceDescriptors,
            bool includeOnlyCapturedDevices = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(captureFields);
            ArgumentNullException.ThrowIfNull(deviceFields);
            ArgumentNullException.ThrowIfNull(deviceDescriptors);
            if (File.Exists(path))
            {
                throw new IOException("The staged export path already exists.");
            }

            _captureFields = captureFields;
            _deviceFields = deviceFields;
            _includeOnlyCapturedDevices = includeOnlyCapturedDevices;
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            try
            {
                _connection.Open();
                _transaction = _connection.BeginTransaction();
                CreateTable("CaptureDevices", deviceFields);
                CreateTable(WebServiceCaptureMethod.TableName, captureFields);
                _deviceInsert = CreateInsert("CaptureDevices", deviceFields);
                _captureInsert = CreateInsert(WebServiceCaptureMethod.TableName, captureFields);
                foreach (RawExportRow descriptor in deviceDescriptors.Values)
                {
                    if (descriptor.Values.TryGetValue("DeviceName", out object value) && value is string name)
                    {
                        if (_includeOnlyCapturedDevices)
                        {
                            _deferredDescriptors.TryAdd(name, descriptor);
                        }
                        else
                        {
                            Insert(_deviceInsert, deviceFields, descriptor);
                            _describedDevices.Add(name);
                        }
                    }
                    else if (!_includeOnlyCapturedDevices)
                    {
                        Insert(_deviceInsert, deviceFields, descriptor);
                    }
                }
            }
            catch (Exception exception) when (IsWriteFailure(exception))
            {
                Dispose();
                throw new CaptureExportException(
                    CaptureExportErrorCodes.WriteFailed,
                    OperationOutcome.Failed,
                    exception);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Write(RawExportRow row, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed)
            {
                throw new InvalidOperationException("The export writer is complete.");
            }
            try
            {
                Insert(_captureInsert, _captureFields, row);
                if (row.Values.TryGetValue(
                    WebServiceCaptureMethod.FieldKey_DeviceName,
                    out object value) &&
                    value is string name)
                {
                    _capturedDevices.Add(name);
                }
                _rowsWritten++;
            }
            catch (Exception exception) when (IsWriteFailure(exception))
            {
                throw new CaptureExportException(
                    CaptureExportErrorCodes.WriteFailed,
                    OperationOutcome.Failed,
                    exception);
            }
        }

        public ExportWriteSummary Complete(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed)
            {
                throw new InvalidOperationException("The export writer is complete.");
            }
            try
            {
                if (_includeOnlyCapturedDevices)
                {
                    foreach (string deviceName in _capturedDevices)
                    {
                        if (_deferredDescriptors.TryGetValue(deviceName, out RawExportRow descriptor))
                        {
                            Insert(_deviceInsert, _deviceFields, descriptor);
                            _describedDevices.Add(deviceName);
                        }
                    }
                }
                foreach (string deviceName in _capturedDevices.Except(_describedDevices, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Insert(
                        _deviceInsert,
                        _deviceFields,
                        new RawExportRow(
                            default,
                            0,
                            0,
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["DeviceType"] = (long)DeviceType.Unknown,
                                ["DeviceName"] = deviceName,
                                ["CaptureType"] = (long)CaptureType.WebProxy,
                            }),
                        automaticRowId: true);
                }
                _transaction.Commit();
                _completed = true;
                return new ExportWriteSummary(_rowsWritten, null);
            }
            catch (Exception exception) when (IsWriteFailure(exception))
            {
                throw new CaptureExportException(
                    CaptureExportErrorCodes.WriteFailed,
                    OperationOutcome.Failed,
                    exception);
            }
        }

        public void Dispose()
        {
            _captureInsert?.Dispose();
            _deviceInsert?.Dispose();
            _transaction?.Dispose();
            _connection?.Dispose();
        }

        private void CreateTable(string name, IReadOnlyList<SqLtSchemaColumn> fields)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = $"CREATE TABLE {Quote(name)}(id INTEGER PRIMARY KEY,{string.Join(
                ",",
                fields.Select(field => $"{Quote(field.Name)} {SqlType(field.DeclaredType)}"))})";
            command.ExecuteNonQuery();
        }

        private SqliteCommand CreateInsert(string table, IReadOnlyList<SqLtSchemaColumn> fields)
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            string[] names = fields.Select((_, index) => $"$v{index}").ToArray();
            command.CommandText = $"INSERT INTO {Quote(table)}(id,{string.Join(
                ",",
                fields.Select(field => Quote(field.Name)))}) VALUES($id,{string.Join(",", names)})";
            SqliteParameter id = command.CreateParameter();
            id.ParameterName = "$id";
            command.Parameters.Add(id);
            foreach (string name in names)
            {
                SqliteParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        private static void Insert(
            SqliteCommand command,
            IReadOnlyList<SqLtSchemaColumn> fields,
            RawExportRow row,
            bool automaticRowId = false)
        {
            command.Parameters["$id"].Value = automaticRowId ? DBNull.Value : row.RowId;
            for (var index = 0; index < fields.Count; index++)
            {
                command.Parameters[index + 1].Value =
                    row.Values.TryGetValue(fields[index].Name, out object value)
                        ? value ?? DBNull.Value
                        : DBNull.Value;
            }
            command.ExecuteNonQuery();
        }

        private static string SqlType(SqLtDeclaredType type) =>
            type switch
            {
                SqLtDeclaredType.Integer => "INTEGER",
                SqLtDeclaredType.Text => "TEXT",
                _ => string.Empty,
            };

        private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

        private static bool IsWriteFailure(Exception exception) =>
            exception is SqliteException or IOException or UnauthorizedAccessException;
    }
}
