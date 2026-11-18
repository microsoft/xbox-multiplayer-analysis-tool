// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.WebSockets;
using XMAT.Operations.Captures;
using XMAT.Operations.Inspection;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture.Models
{
    public class WebSocketMessageModel
    {
        public DateTime Timestamp { get; }
        public int ConnectionID { get; }
        public int RequestNumber { get; }
        public CaptureRowKey RowKey { get; }
        public bool FromHost { get; }
        public string Direction => FromHost ? "Server \u2192 Client" : "Client \u2192 Server";
        public string MessageType { get; }
        public byte[] Payload { get; }
        public int PayloadLength => Payload.Length;
        // Only the start of the message was captured (1 MB); the views say so through its preview's notice.
        public bool PayloadTruncated { get; }

        // A text frame's payload is UTF-8 (RFC 6455).
        private readonly bool _textFrame;

        internal WebSocketMessageModel(WebSocketMessageEventArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);

            Timestamp = args.Timestamp;
            ConnectionID = args.ConnectionID;
            RequestNumber = args.RequestNumber;
            RowKey = args.RowKey;
            FromHost = args.FromHost;
            MessageType = args.MessageType switch
            {
                WebSocketMessageType.Text => "Text",
                WebSocketMessageType.Binary => "Binary",
                WebSocketMessageType.Close => "Close",
                _ => args.MessageType.ToString()
            };
            _textFrame = args.MessageType == WebSocketMessageType.Text;
            Payload = args.Message == null ? Array.Empty<byte>() : (byte[])args.Message.Clone();
            PayloadTruncated = args.PayloadTruncated;
        }

        // The preview of this message in the WebSockets tab (WebSocketMessagesView): its payload as captured, which may
        // be only its start, for the Text, JSON and Hex tabs at once. A text frame is UTF-8 text; any other frame has
        // no content type, so its bytes decide how it is shown.
        internal BodyPreviewRequest PreviewRequest() =>
            new(
                Payload,
                ContentType: _textFrame ? "text/plain; charset=utf-8" : null,
                StoredPrefix: PayloadTruncated,
                Parts: BodyPreviewParts.Text | BodyPreviewParts.Json | BodyPreviewParts.Hex);

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

        internal int RemoveByRowKeys(IReadOnlySet<CaptureRowKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            WebSocketMessageModel[] survivors = this.Where(message => !keys.Contains(message.RowKey)).ToArray();
            int removed = Count - survivors.Length;
            if (removed == 0)
            {
                return 0;
            }
            CheckReentrancy();
            Items.Clear();
            CapturedPayloadBytes = 0;
            foreach (WebSocketMessageModel survivor in survivors)
            {
                Items.Add(survivor);
                CapturedPayloadBytes += survivor.PayloadLength;
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return removed;
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
