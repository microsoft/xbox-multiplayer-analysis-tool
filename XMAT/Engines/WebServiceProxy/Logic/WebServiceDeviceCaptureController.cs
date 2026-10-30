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
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
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

        private enum ScriptPolicyResult
        {
            Completed,
            FailedOpen,
            Aborted
        }

        class ControllerParameters : ICaptureMethodParameters
        {
            private readonly WebServiceDeviceCaptureController _owner;
            public UInt16 Port { get; internal set; }
            public UInt16 PendingPort { get; internal set; }
            public string ProxyHost { get; internal set; }

            internal ControllerParameters(WebServiceDeviceCaptureController owner)
            {
                _owner = owner;
                Port = 0;
            }

            public void Serialized() { }
            public void DeserializeFrom(JsonElement serializedObject)
            {
                if (_owner._initialized || _owner._portReservation != null ||
                    _owner._pendingPortReservation != null || _owner.IsRunning || _owner.IsTransitioning)
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_USE_CHANGE"));
                }
                JsonElement prop;

                if (serializedObject.TryGetProperty(nameof(Port), out prop))
                {
                    Port = prop.GetUInt16();
                }
                if (serializedObject.TryGetProperty(nameof(PendingPort), out prop))
                {
                    PendingPort = prop.GetUInt16();
                }
                if (serializedObject.TryGetProperty(nameof(ProxyHost), out prop))
                {
                    ProxyHost = prop.GetString();
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
                RaisePropertyChange(nameof(CaptureStatusText));
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
        public UInt16 Port
        {
            get => _parameters.Port;
            set
            {
                if (_initialized || _portReservation != null || IsRunning || IsTransitioning)
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_USE_CHANGE"));
                }
                _parameters.Port = value;
                RaisePropertyChange();
                RaisePropertyChange(nameof(PortDescription));
            }
        }
        public UInt16 PendingPort => _parameters.PendingPort;
        public bool HasPendingPort => PendingPort != 0;
        public string PortStatus { get => _portStatus; private set { _portStatus = value; RaisePropertyChange(); } }
        public string ImportNotice { get; private set; }
        public string CaptureStatusText => Localization.GetLocalizedString(
            _readOnly ? "PROXY_CAPTURE_READ_ONLY" : IsRunning ? "PROXY_CAPTURE_RUNNING" : "PROXY_CAPTURE_STOPPED");
        public string PortDescription => Localization.GetLocalizedString(
            _readOnly ? "PROXY_PORT_NONE" : IsPortValid ? "PROXY_PORT_DESCRIPTION" : "PROXY_PORT_UNASSIGNED", Port);
        public bool IsPortChangeInProgress => _portChangeCancellation != null;
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
        // The application's session host lists; stable for the controller's lifetime.
        public HostListPolicy HostListPolicy { get; }
        public BlockListModel BlockList => HostListPolicy.BlockList;
        public BypassListModel BypassList => HostListPolicy.BypassList;
        public WebSocketMessageModel SelectedWebSocketMessage
        {
            get => _selectedWebSocketMessage;
            set
            {
                _selectedWebSocketMessage = value;
                RaisePropertyChange();
            }
        }

        internal void SetImportNotice(string notice)
        {
            if (ImportNotice == notice)
            {
                return;
            }

            ImportNotice = notice;
            RaisePropertyChange(nameof(ImportNotice));
        }

        private const int LoadedCapturesPageBreakSize = 100;
        private const int UiUpdateBatchSize = 50;
        private static readonly TimeSpan CaptureCallbackDrainTimeout =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultScriptExecutionDeadline =
            TimeSpan.FromSeconds(60);
        // A failure probe must not hold a transition indefinitely; late results are ignored.
        private static readonly TimeSpan DefaultEnableFailureProbeTimeout =
            TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProgressUpdateInterval =
            TimeSpan.FromMilliseconds(100);

        private ProxyConnectionModel _selectedConnectionModel;
        private WebSocketMessageModel _selectedWebSocketMessage;
        private readonly DeviceType _deviceType;
        private readonly bool _readOnly;
        private readonly TimeSpan _scriptExecutionDeadline;
        private readonly TimeSpan _enableFailureProbeTimeout;
        private readonly Func<IEnumerable<IPAddress>> _getClassifierIp4Addresses;
        private IWebServiceProxy _webProxy;
        private IDataTable _dataTable;
        private IDataset _loadedCaptureDataset;
        private CaptureDataSource _captureDataSource = CaptureDataSource.ActiveTable;
        private readonly object _lockObj = new object();
        private readonly AsyncOperationTracker _captureCallbacks = new();
        private readonly BatchedDispatcherQueue _uiUpdateQueue;
        private CancellationTokenSource _captureCallbackCancellationTokenSource;
        private bool _isTransitioning;
        private bool _initialized;
        private readonly ProxyPortPool _portPool;
        private readonly IProxyDeviceConfiguration _deviceConfiguration;
        private readonly Func<IWebServiceProxy> _proxyFactory;
        private ProxyPortPool.Reservation _portReservation;
        private ProxyPortPool.Reservation _pendingPortReservation;
        private string _portStatus;
        private Task _portChangeTask;
        private CancellationTokenSource _portChangeCancellation;

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
            : this(deviceName, deviceType, readOnly, ProxyPortPool.Shared)
        {
        }

        // The optional classifier provider must return every assigned unicast IPv4 address and throw on failure.
        // A null policy gives a standalone controller its own isolated lists; the application
        // passes its shared policy so it is in place before Initialize wires the proxy.
        internal WebServiceDeviceCaptureController(
            string deviceName, DeviceType deviceType, bool readOnly, ProxyPortPool portPool,
            IProxyDeviceConfiguration deviceConfiguration = null,
            TimeSpan? scriptExecutionDeadline = null,
            Func<IEnumerable<IPAddress>> getClassifierIp4Addresses = null,
            TimeSpan? enableFailureProbeTimeout = null,
            HostListPolicy hostListPolicy = null,
            Func<IWebServiceProxy> proxyFactory = null)
        {
            _scriptExecutionDeadline =
                scriptExecutionDeadline ?? DefaultScriptExecutionDeadline;
            if (_scriptExecutionDeadline <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(scriptExecutionDeadline));
            }
            _enableFailureProbeTimeout =
                enableFailureProbeTimeout ?? DefaultEnableFailureProbeTimeout;
            if (_enableFailureProbeTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(enableFailureProbeTimeout));
            }

            _portPool = portPool;
            _deviceConfiguration = deviceConfiguration;
            _getClassifierIp4Addresses =
                getClassifierIp4Addresses ?? GetAssignedUnicastIp4Addresses;
            _proxyFactory = proxyFactory ?? (() => WebServiceProxy.CreateProxy());
            HostListPolicy = hostListPolicy ?? new HostListPolicy();
            DeviceName = deviceName;

            _deviceType = deviceType;
            _readOnly = readOnly;
            _parameters = new(this);

            ProxyConnections = new ProxyConnectionsCollection();

            StatusFilterList = new ObservableCollection<CheckedListItem>();
            HostFilterList = new ObservableCollection<CheckedListItem>();
            MethodFilterList = new ObservableCollection<CheckedListItem>();
            WebSocketMessages = new WebSocketMessagesCollection();
            Scripts = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
            ScriptTypes = new ScriptTypeCollection(new Type[] { typeof(WebServiceCaptureScriptParams), typeof(ClientRequest), typeof(ServerResponse), typeof(HeaderCollection) });
            _uiUpdateQueue = new BatchedDispatcherQueue(
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
                UiUpdateBatchSize,
                ex => PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Capture UI update failed: {ex}"));
        }

        public void Initialize() => Initialize(false);

        internal void Initialize(bool deferPortAllocation)
        {
            if (_initialized)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"));
            }
            _initialized = true;
            CaptureMethod = WebServiceCaptureMethod.Method;
            _dataTable = (CaptureMethod as WebServiceCaptureMethod).WebProxyConnectionsTable;

            if (!_readOnly)
            {
                RestoreCurrentPort(deferPortAllocation);

                try
                {
                    _webProxy = _proxyFactory();
                }
                catch
                {
                    _portReservation?.Dispose();
                    _portReservation = null;
                    throw;
                }
                _webProxy.BypassList = BypassList;
                _webProxy.SslConnectionRequestPolicyAsync = WebProxy_ReceivedSslConnectionRequestAsync;
                _webProxy.CompletedSslConnectionRequest += WebProxy_CompletedSslConnectionRequest;
                _webProxy.WebRequestPolicyAsync = WebProxy_ReceivedWebRequestAsync;
                _webProxy.WebResponsePolicyAsync = WebProxy_ReceivedWebResponseAsync;
                _webProxy.WebSocketMessage += WebProxy_WebSocketMessage;
                if (!deferPortAllocation)
                {
                    RestorePendingPort();
                }
            }
        }

        internal void RestoreCurrentPort(bool deferAllocation)
        {
            if (_readOnly || _portReservation != null || (Port == 0 && deferAllocation))
            {
                return;
            }
            try
            {
                _portReservation = Port == 0
                    ? _portPool.ObtainPort(DeviceName)
                    : _portPool.ReservePort(Port, DeviceName);
                _parameters.Port = _portReservation.Port;
                RaisePropertyChange(nameof(Port));
                RaisePropertyChange(nameof(PortDescription));
                PortStatus = null;
            }
            catch (InvalidOperationException ex)
            {
                PortStatus = ex.Message;
                PublicUtilities.AppLog(LogLevel.ERROR, $"Cannot reserve proxy port for {DeviceName}: {ex}");
            }
        }

        internal void RestorePendingPort()
        {
            if (_readOnly || !HasPendingPort || _pendingPortReservation != null)
            {
                return;
            }
            if (PendingPort == Port)
            {
                _parameters.PendingPort = 0;
                return;
            }
            try
            {
                _pendingPortReservation = _portPool.ReservePort(PendingPort, DeviceName);
                PortStatus = Localization.GetLocalizedString("PROXY_PORT_PENDING", PendingPort);
            }
            catch (InvalidOperationException ex)
            {
                PortStatus = ex.Message;
                PublicUtilities.AppLog(LogLevel.ERROR, $"Cannot restore pending proxy port for {DeviceName}: {ex}");
            }
        }

        internal void QueuePortChange(UInt16 port, Action save)
        {
            if (_readOnly || !CanBeginTransition)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_STOP_FIRST"));
            }
            if (port == Port)
            {
                DiscardPortChange(save);
                return;
            }
            var replacement = _pendingPortReservation?.Port == port
                ? _pendingPortReservation : _portPool.ReservePort(port, DeviceName);
            var previous = _pendingPortReservation;
            var previousPort = PendingPort;
            _parameters.PendingPort = port;
            try
            {
                save();
            }
            catch
            {
                _parameters.PendingPort = previousPort;
                if (!ReferenceEquals(previous, replacement))
                {
                    replacement.Dispose();
                }
                throw;
            }
            _pendingPortReservation = replacement;
            if (!ReferenceEquals(previous, replacement))
            {
                previous?.Dispose();
            }
            NotifyPortState();
            PortStatus = Localization.GetLocalizedString("PROXY_PORT_PENDING", port);
        }

        internal void DiscardPortChange(Action save)
        {
            if (_readOnly || IsRunning || IsTransitioning)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_STOP_FIRST"));
            }
            var previous = PendingPort;
            _parameters.PendingPort = 0;
            try
            {
                save();
            }
            catch
            {
                _parameters.PendingPort = previous;
                throw;
            }
            _pendingPortReservation?.Dispose();
            _pendingPortReservation = null;
            PortStatus = null;
            NotifyPortState();
        }

        private void NotifyPortState()
        {
            RaisePropertyChange(nameof(Port));
            RaisePropertyChange(nameof(PortDescription));
            RaisePropertyChange(nameof(PendingPort));
            RaisePropertyChange(nameof(HasPendingPort));
            RaisePropertyChange(nameof(IsPortChangeInProgress));
            CommandManager.InvalidateRequerySuggested();
        }

        private bool OwnsEndpoint(ProxyEndpoint endpoint) =>
            endpoint != null &&
            endpoint.Port == Port &&
            (string.Equals(ProxyEndpoint.NormalizeHost(endpoint.Host),
                ProxyEndpoint.NormalizeHost(_parameters.ProxyHost), StringComparison.OrdinalIgnoreCase) ||
             (_deviceType == DeviceType.LocalPC && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address)) ||
             PublicUtilities.GetMyIp4Addresses().Any(ip => ip.ToString() == endpoint.Host));

        // Unlike the UI address picker, classification must include gateway-less virtual, host-only and VPN adapters.
        private static IEnumerable<IPAddress> GetAssignedUnicastIp4Addresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(networkInterface =>
                    networkInterface.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily ==
                    System.Net.Sockets.AddressFamily.InterNetwork)
                .ToArray();

        internal IProxyDeviceConfiguration CreateDeviceConfiguration() =>
            _deviceConfiguration ?? (_deviceType switch
            {
                DeviceType.XboxConsole => new XboxProxyDeviceConfiguration(DeviceName),
                DeviceType.LocalPC => new PcProxyDeviceConfiguration(),
                _ => throw new NotSupportedException(Localization.GetLocalizedString("PROXY_PORT_MANUAL"))
            });

        internal async Task ApplyPortChangeAsync(
            string host, Action save, bool manualUpdateConfirmed = false)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new ArgumentException(Localization.GetLocalizedString("PROXY_NO_LOCAL_ADDRESS"), nameof(host));
            }
            using var transition = BeginTransition();
            if (_readOnly || _pendingPortReservation == null || PendingPort != _pendingPortReservation.Port)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_NO_PENDING"));
            }
            using var target = await ProxyDeviceOperations.AcquireAsync(_deviceType, DeviceName);
            using var cancellation = new CancellationTokenSource();
            _portChangeCancellation = cancellation;
            NotifyPortState();
            try
            {
                _portChangeTask = ApplyPortChangeCoreAsync(host, save, manualUpdateConfirmed, cancellation.Token);
                await _portChangeTask;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PortStatus = Localization.GetLocalizedString(
                    "PROXY_PORT_APPLY_FAILED",
                    Port,
                    CaptureUtilities.GetProxyOperationErrorMessage(ex));
                throw;
            }
            finally
            {
                _portChangeTask = null;
                _portChangeCancellation = null;
                NotifyPortState();
            }
        }

        internal void CancelPortChange() => _portChangeCancellation?.Cancel();

        private async Task ApplyPortChangeCoreAsync(
            string host, Action save, bool manualUpdateConfirmed, CancellationToken token)
        {
            var desired = new ProxyEndpoint(host, PendingPort);
            async Task Start()
            {
                if (!CaptureAppModel.AppModel.TryBeginCaptureDataOperation(out var lease))
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"));
                }
                using (lease)
                {
                    await StopProxyAsync();
                    await StartProxyCoreAsync(false, desired.Port);
                }
            }
            void Commit()
            {
                var previous = Port;
                var previousHost = _parameters.ProxyHost;
                _parameters.Port = desired.Port;
                _parameters.PendingPort = 0;
                _parameters.ProxyHost = host;
                try
                {
                    save();
                }
                catch
                {
                    _parameters.Port = previous;
                    _parameters.PendingPort = desired.Port;
                    _parameters.ProxyHost = previousHost;
                    throw;
                }
                _portReservation?.Dispose();
                _portReservation = _pendingPortReservation;
                _pendingPortReservation = null;
                NotifyPortState();
            }
            if (_deviceType == DeviceType.GenericProxyDevice)
            {
                if (!manualUpdateConfirmed)
                {
                    throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_MANUAL"));
                }
                await Start();
                try
                {
                    token.ThrowIfCancellationRequested();
                    Commit();
                    PortStatus = Localization.GetLocalizedString("PROXY_PORT_MANUAL");
                }
                catch
                {
                    await TryStopProxyAfterFailedDeviceSetupAsync();
                    throw;
                }
                return;
            }
            var operation = new ProxyPortChangeOperation(
                CreateDeviceConfiguration(), Start, () => StopProxyAsync(), Commit,
                key => PortStatus = Localization.GetLocalizedString(key));
            await operation.ApplyAsync(OwnsEndpoint, desired, token);
        }

        public async Task<bool> CanCloseAsync()
        {
            if (IsTransitioning)
            {
                MessageBox.Show(Localization.GetLocalizedString("PROXY_PORT_BUSY_CLOSE"));
                return false;
            }
            var disabled = true;
            var result = await CanCloseAsync(IsProxyEnabled, ShowDisableProxyPrompt,
                async () => { disabled = await DisableProxying(); });
            return result && disabled;
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
            if (proxyCheckResult == EProxyEnabledCheckResult.ProxyEnabled)
            {
                var configuration = await CreateDeviceConfiguration().ReadAsync();
                if (configuration.State == ProxyConfigurationState.Enabled &&
                    OwnsEndpoint(configuration.Endpoint) && confirmDisableProxy() == true)
                {
                    await disableProxyAsync();
                }
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
            if (IsTransitioning)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_BUSY_CLOSE"));
            }
            CancelExistingFiddlerImport();
            if (!_readOnly)
            {
                await StopProxyAsync();
                _portReservation?.Dispose();
                _pendingPortReservation?.Dispose();
                _portReservation = null;
                _pendingPortReservation = null;
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
            StatusFilterList = UpdateFilter(
                ProxyConnections
                    .Select(connection => connection.Status)
                    .Distinct()
                    .ToList());
            HostFilterList = UpdateFilter(
                ProxyConnections
                    .Select(connection => connection.Host)
                    .Distinct()
                    .ToList());
            MethodFilterList = UpdateFilter(
                ProxyConnections
                    .Select(connection => connection.Method)
                    .Distinct()
                    .ToList());
            NotifyPropertyChangesSafely(
                nameof(StatusFilterList),
                nameof(HostFilterList),
                nameof(MethodFilterList));
        }

        internal void ReplaceLoadedCapturesFrom(
            WebServiceDeviceCaptureController source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _captureDataSource = source._captureDataSource;
            _loadedCaptureDataset = source._loadedCaptureDataset;
            ProxyConnections = source.ProxyConnections;
            StatusFilterList = source.StatusFilterList;
            HostFilterList = source.HostFilterList;
            MethodFilterList = source.MethodFilterList;
            _selectedConnectionModel = null;
            _selectedWebSocketMessage = null;
            NotifyPropertyChangesSafely(
                nameof(ProxyConnections),
                nameof(StatusFilterList),
                nameof(HostFilterList),
                nameof(MethodFilterList),
                nameof(SelectedConnectionModel),
                nameof(SelectedWebSocketMessage));
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
                    var configuration = InternetProxy.ReadConfiguration();
                    configuration.RequireKnown();
                    var desired = new ProxyEndpoint("127.0.0.1", Port);
                    if (configuration.State == ProxyConfigurationState.Enabled &&
                        !configuration.Endpoint.Matches(desired))
                    {
                        throw new InvalidOperationException(
                            Localization.GetLocalizedString("PROXY_ENDPOINT_CONFLICT", configuration.Endpoint, desired));
                    }
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

            using var target = await ProxyDeviceOperations.AcquireAsync(_deviceType, DeviceName);
            var configuration = await CreateDeviceConfiguration().ReadAsync();
            configuration.RequireKnown();
            if (configuration.State == ProxyConfigurationState.Enabled)
            {
                var desired = new ProxyEndpoint(sourceAddress, Port);
                if (configuration.Endpoint.Matches(desired))
                {
                    _parameters.ProxyHost = sourceAddress;
                    PortStatus = Localization.GetLocalizedString("PROXY_PORT_APPLIED");
                    return true;
                }
                throw new InvalidOperationException(
                    Localization.GetLocalizedString("PROXY_ENDPOINT_CONFLICT", configuration.Endpoint, desired));
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
                    _parameters.ProxyHost = sourceAddress;
                    PortStatus = Localization.GetLocalizedString("PROXY_PORT_ENABLING");
                    return await EnableXboxProxyOrReportFailureAsync(
                        () => CaptureUtilities.EnableXboxProxyAsync(DeviceName, sourceAddress, Port),
                        () => MessageBox.Show(
                            Localization.GetLocalizedString("PROXY_ERROR_CONNECTION_DESC"),
                            Localization.GetLocalizedString("PROXY_ERROR_CONNECTION"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error));

                default:
                    throw new NotImplementedException(Localization.GetLocalizedString("PROXY_ERROR_DEVICE_TYPE_XBOX"));
            }
        }

        // A failed setup must report false so the caller rolls the listener back.
        internal static async Task<bool> EnableXboxProxyOrReportFailureAsync(
            Func<Task<bool>> enableXboxProxyAsync,
            Action showFailure)
        {
            if (await enableXboxProxyAsync())
            {
                return true;
            }

            showFailure();
            return false;
        }

        internal Task<bool> DisableProxying() => DisableProxying(description =>
            MessageBox.Show(
                description,
                Localization.GetLocalizedString("PROXY_ERROR_RESTART_REQUIRED"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Exclamation) == MessageBoxResult.OK);

        internal async Task<bool> DisableProxying(Func<string, bool> confirmDisableXboxProxy)
        {
            try
            {
                using var target = await ProxyDeviceOperations.AcquireAsync(_deviceType, DeviceName);
                if (_deviceType == DeviceType.GenericProxyDevice)
                {
                    PortStatus = Localization.GetLocalizedString("PROXY_PORT_MANUAL");
                    return true;
                }

                if (_deviceType == DeviceType.LocalPC)
                {
                    // Recovery must freshly classify and settle the lease even if an earlier snapshot appeared disabled or changed.
                    await CreateDeviceConfiguration().DisableAsync(
                        new ProxyEndpoint("127.0.0.1", Port));
                    PortStatus = Localization.GetLocalizedString("PROXY_DISABLE_CONFIRMED");
                    return true;
                }

                if (_deviceType == DeviceType.XboxConsole &&
                    _deviceConfiguration == null && !GDKXHelper.IsGDKXInstalled(true))
                {
                    PortStatus = Localization.GetLocalizedString("PROXY_DISABLE_FAILED");
                    return false;
                }

                var device = CreateDeviceConfiguration();
                var current = await device.ReadAsync();
                if (current.State == ProxyConfigurationState.Unreachable)
                {
                    current.RequireKnown();
                }
                if (current.State == ProxyConfigurationState.Disabled)
                {
                    PortStatus = Localization.GetLocalizedString("PROXY_DISABLED");
                    return true;
                }
                if (_deviceType == DeviceType.XboxConsole)
                {
                    var description = current.State == ProxyConfigurationState.Unknown
                        ? Localization.GetLocalizedString("PROXY_DISABLE_UNKNOWN_CONFIRM")
                        : !OwnsEndpoint(current.Endpoint)
                            ? Localization.GetLocalizedString("PROXY_DISABLE_FOREIGN_CONFIRM", current.Endpoint)
                            : Localization.GetLocalizedString("PROXY_ERROR_RESTART_DESC");
                    if (!confirmDisableXboxProxy(description))
                    {
                        return false;
                    }
                }

                await device.DisableAsync();
                PortStatus = Localization.GetLocalizedString("PROXY_DISABLE_REQUESTED");
                return true;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PortStatus = Localization.GetLocalizedString("PROXY_DISABLE_FAILED");
                throw;
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
            if (_readOnly || HasPendingPort)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_PORT_PENDING_START"));
            }
            if (_portReservation == null)
            {
                RestoreCurrentPort(false);
            }
            if (_portReservation == null)
            {
                throw new InvalidOperationException(PortStatus);
            }
            await StartProxyCoreAsync(bShowProxyInformation, Port);
        }

        private async Task StartProxyCoreAsync(bool bShowProxyInformation, UInt16 port)
        {
            var options = new WebServiceProxyOptions
            {
                LogFilePath = $"{DeviceName.Replace(" ", "")}-wspe.log",
                Port = port
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
                await _webProxy.StartProxyAsync(
                    options,
                    _captureCallbackCancellationTokenSource.Token);
            }
            catch
            {
                await CleanUpAfterFailedStartAsync();
                throw;
            }

            IsRunning = true;

            if (bShowProxyInformation)
            {
                ShowGenericProxyDetails();
            }
        }

        // Runs while the startup exception is propagating. Every step is attempted and logged on
        // failure so a secondary cleanup error neither masks the startup error nor leaves the
        // callback gate or UI queue open (which would block every later start).
        private async Task CleanUpAfterFailedStartAsync()
        {
            var callbacksDrained = _captureCallbacks.StopAcceptingAndWaitAsync();
            await TryCleanupStepAsync(
                () => _captureCallbackCancellationTokenSource.CancelAsync(),
                "cancel capture callbacks");
            await TryCleanupStepAsync(() => _webProxy.StopProxyAsync(), "stop the proxy");
            await TryCleanupStepAsync(
                () => WaitForCaptureCallbacksAsync(callbacksDrained, null, Stopwatch.StartNew()),
                "drain capture callbacks");
            await TryCleanupStepAsync(() => _uiUpdateQueue.CompleteAndDrainAsync(), "drain the UI queue");
            DisposeCaptureCallbackCancellation();

            // The toggle's checked state follows IsRunning one-way, so it must be told to
            // re-read the unchanged value or it keeps the state the click gave it.
            RaisePropertyChange(nameof(IsRunning));
            CommandManager.InvalidateRequerySuggested();
        }

        private static async Task TryCleanupStepAsync(Func<Task> step, string description)
        {
            try
            {
                await step();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Cleanup after a failed proxy start could not {description}: {ex}");
            }
        }

        // Retain the listener when a device write may have succeeded despite a reported failure.
        internal async Task EnableDeviceProxyingOrRollBackAsync(Func<Task<bool>> enableDevice)
        {
            var enabled = false;
            var activationPort = Port;
            var activationProxyHost = _parameters.ProxyHost;
            PortStatus = null;
            try
            {
                enabled = await enableDevice();
            }
            catch (ProxyActivationUncertainException ex)
            {
                PortStatus = ex.Message;
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                await HandleFailedDeviceSetupAsync(
                    activationPort,
                    activationProxyHost);
                throw;
            }

            if (!enabled)
            {
                await HandleFailedDeviceSetupAsync(
                    activationPort,
                    activationProxyHost);
            }
        }

        private async Task HandleFailedDeviceSetupAsync(
            ushort activationPort,
            string activationProxyHost)
        {
            if (_deviceType == DeviceType.LocalPC &&
                await LocalPcListenerMayStillBeUsedAsync(
                    activationPort,
                    activationProxyHost))
            {
                PortStatus = Localization.GetLocalizedString(
                    "PROXY_ENABLE_LISTENER_KEPT");
                return;
            }
            await TryStopProxyAfterFailedDeviceSetupAsync();
            PortStatus = Localization.GetLocalizedString("PROXY_ENABLE_FAILED");
        }

        private async Task<bool> LocalPcListenerMayStillBeUsedAsync(
            ushort activationPort,
            string activationProxyHost)
        {
            var timeout = Stopwatch.StartNew();
            Task probeTask = null;
            try
            {
                Task<ProxyConfiguration> readTask =
                    CreateDeviceConfiguration().ReadAsync();
                probeTask = readTask;
                ProxyConfiguration configuration = await readTask.WaitAsync(
                    RemainingEnableFailureProbeTime(timeout));
                if (configuration == null ||
                    configuration.State is ProxyConfigurationState.Unknown or
                        ProxyConfigurationState.Unreachable)
                {
                    return true;
                }
                if (configuration.State == ProxyConfigurationState.Disabled)
                {
                    return false;
                }
                if (configuration.Endpoint == null)
                {
                    return true;
                }
                if (configuration.Endpoint.Port != activationPort)
                {
                    return false;
                }

                IPAddress address = null;
                if (IPAddress.TryParse(
                        configuration.Endpoint.Host?.Trim().Trim('[', ']'),
                        out var parsedAddress))
                {
                    address = parsedAddress.IsIPv4MappedToIPv6
                        ? parsedAddress.MapToIPv4()
                        : parsedAddress;
                }
                string host = address?.ToString() ??
                    ProxyEndpoint.NormalizeHost(configuration.Endpoint.Host);
                if (string.Equals(
                        host,
                        NormalizeClassifierHost(activationProxyHost),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (address == null)
                {
                    return true;
                }
                if (IPAddress.IsLoopback(address))
                {
                    return true;
                }
                Task<IPAddress[]> addressesTask = Task.Run(
                    () => _getClassifierIp4Addresses().ToArray());
                probeTask = addressesTask;
                IPAddress[] localAddresses = await addressesTask.WaitAsync(
                    RemainingEnableFailureProbeTime(timeout));
                return localAddresses.Any(local =>
                    {
                        if (local.IsIPv4MappedToIPv6)
                        {
                            local = local.MapToIPv4();
                        }
                        return local.Equals(address);
                    });
            }
            catch (TimeoutException)
            {
                ObserveAbandonedTask(probeTask);
                return true;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return true;
            }
        }

        private TimeSpan RemainingEnableFailureProbeTime(Stopwatch timeout)
        {
            TimeSpan remaining = _enableFailureProbeTimeout - timeout.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }
            return remaining;
        }

        private static string NormalizeClassifierHost(string host)
        {
            if (IPAddress.TryParse(
                    host?.Trim().Trim('[', ']'),
                    out var address))
            {
                return (address.IsIPv4MappedToIPv6
                    ? address.MapToIPv4()
                    : address).ToString();
            }
            return ProxyEndpoint.NormalizeHost(host);
        }

        private async Task TryStopProxyAfterFailedDeviceSetupAsync()
        {
            try
            {
                await StopProxyAsync();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"Rolling back the proxy after device setup failed also failed: {ex}");
            }
        }

        internal async Task StopProxyAsync(
            IProgress<WebProxyStopProgress> progress = null)
        {
            var stopStopwatch = Stopwatch.StartNew();
            var callbacksDrained = _captureCallbacks.StopAcceptingAndWaitAsync();
            if (_captureCallbackCancellationTokenSource != null)
            {
                await _captureCallbackCancellationTokenSource.CancelAsync();
            }
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

        internal IDisposable BeginDeviceTransition()
        {
            if (_readOnly || IsTransitioning)
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("PROXY_OPERATION_BUSY"));
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
            if (request == null)
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
                // Otherwise, assume CONNECT request where Path is the "host:port" authority
                else
                {
                    host = HostAuthority.TryParse(path, out string authorityHost, out _) ? authorityHost : path;
                    path = null;
                }
            }

            // The list model compares the host without its port (DNS spelling kept).
            return BlockList.IsBlocked(host, path);
        }

        private Task<bool> WebProxy_ReceivedSslConnectionRequestAsync(
            SslConnectionRequestEventArgs connectionEvent,
            CancellationToken requestCancellation)
        {
            return ExecuteCapturePolicyAsync(
                "SSL connection",
                async cancellationToken =>
                {
                    var scriptParameters = new WebServiceCaptureScriptParams
                    {
                        Request = connectionEvent.Request,
                        Response = null
                    };
                    ScriptPolicyResult scriptResult = await RunScriptPolicyAsync(
                        WebServiceCaptureScriptableEventType.SslConnectionRequest,
                        scriptParameters,
                        "SSL connection",
                        cancellationToken).ConfigureAwait(false);
                    if (scriptResult == ScriptPolicyResult.Aborted)
                    {
                        return false;
                    }

                    bool acceptConnection =
                        scriptResult == ScriptPolicyResult.FailedOpen ||
                        scriptParameters.Continue;
                    if (acceptConnection && IsRequestBlocked(connectionEvent.Request))
                    {
                        PublicUtilities.AppLog(LogLevel.INFO,
                            $"Blocked SSL CONNECT to '{connectionEvent.Request.Host ?? connectionEvent.Request.Path}' (BlockList)");
                        acceptConnection = false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    string base64Body = string.Empty;
                    if (connectionEvent.Request.BodyBytes != null)
                        base64Body = Convert.ToBase64String(connectionEvent.Request.BodyBytes);

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
                    return acceptConnection;
                },
                requestCancellation);
        }

        private async Task<bool> ExecuteCapturePolicyAsync(
            string policyName,
            Func<CancellationToken, Task<bool>> policy,
            CancellationToken requestCancellation)
        {
            if (requestCancellation.IsCancellationRequested ||
                !_captureCallbacks.TryBegin(out var callbackLease))
            {
                return false;
            }

            using var callbackLeaseScope = callbackLease;
            CancellationToken captureCancellation =
                _captureCallbackCancellationTokenSource?.Token ??
                CancellationToken.None;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellation,
                captureCancellation);
            try
            {
                bool accepted = await policy(linkedCancellation.Token).ConfigureAwait(false);
                return accepted && !linkedCancellation.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"{policyName} capture policy failed: {ex}");
                return false;
            }
        }

        private async Task<ScriptPolicyResult> RunScriptPolicyAsync(
            WebServiceCaptureScriptableEventType eventType,
            WebServiceCaptureScriptParams scriptParameters,
            string scriptName,
            CancellationToken cancellationToken)
        {
            ScriptModel script = Scripts[eventType];
            if (!script.IsEnabled)
            {
                return ScriptPolicyResult.Completed;
            }

            using var scriptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scriptCancellation.CancelAfter(_scriptExecutionDeadline);
            CancellationToken scriptToken = scriptCancellation.Token;
            // Isolate synchronous user code so the transport can enforce its deadline.
            Task<WebServiceCaptureScriptParams> scriptTask = Task.Run(
                () => script.RunScriptAsync(
                    scriptParameters,
                    scriptToken));
            try
            {
                await scriptTask.WaitAsync(scriptToken).ConfigureAwait(false);
                return scriptCancellation.IsCancellationRequested
                    ? ScriptPolicyResult.Aborted
                    : ScriptPolicyResult.Completed;
            }
            catch (OperationCanceledException) when (scriptCancellation.IsCancellationRequested)
            {
                ObserveAbandonedTask(scriptTask);
                if (!cancellationToken.IsCancellationRequested)
                {
                    PublicUtilities.AppLog(
                        LogLevel.ERROR,
                        $"{scriptName} script exceeded the {_scriptExecutionDeadline.TotalSeconds:g} second deadline.");
                }
                return ScriptPolicyResult.Aborted;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                if (scriptCancellation.IsCancellationRequested)
                {
                    return ScriptPolicyResult.Aborted;
                }

                PublicUtilities.AppLog(
                    LogLevel.ERROR,
                    $"{scriptName} script error {ex}");
                return ScriptPolicyResult.FailedOpen;
            }
        }

        private static void ObserveAbandonedTask(Task task)
        {
            if (task == null)
            {
                return;
            }
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                    TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
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

        private Task<bool> WebProxy_ReceivedWebRequestAsync(
            HttpRequestEventArgs requestEvent,
            CancellationToken requestCancellation)
        {
            return ExecuteCapturePolicyAsync(
                "Web request",
                async cancellationToken =>
                {
                    var scriptParameters = new WebServiceCaptureScriptParams
                    {
                        Request = requestEvent.Request,
                        Response = null
                    };
                    ScriptPolicyResult scriptResult = await RunScriptPolicyAsync(
                        WebServiceCaptureScriptableEventType.WebRequest,
                        scriptParameters,
                        "WebRequest",
                        cancellationToken).ConfigureAwait(false);
                    if (scriptResult == ScriptPolicyResult.Aborted)
                    {
                        return false;
                    }

                    bool acceptRequest =
                        scriptResult == ScriptPolicyResult.FailedOpen ||
                        scriptParameters.Continue;
                    if (acceptRequest && IsRequestBlocked(requestEvent.Request))
                    {
                        PublicUtilities.AppLog(LogLevel.INFO,
                            $"Blocked {requestEvent.Request.Method} {requestEvent.Request.Host}{requestEvent.Request.Path} (BlockList)");
                        acceptRequest = false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    string base64Body = string.Empty;
                    if (requestEvent.Request.BodyBytes != null)
                        base64Body = Convert.ToBase64String(requestEvent.Request.BodyBytes);

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
                    return acceptRequest;
                },
                requestCancellation);
        }

        private Task<bool> WebProxy_ReceivedWebResponseAsync(
            HttpResponseEventArgs responseEvent,
            CancellationToken requestCancellation)
        {
            return ExecuteCapturePolicyAsync(
                "Web response",
                async cancellationToken =>
                {
                    var scriptParameters = new WebServiceCaptureScriptParams
                    {
                        Request = responseEvent.Request,
                        Response = responseEvent.Response
                    };
                    ScriptPolicyResult scriptResult = await RunScriptPolicyAsync(
                        WebServiceCaptureScriptableEventType.WebResponse,
                        scriptParameters,
                        "WebResponse",
                        cancellationToken).ConfigureAwait(false);
                    if (scriptResult == ScriptPolicyResult.Aborted)
                    {
                        return false;
                    }

                    bool sendResponse =
                        scriptResult == ScriptPolicyResult.FailedOpen ||
                        scriptParameters.Continue;
                    cancellationToken.ThrowIfCancellationRequested();

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

                    return sendResponse;
                },
                requestCancellation);
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

        private void NotifyPropertyChangesSafely(
            params string[] propertyNames)
        {
            Delegate[] subscribers =
                PropertyChanged?.GetInvocationList();
            if (subscribers == null)
            {
                return;
            }
            foreach (string propertyName in propertyNames)
            {
                var args =
                    new PropertyChangedEventArgs(
                        propertyName);
                foreach (Delegate subscriber in subscribers)
                {
                    try
                    {
                        ((PropertyChangedEventHandler)subscriber)(
                            this,
                            args);
                    }
                    catch (Exception exception) when (
                        !ExceptionUtilities.IsFatal(
                            exception))
                    {
                        PublicUtilities.AppLog(
                            LogLevel.ERROR,
                            $"Capture reload notification failed: {exception}");
                    }
                }
            }
        }
    }
}
