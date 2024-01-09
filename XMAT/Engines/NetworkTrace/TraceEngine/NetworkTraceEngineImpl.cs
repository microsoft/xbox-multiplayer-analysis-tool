// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text;
using System.Net.WebSockets;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Security;
using System.Net;
using System.Windows;

namespace XMAT.NetworkTrace.NTDE
{
    internal partial class NetworkTraceEngineImpl : INetworkTraceEngine
    {
        public event EventHandler<string> EventRecordAvailable;

        internal async static Task<INetworkTraceEngine> Connect(NetworkTraceEngineOptions options)
        {
            NetworkTraceEngineImpl engine = new NetworkTraceEngineImpl();

            await engine.InternalConnect(options);

            return engine;
        }

        private NetworkTraceEngineOptions _options;
        private ClientWebSocket _webSocket;

        private readonly string ConnectErrorString = "NETCAP_ERROR_CONNECT";
        private readonly string StartErrorString = "NETCAP_ERROR_START";
        private readonly string StopErrorString = "NETCAP_ERROR_STOP";
        private readonly string GetErrorString = "NETCAP_ERROR_RETRIEVE";
        private readonly string ResponseErrorString = "NETCAP_ERROR_RESPONSE";
        private readonly string FullUriFormat = "wss://{0}:11443/ext/networktracedata";
        private readonly string OriginUriFormat = "https://{0}:11443";
        private readonly string JsonRequestFormat = "{{\"command\":\"{0}\"}}";
        private readonly string JsonRequestWithParamsFormat = "{{\"command\":\"{0}\", \"params\": {1}}}";
        private readonly string JsonStartRequestParams = "{ \"provider\": \"ndis\" }";
        private readonly string JsonPacketRequestParamsFormat = "{{ \"trace-name\": \"{0}\", \"start-record\": {1}, \"record-count\": {2} }}";
        private readonly string ResponseResultField = "result";
        private readonly string ResponseResultSuccess = "succeeded";
        private readonly string ResponsePacketsField = "packets";
        private readonly string ResponseTraceField = "trace";
        private readonly string TraceSessionProperty = "TraceSession";
        private readonly string StartTraceCommand = "start-packet-trace";
        private readonly string StopTraceCommand = "stop-packet-trace";
        private readonly string GetTraceCommand = "get-packet-trace";
        private readonly string OriginRequestHeader = "Origin";

        internal async Task InternalConnect(NetworkTraceEngineOptions options)
        {
            _options = options;
            _webSocket = new ClientWebSocket();

            IPAddress ipAddress = await PublicUtilities.ResolveIP4AddressAsync(_options.HostName);
            if(ipAddress == null)
            {
                MessageBox.Show(Localization.GetLocalizedString("DNS_RESOLVE_ERROR_MESSAGE", _options.HostName), Localization.GetLocalizedString("DNS_RESOLVE_ERROR_TITLE"), MessageBoxButton.OK, MessageBoxImage.Error) ;
                return;
            }

            var serviceUri = String.Format(FullUriFormat, ipAddress.ToString());
            var originHeader = String.Format(OriginUriFormat, ipAddress.ToString());

            // The Origin header must be correct or the call will fail
            _webSocket.Options.SetRequestHeader(OriginRequestHeader, originHeader);

            // Ignore certificate errors since the device portal uses a self-signed cert
            _webSocket.Options.RemoteCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });

            try
            {
                // Start connecting
                await _webSocket.ConnectAsync(new Uri(serviceUri), CancellationToken.None);
            }
            catch (Exception ex)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Caught exception in InternalConnect: {ex.ToString()}");
                throw new ApplicationException(Localization.GetLocalizedString(ConnectErrorString));
            }
        }

        internal async Task Disconnect()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, String.Empty, CancellationToken.None);

                _webSocket.Dispose();
                _webSocket = null;
            }
        }

        private string _lastTraceName;

        public async Task GetAllEventsAsync()
        {
            Debug.Assert(_webSocket != null && _webSocket.State == WebSocketState.Open);

            int recordsPerChunk = 100;
            int startRecord = 0;
            int recordsReceived = 0;
            int waitDelayMS = 5;

            try
            {
                do
                {
                    var requestParamsJson = String.Format(JsonPacketRequestParamsFormat, _lastTraceName, startRecord, recordsPerChunk);
                    var requestJson = String.Format(JsonRequestWithParamsFormat, GetTraceCommand, requestParamsJson);
                    var requestBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(requestJson));

                    await _webSocket.SendAsync(requestBuffer, WebSocketMessageType.Text, true, CancellationToken.None);

                    var responseJson = await ReceiveFullResponse();

                    var doc = JsonDocument.Parse(responseJson);

                    if (doc.RootElement.GetProperty(ResponseResultField).GetString() == ResponseResultSuccess)
                    {
                        var packetList = doc.RootElement.GetProperty(ResponsePacketsField).EnumerateArray();

                        recordsReceived = 0;

                        foreach (var packet in packetList)
                        {
                            recordsReceived++;
                            EventRecordAvailable?.Invoke(this, packet.ToString());
                        }
                    }
                    else
                    {
                        throw new Exception(Localization.GetLocalizedString(ResponseErrorString));
                    }

                    startRecord += recordsPerChunk;
                    await Task.Delay(waitDelayMS);

                } while (recordsReceived == recordsPerChunk);
            }
            catch(Exception ex)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Caught exception in GetAllEventsAsync: {ex.ToString()}");
                throw new ApplicationException(Localization.GetLocalizedString(GetErrorString));
            }
        }

        public async Task StartPacketTraceAsync()
        {
            Debug.Assert(_webSocket != null && _webSocket.State == WebSocketState.Open);

            var requestJson = String.Format(JsonRequestWithParamsFormat, StartTraceCommand, JsonStartRequestParams);
            var requestBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(requestJson));

            try
            {
                await _webSocket.SendAsync(requestBuffer, WebSocketMessageType.Text, true, CancellationToken.None);

                var responseJson = await ReceiveFullResponse();

                if (responseJson != null)
                {
                    var doc = JsonDocument.Parse(responseJson);

                    if (doc.RootElement.GetProperty(ResponseResultField).GetString() != ResponseResultSuccess)
                    {
                        throw new Exception(Localization.GetLocalizedString(ResponseErrorString));
                    }
                }
            }
            catch (Exception ex)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Caught exception in StartPacketTraceAsync: {ex.ToString()}");
                throw new ApplicationException(Localization.GetLocalizedString(StartErrorString));
            }
        }

        public async Task StopPacketTraceAsync()
        {
            Debug.Assert(_webSocket != null && _webSocket.State == WebSocketState.Open);

            var requestJson = String.Format(JsonRequestFormat, StopTraceCommand);
            var requestBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(requestJson));

            try
            {
                await _webSocket.SendAsync(requestBuffer, WebSocketMessageType.Text, true, CancellationToken.None);

                var responseJson = await ReceiveFullResponse();

                if (responseJson != null)
                {
                    var doc = JsonDocument.Parse(responseJson);

                    if (doc.RootElement.GetProperty(ResponseResultField).GetString() == ResponseResultSuccess)
                    {
                        _lastTraceName = doc.RootElement.GetProperty(ResponseTraceField).GetProperty(TraceSessionProperty).GetString();
                    }
                    else
                    {
                        throw new Exception(Localization.GetLocalizedString(ResponseErrorString));
                    }
                }
            }
            catch (Exception ex)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Caught exception in StopPacketTraceAsync: {ex.ToString()}");
                throw new ApplicationException(Localization.GetLocalizedString(StopErrorString));
            }
        }

        private readonly int BufferChunkSize = 1024;

        internal async Task<string> ReceiveFullResponse()
        {
            StringBuilder responseString = new StringBuilder();
            WebSocketReceiveResult receiveResult;

            do
            {
                var responseBuffer = new ArraySegment<byte>(new byte[BufferChunkSize]);

                // Get data from the websocket
                receiveResult = await _webSocket.ReceiveAsync(responseBuffer, CancellationToken.None);

                // We only expect Text
                Debug.Assert(receiveResult.MessageType == WebSocketMessageType.Text);

                responseBuffer = responseBuffer.Slice(0, receiveResult.Count);

                // Convert and append to the output string
                responseString.Append(System.Text.Encoding.UTF8.GetString(responseBuffer));
            }
            // Keep getting chunks until we have the full buffer
            while (receiveResult.EndOfMessage == false);

            // If we didn't receive anything return null since it could be
            // valid to have an empty string
            return responseString.ToString();
        }
    }
}
