// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Net.WebSockets;
using XMAT.Operations.Captures;

namespace XMAT.WebServiceCapture.Proxy
{
    public class WebSocketOpenedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public int RequestNumber { get; set; }
        public string RemoteEndPoint { get; set; }
        public string SubProtocol { get; set; }
        public bool AcceptConnection { get; set; }
        internal ClientProcessContext ProcessContext { get; set; }
    }

    public class WebSocketMessageEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public int RequestNumber { get; set; }
        public bool FromHost { get; set; }
        public WebSocketMessageType MessageType { get; set; }
        public byte[] Message { get; set; }
        public bool PayloadTruncated { get; set; }
        internal CaptureRowKey RowKey { get; set; }
        internal ClientProcessContext ProcessContext { get; set; }
    }

    public class WebSocketClosedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public int RequestNumber { get; set; }
        internal ClientProcessContext ProcessContext { get; set; }
    }
}
