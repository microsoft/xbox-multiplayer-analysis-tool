// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentAccessHost : IAsyncDisposable
    {
        private const int MaximumConnections = 8;
        private readonly object _gate = new();
        private readonly CaptureAppModel _model;
        private readonly Dictionary<Guid, Connection> _connections = new();
        private readonly CaptureCatalog _catalog;
        private readonly AgentCaptureApplication _application;
        private readonly AgentQueryApplication _queries;
        private readonly AgentDataApplication _data;
        private readonly AgentScriptApplication _scripts;
        private readonly AgentDeviceApplication _devices;
        private readonly AgentProxyApplication _proxy;
        private readonly AgentCaptureFileApplication _captureFiles;
        private readonly AgentClearApplication _clear;
        private readonly AgentAdministrationApplication _administration;
        private readonly ICaptureQueries _captureQueries;
        private CancellationTokenSource _listening;
        private Task _accepting = Task.CompletedTask;
        private Task _stopping;
        private bool _ready;
        private volatile bool _paused;

        internal Guid InstanceId { get; } = Guid.NewGuid();
        internal AgentRuntime Runtime { get; } = new();
        internal bool IsEnabled => Runtime.IsEnabled;
        internal bool IsAccepting => IsEnabled && !_paused;
        internal event EventHandler Changed;

        internal AgentAccessHost(
            CaptureAppModel model,
            Dispatcher dispatcher,
            ICaptureOperations operations = null,
            ICaptureDevices devices = null,
            ICaptureProxyOperations proxyOperations = null,
            ICaptureEtlImports etlImports = null)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(dispatcher);
            _model = model;
            _catalog = model.CaptureCatalog;
            _application = new AgentCaptureApplication(Runtime, _catalog, operations ?? model.CaptureOperations,
                () => IsAccepting, model.CaptureRecoveryOperations);
            _captureQueries = model.CaptureQueries;
            _queries = new AgentQueryApplication(Runtime, _captureQueries, () => IsAccepting, model.CaptureFileExports);
            _data = new AgentDataApplication(Runtime, model.CaptureInvestigation, () => IsAccepting,
                model.CaptureFileExports, model);
            _scripts = new AgentScriptApplication(Runtime, model.CaptureScripts, () => IsAccepting,
                model.CaptureFileExports, model.CaptureFileInputs);
            _devices = new AgentDeviceApplication(Runtime, devices ?? model.CaptureDevices, _catalog, () => IsAccepting);
            _proxy = new AgentProxyApplication(Runtime, _catalog, proxyOperations ?? model.CaptureProxyOperations,
                _application, () => IsAccepting);
            _captureFiles = new AgentCaptureFileApplication(Runtime, model.CaptureQueries, model.CaptureFiles,
                model.CaptureFileExports, () => IsAccepting, model.CaptureFileInputs, model.CaptureFileLoads,
                model.CaptureSazImports, etlImports ?? model.CaptureEtlImports);
            _clear = new AgentClearApplication(Runtime, _catalog, model.CaptureDataOperations, () => IsAccepting);
            _administration = new AgentAdministrationApplication(Runtime, model.HostPolicyOperations,
                model.ApplicationPreferences, model.ApplicationDiagnostics, model.DesktopActions,
                () => IsAccepting, model.CaptureFileExports);
            Runtime.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            model.PropertyChanged += ModelPropertyChanged;
            model.CaptureDeviceContexts.CollectionChanged += DeviceContextsChanged;
        }

        private void DeviceContextsChanged(object sender, NotifyCollectionChangedEventArgs args) =>
            Changed?.Invoke(this, EventArgs.Empty);

        private void ModelPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(CaptureAppModel.ActiveDatabase) or nameof(CaptureAppModel.LoadedDatabase))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        internal void MarkReady() => _ready = true;
        internal void PauseRequests() => _paused = true;
        internal void ResumeRequests() => _paused = false;
        internal Task<IReadOnlyList<CaptureDescriptor>> GetCapturesAsync() => _catalog.GetCapturesAsync();
        internal Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync() => _model.GetInvestigationSourcesAsync();
        internal IReadOnlyList<ExportFolderDescriptor> GetExportFolders() => _model.CaptureFileExports.GetFolders();
        internal Task<ExportFolderDescriptor> RegisterExportFolderAsync(string path) =>
            _model.CaptureFileExports.RegisterFolderAsync(path);
        internal IReadOnlyList<InputFileDescriptor> GetInputFiles() => _model.CaptureFileInputs.GetInputs();
        internal async Task<InputFileDescriptor> RegisterInputFileAsync(string path, long maximumBytes = 512L * 1024 * 1024)
        {
            var input = await _model.CaptureFileInputs.RegisterAsync(path, maximumBytes);
            Changed?.Invoke(this, EventArgs.Empty);
            return input;
        }

        internal void Enable()
        {
            lock (_gate)
            {
                if (!_ready || _paused || _stopping is { IsCompletedSuccessfully: false })
                {
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                }
                if (IsEnabled)
                {
                    return;
                }
                _stopping = null;
                _listening = new CancellationTokenSource();
                var pipe = CreatePipe();
                Runtime.Enable();
                _accepting = AcceptAsync(pipe, _listening.Token);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal Task DisableAsync()
        {
            TaskCompletionSource completion;
            CancellationTokenSource listening;
            Task accepting;
            Connection[] connections;
            bool wasPaused;
            lock (_gate)
            {
                if (_stopping != null)
                {
                    return _stopping;
                }
                wasPaused = _paused;
                _paused = true;
                listening = _listening;
                accepting = _accepting;
                connections = _connections.Values.ToArray();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = completion.Task;
            }
            _ = StopCoreAsync(listening, accepting, connections, wasPaused, completion);
            return completion.Task;
        }

        private async Task StopCoreAsync(
            CancellationTokenSource listening,
            Task accepting,
            Connection[] connections,
            bool wasPaused,
            TaskCompletionSource completion)
        {
            try
            {
                var draining = Runtime.DisableAsync();
                if (listening != null)
                {
                    await listening.CancelAsync();
                }
                foreach (var connection in connections)
                {
                    await CancelConnectionAsync(connection);
                }
                await accepting;
                // Include connections accepted immediately before listening was cancelled.
                lock (_gate)
                { connections = _connections.Values.ToArray(); }
                foreach (var connection in connections)
                {
                    await CancelConnectionAsync(connection);
                }
                await draining;
                await Task.WhenAll(connections.Select(connection => connection.Worker));
                await _queries.DisposeResultsAsync();
                await _data.DisposeResultsAsync();
                await _scripts.DisposeResultsAsync();
                await _administration.DrainAsync();
                listening?.Dispose();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                _paused = wasPaused;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        internal AgentSession FindSession(Guid id)
        {
            lock (_gate)
            {
                return _connections.TryGetValue(id, out var connection) ? connection.Session :
                    throw new AgentAccessException(AgentAccessError.SessionClosed);
            }
        }

        internal async Task RevokeAsync(Guid id)
        {
            Connection connection;
            lock (_gate)
            {
                connection = _connections.TryGetValue(id, out var found) ? found :
                    throw new AgentAccessException(AgentAccessError.SessionClosed);
            }
            var ending = Runtime.EndSessionAsync(connection.Session, revoked: true);
            await CancelConnectionAsync(connection);
            await ending;
        }

        internal string ConnectionConfiguration
        {
            get
            {
                using var process = Process.GetCurrentProcess();
                var command = Path.Combine(AppContext.BaseDirectory, "AgentBridge", "XMAT.AgentBridge.exe");
                return JsonSerializer.Serialize(new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["xmat"] = new
                        {
                            type = "stdio",
                            command,
                            args = new[]
                            {
                                "--instance", InstanceId.ToString("D"),
                                "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "--started", process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            },
                            tools = new[] { "*" }
                        }
                    }
                }, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        private NamedPipeServerStream CreatePipe() => new(
            $"XMAT.Agent.{InstanceId:N}", PipeDirection.InOut, MaximumConnections + 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task AcceptAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    lock (_gate)
                    {
                        if (_connections.Count >= MaximumConnections || !IsAccepting)
                        {
                            pipe.Dispose();
                        }
                        else
                        {
                            Runtime.TrimDisconnectedHistory();
                            var session = Runtime.Connect("MCP client");
                            var connection = new Connection(session,
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                            _connections.Add(session.Id, connection);
                            var connected = pipe;
                            connection.Worker = Task.Run(() => ServeAsync(connection, connected), CancellationToken.None);
                        }
                    }
                    pipe = CreatePipe();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                Trace.TraceError("The XMAT agent listener stopped.");
                _ = Task.Run(() => DisableAsync(), CancellationToken.None);
            }
            finally { pipe.Dispose(); }
        }

        private async Task ServeAsync(Connection connection, NamedPipeServerStream pipe)
        {
            try
            {
                await using var bounded = new BoundedMcpInputStream(pipe);
                McpServer server = null;
                var tools = new AgentMcpTools(Runtime, connection.Session, _application, _queries, _data, _scripts, _devices, _proxy, _captureFiles, _clear, _administration,
                    () => server?.ClientInfo?.Name ?? "MCP client");
                server = McpServer.Create(new StreamServerTransport(bounded, pipe), new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "XMAT", Version = "0.3.0" },
                    ToolCollection = tools.Create()
                });
                await using (server)
                {
                    await server.RunAsync(connection.Cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested) { }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                Trace.TraceError("An XMAT agent connection ended.");
            }
            finally
            {
                pipe.Dispose();
                try
                {
                    await Runtime.EndSessionAsync(connection.Session);
                }
                catch (AgentAccessException exception) when (exception.Error == AgentAccessError.SessionClosed) { }
                finally
                {
                    lock (_gate)
                    { _connections.Remove(connection.Session.Id); }
                    connection.Cancellation.Dispose();
                }
            }
        }

        private static async Task CancelConnectionAsync(Connection connection)
        {
            try
            { await connection.Cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _model.PropertyChanged -= ModelPropertyChanged;
            _model.CaptureDeviceContexts.CollectionChanged -= DeviceContextsChanged;
            await DisableAsync();
            await _administration.DisposeAsync();
            await Runtime.DisposeAsync();
        }

        private sealed class Connection(AgentSession session, CancellationTokenSource cancellation)
        {
            internal AgentSession Session { get; } = session;
            internal CancellationTokenSource Cancellation { get; } = cancellation;
            internal Task Worker { get; set; } = Task.CompletedTask;
        }
    }
}
