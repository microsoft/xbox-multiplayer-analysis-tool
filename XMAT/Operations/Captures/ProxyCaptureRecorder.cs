// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Operations.Captures
{
    internal sealed class ProxyCaptureRecorder : IDisposable
    {
        private readonly object _gate = new();
        private readonly SqliteConnection _connection;
        private long _bytes;
        internal long Records { get; private set; }
        internal TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ProxyCaptureRecorder(string path)
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            try
            {
                _connection.Open();
                Execute(
                    "CREATE TABLE CaptureDevices (id INTEGER PRIMARY KEY, DeviceType INTEGER, DeviceName TEXT, " +
                    "CaptureType INTEGER, NetworkShaping TEXT)");
                Execute(
                    $"CREATE TABLE {WebServiceCaptureMethod.TableName} (id INTEGER PRIMARY KEY," +
                    string.Join(",", WebProxyCaptureSchema.Fields.Select(field =>
                        $"\"{field.Name}\" {(field.ValueType == typeof(long) ? "INTEGER" : "TEXT")}")) + ")");
                Execute("INSERT INTO CaptureDevices VALUES (1, 0, 'Proxy capture', 0, NULL)");
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE CaptureDevices SET DeviceType=$device, CaptureType=$capture";
                command.Parameters.AddWithValue("$device", (long)SharedInterfaces.DeviceType.GenericProxyDevice);
                command.Parameters.AddWithValue("$capture", (long)SharedInterfaces.CaptureType.WebProxy);
                command.ExecuteNonQuery();
            }
            catch
            {
                _connection.Dispose();
                throw;
            }
        }

        internal void Attach(WebServiceProxy proxy)
        {
            proxy.ReceivedSslConnectionRequest += (_, args) =>
                args.AcceptConnection &= RecordRequest(args.Request, args.Timestamp, args.ConnectionID, args.ClientIP);
            proxy.ReceivedWebRequest += (_, args) =>
                args.AcceptRequest &= RecordRequest(args.Request, args.Timestamp, args.ConnectionID, string.Empty);
            proxy.ReceivedWebResponse += (_, args) => args.SendResponse &= Guard(() =>
            {
                ServerResponse response = args.Response;
                string body = Convert.ToBase64String(response.BodyBytes ?? Array.Empty<byte>());
                CheckBudget(response.FirstLineAndHeaders.Length + body.Length);
                using var command = _connection.CreateCommand();
                command.CommandText = $"UPDATE {WebServiceCaptureMethod.TableName} SET " +
                    $"\"{WebServiceCaptureMethod.FieldKey_ResponseTimestamp}\"=$time, " +
                    $"\"{WebServiceCaptureMethod.FieldKey_RequestStatus}\"=$status, " +
                    $"\"{WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders}\"=$headers, " +
                    $"\"{WebServiceCaptureMethod.FieldKey_ResponseBody}\"=$body WHERE id=$id";
                command.Parameters.AddWithValue("$time", Time(args.Timestamp));
                command.Parameters.AddWithValue("$status", response.Status);
                command.Parameters.AddWithValue("$headers", response.FirstLineAndHeaders);
                command.Parameters.AddWithValue("$body", body);
                command.Parameters.AddWithValue("$id", args.Request.RequestNumber);
                command.ExecuteNonQuery();
            });
            proxy.CompletedSslConnectionRequest += (_, args) => Guard(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"UPDATE {WebServiceCaptureMethod.TableName} SET " +
                    $"\"{WebServiceCaptureMethod.FieldKey_ResponseTimestamp}\"=$time, " +
                    $"\"{WebServiceCaptureMethod.FieldKey_RequestStatus}\"='200' WHERE id=$id";
                command.Parameters.AddWithValue("$time", Time(args.Timestamp));
                command.Parameters.AddWithValue("$id", args.Request.RequestNumber);
                command.ExecuteNonQuery();
            });
        }

        private bool RecordRequest(ClientRequest request, DateTime timestamp, int connection, string clientIp) => Guard(() =>
        {
            if (Records >= 100_000)
            {
                throw new InvalidOperationException("Capture record limit reached.");
            }
            var values = new Dictionary<string, object>
            {
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Proxy capture",
                [WebServiceCaptureMethod.FieldKey_RequestNumber] = (long)request.RequestNumber,
                [WebServiceCaptureMethod.FieldKey_ConnectionId] = (long)connection,
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = Time(timestamp),
                [WebServiceCaptureMethod.FieldKey_RequestScheme] = request.Scheme,
                [WebServiceCaptureMethod.FieldKey_RequestHost] = request.Host,
                [WebServiceCaptureMethod.FieldKey_RequestPort] = request.Port,
                [WebServiceCaptureMethod.FieldKey_RequestMethod] = request.Method,
                [WebServiceCaptureMethod.FieldKey_RequestPath] = request.Path,
                [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] = request.FirstLineAndHeaders,
                [WebServiceCaptureMethod.FieldKey_RequestBody] =
                    Convert.ToBase64String(request.BodyBytes ?? Array.Empty<byte>()),
                [WebServiceCaptureMethod.FieldKey_ClientIP] = clientIp,
            };
            object[] fields = WebProxyCaptureSchema.Project(values, ClientProcessInfo.NotRecorded);
            CheckBudget(fields.OfType<string>().Sum(value => (long)Encoding.UTF8.GetByteCount(value)));
            using var command = _connection.CreateCommand();
            command.CommandText = $"INSERT INTO {WebServiceCaptureMethod.TableName} VALUES ($id," +
                string.Join(",", fields.Select((_, index) => $"$v{index}")) + ")";
            command.Parameters.AddWithValue("$id", request.RequestNumber);
            for (int index = 0; index < fields.Length; index++)
            {
                command.Parameters.AddWithValue($"$v{index}", fields[index] ?? DBNull.Value);
            }
            command.ExecuteNonQuery();
            Records++;
        });

        internal void RecordShaping(NetworkShapingMetadata metadata)
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE CaptureDevices SET NetworkShaping=$value";
                command.Parameters.AddWithValue("$value", (object)NetworkShapingMetadata.Serialize(metadata) ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private bool Guard(Action action)
        {
            lock (_gate)
            {
                if (Failure.Task.IsCompleted)
                {
                    return false;
                }
                try
                {
                    action();
                    return true;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    Failure.TrySetResult();
                    return false;
                }
            }
        }

        private void CheckBudget(long bytes)
        {
            if (bytes > 64L * 1024 * 1024 || (_bytes += bytes) > 512L * 1024 * 1024)
            {
                throw new InvalidOperationException("Capture data limit reached.");
            }
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static string Time(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        public void Dispose() => _connection.Dispose();
    }
}
