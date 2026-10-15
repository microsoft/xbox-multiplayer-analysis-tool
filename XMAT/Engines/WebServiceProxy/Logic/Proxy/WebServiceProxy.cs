// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
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

        public bool IsProxyEnabled { get { return _host != null; } }

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

        internal static CertificateManager CertManager { get { return _certManager; } }

        internal static WebServiceProxy CreateProxy()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Please call Initialize first.");
            }

            return new WebServiceProxy();
        }

        public void StartProxy(WebServiceProxyOptions options)
        {
            if (_host != null || _interceptedHost != null)
            {
                throw new InvalidOperationException("The proxy is already running.");
            }

            Reset();

            _logger.InitLog(options.LogFilePath, options.CurrentLogLevel);
            _port = options.Port;
            _cts = new CancellationTokenSource();

            _httpClient = new HttpClient(new HttpClientHandler()
            {
                UseProxy = false,
                Proxy = null,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

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
            _interceptedHost.StartAsync(_cts.Token).GetAwaiter().GetResult();

            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(serverOptions =>
                {
                    serverOptions.Listen(IPAddress.Any, _port, listenOptions =>
                    {
                        listenOptions.UseConnectionHandler<ForwardProxyConnectionHandler>();
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(_certManager);
                    services.AddSingleton(_httpClient);
                    services.AddSingleton(_logger);
                    services.AddSingleton(this);
                    services.AddSingleton(_interceptedHost);
                    services.AddSingleton(_interceptedTunnels);
                    services.AddSingleton<ForwardProxyConnectionHandler>();
                });
                webBuilder.Configure(app => { });
            });

            _host = builder.Build();

            _logger.Log(0, LogLevel.INFO, $"Kestrel proxy starting on port {_port}");

            // StopProxyAsync exclusively owns shutdown and disposal. RunAsync
            // would independently stop and dispose this host on cancellation.
            _host.StartAsync(_cts.Token).GetAwaiter().GetResult();
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

        public async Task StopProxyAsync(
            IProgress<WebProxyStopProgress> progress = null)
        {
            if (_host == null && _interceptedHost == null)
                return;

            var totalStopwatch = Stopwatch.StartNew();
            List<Exception> disposalFailures = new();
            if (_cts != null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            if (_host != null)
            {
                var host = _host;
                var hostDisposed = false;
                var phaseStopwatch = Stopwatch.StartNew();
                progress?.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.StoppingPublicListener,
                    totalStopwatch.Elapsed));
                try
                {
                    await ProxyHostShutdown.StopAndDisposeAsync(
                        host,
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    hostDisposed = true;
                }
                catch (ProxyHostDisposalException ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    disposalFailures.Add(ex);
                    _logger.Log(
                        0,
                        LogLevel.ERROR,
                        $"Error disposing proxy host: {ex}");
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    hostDisposed = true;
                    _logger.Log(0, LogLevel.ERROR, $"Error stopping proxy: {ex.Message}");
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

            if (_interceptedHost != null)
            {
                var interceptedHostDisposed = false;
                var phaseStopwatch = Stopwatch.StartNew();
                progress?.Report(new WebProxyStopProgress(
                    WebProxyStopPhase.StoppingInterceptedListener,
                    totalStopwatch.Elapsed));
                try
                {
                    await _interceptedHost.DisposeAsync().ConfigureAwait(false);
                    interceptedHostDisposed = true;
                }
                catch (ProxyHostDisposalException ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    disposalFailures.Add(ex);
                    _logger.Log(
                        0,
                        LogLevel.ERROR,
                        $"Error disposing intercepted proxy: {ex}");
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    interceptedHostDisposed = true;
                    _logger.Log(
                        0,
                        LogLevel.ERROR,
                        $"Error stopping intercepted proxy: {ex.Message}");
                }
                finally
                {
                    if (interceptedHostDisposed)
                    {
                        _interceptedHost = null;
                    }
                    _logger.Log(
                        0,
                        LogLevel.INFO,
                        $"Intercepted proxy host stopped in {phaseStopwatch.Elapsed}.");
                }
            }

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

            ProxyStopped?.Invoke(this, EventArgs.Empty);
            _logger.Log(
                0,
                LogLevel.INFO,
                $"Proxy hosts stopped in {totalStopwatch.Elapsed}.");
            await Task.Run(_logger.CloseLog).ConfigureAwait(false);
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
            return args.AcceptConnection;
        }

        internal bool RaiseReceivedSslConnectionRequest(int connectionID, string clientIP, ClientRequest request)
        {
            if (_cts?.IsCancellationRequested == true)
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

            ReceivedSslConnectionRequest?.Invoke(this, args);
            return args.AcceptConnection;
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

        internal bool RaiseReceivedWebRequest(int connectionID, ClientRequest request)
        {
            if (_cts?.IsCancellationRequested == true)
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

            ReceivedWebRequest?.Invoke(this, args);
            return args.AcceptRequest;
        }

        internal bool RaiseReceivedWebResponse(int connectionID, ClientRequest request, ServerResponse response)
        {
            if (_cts?.IsCancellationRequested == true)
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

            ReceivedWebResponse?.Invoke(this, args);
            return args.SendResponse;
        }

        internal void RaiseConnectionClosed(int connectionID)
        {
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
