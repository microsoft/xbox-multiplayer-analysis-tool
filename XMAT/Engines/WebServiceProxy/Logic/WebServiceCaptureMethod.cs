// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
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

            if (dataTable is not IImportedCaptureTable importTable)
            {
                throw new InvalidOperationException("The capture table does not support atomic imports.");
            }

            captureController.CancelExistingFiddlerImport();

            var imported = await Task.Run(() =>
            {
                var connections = FiddlerSazHandler.ImportSazFile(
                    fromFilePath,
                    CancellationToken.None);
                var hostFilters = captureController.UpdateFilter(
                    connections.Select(connection => connection.Host).Distinct().ToList());
                var statusFilters = captureController.UpdateFilter(
                    connections.Select(connection => connection.Status).Distinct().ToList());
                var methodFilters = captureController.UpdateFilter(
                    connections.Select(connection => connection.Method).Distinct().ToList());
                var rows = connections.Select(
                    connection => connection.ToDataTableValues(captureController.DeviceName));
                var rowIds = importTable.ReplaceImportedRows(
                    new FieldValue<string>(FieldKey_DeviceName, captureController.DeviceName),
                    rows);
                for (int index = 0; index < connections.Count; index++)
                {
                    connections[index].RecordRowId = rowIds[index];
                }
                return (Connections: connections, HostFilters: hostFilters,
                    StatusFilters: statusFilters, MethodFilters: methodFilters);
            });

            captureController.PrepareForActiveCaptureData();
            captureController.ProxyConnections = imported.Connections;
            captureController.HostFilterList = imported.HostFilters;
            captureController.StatusFilterList = imported.StatusFilters;
            captureController.MethodFilterList = imported.MethodFilters;
            TryNotifyCaptureReplacement(
                () => captureController.RaisePropertyChange(
                    nameof(WebServiceDeviceCaptureController.HostFilterList)),
                logError);
            TryNotifyCaptureReplacement(
                () => captureController.RaisePropertyChange(
                    nameof(WebServiceDeviceCaptureController.StatusFilterList)),
                logError);
            TryNotifyCaptureReplacement(
                () => captureController.RaisePropertyChange(
                    nameof(WebServiceDeviceCaptureController.MethodFilterList)),
                logError);
            TryNotifyCaptureReplacement(
                () => captureController.SelectedConnectionModel = null,
                logError);
            TryNotifyCaptureReplacement(
                () => captureController.RaisePropertyChange(
                    nameof(WebServiceDeviceCaptureController.ProxyConnections)),
                logError);
        }

        private static void TryNotifyCaptureReplacement(Action notification, Action<string> logError)
        {
            try
            {
                notification();
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
                    PublicUtilities.AppLog(LogLevel.ERROR, message);
                }
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
