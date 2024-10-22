// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using static XMAT.WebServiceCapture.Proxy.WebServiceProxy;

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

            EInitializationResult initResult = WebServiceProxy.Initialize(PublicUtilities.StorageDirectoryPath);


			if (initResult == EInitializationResult.FAILED_CERT_INSTALLATION_CANCELLED_OR_FAILED)
            {
				MessageBox.Show(Localization.GetLocalizedString("PROXY_ROOT_CERT_INSTALL_CANCEL_OR_ERROR"), Localization.GetLocalizedString("PROXY_ROOT_CERT_WARNING_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error);
			}
			else if (initResult == EInitializationResult.FAILED_GENERIC)
            {
				MessageBox.Show(Localization.GetLocalizedString("GENERIC_DEVICE_PROXY_INFO_ERROR_MESSAGE"), Localization.GetLocalizedString("GENERIC_DEVICE_PROXY_INFO_CAPTION"), MessageBoxButton.OK, MessageBoxImage.Error);

			}
		}

        public void Shutdown()
        {
            InternetProxy.Disable();
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
            // get the controller for this imported data...
            var captureController = context.CaptureController as WebServiceDeviceCaptureController;

            if (captureController == null)
            {
                throw new InvalidOperationException("WebServiceCaptureMethod does not have a corresponding WebServiceDeviceCaptureController.");
            }

            // TODO: if the controller is currently importing, then cancel if the user does not
            // wish to interrupt it

            // destroy existing captures for that controller and cancel an existing import
            // and wait until that is done...
            captureController.CancelExistingFiddlerImport();
            captureController.ClearAllCaptures();

            // ...then kick off another import
            using(var bo = PublicUtilities.BlockingOperation())
            {
                await Task.Run(() => captureController.ImportFromFiddler(fromFilePath));
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
