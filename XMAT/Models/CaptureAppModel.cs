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

    internal sealed record CaptureLoadResult(
        CaptureTextSource Source,
        bool AddedOrReplaced);

    internal partial class CaptureAppModel : ICaptureAppModel, INotifyPropertyChanged
    {
        private IDatabase _activeDatabase;
        private IReadonlyDatabase _loadedDatabase;
        private IDisposable _loadedWorkspaceLease;
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
                var previousLease = _loadedWorkspaceLease;
                _loadedWorkspaceLease = null;
                _loadedDatabase = value;
                _loadedCaptureRevision++;
                LoadedCaptureDisplayName = null;
                previousLease?.Dispose();
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
                ? proxy.ImportNotice ?? proxy.PortStatus : _statusBarText3;
            set { _statusBarText3 = value; RaisePropertyChange(); }
        }

        internal static readonly CaptureAppModel AppModel = new();

        internal ApplicationServices.CaptureOperations CaptureOperations { get; }
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
        internal ICaptureReadonlySources CaptureReadonlySources { get; }
        internal ApplicationServices.ApplicationShutdown HostLifecycle { get; }
        internal ICaptureFileLoads CaptureFileLoads { get; }
        internal ICaptureSazImports CaptureSazImports { get; }
        internal ICaptureEtlImports CaptureEtlImports { get; }
        internal ICaptureFiles CaptureFiles { get; }
        private AgentAccess.AgentAccessHost _agentAccess;
        private AgentAccess.AgentAccessPolicyService _agentPolicy;
        private Guid? _agentInstanceId;
        private readonly object _agentAccessGate = new();
        internal AgentAccess.AgentAccessHost AgentAccess
        {
            get
            {
                lock (_agentAccessGate)
                    return _agentAccess ??= new(this, _dispatcher, policy: _agentPolicy, instanceId: _agentInstanceId);
            }
        }
        internal bool HasAgentAccess
        {
            get { lock (_agentAccessGate) { return _agentAccess != null; } }
        }
        internal bool IsHeadless { get; private set; }
        internal bool DeferProxyCreation => _agentPolicy != null;

        internal void ConfigureAgentPolicy(
            AgentAccess.AgentAccessPolicyService policy, bool headless = false, Guid? instanceId = null)
        {
            ApplicationDispatch.VerifyUiAccess(_dispatcher);
            ArgumentNullException.ThrowIfNull(policy);
            lock (_agentAccessGate)
            {
                if (_agentAccess != null)
                    throw new InvalidOperationException("Configure the agent policy before creating the host.");
                _agentPolicy = policy;
                _agentInstanceId = instanceId;
                IsHeadless = headless;
            }
        }

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
        // XMAT is exiting (BeginShutdown): no GUI replay starts, and a running one adds no more rows.
        internal bool IsShuttingDown
        {
            get
            {
                lock (_analysisStateLock)
                {
                    return _isShuttingDown;
                }
            }
        }
        internal bool IsClosing
        {
            get
            {
                lock (_analysisStateLock)
                {
                    return _isClosing;
                }
            }
        }


        // The rows that this session's Replays tab recorded, which every composer's history shows; never saved.
        internal ReplayHistory ReplayHistory { get; }
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
            !IsClosing &&
            !IsAnyCaptureRunning &&
            !IsAnalysisRunning &&
            !IsCaptureDataOperationRunning;
        internal bool CanModifyCaptureData =>
            !IsClosing &&
            !IsAnalysisRunning &&
            !IsCaptureDataOperationRunning &&
            HostLifecycle?.IsClosing != true;
        internal bool CanBeginCaptureDataOperation =>
            CanModifyCaptureData &&
            !IsAnyCaptureRunning;
        // Stop waits for a capture-data operation such as an export, which reads the running capture until it publishes.
        internal bool CanStopCaptures => !IsClosing && !IsAnalysisRunning && !IsCaptureDataOperationRunning;
        // File > Export and Export Sanitized: the selected web capture, running or not, when it has sessions.
        internal bool CanExportSelectedCapture =>
            CanModifyCaptureData &&
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
        private bool _isClosing;

        // The name of the read-only tab that GUI replays and composer sends are recorded in (GetOrAddReplayContext), which is
        // also the device name of its rows in the shared capture table. No tab that writes to that table can have it, or a
        // name that starts with it, so their rows never mix with replays: console names have no spaces, the fixed device
        // names and imports' names ("Fiddler - ...") start otherwise, and AddDeviceContext refuses it. Only tabs loaded
        // from a saved capture can have it, and they read their own copy of the rows.
        internal const string ReplayContextName = "XMAT Replays";

        // A name that AddDeviceContext refuses, because it is the replay tab's (ReplayContextName).
        internal static bool IsReplayContextName(string deviceName) =>
            deviceName != null && deviceName.StartsWith(ReplayContextName, StringComparison.Ordinal);

        // The read-only tab that GUI replays are recorded in (GetOrAddReplayContext); null until one is needed.
        private ICaptureDeviceContext _replayContext;

        // The running capture-data operation, if closing XMAT may cancel it and wait for it to end, such as a GUI replay,
        // the task that completes when it has ended and its lease; and whether XMAT is shutting down (BeginShutdown).
        // Guarded by _analysisStateLock.
        private CancellationTokenSource _cancellableOperation;
        private TaskCompletionSource _cancellableOperationEnded;
        private CancellableOperationLease _cancellableOperationLease;
        private bool _isShuttingDown;

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
                ApplicationDispatch.QueueOnUi<IReadOnlyList<string>>(_dispatcher, () => new[] { LoadedCaptureFilePath }).Task);
            CaptureFileInputs = new ApplicationServices.CaptureFileInputs();
            CaptureFileLoads = new ApplicationServices.CaptureFileLoads(this, _dispatcher, queries, CaptureFileInputs);
            CaptureReadonlySources = new ApplicationServices.CaptureReadonlySources(CaptureFileInputs, CaptureFileLoads);
            HostLifecycle = new(this, _dispatcher);
            CaptureSazImports = new ApplicationServices.CaptureSazImports(this, _dispatcher, queries, CaptureFileInputs);
            CaptureEtlImports = new ApplicationServices.CaptureEtlImports(this, _dispatcher, queries, CaptureFileInputs);
            PreferencesModel = new();
            ReplayHistory = new ReplayHistory(this);
            _captureDeviceContexts.CollectionChanged += CaptureDeviceContexts_CollectionChanged;

            foreach (var context in _captureDeviceContexts)
            {
                TrackCaptureController(context.CaptureController);
            }
        }

        internal async Task<bool> CanCloseAsync()
        {
            lock (_analysisStateLock)
            {
                if (_isShuttingDown)
                {
                    return true;
                }
                if (_isClosing)
                {
                    return false;
                }

                _isClosing = true;
            }

            RaisePropertyChange(nameof(IsClosing));
            InvalidateCommands();

            // A replay is cancelled and waited for, so nothing it still adds races the shutdown; other operations, such as
            // an import, have to finish first.
            bool canClose = false;
            try
            {
                if (IsCaptureFileLoadRunning ||
                    (IsCaptureDataOperationRunning && !await CancelCaptureDataOperationAsync()))
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

                canClose = true;
                return true;
            }
            finally
            {
                if (!canClose)
                {
                    CancelClosePreparation();
                }
            }
        }

        internal void CancelClosePreparation()
        {
            bool changed = false;
            lock (_analysisStateLock)
            {
                if (!_isShuttingDown && _isClosing)
                {
                    _isClosing = false;
                    changed = true;
                }
            }

            if (changed)
            {
                RaisePropertyChange(nameof(IsClosing));
                InvalidateCommands();
            }
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
                    _isClosing ||
                    _isShuttingDown ||
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
            bool requireAllCapturesStopped = false) =>
            TryBeginCaptureDataOperationCore(out operationLease, requireAllCapturesStopped, stoppingForClose: false);

        internal bool TryBeginCaptureStopForClose(out IDisposable operationLease) =>
            TryBeginCaptureDataOperationCore(out operationLease, requireAllCapturesStopped: false, stoppingForClose: true);

        private bool TryBeginCaptureDataOperationCore(
            out IDisposable operationLease, bool requireAllCapturesStopped, bool stoppingForClose)
        {
            lock (_analysisStateLock)
            {
                if (_isClosing != stoppingForClose ||
                    _isShuttingDown ||
                    _isAnalysisRunning ||
                    _isCaptureDataOperationRunning ||
                    (stoppingForClose && (_isCaptureFileLoadRunning || HostLifecycle.IsClosing)) ||
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
                if (_isClosing || _isShuttingDown || _isCaptureFileLoadRunning)
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

        // Begins a capture-data operation that closing XMAT may cancel through cancellation and then wait for, instead of
        // refusing to close: a GUI replay (CancelCaptureDataOperationAsync). Disposing the lease ends the operation. None
        // begins once XMAT is shutting down (BeginShutdown).
        internal bool TryBeginCancellableCaptureDataOperation(
            CancellationTokenSource cancellation,
            out IDisposable operationLease)
        {
            ArgumentNullException.ThrowIfNull(cancellation);
            if (IsShuttingDown || !TryBeginCaptureDataOperation(out IDisposable lease))
            {
                operationLease = null;
                return false;
            }

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellableLease = new CancellableOperationLease(this, lease, ended);
            lock (_analysisStateLock)
            {
                _cancellableOperation = cancellation;
                _cancellableOperationEnded = ended;
                _cancellableOperationLease = cancellableLease;
            }

            operationLease = cancellableLease;
            return true;
        }

        // Cancels the running capture-data operation if it may be cancelled (TryBeginCancellableCaptureDataOperation) and
        // waits until it has ended. False when an operation that cannot be cancelled is still running. The operation is
        // revoked too (IsCaptureDataOperationRevoked): XMAT takes it back to close, also when its user had cancelled it, so
        // what it would still do once it ends, such as selecting a row that it recorded, must not change what XMAT shows.
        internal async Task<bool> CancelCaptureDataOperationAsync()
        {
            CancellationTokenSource cancellation;
            Task ended;
            lock (_analysisStateLock)
            {
                cancellation = _cancellableOperation;
                ended = _cancellableOperationEnded?.Task;
                if (_cancellableOperationLease != null)
                {
                    _cancellableOperationLease.Revoked = true;
                }
            }

            if (cancellation != null)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The operation ended and disposed its cancellation meanwhile; ended is complete.
                }

                await ended;
            }

            return !IsCaptureDataOperationRunning;
        }

        // Whether XMAT revoked the cancellable capture-data operation that this lease holds
        // (TryBeginCancellableCaptureDataOperation), as CancelCaptureDataOperationAsync does when XMAT closes. It stays
        // revoked once it has ended; another operation has another lease.
        internal bool IsCaptureDataOperationRevoked(IDisposable operationLease)
        {
            lock (_analysisStateLock)
            {
                return operationLease is CancellableOperationLease { Revoked: true };
            }
        }

        // For XMAT exiting without its main window's Closing, which cancels a GUI replay and waits for it (CanCloseAsync):
        // Windows ending the session, or Application.Shutdown. Cancels a running GUI replay without waiting for it, since
        // the UI thread that calls this is the one that adds the replay's rows, and from now on no replay starts and none
        // adds rows (IsShuttingDown). A request that was being sent may still reach the server.
        // This is terminal: nothing undoes it, so call it only once XMAT will exit. A handler that lets the exit still be
        // cancelled, such as SessionEnding setting e.Cancel, must not call it; it would need a reversible preparation that
        // pauses replays and can resume them.
        internal void BeginShutdown()
        {
            CancellationTokenSource cancellation;
            lock (_analysisStateLock)
            {
                _isShuttingDown = true;
                _isClosing = true;
                cancellation = _cancellableOperation;
            }

            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation ended and disposed its cancellation meanwhile.
            }
        }

        private void EndCancellableOperation(TaskCompletionSource ended)
        {
            lock (_analysisStateLock)
            {
                if (ReferenceEquals(_cancellableOperationEnded, ended))
                {
                    _cancellableOperation = null;
                    _cancellableOperationEnded = null;
                    _cancellableOperationLease = null;
                }
            }
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
            ActiveDatabase.Initialize();

            CaptureDevicesTable = ApplicationServices.CaptureDatabaseSchema.CreateDevices(ActiveDatabase);
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
                Console.Error.WriteLine($"Analyzer initialization failed ({ex.GetType().Name}).");
                throw;
            }
        }

        internal void CommitLoadedCapture(
            IReadonlyDatabase database, string path, string displayName,
            IReadOnlyList<ICaptureDeviceContext> addedContexts,
            IReadOnlyList<ICaptureDeviceContext> owners = null)
        {
            var previousLease = _loadedWorkspaceLease;
            _loadedWorkspaceLease = database is SqLtReadonlyDatabase native
                ? LoadedCaptureDatabaseRegistry.AcquireSource(native, path) : null;
            RegisterPreparedContexts(addedContexts);
            if (database is SqLtReadonlyDatabase loaded)
                foreach (var context in owners ?? addedContexts)
                    LoadedCaptureDatabaseRegistry.Assign(context.CaptureController, loaded);
            _loadedDatabase = database;
            LoadedCaptureFilePath = path;
            LoadedCaptureDisplayName = displayName;
            _loadedCaptureRevision++;
            previousLease?.Dispose();
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
        internal void NotifyCaptureDataRebound(ICaptureDeviceContext context, IQueryDatabase source)
        {
            if (source is SqLtReadonlyDatabase database && _captureDeviceContexts.Contains(context))
                LoadedCaptureDatabaseRegistry.Assign(context.CaptureController, database);
            else
                LoadedCaptureDatabaseRegistry.Release(context.CaptureController);
            RefreshAnalysisSources();
            CaptureDataRebound?.Invoke(context, source);
        }

        internal void ReleaseLoadedWorkspace(IReadonlyDatabase database)
        {
            if (!ReferenceEquals(_loadedDatabase, database))
                return;
            var lease = _loadedWorkspaceLease;
            _loadedWorkspaceLease = null;
            lease?.Dispose();
        }

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

        internal CaptureLoadResult LoadDataCaptures(
            string fromFilePath) =>
            LoadDataCaptures(
                fromFilePath,
                path =>
                    DataAbstractionLayer.DataAbstractionLayer
                        .LoadDatabaseFromFile(path) as
                        SqLtReadonlyDatabase ??
                    throw new InvalidOperationException(
                        "The loaded capture database has an unsupported implementation."));

        internal CaptureLoadResult LoadDataCaptures(
            string fromFilePath,
            Func<string, SqLtReadonlyDatabase> openDatabase)
        {
            SqLtReadonlyDatabase opened =
                openDatabase(fromFilePath);
            CaptureTextSource source =
                opened.CaptureTextSource;
            bool addedOrReplaced = false;
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
                    if (device.CaptureController is WebServiceDeviceCaptureController shapingController)
                    {
                        shapingController.ImportedShaping = Operations.Captures.NetworkShapingMetadata.Parse(
                            record.Str(Operations.Captures.NetworkShapingMetadata.DeviceField));
                    }
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
                addedOrReplaced =
                    added.Count != 0 ||
                    transfers.Count != 0;
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
            return new CaptureLoadResult(
                source,
                addedOrReplaced);
        }

        private static Action CreateLoadedCaptureTransfer(
            IDeviceCaptureController existing,
            IDeviceCaptureController staged) =>
            (existing, staged) switch
            {
                (
                    WebServiceDeviceCaptureController target,
                    WebServiceDeviceCaptureController source) =>
                    () => target.ReplaceLoadedCapturesFrom(source),
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
                    await ImportEtlDesktopAsync(fromFilePath);
                    return;
                }
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
            bool deferPortAllocation = false,
            Func<ICaptureDeviceContext> createContext = null)
        {
            // Rows are kept by device name in the shared capture table, and the replay tab's name is its own.
            if (IsReplayContextName(deviceName))
            {
                throw new ArgumentException(
                    "This name is reserved for the tab that replays are recorded in.", nameof(deviceName));
            }

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
                    HostListPolicy, deferProxyCreation: DeferProxyCreation);
                _captureDeviceContexts.Add(newCaptureDeviceModel);

                if (!readOnly)
                {
                    IDataTable devices = CaptureDevicesTable;
                    bool hasShaping = devices.Dataset.FieldDefs.Any(
                        field => field.Name == Operations.Captures.NetworkShapingMetadata.DeviceField);
                    long deviceRow = hasShaping
                        ? devices.AddRow(
                            (Int64)deviceType,
                            deviceName,
                            (Int64)captureType,
                            string.Empty)
                        : devices.AddRow(
                            (Int64)deviceType,
                            deviceName,
                            (Int64)captureType);
                    if (newCaptureDeviceModel.CaptureController is WebServiceDeviceCaptureController shapingController)
                    {
                        RecordShapingIn(devices, deviceRow, shapingController);
                    }
                }

                InvalidateCommands();
            }

            return newCaptureDeviceModel;
        }

        // The read-only tab where GUI replays and composer sends are recorded (ReplayRecordingTarget), created on first use
        // and again after it is closed. Only the replay feature finds it: name lookups (GetDeviceContext) skip it, so no
        // load, import or new capture reuses it or replaces its rows, and a tab that merely has its name, such as one loaded
        // from a saved capture, is never used for replays. It is named ReplayContextName, or "XMAT Replays (2)" and so on
        // while an open tab has that name. Unlike other read-only tabs it is listed with the capture devices, so that saving
        // keeps it: the saved capture opens it as an ordinary read-only tab.
        internal ICaptureDeviceContext GetOrAddReplayContext()
        {
            if (_replayContext != null)
            {
                if (_captureDeviceContexts.Contains(_replayContext))
                {
                    return _replayContext;
                }

                // Removed without RemoveDeviceContextAsync, it still has rows in the capture table, under a name that the new
                // tab can take: they go first.
                _replayContext.CaptureController.ClearAllCaptures();
                UnlistReplayContext(_replayContext);
                _replayContext = null;
            }

            string deviceName = ReplayContextName;
            for (int suffix = 2; _captureDeviceContexts.Any(context => context.DeviceName == deviceName); suffix++)
            {
                deviceName = $"{ReplayContextName} ({suffix})";
            }

            var context = new CaptureDeviceContextModel(
                DeviceType.Unknown,
                deviceName,
                CaptureType.WebProxy,
                true,
                null,
                hostListPolicy: HostListPolicy);
            CaptureDevicesTable?.AddRow(
                (Int64)context.DeviceType,
                context.DeviceName,
                (Int64)context.CaptureType,
                string.Empty);
            _replayContext = context;
            _captureDeviceContexts.Add(context);
            InvalidateCommands();
            return context;
        }

        private void UnlistReplayContext(ICaptureDeviceContext context) =>
            CaptureDevicesTable?.RemoveRowsWhere(
                new FieldValue<Int64>(@"DeviceType", (Int64)context.DeviceType),
                new FieldValue<string>(@"DeviceName", context.DeviceName),
                new FieldValue<Int64>(@"CaptureType", (Int64)context.CaptureType));

        internal static void RecordShapingIn(
            IDataTable devices,
            long deviceRow,
            WebServiceDeviceCaptureController controller)
        {
            if (!devices.Dataset.FieldDefs.Any(
                field => field.Name == Operations.Captures.NetworkShapingMetadata.DeviceField))
                return;
            controller.ShapingMetadataChanged += (_, _) =>
                devices.UpdateRow(
                    deviceRow,
                    new FieldValue<string>(
                        Operations.Captures.NetworkShapingMetadata.DeviceField,
                        Operations.Captures.NetworkShapingMetadata.Serialize(controller.RecordedShaping)));
        }

        internal ICaptureDeviceContext GetDeviceContext(DeviceType deviceType, string deviceName, CaptureType captureType, bool readOnly)
        {
            // The replay tab is found by reference only (GetOrAddReplayContext).
            return (from cc in _captureDeviceContexts
                    where
                    !ReferenceEquals(cc, _replayContext) &&
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
            else if (ReferenceEquals(captureDeviceContext, _replayContext))
            {
                UnlistReplayContext(captureDeviceContext);
            }

            var index = _captureDeviceContexts.IndexOf(captureDeviceContext);
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
                if (actuallyRemoved &&
                    ReferenceEquals(captureDeviceContext, _replayContext))
                {
                    _replayContext = null;
                }
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
                        if (_loadedWorkspaceLease == null)
                            RefreshLoadedDatabase();
                        else
                            RaisePropertyChange(nameof(LoadedDatabase));
                    }
                    if (actuallyRemoved && ReferenceEquals(SelectedDeviceContext, captureDeviceContext))
                        SelectedDeviceContext = _captureDeviceContexts.Count == 0 ? null :
                            _captureDeviceContexts[Math.Min(Math.Max(index, 0), _captureDeviceContexts.Count - 1)];
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
        // Ends a cancellable capture-data operation, then tells CancelCaptureDataOperationAsync that it has ended.
        private sealed class CancellableOperationLease : IDisposable
        {
            private readonly IDisposable _operationLease;
            private readonly TaskCompletionSource _ended;
            private CaptureAppModel _owner;

            internal CancellableOperationLease(CaptureAppModel owner, IDisposable operationLease, TaskCompletionSource ended)
            {
                _owner = owner;
                _operationLease = operationLease;
                _ended = ended;
            }

            // XMAT took the operation back (CancelCaptureDataOperationAsync). Guarded by the model's _analysisStateLock.
            internal bool Revoked { get; set; }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner == null)
                {
                    return;
                }

                try
                {
                    owner.EndCancellableOperation(_ended);
                    _operationLease.Dispose();
                }
                finally
                {
                    _ended.TrySetResult();
                }
            }
        }
    }
}
