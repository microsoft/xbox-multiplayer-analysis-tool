// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public sealed class SecureWebSocketCaptureTests
    {
        [Fact]
        public async Task SecureMessagesPopulateCaptureTabAndRemainAfterStopping()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] payload = "secure capture"u8.ToArray();
            await using var origin = await LoopbackOrigin.StartAsync(async context =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[64];
                WebSocketReceiveResult message = await socket.ReceiveAsync(buffer, timeout.Token);
                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, message.Count),
                    message.MessageType, true, timeout.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }, useTls: true, useWebSockets: true);
            using var certificate = ProxyTestHarness.CreateCertificate();
            await using LiveProxyCapture capture = await LiveProxyCapture.StartAsync(certificate);
            var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CaptureUiApplicationFixture.RunOnApplication(_ =>
            {
                capture.Controller.WebSocketMessages.CollectionChanged += (_, _) =>
                {
                    if (capture.Controller.WebSocketMessages.Count == 2)
                        displayed.TrySetResult();
                };
            });
            using var client = new ClientWebSocket();
            client.Options.Proxy = new WebProxy($"http://127.0.0.1:{capture.Port}");
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            await client.ConnectAsync(new Uri($"wss://127.0.0.1:{origin.Port}/socket"), timeout.Token);
            await client.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
            var echo = new byte[64];
            WebSocketReceiveResult received = await client.ReceiveAsync(echo, timeout.Token);
            Assert.Equal(payload, echo.Take(received.Count).ToArray());
            await displayed.Task.WaitAsync(timeout.Token);
            client.Abort();
            await capture.Controller.StopProxyAsync().WaitAsync(LiveProxyCapture.StopBudget);

            CaptureUiApplicationFixture.RunOnApplication(_ =>
            {
                var messages = capture.Controller.WebSocketMessages;
                Assert.Equal(2, messages.Count);
                Assert.Single(messages, message => message.FromHost);
                Assert.Single(messages, message => !message.FromHost);
                Assert.All(messages, message =>
                {
                    Assert.Equal(payload, message.Payload);
                    Assert.Equal("Text", message.MessageType);
                    Assert.NotEqual(Guid.Empty, message.RowKey.SourceId);
                });
                Assert.Equal(messages[0].RowKey, messages[1].RowKey);
                Assert.Contains(capture.Controller.SelectedWebSocketMessage, messages);
            });
        }
    }
}
