// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Windows.Networking.Sockets;

namespace XMAT.WebServiceCapture.Proxy
{
    public interface IWebSocketProxy
    {
        event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
        event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
        event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
        event EventHandler ProxyStopped;

//        bool IsProxyEnabled { get; }

//        void StartProxy(WebServiceProxyOptions options);

//        void StopProxy();

//        void Reset();
    }
}
