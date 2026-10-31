// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture.Proxy
{
    public struct WebServiceProxyOptions
    {
        public int Port { get; set; }
        public string LogFilePath { get; set; }
        public LogLevel CurrentLogLevel { get; set; }
    }

    internal class WebServiceProxy : IWebServiceProxy
    {
        public event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
        public event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
        public event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
        public event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
        public event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
        public event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
        public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
        public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
        public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
        public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
        public event EventHandler ProxyStopped;

        public bool IsProxyEnabled { get { return State == ProxyState.Running; } }
        public Func<SslConnectionRequestEventArgs, CancellationToken, Task<bool>> SslConnectionRequestPolicyAsync { get; set; }
        public Func<HttpRequestEventArgs, CancellationToken, Task<bool>> WebRequestPolicyAsync { get; set; }
        public Func<HttpResponseEventArgs, CancellationToken, Task<bool>> WebResponsePolicyAsync { get; set; }

        internal ProxyState State
        {
            get { lock (_stateLock) { return _state; } }
        }

        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _transitionGate = new(1, 1);
        private ProxyState _state = ProxyState.Stopped;
        private Task _stopTask;
        private int _pendingStarts;

        // Test seam: cancellation token of the current run, so tests can wait for a stop's cancellation.
        internal CancellationToken RunCancellationToken => _cts?.Token ?? CancellationToken.None;

        // Test seam: called with the phase name after each startup step so tests can inject failures.
        internal Action<string> StartupFaultInjection { get; set; }

        // Test seam: called during teardown, after the hosts are stopped.
        internal Action ShutdownFaultInjection { get; set; }

        // Test seam: called at the end of teardown, just before the log is closed.
        internal Action LogCloseFaultInjection { get; set; }

        private bool _logOpen;

        // One budget for stopping both hosts; tests shorten it.
        internal TimeSpan StopDeadline { get; set; } = ProxyShutdownPolicy.Deadline;

        // Accepted connections that were still open when the deadline expired on the last stop.
        internal int AbortedConnectionCount { get; private set; }

        internal IHost PublicHost => _host;
        internal InterceptedHttpHost InterceptedHost => _interceptedHost;

        // Why the last startup or cleanup failed while the proxy is Faulted; for diagnostics.
        internal Exception FaultException { get; private set; }

        private readonly ConcurrentDictionary<int, byte> _openConnections = new();

        private readonly Logger _logger = new();
        private static bool _isInitialized = false;
        private static readonly CertificateManager _certManager = new(false);
        private IHost _host;
        private InterceptedHttpHost _interceptedHost;
        private readonly InterceptedTunnelRegistry _interceptedTunnels = new();
        private HttpClient _httpClient;
        private CancellationTokenSource _cts;
        private int _port = -1;

        private int _availableConnectionId;
        private int _availableRequestId;

        internal BypassListModel BypassList { get; set; }

        BypassListModel IWebServiceProxy.BypassList
        {
            get => BypassList;
            set => BypassList = value;
        }

        internal static bool Initialize(string certPath)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException("Only call Initialize once.");
            }

            if (_certManager.Initialize())
            {
                _certManager.ExportRootCertificate(certPath);
                _isInitialized = true;
            }

            return _isInitialized;
        }

        public void Reset()
        {
            _availableConnectionId = 999;
            _availableRequestId = 0;
        }

        // Upstream handler shared by production and tests. Cookies are disabled so a
        // capture-wide cookie jar never injects one client's cookies into another's requests.
        internal static HttpClientHandler CreateUpstreamHandler() => new()
        {
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        internal static void AddRequestReaderServices(
            IServiceCollection services,
            ProxyRequestReadPolicy policy = null,
            ProxyRequestBufferBudget budget = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            policy ??=
                ProxyRequestReadPolicy.Default;
            budget ??=
                ProxyRequestBufferBudget.Shared;
            services.AddSingleton(policy);
            services.AddSingleton(budget);
            services.AddSingleton<
                InitialProxyRequestReader>();
        }

        internal static CertificateManager CertManager { get { return _certManager; } }

        internal static WebServiceProxy CreateProxy()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Please call Initialize first.");
            }

            return new WebServiceProxy();
        }

        public async Task StartProxyAsync(
            WebServiceProxyOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options.Port <= 0 || options.Port > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Port,
                    "The public proxy port must be between 1 and 65535.");
            }

            // Counted before the gate is awaited so a concurrent stop can never mistake a
            // start that already owns (or is about to own) the gate for an idle proxy.
            Interlocked.Increment(ref _pendingStarts);
            try
            {
                await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (State == ProxyState.Faulted)
                    {
                        throw new InvalidOperationException(
                            "The proxy is Faulted and may hold unreleased resources. Call StopProxyAsync and wait for it to succeed before starting again.");
                    }

                    if (State != ProxyState.Stopped)
                    {
                        throw new InvalidOperationException(
                            $"The proxy cannot start while it is {State}.");
                    }

                    StartupFaultInjection?.Invoke("GateAcquired");

                    // Created before Starting is published so a stop that arrives during startup
                    // always has a token to cancel.
                    _cts = new CancellationTokenSource();
                    SetState(ProxyState.Starting);
                    FaultException = null;
                    AbortedConnectionCount = 0;
                    try
                    {
                        await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
                        SetState(ProxyState.Running);
                    }
                    catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                    {
                        await RollBackPartialStartAsync(ex).ConfigureAwait(false);
                        throw;
                    }
                }
                finally
                {
                    _transitionGate.Release();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingStarts);
            }
        }

        private async Task RollBackPartialStartAsync(Exception startFailure)
        {
            _logger.Log(0, LogLevel.ERROR, $"Proxy startup failed, rolling back: {startFailure}");
            try
            {
                await TearDownAsync(null).ConfigureAwait(false);
                SetState(ProxyState.Stopped);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Proxy startup rollback failed: {ex}");
                FaultException = new AggregateException(
                    "Proxy startup failed and its rollback also failed.",
                    startFailure,
                    ex);
                SetState(ProxyState.Faulted);
            }
        }

        private async Task StartCoreAsync(
            WebServiceProxyOptions options,
            CancellationToken cancellationToken)
        {
            if (_host != null || _interceptedHost != null)
            {
                throw new InvalidOperationException("The proxy is already running.");
            }

            Reset();

            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cts.Token);

            _logOpen = true;
            _logger.InitLog(options.LogFilePath, options.CurrentLogLevel);
            StartupFaultInjection?.Invoke("LoggerInitialized");
            startCts.Token.ThrowIfCancellationRequested();
            _port = options.Port;
            _openConnections.Clear();

            _httpClient = new HttpClient(CreateUpstreamHandler());

            // The public listener handles forward-proxy CONNECT requests. The
            // private listener terminates the tunneled TLS and lets Kestrel
            // decode either HTTP/1.1 or HTTP/2 after ALPN negotiation.
            var interceptedApplication = new InterceptedProxyApplication(
                this,
                _httpClient,
                _interceptedTunnels,
                _logger,
                _cts.Token);
            _interceptedHost = new InterceptedHttpHost(
                SelectInterceptedCertificate,
                interceptedApplication.InvokeAsync);
            await _interceptedHost.StartAsync(startCts.Token).ConfigureAwait(false);
            StartupFaultInjection?.Invoke("InterceptedStarted");
            startCts.Token.ThrowIfCancellationRequested();

            // Minimal host: no default configuration sources, and no RunAsync. StartAsync
            // binds the listener, so a bind failure surfaces here instead of in a lost task.
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseKestrel(serverOptions =>
                    {
                        serverOptions.Listen(IPAddress.Any, _port, listenOptions =>
                        {
                            listenOptions.UseConnectionHandler<ForwardProxyConnectionHandler>();
                        });
                    });
                    webBuilder.UseSockets(socketOptions =>
                    {
                        socketOptions.CreateBoundListenSocket = CreateExclusiveListenSocket;
                    });
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddSingleton(_certManager);
                        services.AddSingleton(_httpClient);
                        services.AddSingleton(_logger);
                        services.AddSingleton(this);
                        services.AddSingleton(_interceptedHost);
                        services.AddSingleton(_interceptedTunnels);
                        AddRequestReaderServices(
                            services);
                        services.AddSingleton<ForwardProxyConnectionHandler>();
                    });
                    webBuilder.Configure(app => { });
                })
                .Build();
            _host = host;

            _logger.Log(0, LogLevel.INFO, $"Kestrel proxy starting on port {_port}");
            await host.StartAsync(startCts.Token).ConfigureAwait(false);
            StartupFaultInjection?.Invoke("PublicStarted");
            startCts.Token.ThrowIfCancellationRequested();
        }

        // Kestrel does not set SO_EXCLUSIVEADDRUSE, so on Windows a wildcard bind can succeed
        // over another application's loopback bind of the same port. Bind exclusively instead.
        internal static Socket CreateExclusiveListenSocket(EndPoint endpoint)
        {
            var ipEndPoint = (IPEndPoint)endpoint;
            var socket = new Socket(
                ipEndPoint.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    socket.ExclusiveAddressUse = true;
                }

                if (ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = true;
                }

                socket.Bind(ipEndPoint);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private void SetState(ProxyState state)
        {
            lock (_stateLock)
            {
                _state = state;
            }
        }

        private X509Certificate2 SelectInterceptedCertificate(
            int remotePort,
            string hostName)
        {
            _interceptedTunnels.TryGet(remotePort, out var tunnel);
            if (tunnel != null &&
                tunnel.TryMarkTlsCompleted())
            {
                RaiseCompletedSslConnectionRequest(
                    tunnel.ConnectionId,
                    tunnel.ConnectRequest);
            }

            // Prefer SNI, but CONNECT still supplies the destination for clients
            // that omit SNI, such as some IP-literal or legacy callers.
            string certificateHost = !string.IsNullOrWhiteSpace(hostName)
                ? hostName
                : tunnel?.ConnectRequest.Host ?? "localhost";
            return _certManager.GetCertificateForHost(certificateHost);
        }

        // Concurrent callers share one stop operation, and the first caller's progress wins.
        // A start that already owns the gate is not bypassed: stop queues behind it (cancelling it
        // if it has published its token) and then stops whatever it produced.
        public Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null)
        {
            lock (_stateLock)
            {
                if (_state == ProxyState.Stopped && Volatile.Read(ref _pendingStarts) == 0)
                {
                    return Task.CompletedTask;
                }

                // Task.Run keeps the teardown off the caller's (UI) context.
                return _stopTask ??= Task.Run(() => StopCoreAsync(progress));
            }
        }

        private async Task StopCoreAsync(IProgress<WebProxyStopProgress> progress)
        {
            // Cancel before taking the gate so a start in progress aborts promptly.
            await CancelRunAsync().ConfigureAwait(false);

            await _transitionGate.WaitAsync().ConfigureAwait(false);
            var stopped = false;
            try
            {
                if (State == ProxyState.Stopped)
                    return;

                SetState(ProxyState.Stopping);
                try
                {
                    await TearDownAsync(progress).ConfigureAwait(false);
                    FaultException = null;
                    SetState(ProxyState.Stopped);
                    stopped = true;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // Stays Faulted; calling stop again retries the cleanup.
                    FaultException = ex;
                    SetState(ProxyState.Faulted);
                    throw;
                }
            }
            finally
            {
                // Retire the shared stop task while the gate is still held, so a restart
                // can never be joined by (or stopped through) a stale stop operation.
                lock (_stateLock)
                {
                    _stopTask = null;
                }

                _transitionGate.Release();
                if (stopped)
                {
                    try
                    {
                        ProxyStopped?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                    {
                        PublicUtilities.AppLog(LogLevel.ERROR, $"Proxy stopped observer failed: {ex}");
                    }
                }
            }
        }

        // True while anything started by a run (hosts, client, token sources, log) is still owned.
        internal bool HasOutstandingResources =>
            _host != null || _interceptedHost != null || _httpClient != null ||
            _cts != null || _logOpen;

        // The callbacks belong to in-flight work. One that throws must not abort the stop or a
        // rollback before cleanup (or before the shared stop task is retired), so the failure is
        // logged and cleanup continues. Cancellation has still been requested at that point.
        private async Task CancelRunAsync()
        {
            var cts = _cts;
            if (cts == null)
                return;

            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A finished teardown already disposed the token source; there is nothing left to cancel.
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"A proxy cancellation callback failed: {ex}");
            }
        }

        private async Task TearDownAsync(IProgress<WebProxyStopProgress> progress)
        {
            if (!HasOutstandingResources)
                return;

            var totalStopwatch = Stopwatch.StartNew();
            List<Exception> disposalFailures = new();
            await CancelRunAsync().ConfigureAwait(false);

            // One budget covers both hosts. When it expires the hosts abort what is left.
            using var budget = new CancellationTokenSource();
            budget.CancelAfter(StopDeadline);
            using var deadlineRegistration = budget.Token.Register(() =>
            {
                AbortedConnectionCount = _openConnections.Count;
                if (AbortedConnectionCount > 0)
                {
                    _logger.Log(
                        0,
                        LogLevel.WARN,
                        $"Stop deadline expired with {AbortedConnectionCount} open connection(s); aborting them.");
                }
            });

            var publicHost = _host;
            var interceptedHost = _interceptedHost;
            var publicTask = publicHost == null
                ? Task.FromResult<Exception>(null)
                : StopPublicHostAsync(publicHost, progress, totalStopwatch, budget.Token);
            var interceptedTask = interceptedHost == null
                ? Task.FromResult<Exception>(null)
                : StopInterceptedHostAsync(interceptedHost, progress, totalStopwatch, budget.Token);
            var results = await Task.WhenAll(publicTask, interceptedTask).ConfigureAwait(false);
            foreach (var failure in results)
            {
                if (failure != null)
                    disposalFailures.Add(failure);
            }

            ShutdownFaultInjection?.Invoke();

            if (disposalFailures.Count != 0)
            {
                throw new AggregateException(
                    "One or more proxy hosts could not be disposed.",
                    disposalFailures);
            }

            _httpClient?.Dispose();
            _httpClient = null;
            _cts?.Dispose();
            _cts = null;

            _logger.Log(
                0,
                LogLevel.INFO,
                $"Proxy hosts stopped in {totalStopwatch.Elapsed}.");
            LogCloseFaultInjection?.Invoke();
            await Task.Run(_logger.CloseLog).ConfigureAwait(false);
            _logOpen = false;
        }

        // Returns a disposal failure to report, or null. A host that stopped but failed to
        // dispose stays referenced so a later stop can retry.
        private async Task<Exception> StopPublicHostAsync(
            IHost host,
            IProgress<WebProxyStopProgress> progress,
            Stopwatch totalStopwatch,
            CancellationToken deadline)
        {
            var hostDisposed = false;
            var phaseStopwatch = Stopwatch.StartNew();
            progress?.Report(new WebProxyStopProgress(
                WebProxyStopPhase.StoppingPublicListener,
                totalStopwatch.Elapsed));
            try
            {
                await ProxyHostShutdown.StopAndDisposeAsync(host, deadline).ConfigureAwait(false);
                hostDisposed = true;
                return null;
            }
            catch (ProxyHostDisposalException ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                _logger.Log(0, LogLevel.ERROR, $"Error disposing proxy host: {ex}");
                return ex;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                hostDisposed = true;
                _logger.Log(0, LogLevel.ERROR, $"Error stopping proxy: {ex.Message}");
                return null;
            }
            finally
            {
                if (hostDisposed)
                {
                    _host = null;
                }
                _logger.Log(
                    0,
                    LogLevel.INFO,
                    $"Public proxy host stopped in {phaseStopwatch.Elapsed}.");
            }
        }

        private async Task<Exception> StopInterceptedHostAsync(
            InterceptedHttpHost host,
            IProgress<WebProxyStopProgress> progress,
            Stopwatch totalStopwatch,
            CancellationToken deadline)
        {
            var disposed = false;
            var phaseStopwatch = Stopwatch.StartNew();
            progress?.Report(new WebProxyStopProgress(
                WebProxyStopPhase.StoppingInterceptedListener,
                totalStopwatch.Elapsed));
            try
            {
                await host.StopAndDisposeAsync(deadline).ConfigureAwait(false);
                disposed = true;
                return null;
            }
            catch (ProxyHostDisposalException ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                _logger.Log(0, LogLevel.ERROR, $"Error disposing intercepted proxy: {ex}");
                return ex;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                disposed = true;
                _logger.Log(0, LogLevel.ERROR, $"Error stopping intercepted proxy: {ex.Message}");
                return null;
            }
            finally
            {
                if (disposed)
                {
                    _interceptedHost = null;
                }
                _logger.Log(
                    0,
                    LogLevel.INFO,
                    $"Intercepted proxy host stopped in {phaseStopwatch.Elapsed}.");
            }
        }

        internal int GetNextConnectionID()
        {
            return Interlocked.Increment(ref _availableConnectionId);
        }

        internal int GetNextRequestID()
        {
            return Interlocked.Increment(ref _availableRequestId);
        }

        internal bool RaiseReceivedInitialConnection(int connectionID, string remoteEndPoint)
        {
            if (_cts?.IsCancellationRequested == true)
            {
                return false;
            }

            var args = new InitialConnectionEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                RemoteEndPoint = remoteEndPoint,
                AcceptConnection = true
            };

            ReceivedInitialConnection?.Invoke(this, args);
            if (args.AcceptConnection)
            {
                _openConnections.TryAdd(connectionID, 0);
            }

            return args.AcceptConnection;
        }

        internal async Task<bool> RaiseReceivedSslConnectionRequestAsync(
            int connectionID,
            string clientIP,
            ClientRequest request,
            CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var args = new SslConnectionRequestEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                ClientIP = clientIP,
                Request = request,
                AcceptConnection = true
            };

            bool observersAccepted = InvokeVetoObservers(
                ReceivedSslConnectionRequest,
                args,
                eventArgs => eventArgs.AcceptConnection);
            args.AcceptConnection = observersAccepted;
            var policy = SslConnectionRequestPolicyAsync;
            bool policyAccepted = policy == null ||
                await policy(args, cancellationToken).ConfigureAwait(false);
            args.AcceptConnection = observersAccepted && policyAccepted;
            return args.AcceptConnection &&
                _cts?.IsCancellationRequested != true &&
                !cancellationToken.IsCancellationRequested;
        }

        internal void RaiseCompletedSslConnectionRequest(int connectionID, ClientRequest request)
        {
            CompletedSslConnectionRequest?.Invoke(this,
                new SslConnectionCompletionEventArgs
                {
                    ConnectionID = connectionID,
                    Timestamp = DateTime.Now,
                    Request = request
                });
        }

        internal void RaiseFailedSslConnectionRequest(int connectionID, Exception ex)
        {
            FailedSslConnectionRequest?.Invoke(this,
                new ConnectionFailureEventArgs
                {
                    ConnectionID = connectionID,
                    Timestamp = DateTime.Now,
                    Exception = ex
                });
        }

        internal async Task<bool> RaiseReceivedWebRequestAsync(
            int connectionID,
            ClientRequest request,
            CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var args = new HttpRequestEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                Request = request,
                AcceptRequest = true
            };

            bool observersAccepted = InvokeVetoObservers(
                ReceivedWebRequest,
                args,
                eventArgs => eventArgs.AcceptRequest);
            args.AcceptRequest = observersAccepted;
            var policy = WebRequestPolicyAsync;
            bool policyAccepted = policy == null ||
                await policy(args, cancellationToken).ConfigureAwait(false);
            args.AcceptRequest = observersAccepted && policyAccepted;
            return args.AcceptRequest &&
                _cts?.IsCancellationRequested != true &&
                !cancellationToken.IsCancellationRequested;
        }

        internal async Task<bool> RaiseReceivedWebResponseAsync(
            int connectionID,
            ClientRequest request,
            ServerResponse response,
            CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var args = new HttpResponseEventArgs
            {
                ConnectionID = connectionID,
                Timestamp = DateTime.Now,
                Request = request,
                Response = response,
                SendResponse = true
            };

            bool observersAccepted = InvokeVetoObservers(
                ReceivedWebResponse,
                args,
                eventArgs => eventArgs.SendResponse);
            args.SendResponse = observersAccepted;
            var policy = WebResponsePolicyAsync;
            bool policyAccepted = policy == null ||
                await policy(args, cancellationToken).ConfigureAwait(false);
            args.SendResponse = observersAccepted && policyAccepted;
            return args.SendResponse &&
                _cts?.IsCancellationRequested != true &&
                !cancellationToken.IsCancellationRequested;
        }

        private bool InvokeVetoObservers<TEventArgs>(
            EventHandler<TEventArgs> observers,
            TEventArgs args,
            Func<TEventArgs, bool> isAccepted) where TEventArgs : EventArgs
        {
            bool accepted = true;
            if (observers == null)
            {
                return accepted;
            }

            foreach (EventHandler<TEventArgs> observer in observers.GetInvocationList())
            {
                observer(this, args);
                accepted &= isAccepted(args);
            }

            return accepted;
        }

        internal void RaiseConnectionClosed(int connectionID)
        {
            _openConnections.TryRemove(connectionID, out _);
            ConnectionClosed?.Invoke(this,
                new ConnectionClosedEventArgs
                {
                    ConnectionID = connectionID,
                    Timestamp = DateTime.Now
                });
        }

        internal void RaiseWebSocketOpened(WebSocketOpenedEventArgs args)
        {
            WebSocketOpened?.Invoke(this, args);
        }

        internal void RaiseWebSocketMessage(WebSocketMessageEventArgs args)
        {
            WebSocketMessage?.Invoke(this, args);
        }

        internal void RaiseWebSocketClosed(WebSocketClosedEventArgs args)
        {
            WebSocketClosed?.Invoke(this, args);
        }
    }
}
