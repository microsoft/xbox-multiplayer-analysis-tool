// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
        public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
        public event EventHandler ProxyStopped;

        public bool IsProxyEnabled { get { return _host != null; } }

        private readonly Logger _logger = new();
        private static bool _isInitialized = false;
        private static readonly CertificateManager _certManager = new(false);
        private IHost _host;
        private CancellationTokenSource _cts;
        private int _port = -1;

        private int _availableConnectionId;
        private int _availableRequestId;

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
            Reset();

            _logger.InitLog(options.LogFilePath, options.CurrentLogLevel);
            _port = options.Port;
            _cts = new CancellationTokenSource();

            var httpClient = new HttpClient(new HttpClientHandler()
            {
                UseProxy = false,
                Proxy = null,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

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
                    services.AddSingleton(httpClient);
                    services.AddSingleton(_logger);
                    services.AddSingleton(this);
                    services.AddSingleton<ForwardProxyConnectionHandler>();
                });
                webBuilder.Configure(app => { });
            });

            _host = builder.Build();

            _logger.Log(0, LogLevel.INFO, $"Kestrel proxy starting on port {_port}");

            Task.Run(async () =>
            {
                try
                {
                    await _host.RunAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
                catch (Exception ex)
                {
                    _logger.Log(0, LogLevel.ERROR, $"Kestrel host error: {ex}");
                }
            });
        }

        public void StopProxy()
        {
            if (_host == null)
                return;

            _cts?.Cancel();

            try
            {
                // Run StopAsync on thread pool to avoid deadlocking the UI thread
                Task.Run(async () =>
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Log(0, LogLevel.ERROR, $"Error stopping proxy: {ex.Message}");
            }

            _host.Dispose();
            _host = null;

            ProxyStopped?.Invoke(this, EventArgs.Empty);
            _logger.CloseLog();
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
    }
}
