// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using XMAT.Operations.Captures;
using XMAT.Operations.Inspection;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class WebSocketMessageModelTests
    {
        [Fact]
        public void Constructor_MapsMessageMetadata()
        {
            var timestamp = new DateTime(2026, 9, 8, 8, 30, 0, DateTimeKind.Utc);
            var rowKey = new CaptureRowKey(Guid.NewGuid(), 1, 7, 7);
            var args = new WebSocketMessageEventArgs
            {
                Timestamp = timestamp,
                ConnectionID = 42,
                RequestNumber = 7,
                FromHost = true,
                MessageType = WebSocketMessageType.Text,
                Message = Encoding.UTF8.GetBytes("hello"),
                RowKey = rowKey,
            };

            var model = new WebSocketMessageModel(args);

            Assert.Equal(timestamp, model.Timestamp);
            Assert.Equal(42, model.ConnectionID);
            Assert.Equal(7, model.RequestNumber);
            Assert.Equal(rowKey, model.RowKey);
            Assert.Equal("Server → Client", model.Direction);
            Assert.Equal("Text", model.MessageType);
            Assert.Equal(5, model.PayloadLength);
            Assert.Equal(Encoding.UTF8.GetBytes("hello"), model.Payload);
        }

        // A message is previewed as captured, for its Text, JSON and Hex tabs at once: a text frame as UTF-8 text, any
        // other frame by its bytes, and a payload cut at 1 MB as only the start of the message.
        [Theory]
        [InlineData(WebSocketMessageType.Text, false, "text/plain; charset=utf-8")]
        [InlineData(WebSocketMessageType.Text, true, "text/plain; charset=utf-8")]
        [InlineData(WebSocketMessageType.Binary, false, null)]
        [InlineData(WebSocketMessageType.Close, false, null)]
        public void PreviewRequest_DescribesThePayloadAsCaptured(
            WebSocketMessageType type, bool truncated, string contentType)
        {
            var model = new WebSocketMessageModel(new WebSocketMessageEventArgs
            {
                MessageType = type,
                Message = Encoding.UTF8.GetBytes("{\"a\":1}"),
                PayloadTruncated = truncated
            });

            BodyPreviewRequest request = model.PreviewRequest();

            Assert.Equal(
                (contentType, truncated, BodyPreviewParts.Text | BodyPreviewParts.Json | BodyPreviewParts.Hex, false),
                (request.ContentType, request.StoredPrefix, request.Parts, request.NotRetained));
            Assert.Equal(model.Payload, request.Body.ToArray());
            Assert.Null(request.TransferEncoding);
            Assert.Null(request.ContentEncoding);
        }

        [Theory]
        [InlineData("All", "All", true)]
        [InlineData("Server → Client", "Text", true)]
        [InlineData("Client → Server", "Text", false)]
        [InlineData("Server → Client", "Binary", false)]
        public void MatchesFilter_AppliesDirectionAndType(
            string direction,
            string messageType,
            bool expected)
        {
            var model = new WebSocketMessageModel(new WebSocketMessageEventArgs
            {
                FromHost = true,
                MessageType = WebSocketMessageType.Text,
                Message = Array.Empty<byte>()
            });

            Assert.Equal(expected, model.MatchesFilter(direction, messageType));
        }

        [Fact]
        public void MessageCollection_EvictsOldestMessagesAtCountLimit()
        {
            var collection = new WebSocketMessagesCollection(maxMessages: 2, maxPayloadBytes: 100);

            collection.AddMessage(CreateModel(1, 10));
            collection.AddMessage(CreateModel(2, 10));
            collection.AddMessage(CreateModel(3, 10));

            Assert.Equal(2, collection.Count);
            Assert.Equal(2, collection[0].RequestNumber);
            Assert.Equal(3, collection[1].RequestNumber);
        }

        [Fact]
        public void MessageCollection_EvictsOldestMessagesAtByteLimit()
        {
            var collection = new WebSocketMessagesCollection(maxMessages: 10, maxPayloadBytes: 15);

            collection.AddMessage(CreateModel(1, 10));
            collection.AddMessage(CreateModel(2, 10));

            Assert.Single(collection);
            Assert.Equal(2, collection[0].RequestNumber);
            Assert.Equal(10, collection.CapturedPayloadBytes);
        }

        private static WebSocketMessageModel CreateModel(int requestNumber, int payloadLength)
        {
            return new WebSocketMessageModel(new WebSocketMessageEventArgs
            {
                RequestNumber = requestNumber,
                MessageType = WebSocketMessageType.Binary,
                Message = new byte[payloadLength]
            });
        }
    }
}
