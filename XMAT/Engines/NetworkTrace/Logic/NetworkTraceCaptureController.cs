// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;


namespace XMAT.NetworkTrace
{
    public enum DeviceStatusEnum { Idle, Disconnecting, Connecting, Tracing, Starting, Stopping, Downloading, RecoveryRequired };

    public class NetworkTraceCaptureController : INotifyPropertyChanged, IDeviceCaptureController, IDisposable
    {
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                {
                    return;
                }

                _isRunning = value;
                RaisePropertyChange(nameof(IsRunning));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // TODO: implement my parameters
        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object Parameters { get { return null; } }

        private bool _isCapturing;
        public bool IsCapturing
        {
            get => _isCapturing;
            set
            {
                if (_isCapturing == value)
                {
                    return;
                }

                _isCapturing = value;
                IsRunning = value;
                RaisePropertyChange(nameof(IsCapturing));
            }
        }

        public NetworkTracePacketsCollection NetworkTracePackets { get; internal set; }
        public ICaptureMethod CaptureMethod { get; internal set; }
        public ObservableCollection<CheckedListItem> PidFilterList { get; set; }
        public ObservableCollection<CheckedListItem> TidFilterList { get; set; }
        public ObservableCollection<CheckedListItem> ProtocolFilterList { get; set; }
        public ObservableCollection<CheckedListItem> SourceIpFilterList { get; set; }
        public ObservableCollection<CheckedListItem> DestIpFilterList { get; set; }
        public ItemCollection FilteredItems { get; set; }
        private NetworkTracePacketDataModel _selectedPacket;
        public NetworkTracePacketDataModel SelectedPacket
        {
            get => _selectedPacket;
            set { _selectedPacket = value; RaisePropertyChange(); }
        }

        private readonly string _deviceName;
        private readonly DeviceType _deviceType;
        private readonly INetworkTraceEngineFactory _engineFactory;
        private readonly Dispatcher _dispatcher;
        private IDataTable _dataTable;
        private bool _liveRowsAttached;
        internal INetworkTraceEngine _networkTraceEngine;
        private bool _isRunning;
        private bool _operationInProgress;
        private bool _recoveryRequired;
        private bool _mayBeTracing;
        private CaptureTraceRecoveryPhase _nextPhase;
        private long _viewGeneration;
        private bool _disposed;

        public bool IsTransitioning => _operationInProgress;
        public bool RecoveryRequired => _recoveryRequired;
        public bool MayBeTracing => _mayBeTracing;
        public CaptureTraceRecoveryPhase RecoveryPhase => _recoveryRequired ? _nextPhase : CaptureTraceRecoveryPhase.None;
        public bool CanClearCaptures => !IsCapturing && !IsTransitioning;
        public string RecoveryStatusText => RecoveryRequired
            ? Localization.GetLocalizedString("NETCAP_RECOVERY_STATUS", RecoveryPhase, MayBeTracing, LocalTraceSession ?? "-") : string.Empty;
        public string LocalTraceSession => (_networkTraceEngine as LocalNetworkTraceEngineImpl)?.OwnedSessionName;

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private DeviceStatusEnum _status;
        public DeviceStatusEnum DeviceStatus
        {
            get => _status;
            set
            {
                _status = value;
                RaisePropertyChange(nameof(DeviceStatus));
                RaisePropertyChange(nameof(HaveTraces));
                RaisePropertyChange(nameof(HaveNoTraces));
            }
        }

        public bool HaveTraces { get { return (NetworkTracePackets?.Count > 0) && _status == DeviceStatusEnum.Idle; } }
        public bool HaveNoTraces { get { return !HaveTraces; } }

        public NetworkTraceCaptureController(string deviceName, DeviceType deviceType)
            : this(deviceName, deviceType, new NetworkTraceEngineFactory())
        {
        }

        internal NetworkTraceCaptureController(
            string deviceName, DeviceType deviceType, INetworkTraceEngineFactory engineFactory)
        {
            _deviceName = deviceName;
            _deviceType = deviceType;
            _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
            _dispatcher = App.CaptureUiDispatcher();
            IsCapturing = false;
            DeviceStatus = DeviceStatusEnum.Idle;
            PidFilterList = new ObservableCollection<CheckedListItem>();
            TidFilterList = new ObservableCollection<CheckedListItem>();
            ProtocolFilterList = new ObservableCollection<CheckedListItem>();
            SourceIpFilterList = new ObservableCollection<CheckedListItem>();
            DestIpFilterList = new ObservableCollection<CheckedListItem>();
        }

        public void Initialize()
        {
            Initialize(attachLiveRows: true);
        }

        internal void Initialize(bool attachLiveRows) =>
            Initialize(NetworkTraceCaptureMethod.MethodInstance.NetworkTracePacketsTable, attachLiveRows);

        internal void Initialize(IDataTable table, bool attachLiveRows = true)
        {
            if (IsCapturing || IsTransitioning)
                throw new InvalidOperationException("The trace capture is busy.");
            ArgumentNullException.ThrowIfNull(table);
            if (ReferenceEquals(_dataTable, table))
                return;
            if (_dataTable != null)
                _dataTable.RowAdded -= HandleRowAdded;
            _liveRowsAttached = false;
            _disposed = false;
            Interlocked.Increment(ref _viewGeneration);
            NetworkTracePackets = new NetworkTracePacketsCollection();
            CaptureMethod = NetworkTraceCaptureMethod.Method;
            _dataTable = table;
            if (attachLiveRows)
                AttachLiveRows();
            IsCapturing = false;
            DeviceStatus = DeviceStatusEnum.Idle;
            RaisePropertyChange(nameof(HaveTraces));
            RaisePropertyChange(nameof(HaveNoTraces));
        }

        internal void InitializeReadOnly()
        {
            _disposed = false;
            NetworkTracePackets = new NetworkTracePacketsCollection();
            CaptureMethod = NetworkTraceCaptureMethod.Method;
        }

        internal void AttachLiveRows()
        {
            if (_liveRowsAttached || _disposed)
                return;
            _dataTable ??= NetworkTraceCaptureMethod.MethodInstance.NetworkTracePacketsTable;
            if (_dataTable == null)
                return;
            _dataTable.RowAdded += HandleRowAdded;
            _liveRowsAttached = true;
        }

        public void ClearAllCaptures()
        {
            if (!CanClearCaptures)
                throw new InvalidOperationException(Localization.GetLocalizedString("NETCAP_RECOVERY_CLEAR_BLOCKED"));
            ClearRows();
        }

        private void ClearRows()
        {
            Interlocked.Increment(ref _viewGeneration);
            _dataTable?.RemoveRowsWhere(new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, _deviceName));
            NetworkTracePackets.Clear();
            RaisePropertyChange(nameof(HaveTraces));
            RaisePropertyChange(nameof(HaveNoTraces));
        }

        public Task<bool> CanCloseAsync()
        {
            return Task.FromResult(!IsRunning && !IsTransitioning);
        }

        public Task CloseAsync()
        {
            if (IsRunning || IsTransitioning)
                throw new InvalidOperationException(Localization.GetLocalizedString("NETCAP_RECOVERY_CLOSE_BLOCKED"));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _disposed = true;
            Interlocked.Increment(ref _viewGeneration);
            if (_dataTable != null)
                _dataTable.RowAdded -= HandleRowAdded;
            _liveRowsAttached = false;
        }

        private void HandleRowAdded(IDataTable table, Int64 rowId)
        {
            var generation = Volatile.Read(ref _viewGeneration);
            var record = _dataTable.Dataset.DataRecordsFrom(rowId, 1).First();
            if (record.Str(@"DeviceName").Equals(_deviceName))
            {
                void Publish()
                {
                    if (!_disposed && generation == Volatile.Read(ref _viewGeneration))
                        NetworkTracePackets.AddFromDataRecord(record);
                }
                if (App.HasUiAccess(_dispatcher))
                    Publish();
                else
                    App.PostToUi(_dispatcher, Publish);
            }
        }

        internal CaptureOperationFailure GetAvailability(bool running)
        {
            if (IsTransitioning)
                return CaptureOperationFailure.CaptureBusy;
            if (running)
            {
                if (RecoveryRequired)
                    return CaptureOperationFailure.RecoveryRequired;
                if (IsRunning)
                    return DeviceStatus == DeviceStatusEnum.Tracing ? CaptureOperationFailure.None : CaptureOperationFailure.CaptureBusy;
                if (DeviceStatus != DeviceStatusEnum.Idle)
                    return CaptureOperationFailure.CaptureBusy;
                var prerequisite = _engineFactory.GetPrerequisite(_deviceType);
                if (prerequisite != CaptureOperationFailure.None)
                    return prerequisite;
                return _dataTable == null ? CaptureOperationFailure.NotInitialized : CaptureOperationFailure.None;
            }
            if (!IsRunning)
                return DeviceStatus == DeviceStatusEnum.Idle ? CaptureOperationFailure.None : CaptureOperationFailure.CaptureBusy;
            return DeviceStatus is DeviceStatusEnum.Tracing or DeviceStatusEnum.RecoveryRequired
                ? CaptureOperationFailure.None : CaptureOperationFailure.CaptureBusy;
        }

        internal IDisposable BeginOperation()
        {
            if (_operationInProgress)
                throw new InvalidOperationException("A trace operation is already in progress.");
            _operationInProgress = true;
            RaisePropertyChange(nameof(IsTransitioning));
            return new OperationLease(this);
        }

        internal async Task StartCapture(ICaptureDeviceContext device)
        {
            var prerequisite = _engineFactory.GetPrerequisite(_deviceType);
            if (prerequisite == CaptureOperationFailure.ElevationRequired)
                throw new UnauthorizedAccessException();
            if (prerequisite != CaptureOperationFailure.None)
                throw new InvalidOperationException("Unsupported trace target.");
            var startAttempted = false;
            try
            {
                DeviceStatus = DeviceStatusEnum.Connecting;
                _networkTraceEngine = await _engineFactory.CreateAsync(_deviceName, _deviceType);
                if (_networkTraceEngine == null)
                    throw new InvalidOperationException("The trace engine was not created.");
                ClearRows();
                DeviceStatus = DeviceStatusEnum.Starting;
                _nextPhase = CaptureTraceRecoveryPhase.Stop;
                IsCapturing = true;
                SetMayBeTracing(true);
                startAttempted = true;
                await _networkTraceEngine.StartPacketTraceAsync();
                DeviceStatus = DeviceStatusEnum.Tracing;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                var rejected = exception is UnauthorizedAccessException ||
                    exception is NetworkTraceEngineException { MayHaveCommitted: false };
                if (startAttempted && !rejected)
                {
                    SetRecovery(CaptureTraceRecoveryPhase.Stop);
                }
                else
                {
                    SetMayBeTracing(false);
                    _nextPhase = CaptureTraceRecoveryPhase.Disconnect;
                    try
                    {
                        if (_networkTraceEngine != null)
                            await _engineFactory.DisconnectAsync(_networkTraceEngine, _deviceType);
                        ResetStopped();
                    }
                    catch (Exception cleanupException) when (!ExceptionUtilities.IsFatal(cleanupException))
                    {
                        IsCapturing = true;
                        SetRecovery(CaptureTraceRecoveryPhase.Disconnect);
                    }
                }
                throw;
            }
            finally { CommandManager.InvalidateRequerySuggested(); }
        }

        internal async Task StopCapture(ICaptureDeviceContext device)
        {
            try
            {
                if (_nextPhase == CaptureTraceRecoveryPhase.Stop)
                {
                    DeviceStatus = DeviceStatusEnum.Stopping;
                    await _networkTraceEngine.StopPacketTraceAsync();
                    SetMayBeTracing(false);
                    _nextPhase = CaptureTraceRecoveryPhase.Download;
                }
                if (_nextPhase == CaptureTraceRecoveryPhase.Download)
                {
                    DeviceStatus = DeviceStatusEnum.Downloading;
                    await DownloadAndCommitAsync(_networkTraceEngine, _networkTraceEngine.GetAllEventsAsync);
                    _nextPhase = CaptureTraceRecoveryPhase.Publish;
                }
                if (_nextPhase == CaptureTraceRecoveryPhase.Publish)
                {
                    await PublishPacketsAsync();
                    _nextPhase = CaptureTraceRecoveryPhase.Disconnect;
                }
                DeviceStatus = DeviceStatusEnum.Disconnecting;
                await _engineFactory.DisconnectAsync(_networkTraceEngine, _deviceType);
                ResetStopped();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                SetRecovery(_nextPhase);
                throw;
            }
            finally { CommandManager.InvalidateRequerySuggested(); }
        }

        internal async Task<int> ImportFromEtlFileAsync(string etlPath)
        {
            if (IsCapturing || IsTransitioning)
                throw new InvalidOperationException("The trace capture is busy.");
            using var operation = BeginOperation();
            var fileEngine = new LocalNetworkTraceEngineImpl();
            DeviceStatus = DeviceStatusEnum.Downloading;
            try
            {
                var count = 0;
                await DownloadAndCommitAsync(fileEngine, async () => count = await fileEngine.ReadEventsFromFileAsync(etlPath));
                await PublishPacketsAsync();
                return count;
            }
            finally
            {
                DeviceStatus = DeviceStatusEnum.Idle;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task DownloadAndCommitAsync(INetworkTraceEngine engine, Func<Task> read)
        {
            if (_dataTable is not IImportedCaptureTable imports)
                throw new InvalidOperationException("The trace table does not support atomic capture publication.");
            using var stage = new NetworkTraceEventStage();
            EventHandler<string> handler = (_, record) => stage.Append(record);
            engine.EventRecordAvailable += handler;
            try
            { await read(); }
            finally { engine.EventRecordAvailable -= handler; }
            stage.Complete();
            await Task.Run(() => imports.ReplaceImportedRows(
                new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, _deviceName), stage.Rows(_deviceName)));
            if (IsCapturing)
                _nextPhase = CaptureTraceRecoveryPhase.Publish;
        }

        private async Task PublishPacketsAsync()
        {
            var packets = await Task.Run(() =>
            {
                var result = new NetworkTracePacketsCollection();
                foreach (var record in _dataTable.Dataset.DataRecordsWhen(
                    new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, _deviceName)))
                    result.AddFromDataRecord(record);
                return result;
            });
            Interlocked.Increment(ref _viewGeneration);
            NetworkTracePackets = packets;
            RaisePropertyChange(nameof(NetworkTracePackets));
        }

        private void SetMayBeTracing(bool value)
        {
            _mayBeTracing = value;
            RaisePropertyChange(nameof(MayBeTracing));
        }

        private void SetRecovery(CaptureTraceRecoveryPhase phase)
        {
            _nextPhase = phase;
            _recoveryRequired = true;
            DeviceStatus = DeviceStatusEnum.RecoveryRequired;
            RaisePropertyChange(nameof(RecoveryRequired));
            RaisePropertyChange(nameof(RecoveryPhase));
            RaisePropertyChange(nameof(RecoveryStatusText));
        }

        private void ResetStopped()
        {
            _networkTraceEngine = null;
            _nextPhase = CaptureTraceRecoveryPhase.None;
            _recoveryRequired = false;
            SetMayBeTracing(false);
            DeviceStatus = DeviceStatusEnum.Idle;
            IsCapturing = false;
            RaisePropertyChange(nameof(RecoveryRequired));
            RaisePropertyChange(nameof(RecoveryPhase));
            RaisePropertyChange(nameof(RecoveryStatusText));
        }

        internal async Task AcknowledgeExternalRecoveryAsync()
        {
            if (!RecoveryRequired)
                throw new InvalidOperationException("No trace recovery is pending.");
            if (_networkTraceEngine != null)
                await _engineFactory.DisconnectAsync(_networkTraceEngine, _deviceType);
            ResetStopped();
        }

        private sealed class OperationLease(NetworkTraceCaptureController owner) : IDisposable
        {
            private NetworkTraceCaptureController _owner = owner;
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref _owner, null);
                if (current == null)
                    return;
                current._operationInProgress = false;
                current.RaisePropertyChange(nameof(IsTransitioning));
                App.RefreshCommands();
            }
        }

        public void LoadCaptures(IEnumerable<IDataset> datasets)
        {
            ApplyLoadedCaptures(PrepareLoadedCaptures(datasets?.FirstOrDefault(), CancellationToken.None));
            NotifyLoadedCaptures();
        }

        internal NetworkTracePacketsCollection PrepareLoadedCaptures(IDataset dataset, CancellationToken cancellationToken)
        {
            var packets = new NetworkTracePacketsCollection();
            if (dataset != null)
            {
                foreach (var record in dataset.DataRecordsWhen(
                    new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, _deviceName)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    packets.AddFromDataRecord(record);
                }
            }
            return packets;
        }

        internal void ApplyLoadedCaptures(NetworkTracePacketsCollection packets)
        {
            if (!_liveRowsAttached)
                Interlocked.Increment(ref _viewGeneration);
            NetworkTracePackets = packets;
            _selectedPacket = null;
            FilteredItems = null;
            PidFilterList = CreateFilterList(packets.OrderBy(packet => packet.ProcessId)
                .Select(packet => packet.ProcessId.ToString(CultureInfo.InvariantCulture)).Distinct());
            TidFilterList = CreateFilterList(packets.OrderBy(packet => packet.ThreadId)
                .Select(packet => packet.ThreadId.ToString(CultureInfo.InvariantCulture)).Distinct());
            ProtocolFilterList = CreateFilterList(packets.OrderBy(packet => packet.Protocol)
                .Select(packet => Enum.IsDefined(typeof(NetworkProtocol), packet.Protocol)
                    ? $"{packet.Protocol} ({(NetworkProtocol)packet.Protocol})"
                    : packet.Protocol.ToString(CultureInfo.InvariantCulture)).Distinct());
            SourceIpFilterList = CreateFilterList(packets.Select(packet => packet.SourceIpv4Address).Distinct().OrderBy(value => value));
            DestIpFilterList = CreateFilterList(packets.Select(packet => packet.DestinationIpv4Address).Distinct().OrderBy(value => value));
        }

        internal void NotifyLoadedCaptures()
        {
            RaisePropertyChange(nameof(NetworkTracePackets));
            RaisePropertyChange(nameof(SelectedPacket));
            RaisePropertyChange(nameof(PidFilterList));
            RaisePropertyChange(nameof(TidFilterList));
            RaisePropertyChange(nameof(ProtocolFilterList));
            RaisePropertyChange(nameof(SourceIpFilterList));
            RaisePropertyChange(nameof(DestIpFilterList));
            RaisePropertyChange(nameof(HaveTraces));
            RaisePropertyChange(nameof(HaveNoTraces));
        }

        internal void ReplaceLoadedCapturesFrom(NetworkTraceCaptureController source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ApplyLoadedCaptures(source.NetworkTracePackets);
            NotifyLoadedCaptures();
        }

        private static ObservableCollection<CheckedListItem> CreateFilterList(IEnumerable<string> values) =>
            new(values.Select(value => new CheckedListItem { IsChecked = true, Text = value }));
    }
}
