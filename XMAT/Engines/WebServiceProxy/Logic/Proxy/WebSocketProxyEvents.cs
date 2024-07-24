// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net.Sockets;

namespace XMAT.WebServiceCapture.Proxy
{
    public class WebSocketOpenedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public TcpClient TcpClient { get; set; }
        public bool AcceptConnection { get; set; }
    }

    public class WebSocketMessageEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public byte[] Message { get; set; }
    }

    public class WebSocketClosedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
    }
}
