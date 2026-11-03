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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    public enum ExportType : int
    {
        Fiddler = 0
    }

    public enum ImportType : int
    {
        Fiddler = 0,
        NetTrace = 1
    }

    internal partial class CaptureAppModel : ICaptureAppModel, INotifyPropertyChanged
    {
        private IDatabase _activeDatabase;
        private IReadonlyDatabase _loadedDatabase;
        public IDatabase ActiveDatabase
        {
            get => _activeDatabase;
            internal set
            {
                if (ReferenceEquals(_activeDatabase, value))
                    return;
                _activeDatabase = value;
                RaisePropertyChange();
            }
        }

        // TODO: someday support multiple readonly databases??
        public IReadonlyDatabase LoadedDatabase
        {
            get => _loadedDatabase;
            internal set
            {
                if (ReferenceEquals(_loadedDatabase, value))
                    return;
                _loadedDatabase = value;
                _loadedCaptureRevision++;
                LoadedCaptureDisplayName = null;
                RaisePropertyChange();
            }
        }
        internal string LoadedCaptureFilePath { get; private set; }
        internal string LoadedCaptureDisplayName { get; private set; }
        internal long LoadedCaptureRevision => _loadedCaptureRevision;
        private long _loadedCaptureRevision;

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
                ? proxy.PortStatus : _statusBarText3;
            set { _statusBarText3 = value; RaisePropertyChange(); }
        }

        internal static readonly CaptureAppModel AppModel = new();

        internal ICaptureOperations CaptureOperations { get; }
        internal ICaptureDataOperations CaptureDataOperations { get; }
        internal ICaptureRecoveryOperations CaptureRecoveryOperations { get; }
        internal ApplicationServices.CaptureCatalog CaptureCatalog { get; }
        internal ICaptureQueries CaptureQueries { get; }
        internal ICaptureInvestigation CaptureInvestigation { get; }
        internal ICaptureScripts CaptureScripts { get; }
        internal ApplicationServices.CaptureDevices CaptureDevices { get; }
        internal ICaptureProxyOperations CaptureProxyOperations { get; }
        internal ICaptureFileExports CaptureFileExports { get; }
        internal ICaptureFileInputs CaptureFileInputs { get; }
        internal ICaptureFileLoads CaptureFileLoads { get; }
        internal ICaptureSazImports CaptureSazImports { get; }
        internal ICaptureEtlImports CaptureEtlImports { get; }
        internal ICaptureFiles CaptureFiles { get; }
        internal XMAT.AgentAccess.AgentAccessHost AgentAccess { get; }

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
        internal bool IsCaptureFileLoadRunning
        {
            get { lock (_analysisStateLock) { return _isCaptureFileLoadRunning; } }
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
        internal bool CanStopCaptures => !IsAnalysisRunning;
        internal bool CanExportSanitizedCaptures =>
            CanBeginCaptureDataOperation &&
            SelectedDeviceContext?.CaptureType == CaptureType.WebProxy &&
            SelectedDeviceContext.CaptureController is WebServiceCapture.WebServiceDeviceCaptureController controller &&
            controller.ProxyConnections.Count > 0;

        private readonly CaptureContextCollection _captureDeviceContexts = new();
        private ICaptureDeviceContext _selectedDeviceContext = null;
        private readonly List<ICaptureAnalyzer> _captureAnalyzers = new();
        private readonly ObservableCollection<ICaptureAnalysisRun> _analysisRuns = new();
        private readonly object _analysisStateLock = new();
        private readonly HashSet<IDeviceCaptureController> _trackedCaptureControllers = new();
        private readonly HashSet<IDeviceCaptureController> _runningCaptureControllers = new();
        private readonly Dispatcher _dispatcher;
        private bool _isAnalysisRunning;
        private bool _isCaptureDataOperationRunning;
        private bool _isCaptureFileLoadRunning;

        public CaptureAppModel() :
            this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
        {
        }

        internal CaptureAppModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            CaptureCatalog = new ApplicationServices.CaptureCatalog(this, _dispatcher);
            var captureOperations = new ApplicationServices.CaptureOperations(this, _dispatcher);
            CaptureOperations = captureOperations;
            CaptureDataOperations = new ApplicationServices.CaptureDataOperations(this);
            CaptureRecoveryOperations = captureOperations;
            var queries = new ApplicationServices.CaptureQueries(this, _dispatcher);
            CaptureQueries = queries;
            CaptureFiles = new ApplicationServices.CaptureFiles(queries);
            CaptureInvestigation = new ApplicationServices.CaptureInvestigation(this, _dispatcher, CaptureQueries);
            CaptureScripts = new ApplicationServices.CaptureScripts(this, CaptureCatalog);
            CaptureDevices = new ApplicationServices.CaptureDevices(this, _dispatcher);
            CaptureProxyOperations = new ApplicationServices.CaptureProxyOperations(this);
            CaptureFileExports = new ApplicationServices.CaptureFileExports(() =>
                _dispatcher.InvokeAsync<IReadOnlyList<string>>(() => new[] { LoadedCaptureFilePath }).Task);
            CaptureFileInputs = new ApplicationServices.CaptureFileInputs();
            CaptureFileLoads = new ApplicationServices.CaptureFileLoads(this, _dispatcher, queries, CaptureFileInputs);
            CaptureSazImports = new ApplicationServices.CaptureSazImports(this, _dispatcher, queries, CaptureFileInputs);
            CaptureEtlImports = new ApplicationServices.CaptureEtlImports(this, _dispatcher, queries, CaptureFileInputs);
            AgentAccess = new XMAT.AgentAccess.AgentAccessHost(this, _dispatcher);
            PreferencesModel = new();
            _captureDeviceContexts.CollectionChanged += CaptureDeviceContexts_CollectionChanged;

            foreach (var context in _captureDeviceContexts)
            {
                TrackCaptureController(context.CaptureController);
            }
        }

        internal async Task<bool> CanCloseAsync()
        {
            if (IsCaptureDataOperationRunning || IsCaptureFileLoadRunning)
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

        public ICaptureDeviceContext SelectCaptureDeviceContext(string deviceContextName)
        {
            ICaptureDeviceContext context = _captureDeviceContexts.FirstOrDefault(
                captureDeviceContext => captureDeviceContext.DeviceName == deviceContextName);
            if (context != null && context != SelectedDeviceContext)
            {
                SelectedDeviceContext = context;
            }
            return context;
        }

        internal bool TryBeginAnalysis(out IDisposable analysisLease)
        {
            return TryBeginAnalysisCore(_selectedDeviceContext, out analysisLease);
        }

        internal bool TryBeginAnalysis(
            ICaptureDeviceContext target,
            out IDisposable analysisLease)
        {
            if (target == null || !_captureDeviceContexts.Contains(target))
            {
                analysisLease = null;
                return false;
            }

            return TryBeginAnalysisCore(target, out analysisLease);
        }

        internal bool TryBeginSourceAnalysis(out IDisposable analysisLease) =>
            TryBeginAnalysisCore(null, out analysisLease, sourceOnly: true);

        private bool TryBeginAnalysisCore(
            ICaptureDeviceContext target,
            out IDisposable analysisLease,
            bool sourceOnly = false)
        {
            lock (_analysisStateLock)
            {
                if ((!sourceOnly && target == null) ||
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

            operationLease = new CaptureDataOperationLease(this);
            RaisePropertyChange(nameof(IsCaptureDataOperationRunning));
            InvalidateCommands();
            return true;
        }

        internal bool TryBeginCaptureFileLoad(out IDisposable lease)
        {
            lock (_analysisStateLock)
            {
                if (_isCaptureFileLoadRunning)
                {
                    lease = null;
                    return false;
                }
                _isCaptureFileLoadRunning = true;
            }
            lease = new CaptureFileLoadLease(this);
            RaisePropertyChange(nameof(IsCaptureFileLoadRunning));
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
                 e.PropertyName != nameof(WebServiceCapture.WebServiceDeviceCaptureController.PortStatus)))
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

        internal void CommitLoadedCapture(
            IReadonlyDatabase database, string path, string displayName,
            IReadOnlyList<ICaptureDeviceContext> addedContexts)
        {
            RegisterPreparedContexts(addedContexts);
            _loadedDatabase = database;
            LoadedCaptureFilePath = path;
            LoadedCaptureDisplayName = displayName;
            _loadedCaptureRevision++;
        }

        internal void RegisterPreparedContexts(IEnumerable<ICaptureDeviceContext> contexts)
        {
            foreach (var context in contexts)
            {
                _captureDeviceContexts.AppendWithoutNotification(context);
                TrackCaptureController(context.CaptureController);
            }
        }

        internal event Action<ICaptureDeviceContext, IQueryDatabase> CaptureDataRebound;
        internal void NotifyCaptureDataRebound(ICaptureDeviceContext context, IQueryDatabase source) =>
            CaptureDataRebound?.Invoke(context, source);

        internal void NotifyLoadedContexts()
        {
            var selected = SelectedDeviceContext;
            try
            { _captureDeviceContexts.NotifyReplacement(); }
            finally
            {
                SelectedDeviceContext = selected;
                RaisePropertyChange(nameof(SelectedDeviceContext));
            }
        }

        internal void SaveDataCaptures(string toFilePath)
        {
            ActiveDatabase.SaveToFile(toFilePath);
        }

        internal Task ImportDataCapturesAsync(
            ImportType importType,
            string fromFilePath)
        {
            if (importType == ImportType.Fiddler)
                return ImportSazDesktopAsync(fromFilePath);
            if (importType == ImportType.NetTrace)
                return ImportEtlDesktopAsync(fromFilePath);
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

        private async Task ImportSazDesktopAsync(string path)
        {
            var receipt = await CaptureSazImports.ImportDesktopAsync(path);
            await CaptureCatalog.WithTargetAsync(receipt.CaptureId, null, (target, _) =>
            {
                SelectedDeviceContext = target;
                return Task.FromResult(true);
            });
        }

        private async Task ImportEtlDesktopAsync(string path)
        {
            var receipt = await CaptureEtlImports.ImportDesktopAsync(path);
            await CaptureCatalog.WithTargetAsync(receipt.CaptureId, null, (target, _) =>
            {
                SelectedDeviceContext = target;
                return Task.FromResult(true);
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
                }
                break;

                // create a new read-only network trace tab and populate it from a netsh .etl file
                case ImportType.NetTrace:
                {
                    await ImportEtlDesktopAsync(fromFilePath);
                    return;
                }
            }

            if (captureDevice != null)
            {
                SelectedDeviceContext = captureDevice;
            }
        }

        internal void ExportDataCaptures(ExportType exportType, string toFilePath)
        {
            switch (exportType)
            {
                case ExportType.Fiddler:
                    (SelectedDeviceContext.CaptureController.CaptureMethod as WebServiceCapture.WebServiceCaptureMethod)?.ExportToFiddler(toFilePath);
                    break;
            }
        }

        internal async Task<WebServiceCapture.SanitizedExportSummary> ExportSanitizedDataCapturesAsync(
            string toFilePath, CancellationToken cancellationToken = default)
        {
            if (!CanExportSanitizedCaptures ||
                !TryBeginCaptureDataOperation(out var operation, requireAllCapturesStopped: true))
            {
                throw new InvalidOperationException(Localization.GetLocalizedString("SANITIZED_EXPORT_UNAVAILABLE"));
            }
            using (operation)
            {
                var controller = (WebServiceCapture.WebServiceDeviceCaptureController)SelectedDeviceContext.CaptureController;
                IEnumerable<WebServiceCapture.Models.ProxyConnectionModel> Records()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var record in controller.GetCaptureRecords())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return new WebServiceCapture.Models.ProxyConnectionModel(record);
                    }
                }
                return await Task.Run(() => WebServiceCapture.FiddlerSazHandler.CreateSanitizedSazFile(toFilePath,
                    Records(), cancellationToken));
            }
        }

        internal ICaptureDeviceContext AddDeviceContext(
            DeviceType deviceType,
            string deviceName,
            CaptureType captureType,
            bool readOnly,
            object serializedParameters,
            bool deferPortAllocation = false,
            Func<ICaptureDeviceContext> createContext = null)
        {
            var newCaptureDeviceModel = GetDeviceContext(
                deviceType,
                deviceName,
                captureType,
                readOnly);

            if (newCaptureDeviceModel == null)
            {
                newCaptureDeviceModel = createContext?.Invoke() ?? new CaptureDeviceContextModel(
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
                    CaptureDevicesTable.AddRow(
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

            var index = _captureDeviceContexts.IndexOf(captureDeviceContext);
            _captureDeviceContexts.Remove(captureDeviceContext);
            if (ReferenceEquals(SelectedDeviceContext, captureDeviceContext))
            {
                SelectedDeviceContext = _captureDeviceContexts.Count == 0 ? null :
                    _captureDeviceContexts[Math.Min(Math.Max(index, 0), _captureDeviceContexts.Count - 1)];
            }
            InvalidateCommands();
        }

        internal static bool SameProxyDevice(DeviceType type, string first, string second) =>
            type == DeviceType.LocalPC ||
            (type == DeviceType.XboxConsole
                ? string.Equals(
                    XMAT.WebServiceCapture.ProxyEndpoint.NormalizeHost(first),
                    XMAT.WebServiceCapture.ProxyEndpoint.NormalizeHost(second),
                    StringComparison.OrdinalIgnoreCase)
                : first == second);

        internal AnalysisRunModel AddAnalysisRun(ICaptureAnalyzer analyzer)
        {
            var headerText = $"{analyzer.Description} [{DateTime.Now}]";
            var id = (Int64)DateTime.Now.ToFileTimeUtc();
            var analysisRun = new AnalysisRunModel(analyzer, id)
            {
                Header = headerText
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

        private sealed class CaptureContextCollection : ObservableCollection<ICaptureDeviceContext>
        {
            internal void AppendWithoutNotification(ICaptureDeviceContext context) => Items.Add(context);

            internal void NotifyReplacement()
            {
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        private sealed class CaptureFileLoadLease(CaptureAppModel owner) : IDisposable
        {
            private CaptureAppModel _owner = owner;
            public void Dispose()
            {
                var model = Interlocked.Exchange(ref _owner, null);
                if (model == null)
                    return;
                lock (model._analysisStateLock)
                { model._isCaptureFileLoadRunning = false; }
                model.RaisePropertyChange(nameof(IsCaptureFileLoadRunning));
            }
        }
    }
}
