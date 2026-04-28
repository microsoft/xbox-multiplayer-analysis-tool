// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.WebServiceCapture.Proxy
{
    public interface IWebSocketProxy
    {
        event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
        event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
        event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
    }
}
