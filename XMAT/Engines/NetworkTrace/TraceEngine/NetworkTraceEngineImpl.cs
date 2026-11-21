// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.NetworkTrace.NTDE
{
    internal partial class NetworkTraceEngineImpl : INetworkTraceEngine
    {
        private readonly TimeSpan _commandTimeout;
        private NetworkTraceEngineOptions _options;
        private WebSocket _webSocket;
        private string _lastTraceName;
        public event EventHandler<string> EventRecordAvailable;

        internal NetworkTraceEngineImpl(WebSocket socket = null, TimeSpan? commandTimeout = null)
        {
            _webSocket = socket;
            _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);
        }

        internal static async Task<INetworkTraceEngine> Connect(NetworkTraceEngineOptions options)
        {
            var engine = new NetworkTraceEngineImpl { _options = options };
            await engine.EnsureConnectedAsync();
            return engine;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_webSocket?.State == WebSocketState.Open)
                return;
            _webSocket?.Dispose();
            _webSocket = null;
            if (string.IsNullOrWhiteSpace(_options.HostName))
                throw Failure("NETCAP_ERROR_CONNECT", false);
            using var timeout = new CancellationTokenSource(_commandTimeout);
            var socket = new ClientWebSocket();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(_options.HostName, timeout.Token);
                var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork);
                if (address == null)
                    throw Failure("NETCAP_ERROR_DNS", false);
                socket.Options.SetRequestHeader("Origin", $"https://{address}:11443");
                socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                await socket.ConnectAsync(new Uri($"wss://{address}:11443/ext/networktracedata"), timeout.Token);
                _webSocket = socket;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                socket.Dispose();
                if (exception is NetworkTraceEngineException)
                    throw;
                throw Failure("NETCAP_ERROR_CONNECT", false);
            }
        }

        internal async Task Disconnect()
        {
            var socket = _webSocket;
            _webSocket = null;
            if (socket == null)
                return;
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception)) { socket.Abort(); }
            finally { socket.Dispose(); }
        }

        public async Task StartPacketTraceAsync()
        {
            var response = await CommandAsync("start-packet-trace", new Dictionary<string, object> { ["provider"] = "ndis" },
                "NETCAP_ERROR_START", true);
            if (response.TryGetProperty("trace", out var trace) && trace.TryGetProperty("TraceSession", out var session))
                _lastTraceName = session.GetString();
        }

        public async Task StopPacketTraceAsync()
        {
            var response = await CommandAsync("stop-packet-trace", null, "NETCAP_ERROR_STOP", true);
            if (!response.TryGetProperty("trace", out var trace) ||
                !trace.TryGetProperty("TraceSession", out var session) || string.IsNullOrEmpty(session.GetString()))
                throw Failure("NETCAP_ERROR_RESPONSE", true);
            _lastTraceName = session.GetString();
        }

        public async Task GetAllEventsAsync()
        {
            if (string.IsNullOrEmpty(_lastTraceName))
                throw Failure("NETCAP_ERROR_RETRIEVE", false);
            long start = 0;
            while (true)
            {
                var response = await CommandAsync("get-packet-trace", new Dictionary<string, object>
                {
                    ["trace-name"] = _lastTraceName,
                    ["start-record"] = start,
                    ["record-count"] = 100
                }, "NETCAP_ERROR_RETRIEVE", false);
                if (!response.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
                    throw Failure("NETCAP_ERROR_RESPONSE", false);
                var count = packets.GetArrayLength();
                foreach (var packet in packets.EnumerateArray())
                    EventRecordAvailable?.Invoke(this, packet.GetRawText());
                if (count < 100)
                    return;
                start = checked(start + count);
                await Task.Delay(5);
            }
        }

        private async Task<JsonElement> CommandAsync(
            string command, Dictionary<string, object> parameters, string failureMessage, bool changesTrace)
        {
            await EnsureConnectedAsync();
            var request = new Dictionary<string, object> { ["command"] = command };
            if (parameters != null)
                request.Add("params", parameters);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            using var timeout = new CancellationTokenSource(_commandTimeout);
            var sent = false;
            try
            {
                sent = true;
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                var json = await ReceiveFullResponseAsync(timeout.Token);
                using var document = JsonDocument.Parse(json);
                var response = document.RootElement;
                if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
                    throw Failure("NETCAP_ERROR_RESPONSE", changesTrace && sent);
                if (result.GetString() != "succeeded")
                    throw Failure(failureMessage, false);
                return response.Clone();
            }
            catch (NetworkTraceEngineException) { throw; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                _webSocket?.Abort();
                throw Failure(exception is OperationCanceledException ? "NETCAP_COMMAND_TIMEOUT" : failureMessage, changesTrace && sent);
            }
        }

        internal async Task<string> ReceiveFullResponse()
        {
            using var timeout = new CancellationTokenSource(_commandTimeout);
            return await ReceiveFullResponseAsync(timeout.Token);
        }

        private async Task<string> ReceiveFullResponseAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var output = new MemoryStream();
            while (true)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType != WebSocketMessageType.Text || result.Count == 0 && !result.EndOfMessage)
                    throw new InvalidDataException("Invalid trace response.");
                if (output.Length + result.Count > 16 * 1024 * 1024)
                    throw new InvalidDataException("The trace response exceeded its byte limit.");
                output.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }

        private static NetworkTraceEngineException Failure(string resource, bool mayHaveCommitted) =>
            new(Localization.GetLocalizedString(resource), mayHaveCommitted);
    }
}
