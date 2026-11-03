// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    public class WebServiceCaptureMethod : ICaptureMethod
    {
        private ICaptureAppModel _captureAppModel;

        public static ICaptureMethod Method { get { return MethodInstance; } }

        internal static readonly WebServiceCaptureMethod MethodInstance = new();

        internal IDataTable WebProxyConnectionsTable { get; private set; }

        public const string TableName = @"WebProxyConnections";
        public const string FieldKey_DeviceName = @"DeviceName";
        public const string FieldKey_RequestNumber = @"RequestNumber";
        public const string FieldKey_ConnectionId = @"ConnectionId";
        public const string FieldKey_RequestTimestamp = @"RequestTimestamp";
        public const string FieldKey_ResponseTimestamp = @"ResponseTimestamp";
        public const string FieldKey_RequestScheme = @"RequestScheme";
        public const string FieldKey_RequestHost = @"RequestHost";
        public const string FieldKey_RequestPort = @"RequestPort";
        public const string FieldKey_RequestMethod = @"RequestMethod";
        public const string FieldKey_RequestPath = @"RequestPath";
        public const string FieldKey_RequestStatus = @"RequestStatus";
        public const string FieldKey_RequestLineAndHeaders = @"RequestLineAndHeaders";
        public const string FieldKey_RequestBody = @"RequestBody";
        public const string FieldKey_ResponseLineAndHeaders = @"ResponseLineAndHeaders";
        public const string FieldKey_ResponseBody = @"ResponseBody";
        public const string FieldKey_ClientIP = @"ClientIP";

        public ICaptureMethodParameters PreferencesModel { get; }

        public void Initialize(ICaptureAppModel appModel)
        {
            _captureAppModel = appModel;

            InitializeDataTables();

            if (!WebServiceProxy.Initialize(PublicUtilities.StorageDirectoryPath))
            {
                MessageBox.Show(@"Failed to initialize WebServiceProxy!");
            }
        }

        public void Shutdown()
        {
            // Undo only this process's own, unchanged Local PC proxy; never a proxy XMAT did not set.
            if (PcProxyRecovery.Current.RestoreOnShutdown() == RecoveryResult.Failed)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, "Could not restore the Windows proxy settings during shutdown.");
            }
        }

        public bool OwnsDataTable(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && tableName.Equals(TableName);
        }

        private WebServiceCaptureMethod()
        {
            PreferencesModel = new PreferencesModel();
        }

        private void InitializeDataTables()
        {
            // TODO: expose these constants
            WebProxyConnectionsTable = _captureAppModel.ActiveDatabase.CreateTable(
                TableName,
                new Field<string>(FieldKey_DeviceName),
                new Field<Int64>(FieldKey_RequestNumber),
                new Field<Int64>(FieldKey_ConnectionId),
                new Field<string>(FieldKey_RequestTimestamp),
                new Field<string>(FieldKey_ResponseTimestamp),
                new Field<string>(FieldKey_RequestScheme),
                new Field<string>(FieldKey_RequestHost),
                new Field<string>(FieldKey_RequestPort),
                new Field<string>(FieldKey_RequestMethod),
                new Field<string>(FieldKey_RequestPath),
                new Field<string>(FieldKey_RequestStatus),
                new Field<string>(FieldKey_RequestLineAndHeaders),
                new Field<string>(FieldKey_RequestBody),
                new Field<string>(FieldKey_ResponseLineAndHeaders),
                new Field<string>(FieldKey_ResponseBody),
                new Field<string>(FieldKey_ClientIP)
            );
        }

        public async Task ImportFromFiddlerAsync(string fromFilePath, ICaptureDeviceContext context)
        {
            using (PublicUtilities.BlockingOperation())
            {
                await ImportFromFiddlerAsync(fromFilePath, context, WebProxyConnectionsTable);
            }
        }

        internal async Task ImportFromFiddlerAsync(
            string fromFilePath,
            ICaptureDeviceContext context,
            IDataTable dataTable,
            Action<string> logError = null)
        {
            var captureController = context.CaptureController as WebServiceDeviceCaptureController;

            if (captureController == null)
            {
                throw new InvalidOperationException("WebServiceCaptureMethod does not have a corresponding WebServiceDeviceCaptureController.");
            }

            if (dataTable is not IImportedCaptureTable)
            {
                throw new InvalidOperationException("The capture table does not support atomic imports.");
            }

            captureController.CancelExistingFiddlerImport();

            var imported = await Task.Run(() =>
            {
                var prepared = PrepareFiddlerImport(fromFilePath, captureController, long.MaxValue, CancellationToken.None);
                PersistFiddlerImport(prepared, dataTable, captureController.DeviceName, long.MaxValue, CancellationToken.None);
                return prepared;
            });

            captureController.ApplyImportedCaptures(imported, dataTable);
            NotifyFiddlerImport(captureController, logError);
        }

        internal static WebServiceDeviceCaptureController.ImportedCaptures PrepareFiddlerImport(
            string path, WebServiceDeviceCaptureController controller, long maximumBytes, CancellationToken cancellationToken)
        {
            var connections = FiddlerSazHandler.ImportSazFile(path, maximumBytes, cancellationToken);
            return new(connections,
                controller.UpdateFilter(connections.Select(connection => connection.Host).Distinct().ToList()),
                controller.UpdateFilter(connections.Select(connection => connection.Status).Distinct().ToList()),
                controller.UpdateFilter(connections.Select(connection => connection.Method).Distinct().ToList()));
        }

        internal static void PersistFiddlerImport(
            WebServiceDeviceCaptureController.ImportedCaptures prepared, IDataTable table, string deviceName,
            long maximumBytes, CancellationToken cancellationToken)
        {
            IEnumerable<object[]> Rows()
            {
                var remaining = maximumBytes;
                foreach (var connection in prepared.Connections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = connection.ToDataTableValues(deviceName);
                    long size = 64;
                    foreach (var value in row)
                        size += value is string text ? Encoding.UTF8.GetByteCount(text) + (long)text.Count(c => c == '"') + 16 : 24;
                    if (size > remaining)
                        throw new QueryException(QueryError.ResultLimit, "The imported rows exceed the approved data limit.");
                    remaining -= size;
                    yield return row;
                }
            }
            var ids = ((IImportedCaptureTable)table).ReplaceImportedRows(
                new FieldValue<string>(FieldKey_DeviceName, deviceName), Rows(), cancellationToken);
            for (var index = 0; index < prepared.Connections.Count; index++)
                prepared.Connections[index].RecordRowId = ids[index];
        }

        internal static bool NotifyFiddlerImport(WebServiceDeviceCaptureController controller, Action<string> logError = null)
        {
            var notified = true;
            foreach (var property in new[]
            {
                nameof(WebServiceDeviceCaptureController.HostFilterList),
                nameof(WebServiceDeviceCaptureController.StatusFilterList),
                nameof(WebServiceDeviceCaptureController.MethodFilterList),
                nameof(WebServiceDeviceCaptureController.SelectedConnectionModel),
                nameof(WebServiceDeviceCaptureController.ProxyConnections)
            })
                notified &= TryNotifyCaptureReplacement(() => controller.RaisePropertyChange(property), logError);
            return notified;
        }

        private static bool TryNotifyCaptureReplacement(Action notification, Action<string> logError)
        {
            try
            {
                notification();
                return true;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                string message = $"Failed to notify the capture view after import: {ex}";
                if (logError != null)
                {
                    logError(message);
                }
                else
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Failed to notify the capture view after import: {ex.GetType().Name}");
                }
                return false;
            }
        }

        public void ExportToFiddler(string toFilePath)
        {
            var list = new List<ProxyConnectionModel>();
            if (WebProxyConnectionsTable != null)
            {
                var records = WebProxyConnectionsTable.Dataset.DataRecords;
                foreach (var record in records)
                {
                    var m = new ProxyConnectionModel(record);
                    list.Add(m);
                }
            }

            FiddlerSazHandler.CreateSazFile(toFilePath, list);
        }
    }
}
