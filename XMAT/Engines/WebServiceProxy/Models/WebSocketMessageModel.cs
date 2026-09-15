// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture.Models
{
    public class WebSocketMessageModel
    {
        public DateTime Timestamp { get; }
        public int ConnectionID { get; }
        public int RequestNumber { get; }
        public bool FromHost { get; }
        public string Direction => FromHost ? "Server \u2192 Client" : "Client \u2192 Server";
        public string MessageType { get; }
        public byte[] Payload { get; }
        public int PayloadLength => Payload.Length;
        public bool PayloadTruncated { get; }
        public string PayloadNotice => PayloadTruncated
            ? "Payload capture truncated at 1 MB. The complete message was still forwarded."
            : string.Empty;
        public string PayloadText => Encoding.UTF8.GetString(Payload);
        public string PayloadHex => BitConverter.ToString(Payload).Replace('-', ' ');

        internal WebSocketMessageModel(WebSocketMessageEventArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);

            Timestamp = args.Timestamp;
            ConnectionID = args.ConnectionID;
            RequestNumber = args.RequestNumber;
            FromHost = args.FromHost;
            MessageType = args.MessageType switch
            {
                WebSocketMessageType.Text => "Text",
                WebSocketMessageType.Binary => "Binary",
                WebSocketMessageType.Close => "Close",
                _ => args.MessageType.ToString()
            };
            Payload = args.Message == null ? Array.Empty<byte>() : (byte[])args.Message.Clone();
            PayloadTruncated = args.PayloadTruncated;
        }

        public bool MatchesFilter(string direction, string messageType)
        {
            bool directionMatches = string.IsNullOrEmpty(direction) ||
                direction == "All" ||
                direction == Direction;
            bool typeMatches = string.IsNullOrEmpty(messageType) ||
                messageType == "All" ||
                messageType == MessageType;

            return directionMatches && typeMatches;
        }
    }

    public class WebSocketMessagesCollection : ObservableCollection<WebSocketMessageModel>
    {
        public const int DefaultMaximumMessages = 5000;
        public const long DefaultMaximumPayloadBytes = 32 * 1024 * 1024;

        private readonly int _maximumMessages;
        private readonly long _maximumPayloadBytes;

        public long CapturedPayloadBytes { get; private set; }

        public WebSocketMessagesCollection(
            int maxMessages = DefaultMaximumMessages,
            long maxPayloadBytes = DefaultMaximumPayloadBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);

            _maximumMessages = maxMessages;
            _maximumPayloadBytes = maxPayloadBytes;
        }

        public void AddMessage(WebSocketMessageModel message)
        {
            Add(message);
        }

        protected override void InsertItem(int index, WebSocketMessageModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.PayloadLength > _maximumPayloadBytes)
            {
                throw new ArgumentException(
                    "A WebSocket message exceeds the collection payload limit.",
                    nameof(item));
            }

            // Evict oldest entries until both retention limits can accommodate the new message.
            while (Count > 0 &&
                (Count >= _maximumMessages ||
                 CapturedPayloadBytes + item.PayloadLength > _maximumPayloadBytes))
            {
                RemoveAt(0);
            }

            base.InsertItem(Math.Min(index, Count), item);
            CapturedPayloadBytes += item.PayloadLength;
        }

        protected override void RemoveItem(int index)
        {
            CapturedPayloadBytes -= this[index].PayloadLength;
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            base.ClearItems();
            CapturedPayloadBytes = 0;
        }
    }
}
