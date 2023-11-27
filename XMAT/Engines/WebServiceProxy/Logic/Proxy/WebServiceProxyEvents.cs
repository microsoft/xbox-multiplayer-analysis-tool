// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net.Sockets;

namespace XMAT.WebServiceCapture.Proxy
{
    public class InitialConnectionEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public TcpClient TcpClient { get; set; }
        public bool AcceptConnection { get; set; }
    }

    public class SslConnectionRequestEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public string ClientIP { get; set; }
        public ClientRequest Request { get; set; }
        public bool AcceptConnection { get; set; }
    }

    public class SslConnectionCompletionEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public ClientRequest Request { get; set; }
    }

    public class ConnectionFailureEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public Exception Exception { get; set; }
    }

    public class HttpRequestEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public ClientRequest Request { get; set; }
        public bool AcceptRequest { get; set; }
    }

    public class HttpResponseEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
        public ClientRequest Request { get; set; }
        public ServerResponse Response { get; set; }
        public bool SendResponse { get; set; }
    }

    public class ConnectionClosedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public int ConnectionID { get; set; }
    }
}
