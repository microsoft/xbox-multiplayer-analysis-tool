// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Models;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;


namespace XMAT.NetworkTrace
{
    public enum DeviceStatusEnum { Idle, Disconnecting, Connecting, Tracing, Starting, Stopping, Downloading };

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

        private readonly string _deviceName;
        private readonly Func<INetworkTraceEngine, Task> _disconnectEngine;
        private DeviceType _deviceType;
        private IDataTable _dataTable;
        internal INetworkTraceEngine _networkTraceEngine;
        private bool _isRunning;

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

        public bool HaveTraces { get { return (NetworkTracePackets.Count > 0) && _status == DeviceStatusEnum.Idle; } }
        public bool HaveNoTraces { get { return !HaveTraces; } }

        public NetworkTraceCaptureController(string deviceName, DeviceType deviceType)
            : this(deviceName, deviceType, NetworkTraceEngine.Disconnect)
        {
        }

        internal NetworkTraceCaptureController(
            string deviceName, DeviceType deviceType, Func<INetworkTraceEngine, Task> disconnectEngine)
        {
            _deviceName = deviceName;
            _deviceType = deviceType;
            _disconnectEngine = disconnectEngine ?? throw new ArgumentNullException(nameof(disconnectEngine));
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
            NetworkTracePackets = new NetworkTracePacketsCollection();
            CaptureMethod = NetworkTraceCaptureMethod.Method;
            _dataTable = (CaptureMethod as NetworkTraceCaptureMethod).NetworkTracePacketsTable;
            _dataTable.RowAdded += HandleRowAdded;
            IsCapturing = false;
            DeviceStatus = DeviceStatusEnum.Idle;
            RaisePropertyChange(nameof(HaveTraces));
            RaisePropertyChange(nameof(HaveNoTraces));
        }

        public void ClearAllCaptures()
        {
            _dataTable.RemoveRowsWhere(new FieldValue<string>(@"DeviceName", _deviceName));
            NetworkTracePackets.Clear();
            RaisePropertyChange(nameof(HaveTraces));
            RaisePropertyChange(nameof(HaveNoTraces));
        }

        public Task<bool> CanCloseAsync()
        {
            return Task.FromResult(!IsRunning);
        }

        public Task CloseAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        private void HandleRowAdded(IDataTable table, Int64 rowId)
        {
            var record = _dataTable.Dataset.DataRecordsFrom(rowId, 1).First();
            if (record.Str(@"DeviceName").Equals(_deviceName))
            {
                NetworkTracePackets.AddFromDataRecord(record);
            }
        }

        internal async Task StartCapture(ICaptureDeviceContext device, IDisposable operationLease = null)
        {
            if (IsCapturing || DeviceStatus != DeviceStatusEnum.Idle)
            {
                throw new InvalidOperationException("A capture operation is already in progress.");
            }

            IsCapturing = true;
            try
            {
                CaptureAppModel.AppModel.StatusBarText1 = Localization.GetLocalizedString("NETCAP_STATUS_RUNNING");

                if (_deviceType == DeviceType.XboxConsole)
                {
                    DeviceStatus = DeviceStatusEnum.Connecting;
                    await ConnectToDevice(device);
                }
                else if (_deviceType == DeviceType.LocalPC)
                {
                    _networkTraceEngine ??= NetworkTraceEngine.CreateLocal();
                }
                else
                {
                    throw new ApplicationException("Unsupported DeviceType");
                }

                // Reused engines need one subscription per capture, including after a failed attempt.
                _networkTraceEngine.EventRecordAvailable -= OnEventRecordAvailable;
                _networkTraceEngine.EventRecordAvailable += OnEventRecordAvailable;
                DeviceStatus = DeviceStatusEnum.Starting;

                await _networkTraceEngine.StartPacketTraceAsync();

                DeviceStatus = DeviceStatusEnum.Tracing;

                CommandManager.InvalidateRequerySuggested();
            }
            catch
            {
                // The application's close check also observes the view's capture-data lease.
                operationLease?.Dispose();
                await CompleteCaptureAsync(preserveCaptureError: true);
                throw;
            }
        }

        internal async Task StopCapture(ICaptureDeviceContext device)
        {
            if (!IsCapturing || DeviceStatus != DeviceStatusEnum.Tracing)
            {
                throw new InvalidOperationException("No trace is ready to stop.");
            }

            bool captureFailed = true;
            try
            {
                DeviceStatus = DeviceStatusEnum.Stopping;
                await _networkTraceEngine.StopPacketTraceAsync();

                DeviceStatus = DeviceStatusEnum.Downloading;
                await _networkTraceEngine.GetAllEventsAsync();
                captureFailed = false;
            }
            finally
            {
                try
                {
                    await CompleteCaptureAsync(preserveCaptureError: captureFailed);
                }
                finally
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private async Task CompleteCaptureAsync(bool preserveCaptureError)
        {
            var engine = _networkTraceEngine;
            if (engine != null)
            {
                engine.EventRecordAvailable -= OnEventRecordAvailable;
            }

            bool needsDisconnect = engine != null && _deviceType == DeviceType.XboxConsole;
            if (needsDisconnect)
            {
                _networkTraceEngine = null;
                DeviceStatus = DeviceStatusEnum.Disconnecting;
            }

            // Publish the retained packets and allow close, but keep Start disabled until cleanup finishes.
            IsCapturing = false;
            CaptureAppModel.AppModel.StatusBarText1 = String.Empty;
            try
            {
                if (needsDisconnect)
                {
                    await _disconnectEngine(engine);
                }
            }
            catch (Exception ex) when (preserveCaptureError && !ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Network trace recovery disconnect failed: {ex}");
            }
            finally
            {
                DeviceStatus = DeviceStatusEnum.Idle;
            }
        }

        internal async Task<int> ImportFromEtlFileAsync(string etlPath)
        {
            var fileEngine = new LocalNetworkTraceEngineImpl();
            fileEngine.EventRecordAvailable += OnEventRecordAvailable;

            DeviceStatus = DeviceStatusEnum.Downloading;
            try
            {
                return await fileEngine.ReadEventsFromFileAsync(etlPath);
            }
            finally
            {
                fileEngine.EventRecordAvailable -= OnEventRecordAvailable;
                DeviceStatus = DeviceStatusEnum.Idle;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        internal async Task ConnectToDevice(ICaptureDeviceContext device)
        {
            if (_networkTraceEngine == null)
            {
                NetworkTraceEngineOptions options = new NetworkTraceEngineOptions();

                options.HostName = _deviceName;

                _networkTraceEngine = await NetworkTraceEngine.Connect(options);
            }
        }

        private void OnEventRecordAvailable(object sender, string eventRecord)
        {
            var doc = JsonDocument.Parse(eventRecord);
            var eventNode = doc.RootElement.GetProperty("event");
            var packetNode = doc.RootElement.GetProperty("packet");

            int startFlag = 0,
                endFlag = 0,
                fragFlag = 0,
                sendFlag = 0,
                recvFlag = 0;

            foreach (var element in packetNode.GetProperty("flags").EnumerateArray())
            {
                switch (element.GetString())
                {
                    case "start":
                        startFlag = 1;
                        break;
                    case "end":
                        endFlag = 1;
                        break;
                    case "fragment":
                        fragFlag = 1;
                        break;
                    case "send":
                        sendFlag = 1;
                        break;
                    case "receive":
                        recvFlag = 1;
                        break;
                }
            }

            _dataTable.AddRow(
                _deviceName,
                eventNode.GetProperty("processId").GetInt32(),
                eventNode.GetProperty("threadId").GetInt32(),
                eventNode.GetProperty("timestamp").GetDateTime().ToLocalTime().ToString("HH:mm:ss.fffffff"),
                packetNode.GetProperty("mediaType").GetString(),
                startFlag,
                endFlag,
                fragFlag,
                sendFlag,
                recvFlag,
                packetNode.GetProperty("data").GetString()
                );
        }

        public void LoadCaptures(IEnumerable<IDataset> datasets)
        {
            if (datasets != null && datasets.FirstOrDefault() != null)
            {
                var records = datasets.First().DataRecordsWhen(
                    new FieldValue<string>(@"DeviceName", _deviceName)
                    );

                foreach (var record in records)
                {
                    NetworkTracePackets.AddFromDataRecord(record);
                }

                RaisePropertyChange(nameof(NetworkTracePackets));
            }
        }
    }
}
