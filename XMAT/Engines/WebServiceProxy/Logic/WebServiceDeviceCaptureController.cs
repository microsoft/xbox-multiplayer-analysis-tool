// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static XboxClient.XboxClientConnection;

namespace XMAT.WebServiceCapture
{
    public class WebServiceDeviceCaptureController : INotifyPropertyChanged, IDeviceCaptureController
    {
        class ControllerParameters : ICaptureMethodParameters
        {
            public UInt16 Port { get; set; }
            public bool PromptToDisableOnClose { get; set; }

            internal ControllerParameters()
            {
                Port = 0;
                PromptToDisableOnClose = true;
            }

            public void Serialized() { }
            public void DeserializeFrom(JsonElement serializedObject)
            {
                JsonElement prop;

                if (serializedObject.TryGetProperty(nameof(Port), out prop))
                {
                    Port = prop.GetUInt16();
                }

                if (serializedObject.TryGetProperty(nameof(PromptToDisableOnClose), out prop))
                {
                    PromptToDisableOnClose = prop.GetBoolean();
                }
            }
        }

        public bool IsRunning { get => _isRunning; set { _isRunning = value; RaisePropertyChange(); } }
        public UInt16 Port { get => _parameters.Port; set { _parameters.Port = value; RaisePropertyChange(); } }
        public bool PromptToDisableOnClose { get => _parameters.PromptToDisableOnClose; set { _parameters.PromptToDisableOnClose = value; RaisePropertyChange(); } }
        public bool IsPortValid { get => Port > 0; }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object Parameters { get => _parameters; }

        public ProxyConnectionsCollection ProxyConnections { get; internal set; }
        public ICaptureMethod CaptureMethod { get; internal set; }
        public ObservableCollection<CheckedListItem> StatusFilterList { get; set; }
        public ObservableCollection<CheckedListItem> HostFilterList { get; set; }
        public ObservableCollection<CheckedListItem> MethodFilterList { get; set; }
        public string DeviceName { get; }
        public ProxyConnectionModel SelectedConnectionModel
        {
            get => _selectedConnectionModel;
            set
            {
                _selectedConnectionModel = value;
                RaisePropertyChange();
            }
        }

        public ScriptCollection Scripts { get; set; }
        public ScriptTypeCollection ScriptTypes { get; set; }

        private const int LoadedCapturesPageBreakSize = 100;

        private ProxyConnectionModel _selectedConnectionModel;
        private readonly DeviceType _deviceType;
        private readonly bool _readOnly;
        private IWebServiceProxy _webProxy;
        private IDataTable _dataTable;
        private readonly object _lockObj = new object();

        public async Task<EProxyEnabledCheckResult> IsProxyEnabled()
        {
            EProxyEnabledCheckResult result = EProxyEnabledCheckResult.ConsoleUnreachable;
            if (_deviceType == DeviceType.XboxConsole)
            {
                if (!GDKXHelper.IsGDKXInstalled(false)) { return EProxyEnabledCheckResult.DeviceIsXbox_NoGDKX; }

                if (!string.IsNullOrEmpty(DeviceName))
                {
                    result = await CaptureUtilities.IsXboxProxyEnabledAsync(DeviceName);
                }
            }
            else if (_deviceType == DeviceType.LocalPC)
            {
                result = InternetProxy.IsEnabled() ? EProxyEnabledCheckResult.ProxyEnabled : EProxyEnabledCheckResult.ProxyDisabled;
            }

            return result;
        }

        public WebServiceDeviceCaptureController(string deviceName, DeviceType deviceType, bool readOnly)
        {
            DeviceName = deviceName;

            _deviceType = deviceType;
            _readOnly   = readOnly;
            _parameters = new();

            ProxyConnections = new ProxyConnectionsCollection();

            StatusFilterList = new ObservableCollection<CheckedListItem>();
            HostFilterList = new ObservableCollection<CheckedListItem>();
            MethodFilterList = new ObservableCollection<CheckedListItem>();
            Scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
            ScriptTypes = new ScriptTypeCollection(new Type[] { typeof(WebServiceCaptureScriptParams), typeof(ClientRequest), typeof(ServerResponse), typeof(HeaderCollection) });
        }

        public void Initialize()
        {
            CaptureMethod = WebServiceCaptureMethod.Method;
            _dataTable = (CaptureMethod as WebServiceCaptureMethod).WebProxyConnectionsTable;

            if(!_readOnly)
            {
                ProxyPortPool.Initialize(
                    (CaptureMethod.PreferencesModel as PreferencesModel).FirstPort,
                    (CaptureMethod.PreferencesModel as PreferencesModel).LastPort);

                if (!IsPortValid)
                {
                    Port = ProxyPortPool.ObtainPort();
                }

                PromptToDisableOnClose = (CaptureMethod.PreferencesModel as PreferencesModel).PromptToDisableOnClose;

                _webProxy = WebServiceProxy.CreateProxy();
                _webProxy.ReceivedSslConnectionRequest  += WebProxy_ReceivedSslConnectionRequestAsync;
                _webProxy.CompletedSslConnectionRequest += WebProxy_CompletedSslConnectionRequest;
                _webProxy.ReceivedWebRequest            += WebProxy_ReceivedWebRequestAsync;
                _webProxy.ReceivedWebResponse           += WebProxy_ReceivedWebResponseAsync;
            }
        }

        public async Task<bool> CanCloseAsync()
        {
            EProxyEnabledCheckResult proxyCheckResult = await IsProxyEnabled();
            if (proxyCheckResult == EProxyEnabledCheckResult.ProxyEnabled && _parameters.PromptToDisableOnClose)
            {
                DisableDeviceOnCloseWindow disableProxy = new()
                {
                    Owner = Application.Current.MainWindow,
                    DataContext = this
                };

                var confirmed = disableProxy.ShowDialog();

                if (confirmed == true)
                {
                    await DisableProxying();
                }
            }
            return true;
        }

        public void Close()
        {
            CancelExistingFiddlerImport();
            if(!_readOnly)
            {
                StopProxy();
                ProxyPortPool.ReleasePort(Port);
            }
            ClearAllCaptures();
        }

        public void LoadCaptures(IEnumerable<IDataset> tables)
        {
            using(var bo = PublicUtilities.BlockingOperation())
            {
                if (tables != null && tables.FirstOrDefault() != null)
                {
                    IEnumerable<IDataRecord> records = tables.First().DataRecordsWhen(
                        new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName,
                        DeviceName)
                        );

                    foreach (IDataRecord record in records)
                    {
                        ProxyConnections.AddFromDataRecord(record, true);
                    }
                }
            }
        }

        internal void ImportCapturesToTable(
            string deviceName,
            IDataTable dataTable,
            CancellationToken cancelToken)
        {
            if(ProxyConnections == null)
                return;

            foreach (var fiddlerProxyConnectionModel in ProxyConnections)
            {
                cancelToken.ThrowIfCancellationRequested();
                fiddlerProxyConnectionModel.AddToDataTable(deviceName, dataTable);
            }
        }

        internal void EnableProxyingPC()
        {
            switch (_deviceType)
            {
                case DeviceType.LocalPC:
                    InternetProxy.Enable("127.0.0.1", Port);
                    break;

                default:
                    throw new NotImplementedException(Localization.GetLocalizedString("PROXY_ERROR_DEVICE_TYPE_PC"));
            }
        }

        internal async Task<bool> EnableProxyingXbox(string sourceAddress)
        {
            if (!GDKXHelper.IsGDKXInstalled(true)) { return false; }

            EProxyEnabledCheckResult proxyCheckResult = await IsProxyEnabled();
            if (proxyCheckResult == EProxyEnabledCheckResult.ProxyEnabled)
            {
                MessageBox.Show(Localization.GetLocalizedString("PROXY_ALREADY_ENABLED"));
                // TODO(scmatlof): We should have some indication to the user that the proxy is already enabled
                return false;
            }

            switch (_deviceType)
            {
                case DeviceType.XboxConsole:
                    if (MessageBox.Show(
                        Localization.GetLocalizedString("PROXY_ERROR_RESTART_DESC"),
                        Localization.GetLocalizedString("PROXY_ERROR_RESTART_REQUIRED"),
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Exclamation) != MessageBoxResult.OK)
                    {
                        return false;
                    }

                    // TODO: async this, it hangs the UI if the Xbox is not connected
                    if (!await CaptureUtilities.EnableXboxProxyAsync(DeviceName, sourceAddress, Port))
                    {
                        MessageBox.Show(
                           Localization.GetLocalizedString("PROXY_ERROR_CONNECTION_DESC"),
                           Localization.GetLocalizedString("PROXY_ERROR_CONNECTION"),
                           MessageBoxButton.OK,
                           MessageBoxImage.Error);
                    }
                    break;

                default:
                    throw new NotImplementedException(Localization.GetLocalizedString("PROXY_ERROR_DEVICE_TYPE_XBOX"));
            }

            return true;
        }

        internal async Task DisableProxying()
        {
            if (!GDKXHelper.IsGDKXInstalled(true)) { return; }

            EProxyEnabledCheckResult proxyCheckResult = await IsProxyEnabled();
            if (proxyCheckResult != EProxyEnabledCheckResult.ProxyEnabled)
            {
                return;
            }
            switch (_deviceType)
            {
                case DeviceType.LocalPC:
                    InternetProxy.Disable();
                    break;

                case DeviceType.XboxConsole:
                    if (MessageBox.Show(
                        Localization.GetLocalizedString("PROXY_ERROR_RESTART_DESC"),
                        Localization.GetLocalizedString("PROXY_ERROR_RESTART_REQUIRED"),
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Exclamation) != MessageBoxResult.OK)
                    {
                        return;
                    }

                    // TODO: async this, it hangs the UI if the Xbox is not connected
                    if (!await CaptureUtilities .DisableXboxProxyAsync(DeviceName))
                    {
                        MessageBox.Show(
                           Localization.GetLocalizedString("PROXY_ERROR_CONNECTION_DESC"),
                           Localization.GetLocalizedString("PROXY_ERROR_CONNECTION"),
                           MessageBoxButton.OK,
                           MessageBoxImage.Error);
                        return;
                    }
                    break;

                default:
                    throw new NotImplementedException($"Unimplemented device {DeviceName}");
            }
        }

        internal void StartProxy()
        {
            var options = new WebServiceProxyOptions
            {
                LogFilePath = $"{DeviceName.Replace(" ", "")}-wspe.log",
                Port = Port
            };

            _webProxy.StartProxy(options);
            IsRunning = true;
        }

        internal void StopProxy()
        {
            _webProxy.StopProxy();
            IsRunning = false;
        }

        private void HandleRowAdded(Int64 rowId)
        {
            var records = _dataTable.Dataset.DataRecordsFrom(rowId, 1);
            var record = records.FirstOrDefault();
            if (record != null && record.Str(WebServiceCaptureMethod.FieldKey_DeviceName).Equals(
                DeviceName))
            {
                ProxyConnections.AddFromDataRecord(record, false);
            }
        }

        private void HandleRowUpdated(Int64 rowId)
        {
            var records = _dataTable.Dataset.DataRecordsFrom(rowId, 1);
            var record = records.FirstOrDefault();
            if (record != null && record.Str(WebServiceCaptureMethod.FieldKey_DeviceName).Equals(
                DeviceName))
            {
                ProxyConnections.UpdateFromDataRecord(record);
            }
        }

        private async void WebProxy_ReceivedSslConnectionRequestAsync(object sender, SslConnectionRequestEventArgs connectionEvent)
        {
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = connectionEvent.Request, Response = null };
                await Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest].RunScriptAsync(sp);
                connectionEvent.AcceptConnection = sp.Continue;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"SSL connection script error {e}");
            }

            string base64Body = string.Empty;
            if (connectionEvent.Request.BodyBytes != null)
                base64Body = Convert.ToBase64String(connectionEvent.Request.BodyBytes);

            // see layout in WebServiceCaptureMethod.InitializeDataTables()

            long rowId = _dataTable.AddRow(
                DeviceName,
                (long)connectionEvent.Request.RequestNumber,
                (long)connectionEvent.ConnectionID,
                ProxyConnectionModel.ToDateTimeString(connectionEvent.Timestamp),
                string.Empty,
                connectionEvent.Request.Scheme,
                connectionEvent.Request.Host,
                connectionEvent.Request.Port,
                connectionEvent.Request.Method,
                connectionEvent.Request.Path,
                string.Empty,
                connectionEvent.Request.FirstLineAndHeaders,
                base64Body,
                string.Empty,
                string.Empty,
                connectionEvent.ClientIP
            );

            PublicUtilities.SafeInvoke(() => HandleRowAdded(rowId));
        }

        private void WebProxy_CompletedSslConnectionRequest(object sender, SslConnectionCompletionEventArgs completionEvent)
        {
            var record = _dataTable.Dataset.DataRecordsWhen(
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, DeviceName),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, completionEvent.Request.RequestNumber),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_ConnectionId, completionEvent.ConnectionID)
            ).First();

            _dataTable.UpdateRow(
                record.RowId,
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, ProxyConnectionModel.ToDateTimeString(completionEvent.Timestamp)),
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_RequestStatus, @"200")
            );

            PublicUtilities.SafeInvoke(() => HandleRowUpdated(record.RowId));
        }

        private async void WebProxy_ReceivedWebRequestAsync(object sender, HttpRequestEventArgs requestEvent)
        {
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = requestEvent.Request, Response = null };
                await Scripts[WebServiceCaptureScriptableEventType.WebRequest].RunScriptAsync(sp);
                requestEvent.AcceptRequest = sp.Continue;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"WebRequest script error {e}");
            }

            string base64Body = string.Empty;
            if (requestEvent.Request.BodyBytes != null)
                base64Body = Convert.ToBase64String(requestEvent.Request.BodyBytes);

            // see layout in WebServiceCaptureMethod.InitializeDataTables()

            long rowId = _dataTable.AddRow(
                DeviceName,
                (long)requestEvent.Request.RequestNumber,
                (long)requestEvent.ConnectionID,
                ProxyConnectionModel.ToDateTimeString(requestEvent.Timestamp),
                string.Empty,
                requestEvent.Request.Scheme,
                requestEvent.Request.Host,
                requestEvent.Request.Port,
                requestEvent.Request.Method,
                requestEvent.Request.Path,
                string.Empty,
                requestEvent.Request.FirstLineAndHeaders,
                base64Body,
                string.Empty,
                string.Empty,
                DeviceName
            );

            PublicUtilities.SafeInvoke(() => HandleRowAdded(rowId));
        }

        private async void WebProxy_ReceivedWebResponseAsync(object sender, HttpResponseEventArgs responseEvent)
        {
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = responseEvent.Request, Response = responseEvent.Response };
                await Scripts[WebServiceCaptureScriptableEventType.WebResponse].RunScriptAsync(sp);
                responseEvent.SendResponse = sp.Continue;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"WebResponse script error {e}");
            }

            var record = _dataTable.Dataset.DataRecordsWhen(
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, DeviceName),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, responseEvent.Response.RequestNumber),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_ConnectionId, responseEvent.ConnectionID)
            ).FirstOrDefault();

            if(record != null)
            {
                var base64Body = Convert.ToBase64String(responseEvent.Response.BodyBytes);

                _dataTable.UpdateRow(
                    record.RowId,
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, ProxyConnectionModel.ToDateTimeString(responseEvent.Timestamp)),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_RequestStatus, responseEvent.Response.Status),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders, responseEvent.Response.FirstLineAndHeaders),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseBody, base64Body)
                );

                PublicUtilities.SafeInvoke(() => HandleRowUpdated(record.RowId));
            }
        }

        public void ClearAllCaptures()
        {
            ProxyConnections.RemoveAll();
            _dataTable.RemoveRowsWhere(
                new FieldValue<string>(
                    WebServiceCaptureMethod.FieldKey_DeviceName,
                    DeviceName)
                );
            _webProxy?.Reset();
        }

        public void ImportFromFiddler(string fromFilePath)
        {
            using (var cancelTokenSource = new CancellationTokenSource())
            {
                _fiddlerAsyncImportCancelTokenSource = cancelTokenSource;
                _importResetEvent.Reset();
                try
                {
                    var cancelToken = _fiddlerAsyncImportCancelTokenSource.Token;

                    // convert what we can from a Fiddler file into a bunch of
                    // proxy connection models...
                    ProxyConnections = FiddlerSazHandler.ImportSazFile(fromFilePath, cancelToken);

                    // ...and then do the actual adding to the table
                    ImportCapturesToTable(
                        DeviceName,
                        _dataTable,
                        cancelToken);

                    // update the 3 filter sets
                    var list = (from p in ProxyConnections select p.Host).Distinct().ToList();
                    HostFilterList = UpdateFilter(list);
                    list = (from p in ProxyConnections select p.Status).Distinct().ToList();
                    StatusFilterList = UpdateFilter(list);
                    list = (from p in ProxyConnections select p.Method).Distinct().ToList();
                    MethodFilterList = UpdateFilter(list);
                }
                catch (OperationCanceledException)
                {
                    // we do not need to do anything...
                }
                finally
                {
                    lock (_lockObj)
                    {
                        _fiddlerAsyncImportCancelTokenSource = null;
                    }
                    _importResetEvent.Set();
                }
            }
        }

        public ObservableCollection<CheckedListItem> UpdateFilter(IList<string> list)
        {
            var cli = list.Select(l => new CheckedListItem() { IsChecked = true, Text = l });
            return new ObservableCollection<CheckedListItem>(cli.ToList());
        }

        public void CancelExistingFiddlerImport()
        {
            lock (_lockObj)
            {
                if (_fiddlerAsyncImportCancelTokenSource != null)
                {
                    _fiddlerAsyncImportCancelTokenSource.Cancel();
                }
            }

            // we usually want to do some cleanup, or another import, right away
            // so we will wait until the cancellation completes
            _importResetEvent.WaitOne();
        }

        private ManualResetEvent _importResetEvent = new ManualResetEvent(true);
        private CancellationTokenSource _fiddlerAsyncImportCancelTokenSource;
        private bool _isRunning;
        private ControllerParameters _parameters;

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
