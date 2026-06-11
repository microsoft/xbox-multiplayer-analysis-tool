// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture.Proxy
{
    public interface IWebServiceProxy
    {
        event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
        event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
        event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
        event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
        event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
        event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
        event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
        event EventHandler ProxyStopped;

        bool IsProxyEnabled { get; }

        BypassListModel BypassList { get; set; }

        void StartProxy(WebServiceProxyOptions options);

        void StopProxy();

        void Reset();
    }
}
