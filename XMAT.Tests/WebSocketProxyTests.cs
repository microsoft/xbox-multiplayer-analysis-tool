// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class WebSocketProxyTests
    {
        [Theory]
        [InlineData("http://example.test/socket", "ws://example.test/socket")]
        [InlineData("https://example.test/socket", "wss://example.test/socket")]
        [InlineData("https://example.test:8443/socket?token=1", "wss://example.test:8443/socket?token=1")]
        public void CreateUpstreamUri_PreservesEndpointAndMapsScheme(string source, string expected)
        {
            Uri result = WebSocketProxy.CreateUpstreamUri(new Uri(source));

            Assert.Equal(expected, result.AbsoluteUri);
        }

        [Fact]
        public void IsWebSocketUpgrade_RequiresUpgradeAndConnectionHeaders()
        {
            var request = CreateValidRequest();
            request.Headers["Upgrade"] = "websocket";
            request.Headers["Connection"] = "keep-alive, Upgrade";

            Assert.True(WebSocketProxy.IsWebSocketUpgrade(request));

            request.Headers["Connection"] = "keep-alive";
            Assert.False(WebSocketProxy.IsWebSocketUpgrade(request));
        }

        [Theory]
        [InlineData("POST", "HTTP/1.1", "13", "dGhlIHNhbXBsZSBub25jZQ==")]
        [InlineData("GET", "HTTP/1.0", "13", "dGhlIHNhbXBsZSBub25jZQ==")]
        [InlineData("GET", "HTTP/1.1", "12", "dGhlIHNhbXBsZSBub25jZQ==")]
        [InlineData("GET", "HTTP/1.1", "13", "not-base64")]
        [InlineData("GET", "HTTP/1.1", "13", "dG9vLXNob3J0")]
        public void IsValidHandshake_RejectsInvalidRfc6455Requests(
            string method,
            string version,
            string webSocketVersion,
            string key)
        {
            ClientRequest request = CreateValidRequest();
            request.Method = method;
            request.Version = version;
            request.Headers["Sec-WebSocket-Version"] = webSocketVersion;
            request.Headers["Sec-WebSocket-Key"] = key;

            Assert.False(WebSocketProxy.IsValidHandshake(request));
        }

        [Fact]
        public void IsValidHandshake_AcceptsRfc6455Request()
        {
            Assert.True(WebSocketProxy.IsValidHandshake(CreateValidRequest()));
        }

        [Fact]
        public void CreateSecWebSocketAcceptKey_UsesRfc6455Algorithm()
        {
            string result = WebSocketProxy.CreateSecWebSocketAcceptKey("dGhlIHNhbXBsZSBub25jZQ==");

            Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", result);
        }

        [Fact]
        public void GetRequestedSubprotocols_ParsesCommaSeparatedValues()
        {
            var request = new ClientRequest();
            request.Headers["Sec-WebSocket-Protocol"] = "chat, superchat";

            Assert.Equal(
                new[] { "chat", "superchat" },
                WebSocketProxy.GetRequestedSubprotocols(request));
        }

        [Fact]
        public void PublishMessage_RaisesDirectionTypeAndPayload()
        {
            var proxy = new WebSocketProxy();
            WebSocketMessageEventArgs received = null;
            proxy.WebSocketMessage += (_, args) => received = args;
            byte[] payload = Encoding.UTF8.GetBytes("hello");

            proxy.PublishMessage(
                connectionID: 42,
                requestNumber: 7,
                fromHost: true,
                WebSocketMessageType.Text,
                payload);

            Assert.NotNull(received);
            Assert.Equal(42, received.ConnectionID);
            Assert.Equal(7, received.RequestNumber);
            Assert.True(received.FromHost);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            Assert.Equal(payload, received.Message);
        }

        [Fact]
        public void PublishOpened_HonorsSubscriberRejection()
        {
            var proxy = new WebSocketProxy();
            proxy.WebSocketOpened += (_, args) => args.AcceptConnection = false;

            bool accepted = proxy.PublishOpened(
                connectionID: 42,
                requestNumber: 7,
                remoteEndPoint: "wss://example.test/socket",
                subProtocol: "chat");

            Assert.False(accepted);
        }

        [Fact]
        public void GetNonWebSocketServerHeaders_RemovesHandshakeHeaders()
        {
            IReadOnlyDictionary<string, IEnumerable<string>> headers =
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Sec-WebSocket-Protocol"] = new[] { "chat" },
                    ["Sec-WebSocket-Accept"] = new[] { "accept" },
                    ["X-Custom"] = new[] { "value" }
                };

            HeaderCollection result = WebSocketProxy.GetNonWebSocketServerHeaders(headers);

            Assert.Equal(string.Empty, result["Sec-WebSocket-Protocol"]);
            Assert.Equal(string.Empty, result["Sec-WebSocket-Accept"]);
            Assert.Equal("value", result["X-Custom"]);
        }

        [Fact]
        public void AppendCapturedPayload_TruncatesAtConfiguredLimit()
        {
            using var captured = new MemoryStream();
            byte[] first = { 1, 2, 3 };
            byte[] second = { 4, 5, 6 };

            bool firstTruncated = WebSocketProxy.AppendCapturedPayload(captured, first, first.Length, 5);
            bool secondTruncated = WebSocketProxy.AppendCapturedPayload(captured, second, second.Length, 5);

            Assert.False(firstTruncated);
            Assert.True(secondTruncated);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, captured.ToArray());
        }

        private static ClientRequest CreateValidRequest()
        {
            var request = new ClientRequest
            {
                Method = "GET",
                Version = "HTTP/1.1"
            };
            request.Headers["Upgrade"] = "websocket";
            request.Headers["Connection"] = "Upgrade";
            request.Headers["Sec-WebSocket-Version"] = "13";
            request.Headers["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==";
            return request;
        }
    }
}
