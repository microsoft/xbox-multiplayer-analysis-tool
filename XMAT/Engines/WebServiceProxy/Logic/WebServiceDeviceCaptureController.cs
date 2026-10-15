// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using static XboxClient.XboxClientConnection;

namespace XMAT.WebServiceCapture
{
    public class WebServiceDeviceCaptureController : INotifyPropertyChanged, IDeviceCaptureController
    {
        private enum CaptureDataSource
        {
            ActiveTable,
            LoadedDataset
        }

        class ControllerParameters : ICaptureMethodParameters
        {
            public UInt16 Port { get; set; }

            internal ControllerParameters()
            {
                Port = 0;
            }

            public void Serialized() { }
            public void DeserializeFrom(JsonElement serializedObject)
            {
                JsonElement prop;

                if (serializedObject.TryGetProperty(nameof(Port), out prop))
                {
                    Port = prop.GetUInt16();
                }
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning == value)
                {
                    return;
                }

                _isRunning = value;
                RaisePropertyChange();
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public bool IsTransitioning
        {
            get => _isTransitioning;
            internal set
            {
                if (_isTransitioning == value)
                {
                    return;
                }

                _isTransitioning = value;
                RaisePropertyChange();
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public UInt16 Port { get => _parameters.Port; set { _parameters.Port = value; RaisePropertyChange(); } }
        public bool IsPortValid { get => Port > 0; }
        internal bool CanBeginTransition => !IsRunning && !IsTransitioning;
        internal bool CanBeginStopTransition => IsRunning && !IsTransitioning;

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
        public WebSocketMessagesCollection WebSocketMessages { get; }
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
        public BlockListModel BlockList { get; set; }
        public BypassListModel BypassList { get; set; }
        public WebSocketMessageModel SelectedWebSocketMessage
        {
            get => _selectedWebSocketMessage;
            set
            {
                _selectedWebSocketMessage = value;
                RaisePropertyChange();
            }
        }

        private const int LoadedCapturesPageBreakSize = 100;
        private const int UiUpdateBatchSize = 50;
        private static readonly TimeSpan CaptureCallbackDrainTimeout =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProgressUpdateInterval =
            TimeSpan.FromMilliseconds(100);

        private ProxyConnectionModel _selectedConnectionModel;
        private WebSocketMessageModel _selectedWebSocketMessage;
        private readonly DeviceType _deviceType;
        private readonly bool _readOnly;
        private IWebServiceProxy _webProxy;
        private IDataTable _dataTable;
        private IDataset _loadedCaptureDataset;
        private CaptureDataSource _captureDataSource = CaptureDataSource.ActiveTable;
        private readonly object _lockObj = new object();
        private readonly AsyncOperationTracker _captureCallbacks = new();
        private readonly BatchedDispatcherQueue _uiUpdateQueue;
        private CancellationTokenSource _captureCallbackCancellationTokenSource;
        private bool _isTransitioning;

        public async Task<EProxyEnabledCheckResult> IsProxyEnabled()
        {
            EProxyEnabledCheckResult result = EProxyEnabledCheckResult.ConsoleUnreachable;
            if (_deviceType == DeviceType.XboxConsole)
            {
                if (!GDKXHelper.IsGDKXInstalled(false))
                { return EProxyEnabledCheckResult.DeviceIsXbox_NoGDKX; }

                if (!string.IsNullOrEmpty(DeviceName))
                {
                    result = await CaptureUtilities.IsXboxProxyEnabledAsync(DeviceName);
                }
            }
            else if (_deviceType == DeviceType.LocalPC)
            {
                result = InternetProxy.IsEnabled() ? EProxyEnabledCheckResult.ProxyEnabled : EProxyEnabledCheckResult.ProxyDisabled;
            }
            else if (_deviceType == DeviceType.GenericProxyDevice)
            {
                result = EProxyEnabledCheckResult.ProxyingGenericDevice;
            }

            return result;
        }

        public WebServiceDeviceCaptureController(string deviceName, DeviceType deviceType, bool readOnly)
        {
            DeviceName = deviceName;

            _deviceType = deviceType;
            _readOnly = readOnly;
            _parameters = new();

            ProxyConnections = new ProxyConnectionsCollection();

            StatusFilterList = new ObservableCollection<CheckedListItem>();
            HostFilterList = new ObservableCollection<CheckedListItem>();
            MethodFilterList = new ObservableCollection<CheckedListItem>();
            WebSocketMessages = new WebSocketMessagesCollection();
            Scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
            ScriptTypes = new ScriptTypeCollection(new Type[] { typeof(WebServiceCaptureScriptParams), typeof(ClientRequest), typeof(ServerResponse), typeof(HeaderCollection) });
            BlockList = new BlockListModel();
            BypassList = new BypassListModel();
            _uiUpdateQueue = new BatchedDispatcherQueue(
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
                UiUpdateBatchSize,
                ex => PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Capture UI update failed: {ex}"));
        }

        public void Initialize()
        {
            CaptureMethod = WebServiceCaptureMethod.Method;
            _dataTable = (CaptureMethod as WebServiceCaptureMethod).WebProxyConnectionsTable;

            if (!_readOnly)
            {
                ProxyPortPool.Initialize(
                    (CaptureMethod.PreferencesModel as PreferencesModel).FirstPort,
                    (CaptureMethod.PreferencesModel as PreferencesModel).LastPort);

                if (!IsPortValid)
                {
                    Port = ProxyPortPool.ObtainPort();
                }

                _webProxy = WebServiceProxy.CreateProxy();
                _webProxy.BypassList = BypassList;
                _webProxy.ReceivedSslConnectionRequest += WebProxy_ReceivedSslConnectionRequestAsync;
                _webProxy.CompletedSslConnectionRequest += WebProxy_CompletedSslConnectionRequest;
                _webProxy.ReceivedWebRequest += WebProxy_ReceivedWebRequestAsync;
                _webProxy.ReceivedWebResponse += WebProxy_ReceivedWebResponseAsync;
                _webProxy.WebSocketMessage += WebProxy_WebSocketMessage;
            }
        }

        public Task<bool> CanCloseAsync()
        {
            return CanCloseAsync(IsProxyEnabled, ShowDisableProxyPrompt, DisableProxying);
        }

        internal async Task<bool> CanCloseAsync(
            Func<Task<EProxyEnabledCheckResult>> checkProxyEnabledAsync,
            Func<bool?> confirmDisableProxy,
            Func<Task> disableProxyAsync)
        {
            if (_readOnly || !((PreferencesModel)CaptureMethod.PreferencesModel).PromptToDisableOnClose)
            {
                return true;
            }

            var proxyCheckResult = await checkProxyEnabledAsync();
            if (proxyCheckResult == EProxyEnabledCheckResult.ProxyEnabled && confirmDisableProxy() == true)
            {
                await disableProxyAsync();
            }

            return true;
        }

        private bool? ShowDisableProxyPrompt()
        {
            var disableProxy = new DisableDeviceOnCloseWindow
            {
                Owner = Application.Current.MainWindow,
                DataContext = this
            };

            return disableProxy.ShowDialog();
        }

        public async Task CloseAsync()
        {
            CancelExistingFiddlerImport();
            if (!_readOnly)
            {
                await StopProxyAsync();
                ProxyPortPool.ReleasePort(Port);
            }
            ClearAllCaptures();
        }

        public void LoadCaptures(IEnumerable<IDataset> tables)
        {
            using (var bo = PublicUtilities.BlockingOperation())
            {
                LoadCaptureDataset(tables?.FirstOrDefault());
            }
        }

        internal void LoadCaptureDataset(IDataset dataset)
        {
            ClearCaptureViewModels();
            _captureDataSource = CaptureDataSource.LoadedDataset;
            _loadedCaptureDataset = dataset;
            if (_loadedCaptureDataset != null)
            {
                foreach (IDataRecord record in GetCaptureRecords())
                {
                    ProxyConnections.AddFromDataRecord(record, true);
                }
            }
        }

        internal IEnumerable<IDataRecord> GetCaptureRecords()
        {
            var dataset = _captureDataSource == CaptureDataSource.LoadedDataset
                ? _loadedCaptureDataset
                : _dataTable?.Dataset;
            if (dataset == null)
            {
                return Enumerable.Empty<IDataRecord>();
            }

            return dataset.DataRecordsWhen(
                new FieldValue<string>(
                    WebServiceCaptureMethod.FieldKey_DeviceName,
                    DeviceName));
        }

        internal void ImportCapturesToTable(
            string deviceName,
            IDataTable dataTable,
            CancellationToken cancelToken)
        {
            PrepareForActiveCaptureData();

            if (ProxyConnections == null)
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
            if (!GDKXHelper.IsGDKXInstalled(true))
            { return false; }

            EProxyEnabledCheckResult proxyCheckResult = await IsProxyEnabled();
            if (proxyCheckResult == EProxyEnabledCheckResult.ProxyEnabled)
            {
                MessageBox.Show(Localization.GetLocalizedString("PROXY_ALREADY_ENABLED"));
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
            switch (_deviceType)
            {
                case DeviceType.LocalPC:
                    InternetProxy.Disable();
                    return;

                case DeviceType.GenericProxyDevice:
                    return;
            }

            if (!GDKXHelper.IsGDKXInstalled(true))
            { return; }

            EProxyEnabledCheckResult proxyCheckResult = await IsProxyEnabled();
            if (proxyCheckResult != EProxyEnabledCheckResult.ProxyEnabled)
            {
                return;
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
                        return;
                    }

                    // TODO: async this, it hangs the UI if the Xbox is not connected
                    if (!await CaptureUtilities.DisableXboxProxyAsync(DeviceName))
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

        public void ShowGenericProxyDetails()
        {
            // TODO: Support machine with multiple local IPs
            IEnumerable<IPAddress> arrIPAddrs = PublicUtilities.GetMyIp4Addresses();
            MessageBox.Show(Localization.GetLocalizedString("GENERIC_DEVICE_PROXY_INFO_MESSAGE", arrIPAddrs.First().ToString(), Port, PublicUtilities.StorageDirectoryPath), Localization.GetLocalizedString("GENERIC_DEVICE_PROXY_INFO_CAPTION"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        internal async Task StartProxyAsync(bool bShowProxyInformation)
        {
            var options = new WebServiceProxyOptions
            {
                LogFilePath = $"{DeviceName.Replace(" ", "")}-wspe.log",
                Port = Port
            };

            _captureCallbacks.StartAccepting();
            try
            {
                _uiUpdateQueue.StartAccepting();
            }
            catch
            {
                await _captureCallbacks.StopAcceptingAndWaitAsync();
                throw;
            }
            _captureCallbackCancellationTokenSource?.Dispose();
            _captureCallbackCancellationTokenSource = new CancellationTokenSource();
            try
            {
                _webProxy.StartProxy(options);
            }
            catch
            {
                var callbacksDrained = _captureCallbacks.StopAcceptingAndWaitAsync();
                _captureCallbackCancellationTokenSource.Cancel();
                await _webProxy.StopProxyAsync();
                await WaitForCaptureCallbacksAsync(
                    callbacksDrained,
                    null,
                    Stopwatch.StartNew());
                await _uiUpdateQueue.CompleteAndDrainAsync();
                DisposeCaptureCallbackCancellation();
                throw;
            }

            IsRunning = true;

            if (bShowProxyInformation)
            {
                ShowGenericProxyDetails();
            }
        }

        internal async Task StopProxyAsync(
            IProgress<WebProxyStopProgress> progress = null)
        {
            var stopStopwatch = Stopwatch.StartNew();
            var callbacksDrained = _captureCallbacks.StopAcceptingAndWaitAsync();
            _captureCallbackCancellationTokenSource?.Cancel();
            Exception proxyStopException = null;
            try
            {
                await _webProxy.StopProxyAsync(progress);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                proxyStopException = ex;
            }

            await WaitForCaptureCallbacksAsync(
                callbacksDrained,
                progress,
                stopStopwatch);

            var uiDrainStopwatch = Stopwatch.StartNew();
            var uiUpdatesDrained = _uiUpdateQueue.CompleteAndDrainAsync();
            await WaitForUiUpdatesAsync(
                uiUpdatesDrained,
                progress,
                stopStopwatch);
            PublicUtilities.AppLog(
                LogLevel.INFO,
                $"Capture UI updates drained in {uiDrainStopwatch.Elapsed}.");

            DisposeCaptureCallbackCancellation();
            if (proxyStopException != null)
            {
                ExceptionDispatchInfo.Capture(proxyStopException).Throw();
            }

            IsRunning = false;
            PublicUtilities.AppLog(
                LogLevel.INFO,
                $"Web proxy capture stopped in {stopStopwatch.Elapsed}.");
        }

        private async Task WaitForCaptureCallbacksAsync(
            Task callbacksDrained,
            IProgress<WebProxyStopProgress> progress,
            Stopwatch stopStopwatch)
        {
            var callbackStopwatch = Stopwatch.StartNew();
            var timeoutTask = Task.Delay(CaptureCallbackDrainTimeout);
            try
            {
                while (!callbacksDrained.IsCompleted)
                {
                    progress?.Report(new WebProxyStopProgress(
                        WebProxyStopPhase.WaitingForCallbacks,
                        stopStopwatch.Elapsed,
                        _captureCallbacks.ActiveOperationCount,
                        _uiUpdateQueue.PendingCount));

                    var completed = await Task.WhenAny(
                        callbacksDrained,
                        timeoutTask,
                        Task.Delay(ProgressUpdateInterval));
                    if (completed == timeoutTask)
                    {
                        throw new TimeoutException();
                    }
                }

                await callbacksDrained;
                progress?.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.WaitingForCallbacks,
                    stopStopwatch.Elapsed,
                    0,
                    _uiUpdateQueue.PendingCount));
                PublicUtilities.AppLog(
                    LogLevel.INFO,
                    $"Capture callbacks drained in {callbackStopwatch.Elapsed}.");
            }
            catch (TimeoutException ex)
            {
                var timeoutSeconds = (int)CaptureCallbackDrainTimeout.TotalSeconds;
                throw new TimeoutException(
                    Localization.GetLocalizedString(
                        "PROXY_CAPTURE_CALLBACK_TIMEOUT",
                        timeoutSeconds),
                    ex);
            }
        }

        private async Task WaitForUiUpdatesAsync(
            Task uiUpdatesDrained,
            IProgress<WebProxyStopProgress> progress,
            Stopwatch stopStopwatch)
        {
            while (!uiUpdatesDrained.IsCompleted)
            {
                progress?.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.DrainingUiUpdates,
                    stopStopwatch.Elapsed,
                    0,
                    _uiUpdateQueue.PendingCount));

                await Task.WhenAny(
                    uiUpdatesDrained,
                    Task.Delay(ProgressUpdateInterval));
            }

            await uiUpdatesDrained;
            progress?.Report(new WebProxyStopProgress(
                WebProxyStopPhase.DrainingUiUpdates,
                stopStopwatch.Elapsed,
                0,
                0));
        }

        private void DisposeCaptureCallbackCancellation()
        {
            _captureCallbackCancellationTokenSource?.Dispose();
            _captureCallbackCancellationTokenSource = null;
        }

        internal IDisposable BeginTransition()
        {
            if (!CanBeginTransition)
            {
                throw new InvalidOperationException("The proxy cannot begin another transition.");
            }

            IsTransitioning = true;
            return new TransitionLease(this);
        }

        internal IDisposable BeginStopTransition()
        {
            if (!CanBeginStopTransition)
            {
                throw new InvalidOperationException(
                    "The proxy cannot begin a stop transition.");
            }

            IsTransitioning = true;
            return new TransitionLease(this);
        }

        private IDataRecord DataRecordFromRow(Int64 rowId)
        {
            var records = _dataTable.Dataset.DataRecordsFrom(rowId, 1);
            return records.FirstOrDefault();
        }

        private void HandleRowAdded(IDataRecord record)
        {
            if (record != null && record.Str(WebServiceCaptureMethod.FieldKey_DeviceName).Equals(
                DeviceName))
            {
                ProxyConnections.AddFromDataRecord(record, false);
            }
        }

        private void HandleRowUpdated(IDataRecord record)
        {
            if (record != null && record.Str(WebServiceCaptureMethod.FieldKey_DeviceName).Equals(
                DeviceName))
            {
                ProxyConnections.UpdateFromDataRecord(record);
            }
        }

        private bool IsRequestBlocked(ClientRequest request)
        {
            if (BlockList == null || request == null)
                return false;

            string host = request.Host;
            string path = request.Path;

            if (string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(path))
            {
                // Check if this is an absolute-form URI (e.g., GET http://example.com/path HTTP/1.1)
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri absoluteUri))
                {
                    host = absoluteUri.Host;
                    path = absoluteUri.PathAndQuery;
                }
                // Otherwise, assume CONNECT request where Path is "host:port"
                else
                {
                    int colon = path.IndexOf(':');
                    host = colon > 0 ? path.Substring(0, colon) : path;
                    path = null;
                }
            }

            return BlockList.IsBlocked(host, path);
        }

        private async void WebProxy_ReceivedSslConnectionRequestAsync(object sender, SslConnectionRequestEventArgs connectionEvent)
        {
            if (!_captureCallbacks.TryBegin(out var callbackLease))
            {
                return;
            }

            using var callbackLeaseScope = callbackLease;
            var cancellationToken =
                _captureCallbackCancellationTokenSource?.Token ??
                CancellationToken.None;
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = connectionEvent.Request, Response = null };
                await Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest].RunScriptAsync(
                    sp,
                    cancellationToken);
                connectionEvent.AcceptConnection = sp.Continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                connectionEvent.AcceptConnection = false;
                return;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"SSL connection script error {e}");
            }

            if (connectionEvent.AcceptConnection && IsRequestBlocked(connectionEvent.Request))
            {
                PublicUtilities.AppLog(LogLevel.INFO,
                    $"Blocked SSL CONNECT to '{connectionEvent.Request.Host ?? connectionEvent.Request.Path}' (BlockList)");
                connectionEvent.AcceptConnection = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                connectionEvent.AcceptConnection = false;
                return;
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

            var addedRecord = DataRecordFromRow(rowId);
            _uiUpdateQueue.TryEnqueue(() => HandleRowAdded(addedRecord));
        }

        private void WebProxy_CompletedSslConnectionRequest(object sender, SslConnectionCompletionEventArgs completionEvent)
        {
            if (!_captureCallbacks.TryBegin(out var callbackLease))
            {
                return;
            }

            using var callbackLeaseScope = callbackLease;
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

            var updatedRecord = DataRecordFromRow(record.RowId);
            _uiUpdateQueue.TryEnqueue(() => HandleRowUpdated(updatedRecord));
        }

        private async void WebProxy_ReceivedWebRequestAsync(object sender, HttpRequestEventArgs requestEvent)
        {
            if (!_captureCallbacks.TryBegin(out var callbackLease))
            {
                return;
            }

            using var callbackLeaseScope = callbackLease;
            var cancellationToken =
                _captureCallbackCancellationTokenSource?.Token ??
                CancellationToken.None;
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = requestEvent.Request, Response = null };
                await Scripts[WebServiceCaptureScriptableEventType.WebRequest].RunScriptAsync(
                    sp,
                    cancellationToken);
                requestEvent.AcceptRequest = sp.Continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                requestEvent.AcceptRequest = false;
                return;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"WebRequest script error {e}");
            }

            if (requestEvent.AcceptRequest && IsRequestBlocked(requestEvent.Request))
            {
                PublicUtilities.AppLog(LogLevel.INFO,
                    $"Blocked {requestEvent.Request.Method} {requestEvent.Request.Host}{requestEvent.Request.Path} (BlockList)");
                requestEvent.AcceptRequest = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                requestEvent.AcceptRequest = false;
                return;
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

            var addedRecord = DataRecordFromRow(rowId);
            _uiUpdateQueue.TryEnqueue(() => HandleRowAdded(addedRecord));
        }

        private async void WebProxy_ReceivedWebResponseAsync(object sender, HttpResponseEventArgs responseEvent)
        {
            if (!_captureCallbacks.TryBegin(out var callbackLease))
            {
                return;
            }

            using var callbackLeaseScope = callbackLease;
            var cancellationToken =
                _captureCallbackCancellationTokenSource?.Token ??
                CancellationToken.None;
            try
            {
                // Vars passed into the script are modified for realsies...so if a user modifies the Request/Response object in the script, the real Request/Response object is changed here in the app
                var sp = new WebServiceCaptureScriptParams() { Request = responseEvent.Request, Response = responseEvent.Response };
                await Scripts[WebServiceCaptureScriptableEventType.WebResponse].RunScriptAsync(
                    sp,
                    cancellationToken);
                responseEvent.SendResponse = sp.Continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                responseEvent.SendResponse = false;
                return;
            }
            catch (Exception e)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"WebResponse script error {e}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                responseEvent.SendResponse = false;
                return;
            }

            var record = _dataTable.Dataset.DataRecordsWhen(
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, DeviceName),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, responseEvent.Response.RequestNumber),
                new FieldValue<long>(WebServiceCaptureMethod.FieldKey_ConnectionId, responseEvent.ConnectionID)
            ).FirstOrDefault();

            if (record != null)
            {
                var base64Body = Convert.ToBase64String(responseEvent.Response.BodyBytes);

                _dataTable.UpdateRow(
                    record.RowId,
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, ProxyConnectionModel.ToDateTimeString(responseEvent.Timestamp)),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_RequestStatus, responseEvent.Response.Status),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders, responseEvent.Response.FirstLineAndHeaders),
                    new FieldValue<string>(WebServiceCaptureMethod.FieldKey_ResponseBody, base64Body)
                );

                var updatedRecord = DataRecordFromRow(record.RowId);
                _uiUpdateQueue.TryEnqueue(() => HandleRowUpdated(updatedRecord));
            }
        }

        private void WebProxy_WebSocketMessage(object sender, WebSocketMessageEventArgs messageEvent)
        {
            if (!_captureCallbacks.TryBegin(out var callbackLease))
            {
                return;
            }

            using var callbackLeaseScope = callbackLease;
            _uiUpdateQueue.TryEnqueue(() =>
            {
                var message = new WebSocketMessageModel(messageEvent);
                WebSocketMessages.AddMessage(message);
                SelectedWebSocketMessage = message;
            });
        }

        public void ClearAllCaptures()
        {
            ClearCaptureViewModels();
            if (_captureDataSource == CaptureDataSource.ActiveTable)
            {
                _dataTable?.RemoveRowsWhere(
                    new FieldValue<string>(
                        WebServiceCaptureMethod.FieldKey_DeviceName,
                        DeviceName)
                    );
            }
            _loadedCaptureDataset = null;
            _webProxy?.Reset();
        }

        internal void PrepareForActiveCaptureData()
        {
            _captureDataSource = CaptureDataSource.ActiveTable;
            _loadedCaptureDataset = null;
        }

        private void ClearCaptureViewModels()
        {
            ProxyConnections.RemoveAll();
            WebSocketMessages.Clear();
            SelectedWebSocketMessage = null;
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

        private sealed class TransitionLease : IDisposable
        {
            private WebServiceDeviceCaptureController _owner;

            internal TransitionLease(WebServiceDeviceCaptureController owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner != null)
                {
                    owner.IsTransitioning = false;
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
