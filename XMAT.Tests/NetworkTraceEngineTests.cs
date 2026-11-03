// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using XMAT.NetworkTrace.NTDE;

namespace XMAT.Tests
{
    public class NetworkTraceEngineTests
    {
        [Fact]
        public async Task LocalCommandsUseUniqueOwnedSessionsAndNeverRequestShellElevation()
        {
            var commands = new List<IReadOnlyList<string>>();
            Task<int> Run(IReadOnlyList<string> arguments)
            { commands.Add(arguments); return Task.FromResult(0); }
            var first = new LocalNetworkTraceEngineImpl(Run, () => true, _ => true);
            var second = new LocalNetworkTraceEngineImpl(Run, () => true, _ => true);
            await first.StartPacketTraceAsync();
            await second.StartPacketTraceAsync();
            Assert.NotEqual(first.OwnedSessionName, second.OwnedSessionName);
            await first.StopPacketTraceAsync();
            Assert.Equal($"sessionname={first.OwnedSessionName}", commands[^1].Last());
            Assert.DoesNotContain(commands[^1], argument => argument.Contains(second.OwnedSessionName));
            var info = LocalNetworkTraceEngineImpl.CreateNetshStartInfo(commands[0]);
            Assert.False(info.UseShellExecute);
            Assert.Empty(info.Verb);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "netsh.exe"), info.FileName);
            Assert.True(info.RedirectStandardError);
            var neverStarted = new LocalNetworkTraceEngineImpl(Run, () => true, _ => true);
            await Assert.ThrowsAsync<NetworkTraceEngineException>(neverStarted.StopPacketTraceAsync);
            Assert.Equal(3, commands.Count);
        }

        [Fact]
        public async Task LocalElevationIsCheckedBeforeAnyCommandAndAlreadyStoppedOwnedSessionsAreSafe()
        {
            var calls = 0;
            Task<int> Run(IReadOnlyList<string> arguments)
            { calls++; return Task.FromResult(0); }
            var denied = new LocalNetworkTraceEngineImpl(Run, () => false, _ => true);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(denied.StartPacketTraceAsync);
            Assert.Equal(0, calls);
            var stopped = new LocalNetworkTraceEngineImpl(Run, () => true, _ => false);
            await stopped.StartPacketTraceAsync();
            await stopped.StopPacketTraceAsync();
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task RemoteProtocolKeepsTheStoppedTraceIdentityAndParsesCompleteMessages()
        {
            using var socket = new ScriptedSocket(
                "{\"result\":\"succeeded\"}",
                "{\"result\":\"succeeded\",\"trace\":{\"TraceSession\":\"owned-trace\"}}",
                "{\"result\":\"succeeded\",\"packets\":[" + NetworkTraceTestData.Event(1) + "]}");
            var engine = new NetworkTraceEngineImpl(socket);
            var events = new List<string>();
            engine.EventRecordAvailable += (_, record) => events.Add(record);
            await engine.StartPacketTraceAsync();
            await engine.StopPacketTraceAsync();
            await engine.GetAllEventsAsync();
            Assert.Single(events);
            using var request = JsonDocument.Parse(socket.Requests[^1]);
            Assert.Equal("owned-trace", request.RootElement.GetProperty("params").GetProperty("trace-name").GetString());
            Assert.Equal(0, request.RootElement.GetProperty("params").GetProperty("start-record").GetInt64());
            await engine.Disconnect();
            Assert.Equal(WebSocketState.Closed, socket.State);
        }

        [Theory]
        [InlineData("{\"result\":\"failed\"}", false)]
        [InlineData("", true)]
        [InlineData("{\"unexpected\":\"response\"}", true)]
        public async Task RemoteStartDistinguishesDefinitiveRejectionFromUncertainCommit(string response, bool uncertain)
        {
            using var socket = new ScriptedSocket(response);
            var engine = new NetworkTraceEngineImpl(socket);
            var failure = await Assert.ThrowsAsync<NetworkTraceEngineException>(engine.StartPacketTraceAsync);
            Assert.Equal(uncertain, failure.MayHaveCommitted);
        }

        [Fact]
        public async Task FragmentedUtf8AndIncompleteStopRepliesNeverInventATraceIdentity()
        {
            using var socket = new ScriptedSocket("{\"value\":\"\u03bb\U0001F600\"}") { FragmentBytes = 1 };
            var engine = new NetworkTraceEngineImpl(socket);
            Assert.Equal("{\"value\":\"\u03bb\U0001F600\"}", await engine.ReceiveFullResponse());
            using var missing = new ScriptedSocket("{\"result\":\"succeeded\"}");
            var stop = new NetworkTraceEngineImpl(missing);
            var failure = await Assert.ThrowsAsync<NetworkTraceEngineException>(stop.StopPacketTraceAsync);
            Assert.True(failure.MayHaveCommitted);
            await Assert.ThrowsAsync<NetworkTraceEngineException>(stop.GetAllEventsAsync);
        }

        private sealed class ScriptedSocket(params string[] responses) : WebSocket
        {
            private readonly Queue<byte[]> _responses = new(responses.Select(Encoding.UTF8.GetBytes));
            private byte[] _message;
            private int _position;
            private WebSocketState _state = WebSocketState.Open;
            internal int FragmentBytes { get; set; } = 31;
            internal List<string> Requests { get; } = new();
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string SubProtocol => null;
            public override void Abort() => _state = WebSocketState.Aborted;
            public override void Dispose() => _state = WebSocketState.Closed;
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken cancellationToken)
            { _state = WebSocketState.Closed; return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken cancellationToken) =>
                CloseAsync(closeStatus, description, cancellationToken);
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            { Requests.Add(Encoding.UTF8.GetString(buffer)); return Task.CompletedTask; }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _message ??= _responses.Dequeue();
                var count = Math.Min(Math.Min(FragmentBytes, buffer.Count), _message.Length - _position);
                _message.AsSpan(_position, count).CopyTo(buffer.AsSpan());
                _position += count;
                var complete = _position == _message.Length;
                if (complete)
                { _message = null; _position = 0; }
                return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, complete));
            }
        }
    }
}
