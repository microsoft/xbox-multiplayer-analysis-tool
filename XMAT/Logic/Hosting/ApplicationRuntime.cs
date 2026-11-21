// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.AgentConnection;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Hosting
{
    internal enum ApplicationHostMode { Desktop, Headless }
    internal enum ApplicationRuntimeState { Created, Starting, Ready, Stopping, Stopped, Failed }

    internal sealed class ApplicationRuntime : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Dispatcher _dispatcher;
        private readonly bool _ownsPolicy;
        private readonly Action<CaptureAppModel> _initializeDatabase;
        private readonly Action<CaptureAppModel, bool> _initializeMethods;
        private readonly Action<CaptureAppModel> _loadPreferences;
        private readonly IReadOnlyList<ICaptureAnalyzer> _analyzers;
        private readonly Action _shutdownMethods;
        private readonly AgentInstanceDirectory _instances;
        private readonly AgentInstanceRecord _instance;
        private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
        private IDisposable _dispatchBinding;
        private ApplicationCaptureSessions _captureSessions;
        private AgentAccessHost _host;
        private AgentInstanceRecord _lastQueuedRecord;
        private Task _publishing = Task.CompletedTask;
        private Task _disposing;
        private bool _databaseInitialized;
        private bool _methodsInitialized;
        private volatile ApplicationRuntimeState _state;

        internal CaptureAppModel Model { get; }
        internal AgentAccessPolicyService Policy { get; }
        internal ApplicationHostMode Mode { get; }
        internal Guid InstanceId => _instance.InstanceId;
        internal ApplicationRuntimeState State { get => _state; private set => _state = value; }
        internal IReadOnlyList<string> Diagnostics
        {
            get { lock (_gate) { return _diagnostics.Order(StringComparer.Ordinal).ToArray(); } }
        }

        internal ApplicationRuntime(
            Dispatcher dispatcher, ApplicationHostMode mode, CaptureAppModel model = null,
            AgentAccessPolicyService policy = null,
            Action<CaptureAppModel> initializeDatabase = null,
            Action<CaptureAppModel, bool> initializeMethods = null,
            Action<CaptureAppModel> loadPreferences = null,
            IReadOnlyList<ICaptureAnalyzer> analyzers = null, Action shutdownMethods = null,
            AgentInstanceDirectory instances = null, Guid? instanceId = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            ApplicationDispatch.VerifyUiAccess(dispatcher);
            Mode = mode;
            Model = model ?? CaptureAppModel.AppModel;
            Policy = policy ?? new();
            _ownsPolicy = policy == null;
            _initializeDatabase = initializeDatabase ?? (target => target.InitializeDatabase());
            _initializeMethods = initializeMethods ?? InitializeMethods;
            _loadPreferences = loadPreferences ?? CaptureAppSettings.Deserialize;
            _analyzers = analyzers ?? new[]
            {
                XboxLiveCaptureAnalyzer.Analyzer, NetworkTraceCaptureAnalyzer.Analyzer
            };
            _shutdownMethods = shutdownMethods ?? ShutdownMethods;
            _instances = instances ?? new(Path.Combine(AppContext.BaseDirectory, "XboxMultiplayerAnalysisTool.exe"));
            _instance = AgentInstanceRecord.ForCurrentProcess(instanceId ?? Guid.NewGuid(),
                mode == ApplicationHostMode.Headless ? "headless" : "desktop", "starting", Policy.Current.Settings.Enabled);
            Model.ConfigureAgentPolicy(Policy, mode == ApplicationHostMode.Headless, _instance.InstanceId);
        }

        internal void Initialize()
        {
            ApplicationDispatch.VerifyUiAccess(_dispatcher);
            if (State != ApplicationRuntimeState.Created)
                throw new InvalidOperationException("The application runtime has already started.");
            State = ApplicationRuntimeState.Starting;
            _dispatchBinding = ApplicationDispatch.BindRuntime(_dispatcher);
            _ = PublishAndReportAsync();
            try
            {
                Localization.LoadLanguage("en-us");
                _initializeDatabase(Model);
                _databaseInitialized = true;
                _initializeMethods(Model, Mode == ApplicationHostMode.Headless);
                _methodsInitialized = true;
                _loadPreferences(Model);
                Localization.LoadLanguage(Model.PreferencesModel.Language);
                _captureSessions = new(Model);
                foreach (var analyzer in _analyzers)
                {
                    try
                    { Model.AddCaptureAnalyzer(analyzer); }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                    { AddDiagnostic("AnalyzerInitializationFailed"); }
                }
            }
            catch
            {
                State = ApplicationRuntimeState.Failed;
                _ = PublishAndReportAsync();
                throw;
            }
        }

        internal Task AdvertiseStartingAsync() => PublishAndReportAsync();

        internal async Task MarkReadyAsync()
        {
            ApplicationDispatch.VerifyUiAccess(_dispatcher);
            if (State != ApplicationRuntimeState.Starting)
                throw new InvalidOperationException("The runtime is not ready to accept connections.");
            _host = Model.AgentAccess;
            _host.Changed += OnHostChanged;
            _host.MarkReady();
            await _host.SynchronizePolicyAsync();
            State = ApplicationRuntimeState.Ready;
            await PublishStateAsync();
        }

        private void OnHostChanged(object sender, EventArgs args) => _ = PublishAndReportAsync();

        private async Task PublishAndReportAsync()
        {
            try
            { await PublishStateAsync(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { AddDiagnostic("InstanceAdvertisementFailed"); }
        }

        private Task PublishStateAsync()
        {
            lock (_gate)
            {
                var state = State switch
                {
                    ApplicationRuntimeState.Ready => _host?.IsAccepting == true ? "ready" :
                        Policy.Current.Settings.Enabled ? "stopping" : "disabled",
                    ApplicationRuntimeState.Stopping or ApplicationRuntimeState.Stopped => "stopping",
                    ApplicationRuntimeState.Failed => "failed",
                    _ => "starting"
                };
                var record = _instance with { State = state, Enabled = Policy.Current.Settings.Enabled };
                if (record == _lastQueuedRecord)
                    return _publishing;
                _lastQueuedRecord = record;
                _publishing = _publishing.ContinueWith(previous =>
                {
                    _ = previous.Exception;
                    _instances.Publish(record);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return _publishing;
            }
        }

        private void AddDiagnostic(string diagnostic)
        {
            lock (_gate)
            { _diagnostics.Add(diagnostic); }
        }

        private static void InitializeMethods(CaptureAppModel model, bool headless)
        {
            WebServiceCaptureMethod.MethodInstance.InitializeData(model);
            NetworkTraceCaptureMethod.Method.Initialize(model);
            model.PreferencesModel.WebProxyCapturePreferences = WebServiceCaptureMethod.Method.PreferencesModel;
            model.PreferencesModel.NetworkCapturePreferences = NetworkTraceCaptureMethod.Method.PreferencesModel;
            model.PreferencesModel.NetworkAnalysisPreferences = NetworkTraceCaptureAnalyzer.Analyzer.PreferencesModel;
        }

        private static void ShutdownMethods()
        {
            WebServiceCaptureMethod.Method.Shutdown();
            NetworkTraceCaptureMethod.Method.Shutdown();
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposing != null)
                    return new(_disposing);
                if (Model.IsAnyCaptureRunning || Model.IsAnalysisRunning ||
                    Model.IsCaptureDataOperationRunning || Model.IsCaptureFileLoadRunning)
                    throw new InvalidOperationException("Stop and drain application work before shutting down.");
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposing = completion.Task;
            }
            _ = CompleteDisposalAsync(completion);
            return new(completion.Task);
        }

        private async Task CompleteDisposalAsync(TaskCompletionSource completion)
        {
            try
            {
                await ApplicationDispatch.RunOnUiAsync(_dispatcher, DisposeCoreAsync);
                completion.TrySetResult();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                lock (_gate)
                { _disposing = null; }
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeCoreAsync()
        {
            State = ApplicationRuntimeState.Stopping;
            await PublishAndReportAsync();
            if (_host != null)
                _host.Changed -= OnHostChanged;
            try
            {
                if (Model.HasAgentAccess)
                {
                    Model.AgentAccess.PauseRequests();
                    await Model.AgentAccess.DisableAsync();
                    if (Model.AgentAccess.Runtime.GetOwnedResources().Any(resource => resource.State == AgentResourceState.RecoveryRequired))
                        throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                }
                await Model.DisposeSqlWorkspaceAsync();
                await Model.DisposeInvestigationAsync();
                await Model.CaptureReadonlySources.DisposeAsync();
                await Task.WhenAll(Model.CaptureSazImports.DisposeAsync().AsTask(),
                    Model.CaptureEtlImports.DisposeAsync().AsTask(), Model.CaptureFileLoads.DisposeAsync().AsTask());
                foreach (var analyzer in Model.CaptureAnalyzers)
                    analyzer.Shutdown();
                if (_methodsInitialized)
                    _shutdownMethods();
                _captureSessions?.Dispose();
                if (Model.HasAgentAccess)
                    await Model.AgentAccess.DisposeAsync();
                if (_databaseInitialized)
                {
                    Model.ActiveDatabase.Shutdown();
                    await Model.HostLifecycle.DatabaseDisposedAsync();
                }
                _dispatchBinding?.Dispose();
                if (_ownsPolicy)
                    await Policy.DisposeAsync();
                try
                { await Task.Run(() => _instances.Remove(_instance)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { AddDiagnostic("InstanceAdvertisementCleanupFailed"); }
                State = ApplicationRuntimeState.Stopped;
            }
            catch
            {
                State = ApplicationRuntimeState.Failed;
                AddDiagnostic("ShutdownBlocked");
                await PublishAndReportAsync();
                throw;
            }
        }

        internal async Task ResumeAfterBlockedShutdownAsync()
        {
            ApplicationDispatch.VerifyUiAccess(_dispatcher);
            Model.HostLifecycle.CancelPrepared();
            if (Model.HasAgentAccess)
            {
                Model.AgentAccess.ResumeRequests();
                await Model.AgentAccess.SynchronizePolicyAsync();
            }
            State = ApplicationRuntimeState.Ready;
            if (_host != null)
            {
                _host.Changed -= OnHostChanged;
                _host.Changed += OnHostChanged;
            }
            await PublishAndReportAsync();
        }
    }
}
