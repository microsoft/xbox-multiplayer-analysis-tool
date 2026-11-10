// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.NetworkTrace;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Models
{
    public enum ImportType : int
    {
        Fiddler = 0,
        NetTrace = 1
    }

    internal class CaptureAppModel : ICaptureAppModel, INotifyPropertyChanged
    {
        public IDatabase ActiveDatabase { get; internal set; }

        // TODO: someday support multiple readonly databases??
        public IReadonlyDatabase LoadedDatabase { get; internal set; }

        public ObservableCollection<ICaptureDeviceContext> CaptureDeviceContexts { get => _captureDeviceContexts; }
        public ICaptureDeviceContext SelectedDeviceContext
        {
            get => _selectedDeviceContext;
            set
            {
                if (_selectedDeviceContext == value)
                {
                    return;
                }

                if (_selectedDeviceContext?.CaptureController is IDeviceCaptureController previousController)
                {
                    PropertyChangedEventManager.RemoveHandler(
                        previousController, SelectedCaptureController_PropertyChanged, string.Empty);
                }
                _selectedDeviceContext = value;
                if (_selectedDeviceContext?.CaptureController is IDeviceCaptureController currentController)
                {
                    PropertyChangedEventManager.AddHandler(
                        currentController, SelectedCaptureController_PropertyChanged, string.Empty);
                }
                RaisePropertyChange();
                RaiseCaptureStatusProperties();
                InvalidateCommands();
            }
        }
        public ObservableCollection<ICaptureAnalysisRun> AnalysisRuns { get => _analysisRuns; }

        public ICaptureAnalysisRun AnalysisRunById(int id)
        {
            return AnalysisRuns.FirstOrDefault(run => id == run.Id);
        }

        private string _statusBarText1;
        private string _statusBarText2;
        private string _statusBarText3;

        public string StatusBarText1
        {
            get => SelectedDeviceContext?.CaptureController is WebServiceCapture.WebServiceDeviceCaptureController proxy
                ? proxy.CaptureStatusText : _statusBarText1;
            set { _statusBarText1 = value; RaisePropertyChange(); }
        }
        public string StatusBarText2
        {
            get => SelectedDeviceContext?.CaptureController is WebServiceCapture.WebServiceDeviceCaptureController proxy
                ? proxy.PortDescription : _statusBarText2;
            set { _statusBarText2 = value; RaisePropertyChange(); }
        }
        public string StatusBarText3
        {
            get => SelectedDeviceContext?.CaptureController is WebServiceCapture.WebServiceDeviceCaptureController proxy
                ? proxy.ImportNotice ?? proxy.PortStatus : _statusBarText3;
            set { _statusBarText3 = value; RaisePropertyChange(); }
        }

        internal static readonly CaptureAppModel AppModel = new();

        // Session-only Block and Bypass lists shared by every capture and analysis view of this
        // application instance. They are never saved with settings or captures.
        internal WebServiceCapture.Models.HostListPolicy HostListPolicy { get; } = new();

        internal IDataTable CaptureDevicesTable { get; set; }
        internal IList<ICaptureAnalyzer> CaptureAnalyzers { get => _captureAnalyzers; }
        internal AppPreferencesModel PreferencesModel { get; set; }
        internal bool IsAnalysisRunning
        {
            get
            {
                lock (_analysisStateLock)
                {
                    return _isAnalysisRunning;
                }
            }
        }
        internal bool IsCaptureDataOperationRunning
        {
            get
            {
                lock (_analysisStateLock)
                {
                    return _isCaptureDataOperationRunning;
                }
            }
        }
        internal bool IsAnyCaptureRunning
        {
            get
            {
                lock (_analysisStateLock)
                {
                    return _runningCaptureControllers.Count != 0;
                }
            }
        }
        internal bool CanAnalyzeCaptures =>
            SelectedDeviceContext != null &&
            !IsAnyCaptureRunning &&
            !IsAnalysisRunning &&
            !IsCaptureDataOperationRunning;
        internal bool CanModifyCaptureData =>
            !IsAnalysisRunning &&
            !IsCaptureDataOperationRunning;
        internal bool CanBeginCaptureDataOperation =>
            CanModifyCaptureData &&
            !IsAnyCaptureRunning;
        // Stop waits for a capture-data operation such as an export, which reads the running capture until it publishes.
        internal bool CanStopCaptures => !IsAnalysisRunning && !IsCaptureDataOperationRunning;
        // File > Export and Export Sanitized: the selected web capture, running or not, when it has sessions.
        internal bool CanExportSelectedCapture =>
            CanModifyCaptureData &&
            SelectedDeviceContext?.CaptureType == CaptureType.WebProxy &&
            SelectedDeviceContext.CaptureController is WebServiceCapture.WebServiceDeviceCaptureController controller &&
            controller.ProxyConnections.Count > 0;

        private readonly ObservableCollection<ICaptureDeviceContext> _captureDeviceContexts = new();
        private ICaptureDeviceContext _selectedDeviceContext = null;
        private readonly List<ICaptureAnalyzer> _captureAnalyzers = new();
        private readonly ObservableCollection<ICaptureAnalysisRun> _analysisRuns = new();
        private readonly object _analysisStateLock = new();
        private readonly HashSet<IDeviceCaptureController> _trackedCaptureControllers = new();
        private readonly HashSet<IDeviceCaptureController> _runningCaptureControllers = new();
        private readonly Dispatcher _dispatcher;
        private bool _isAnalysisRunning;
        private bool _isCaptureDataOperationRunning;

        public CaptureAppModel() :
            this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
        {
        }

        internal CaptureAppModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            PreferencesModel = new();
            _captureDeviceContexts.CollectionChanged += CaptureDeviceContexts_CollectionChanged;

            foreach (var context in _captureDeviceContexts)
            {
                TrackCaptureController(context.CaptureController);
            }
        }

        internal async Task<bool> CanCloseAsync()
        {
            if (IsCaptureDataOperationRunning)
            {
                return false;
            }
            var devices = CaptureDeviceContexts.Where(context => !context.IsReadOnly).ToList();
            foreach (var device in devices)
            {
                if (!await device.CaptureController.CanCloseAsync())
                {
                    return false;
                }
            }

            return true;
        }

        // The open capture tab that an analysis read, while it still holds the sessions that the analysis saw; null once
        // that tab was closed or its sessions were deleted, cleared or reloaded. It never selects a tab.
        internal ICaptureDeviceContext ResolveCaptureSource(CaptureSourceStamp stamp) =>
            stamp.SourceId == Guid.Empty
                ? null
                : _captureDeviceContexts.FirstOrDefault(context =>
                    context.CaptureController is WebServiceDeviceCaptureController controller && controller.SourceStamp == stamp);

        // Analyses whose source no longer resolves become historical; called on the UI thread after the tabs or their
        // sessions change.
        private void RefreshAnalysisSources()
        {
            foreach (var results in _analysisRuns
                .Select(run => run.AnalysisData)
                .OfType<XboxLiveCaptureAnalysis.Models.AnalysisResultsModel>()
                .Where(candidate => ResolveCaptureSource(candidate.SourceStamp) == null))
            {
                results.MarkSourceStale();
            }
        }

        internal bool TryBeginAnalysis(out IDisposable analysisLease)
        {
            lock (_analysisStateLock)
            {
                if (_selectedDeviceContext == null ||
                    _isAnalysisRunning ||
                    _isCaptureDataOperationRunning ||
                    _runningCaptureControllers.Count != 0)
                {
                    analysisLease = null;
                    return false;
                }

                _isAnalysisRunning = true;
            }

            RaisePropertyChange(nameof(IsAnalysisRunning));
            InvalidateCommands();
            analysisLease = new AnalysisLease(this);
            return true;
        }

        internal bool TryBeginCaptureDataOperation(
            out IDisposable operationLease,
            bool requireAllCapturesStopped = false)
        {
            lock (_analysisStateLock)
            {
                if (_isAnalysisRunning ||
                    _isCaptureDataOperationRunning ||
                    (requireAllCapturesStopped && _runningCaptureControllers.Count != 0))
                {
                    operationLease = null;
                    return false;
                }

                _isCaptureDataOperationRunning = true;
            }

            RaisePropertyChange(nameof(IsCaptureDataOperationRunning));
            InvalidateCommands();
            operationLease = new CaptureDataOperationLease(this);
            return true;
        }

        private void CaptureDeviceContexts_CollectionChanged(
            object sender,
            NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                IDeviceCaptureController[] trackedControllers;
                lock (_analysisStateLock)
                {
                    trackedControllers = _trackedCaptureControllers.ToArray();
                }

                foreach (var controller in trackedControllers)
                {
                    UntrackCaptureController(controller);
                }

                foreach (var context in _captureDeviceContexts)
                {
                    TrackCaptureController(context.CaptureController);
                }
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (ICaptureDeviceContext context in e.OldItems)
                    {
                        UntrackCaptureController(context.CaptureController);
                    }
                }

                if (e.NewItems != null)
                {
                    foreach (ICaptureDeviceContext context in e.NewItems)
                    {
                        TrackCaptureController(context.CaptureController);
                    }
                }
            }

            RefreshAnalysisSources();
            InvalidateCommands();
        }

        private void RaiseCaptureStatusProperties()
        {
            RaisePropertyChange(nameof(StatusBarText1));
            RaisePropertyChange(nameof(StatusBarText2));
            RaisePropertyChange(nameof(StatusBarText3));
        }

        private void SelectedCaptureController_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, SelectedDeviceContext?.CaptureController) ||
                sender is not WebServiceCapture.WebServiceDeviceCaptureController ||
                (!string.IsNullOrEmpty(e.PropertyName) &&
                 e.PropertyName != nameof(WebServiceCapture.WebServiceDeviceCaptureController.CaptureStatusText) &&
                 e.PropertyName != nameof(WebServiceCapture.WebServiceDeviceCaptureController.PortDescription) &&
                 e.PropertyName != nameof(WebServiceCapture.WebServiceDeviceCaptureController.PortStatus) &&
                 e.PropertyName != nameof(WebServiceCapture.WebServiceDeviceCaptureController.ImportNotice)))
            {
                return;
            }
            void UpdateStatus()
            {
                if (ReferenceEquals(sender, SelectedDeviceContext?.CaptureController))
                {
                    RaiseCaptureStatusProperties();
                }
            }
            if (_dispatcher.CheckAccess())
            {
                UpdateStatus();
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(UpdateStatus));
            }
        }

        private void TrackCaptureController(IDeviceCaptureController controller)
        {
            if (controller == null)
            {
                return;
            }

            lock (_analysisStateLock)
            {
                if (!_trackedCaptureControllers.Add(controller))
                {
                    return;
                }
            }

            PropertyChangedEventManager.AddHandler(
                controller,
                CaptureController_PropertyChanged,
                nameof(IDeviceCaptureController.IsRunning));

            lock (_analysisStateLock)
            {
                if (controller.IsRunning)
                {
                    _runningCaptureControllers.Add(controller);
                }
                else
                {
                    _runningCaptureControllers.Remove(controller);
                }
            }
        }

        private void UntrackCaptureController(IDeviceCaptureController controller)
        {
            if (controller == null)
            {
                return;
            }

            PropertyChangedEventManager.RemoveHandler(
                controller,
                CaptureController_PropertyChanged,
                nameof(IDeviceCaptureController.IsRunning));

            lock (_analysisStateLock)
            {
                _trackedCaptureControllers.Remove(controller);
                _runningCaptureControllers.Remove(controller);
            }
        }

        private void CaptureController_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (sender is not IDeviceCaptureController controller ||
                (!string.IsNullOrEmpty(e.PropertyName) &&
                 e.PropertyName != nameof(IDeviceCaptureController.IsRunning)))
            {
                return;
            }

            lock (_analysisStateLock)
            {
                if (!_trackedCaptureControllers.Contains(controller))
                {
                    return;
                }

                if (controller.IsRunning)
                {
                    _runningCaptureControllers.Add(controller);
                }
                else
                {
                    _runningCaptureControllers.Remove(controller);
                }
            }

            InvalidateCommands();
        }

        private void InvalidateCommands()
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(CommandManager.InvalidateRequerySuggested));
                return;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private void EndAnalysis()
        {
            lock (_analysisStateLock)
            {
                if (!_isAnalysisRunning)
                {
                    return;
                }

                _isAnalysisRunning = false;
            }

            RaisePropertyChange(nameof(IsAnalysisRunning));
            InvalidateCommands();
        }

        private void EndCaptureDataOperation()
        {
            lock (_analysisStateLock)
            {
                if (!_isCaptureDataOperationRunning)
                {
                    return;
                }

                _isCaptureDataOperationRunning = false;
            }

            RaisePropertyChange(nameof(IsCaptureDataOperationRunning));
            InvalidateCommands();
        }

        internal void InitializeDatabase()
        {
            ActiveDatabase = DataAbstractionLayer.DataAbstractionLayer.Database;
            AppModel.ActiveDatabase.Initialize();

            // add the devices table
            AppModel.CaptureDevicesTable = AppModel.ActiveDatabase.CreateTable(
                @"CaptureDevices",
                new Field<Int64>(@"DeviceType"),
                new Field<string>(@"DeviceName"),
                new Field<Int64>(@"CaptureType")
            );
        }

        internal void AddCaptureAnalyzer(ICaptureAnalyzer analyzer)
        {
            try
            {
                analyzer.Initialize(this);
                _captureAnalyzers.Add(analyzer);
            }
            catch (Exception ex)
            {
                // since the analyzer failed to initialize, then we do not
                // add it to our active set...
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        internal void LoadDataCaptures(string fromFilePath) =>
            LoadDataCaptures(
                fromFilePath,
                path =>
                    DataAbstractionLayer.DataAbstractionLayer
                        .LoadDatabaseFromFile(path) as
                        SqLtReadonlyDatabase ??
                    throw new InvalidOperationException(
                        "The loaded capture database has an unsupported implementation."));

        internal void LoadDataCaptures(
            string fromFilePath,
            Func<string, SqLtReadonlyDatabase> openDatabase)
        {
            SqLtReadonlyDatabase opened =
                openDatabase(fromFilePath);
            var staged = new List<ICaptureDeviceContext>();
            try
            {
                IReadonlyDataTable devicesTable =
                    opened.TableByName("CaptureDevices");
                if (devicesTable == null)
                {
                    throw new SqLtSchemaException(
                        SqLtSchemaFailure.Unsupported);
                }
                foreach (IDataRecord record in
                    devicesTable.Dataset.DataRecords)
                {
                    var device =
                        new CaptureDeviceContextModel(
                            (DeviceType)record.Int("DeviceType"),
                            record.Str("DeviceName"),
                            (CaptureType)record.Int("CaptureType"),
                            true,
                            null,
                            hostListPolicy: HostListPolicy,
                            attachLiveRows: false);
                    staged.Add(device);
                    var datasets = new List<IDataset>();
                    foreach (string tableName in
                        opened.TableNames().Where(name =>
                            device.CaptureController
                                .CaptureMethod
                                .OwnsDataTable(name)))
                    {
                        datasets.Add(
                            opened.TableByName(
                                tableName).Dataset);
                    }
                    device.CaptureController.LoadCaptures(
                        datasets);
                }

                ICaptureDeviceContext previousSelection =
                    SelectedDeviceContext;
                var added = new List<ICaptureDeviceContext>();
                var transfers = new List<Action>();
                var owners =
                    new List<IDeviceCaptureController>();
                foreach (ICaptureDeviceContext device in staged)
                {
                    ICaptureDeviceContext existing =
                        GetDeviceContext(
                            device.DeviceType,
                            device.DeviceName,
                            device.CaptureType,
                            true) ??
                        added.FirstOrDefault(candidate =>
                            candidate.DeviceType ==
                                device.DeviceType &&
                            candidate.DeviceName ==
                                device.DeviceName &&
                            candidate.CaptureType ==
                                device.CaptureType &&
                            candidate.IsReadOnly);
                    if (existing == null)
                    {
                        added.Add(device);
                        owners.Add(
                            device.CaptureController);
                    }
                    else
                    {
                        transfers.Add(
                            CreateLoadedCaptureTransfer(
                                existing.CaptureController,
                                device.CaptureController));
                        owners.Add(
                            existing.CaptureController);
                    }
                }

                try
                {
                    foreach (ICaptureDeviceContext device in
                        added)
                    {
                        _captureDeviceContexts.Add(device);
                    }
                }
                catch
                {
                    foreach (ICaptureDeviceContext device in
                        added)
                    {
                        _captureDeviceContexts.Remove(device);
                    }
                    SelectedDeviceContext =
                        previousSelection;
                    throw;
                }
                foreach (Action transfer in transfers)
                {
                    transfer();
                }
                RefreshAnalysisSources();
                IDeviceCaptureController[] distinctOwners =
                    owners.Where(owner =>
                            _captureDeviceContexts.Any(
                                context =>
                                    ReferenceEquals(
                                        context.CaptureController,
                                        owner)))
                        .Distinct(
                            ReferenceEqualityComparer.Instance)
                        .Cast<IDeviceCaptureController>()
                        .ToArray();
                foreach (IDeviceCaptureController owner in
                    distinctOwners)
                {
                    LoadedCaptureDatabaseRegistry.Assign(
                        owner,
                        opened);
                }
                if (distinctOwners.Length == 0)
                {
                    opened.Dispose();
                    opened = null;
                }
                else
                {
                    LoadedDatabase = opened;
                    opened = null;
                }
                // New tabs show live rows from now on; transfer sources and discarded stages never do.
                foreach (ICaptureDeviceContext device in added)
                {
                    if (_captureDeviceContexts.Contains(
                            device) &&
                        device.CaptureController is
                            NetworkTraceCaptureController network)
                    {
                        network.AttachLiveRows();
                    }
                }
            }
            finally
            {
                try
                {
                    // Staged controllers that did not become tabs are disposed, so they never attach.
                    foreach (ICaptureDeviceContext device in staged)
                    {
                        if (!_captureDeviceContexts.Contains(
                            device))
                        {
                            (device.CaptureController as
                                IDisposable)?.Dispose();
                        }
                    }
                }
                finally
                {
                    opened?.Dispose();
                }
            }
        }

        private static Action CreateLoadedCaptureTransfer(
            IDeviceCaptureController existing,
            IDeviceCaptureController staged) =>
            (existing, staged) switch
            {
                (
                    WebServiceDeviceCaptureController target,
                    WebServiceDeviceCaptureController source) =>
                    () => target.ReplaceLoadedCapturesFrom(
                        source),
                (
                    NetworkTraceCaptureController target,
                    NetworkTraceCaptureController source) =>
                    () => target.ReplaceLoadedCapturesFrom(
                        source),
                _ => throw new SqLtSchemaException(
                    SqLtSchemaFailure.Unsupported),
            };

        // Saving is a capture data operation: it never overlaps an import, export, close or analysis.
        internal void SaveDataCaptures(string toFilePath)
        {
            if (!TrySaveDataCaptures(toFilePath))
            {
                throw new InvalidOperationException("Another capture data operation is running.");
            }
        }

        // Returns false, without writing, while another capture data operation runs.
        internal bool TrySaveDataCaptures(string toFilePath)
        {
            if (!TryBeginCaptureDataOperation(out IDisposable operation))
            {
                return false;
            }

            using (operation)
            {
                ActiveDatabase.SaveToFile(toFilePath);
            }

            return true;
        }

        // Clears one capture. Clearing is a capture data operation, so it never overlaps an import, export, save, close or
        // analysis: returns false, without clearing, while one of them runs, and releases the lease even if it fails.
        internal bool TryClearAllCaptures(IDeviceCaptureController controller)
        {
            ArgumentNullException.ThrowIfNull(controller);
            if (!TryBeginCaptureDataOperation(out IDisposable operation))
            {
                return false;
            }

            using (operation)
            {
                try
                {
                    controller.ClearAllCaptures();
                }
                finally
                {
                    RefreshAnalysisSources();
                }
            }

            return true;
        }

        // Deletes sessions of a writable capture tab on the UI thread. Deleting is a capture data operation: returns false,
        // deleting nothing, while another one or an analysis runs. Analyses of that capture become historical.
        internal bool TryDeleteSessions(
            WebServiceDeviceCaptureController controller,
            IReadOnlyList<CaptureDeleteTarget> targets,
            IReadOnlySet<Operations.Captures.CaptureRowKey> currentlyVisible,
            out CaptureDeleteResult result)
        {
            ArgumentNullException.ThrowIfNull(controller);
            result = null;
            if (!TryBeginCaptureDataOperation(out IDisposable operation))
            {
                return false;
            }

            using (operation)
            {
                try
                {
                    result = controller.DeleteSessions(targets, currentlyVisible);
                }
                finally
                {
                    RefreshAnalysisSources();
                }
            }

            return true;
        }

        internal Task ImportDataCapturesAsync(
            ImportType importType,
            string fromFilePath)
        {
            return ImportDataCapturesAsync(
                importType,
                fromFilePath,
                (path, context) =>
                {
                    var captureMethod =
                        context.CaptureController.CaptureMethod as
                            WebServiceCapture.WebServiceCaptureMethod;
                    if (captureMethod == null)
                    {
                        throw new InvalidOperationException(
                            "Fiddler imports require a web service capture method.");
                    }

                    return captureMethod.ImportFromFiddlerAsync(path, context);
                });
        }

        internal async Task ImportDataCapturesAsync(
            ImportType importType,
            string fromFilePath,
            Func<string, ICaptureDeviceContext, Task> importFromFiddlerAsync)
        {
            ICaptureDeviceContext captureDevice = null;

            switch (importType)
            {
                // create a new tab with an "Unknown" device and WebServiceCaptureControl capture method
                // that will be read-only and imports this data...
                case ImportType.Fiddler:
                {
                    var previousSelection = SelectedDeviceContext;
                    string deviceName = $"{importType} - {Path.GetFileName(fromFilePath)}";
                    var existingDevice = GetDeviceContext(
                        DeviceType.Unknown, deviceName, CaptureType.WebProxy, true);
                    captureDevice = AddDeviceContext(
                        DeviceType.Unknown,
                        deviceName,
                        CaptureType.WebProxy,
                        true,
                        // NOTE: since this is a READONLY
                        // capture, we do not need parameters maybe?
                        null
                    );
                    try
                    {
                        if (importFromFiddlerAsync == null)
                        {
                            throw new ArgumentNullException(
                                nameof(importFromFiddlerAsync));
                        }

                        await importFromFiddlerAsync(
                            fromFilePath,
                            captureDevice);
                    }
                    catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                    {
                        try
                        {
                            if (existingDevice == null)
                            {
                                try
                                {
                                    await RemoveDeviceContextAsync(captureDevice);
                                }
                                finally
                                {
                                    if (_captureDeviceContexts.Remove(captureDevice))
                                    {
                                        InvalidateCommands();
                                    }

                                    if (previousSelection == null ||
                                        _captureDeviceContexts.Contains(previousSelection))
                                    {
                                        SelectedDeviceContext = previousSelection;
                                    }
                                }
                            }
                        }
                        catch (Exception cleanupException) when (!ExceptionUtilities.IsFatal(cleanupException))
                        {
                            PublicUtilities.AppLog(LogLevel.ERROR, $"Failed to clean up Fiddler import: {cleanupException}");
                        }
                        throw;
                    }
                    finally
                    {
                        // Importing again into an existing tab replaces its sessions, so its analyses become historical.
                        RefreshAnalysisSources();
                    }
                }
                break;

                // create a new read-only network trace tab and populate it from a netsh .etl file
                case ImportType.NetTrace:
                {
                    captureDevice = AddDeviceContext(
                        DeviceType.Unknown,
                        $"{importType} - {Path.GetFileName(fromFilePath)}",
                        CaptureType.NetworkTrace,
                        true,
                        null
                    );

                    try
                    {
                        int packetCount = await (captureDevice.CaptureController as NetworkTrace.NetworkTraceCaptureController).ImportFromEtlFileAsync(fromFilePath);
                        if (packetCount == 0)
                        {
                            System.Windows.MessageBox.Show(
                                Localization.GetLocalizedString("NETCAP_IMPORT_NO_PACKETS"),
                                Localization.GetLocalizedString("NETCAP_ERROR"));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(ex.Message, Localization.GetLocalizedString("NETCAP_ERROR"));
                    }
                }
                break;
            }

            if (captureDevice != null)
            {
                SelectedDeviceContext = captureDevice;
            }
        }

        internal ICaptureDeviceContext AddDeviceContext(
            DeviceType deviceType,
            string deviceName,
            CaptureType captureType,
            bool readOnly,
            object serializedParameters,
            bool deferPortAllocation = false)
        {
            var newCaptureDeviceModel = GetDeviceContext(
                deviceType,
                deviceName,
                captureType,
                readOnly);

            if (newCaptureDeviceModel == null)
            {
                newCaptureDeviceModel = new CaptureDeviceContextModel(
                    deviceType,
                    deviceName,
                    captureType,
                    readOnly,
                    serializedParameters,
                    deferPortAllocation,
                    HostListPolicy);
                _captureDeviceContexts.Add(newCaptureDeviceModel);

                if (!readOnly)
                {
                    AppModel.CaptureDevicesTable.AddRow(
                        (Int64)deviceType,
                        deviceName,
                        (Int64)captureType);
                }

                InvalidateCommands();
            }

            return newCaptureDeviceModel;
        }

        internal ICaptureDeviceContext GetDeviceContext(DeviceType deviceType, string deviceName, CaptureType captureType, bool readOnly)
        {
            return (from cc in _captureDeviceContexts
                    where
                    cc.DeviceType == deviceType &&
                    (captureType == CaptureType.WebProxy && !readOnly
                        ? SameProxyDevice(deviceType, cc.DeviceName, deviceName)
                        : cc.DeviceName == deviceName) &&
                    cc.CaptureType == captureType &&
                    cc.IsReadOnly == readOnly
                    select cc
            ).FirstOrDefault();
        }

        internal async Task RemoveDeviceContextAsync(ICaptureDeviceContext captureDeviceContext)
        {
            await captureDeviceContext.CaptureController.CloseAsync();

            if (!captureDeviceContext.IsReadOnly)
            {
                // remove device from the devices table
                CaptureDevicesTable.RemoveRowsWhere(
                    new FieldValue<Int64>(@"DeviceType", (Int64)captureDeviceContext.DeviceType),
                    new FieldValue<string>(@"DeviceName", captureDeviceContext.DeviceName),
                    new FieldValue<Int64>(@"CaptureType", (Int64)captureDeviceContext.CaptureType)
                );
            }

            bool wasPresent =
                _captureDeviceContexts.Contains(
                    captureDeviceContext);
            bool removed = false;
            try
            {
                removed = _captureDeviceContexts.Remove(
                    captureDeviceContext);
            }
            finally
            {
                bool actuallyRemoved =
                    removed ||
                    (wasPresent &&
                     !_captureDeviceContexts.Contains(
                         captureDeviceContext));
                try
                {
                    // Only a removed tab stops receiving live rows; a failed removal leaves it working.
                    if (actuallyRemoved)
                    {
                        (captureDeviceContext.CaptureController as
                            IDisposable)?.Dispose();
                    }
                }
                finally
                {
                    if (captureDeviceContext.IsReadOnly &&
                        actuallyRemoved)
                    {
                        LoadedCaptureDatabaseRegistry.Release(
                            captureDeviceContext.CaptureController);
                        RefreshLoadedDatabase();
                    }
                    InvalidateCommands();
                }
            }
        }

        private void RefreshLoadedDatabase()
        {
            LoadedDatabase =
                _captureDeviceContexts
                    .Where(context =>
                        context.IsReadOnly)
                    .Select(context =>
                        LoadedCaptureDatabaseRegistry
                            .DatabaseFor(
                                context.CaptureController))
                    .Where(database =>
                        database != null)
                    .LastOrDefault();
        }

        internal static bool SameProxyDevice(DeviceType type, string first, string second) =>
            type == DeviceType.LocalPC ||
            (type == DeviceType.XboxConsole
                ? string.Equals(
                    XMAT.WebServiceCapture.ProxyEndpoint.NormalizeHost(first),
                    XMAT.WebServiceCapture.ProxyEndpoint.NormalizeHost(second),
                    StringComparison.OrdinalIgnoreCase)
                : first == second);

        internal AnalysisRunModel AddAnalysisRun(ICaptureAnalyzer analyzer) =>
            AddAnalysisRun(analyzer, ProcessFilterSpec.Empty);

        // A run with a process filter analyzes only the sessions it selects; the specification is immutable, so the run
        // keeps the filter as it was when the run started.
        internal AnalysisRunModel AddAnalysisRun(ICaptureAnalyzer analyzer, ProcessFilterSpec processFilter)
        {
            bool filtered = !ProcessFilter.IsEmpty(processFilter);
            var headerText = $"{analyzer.Description} [{DateTime.Now}]";
            if (filtered)
            {
                headerText += " " + Localization.GetLocalizedString("ANALYSIS_PROCESS_FILTER_TAB");
            }

            var id = (Int64)DateTime.Now.ToFileTimeUtc();
            var analysisRun = new AnalysisRunModel(analyzer, id)
            {
                Header = headerText,
                ProcessFilter = filtered ? processFilter : ProcessFilterSpec.Empty,
            };

            _analysisRuns.Add(analysisRun);

            return analysisRun;
        }

        internal void RemoveAnalysisRun(AnalysisRunModel analysisRun)
        {
            _analysisRuns.Remove(analysisRun);
        }

        internal async Task<ECaptureAnalyzerResult> RunAnalysisAsync(
            AnalysisRunModel analysisRun,
            IDeviceCaptureController captureController,
            Action<string> logError = null)
        {
            var result = ECaptureAnalyzerResult.UnknownError;
            analysisRun.IsProcessing = true;
            try
            {
                result = await analysisRun.SourceAnalyzer.RunAsync(analysisRun, captureController);
                return result;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                string message = $"Capture analysis {analysisRun.Id} failed: {ex}";
                if (logError != null)
                {
                    logError(message);
                }
                else
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, message);
                }
                return ECaptureAnalyzerResult.UnknownError;
            }
            finally
            {
                analysisRun.IsProcessing = false;
                if (result != ECaptureAnalyzerResult.Success)
                {
                    RemoveAnalysisRun(analysisRun);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private sealed class AnalysisLease : IDisposable
        {
            private CaptureAppModel _owner;

            internal AnalysisLease(CaptureAppModel owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.EndAnalysis();
            }
        }

        private sealed class CaptureDataOperationLease : IDisposable
        {
            private CaptureAppModel _owner;

            internal CaptureDataOperationLease(CaptureAppModel owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.EndCaptureDataOperation();
            }
        }
    }
}
