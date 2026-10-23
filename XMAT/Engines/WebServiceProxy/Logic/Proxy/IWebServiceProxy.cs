// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
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
        event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
        event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
        event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
        event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
        event EventHandler ProxyStopped;

        bool IsProxyEnabled { get; }

        BypassListModel BypassList { get; set; }

        Func<SslConnectionRequestEventArgs, CancellationToken, Task<bool>> SslConnectionRequestPolicyAsync { get; set; }
        Func<HttpRequestEventArgs, CancellationToken, Task<bool>> WebRequestPolicyAsync { get; set; }
        Func<HttpResponseEventArgs, CancellationToken, Task<bool>> WebResponsePolicyAsync { get; set; }

        // Completes only after both listeners are bound. On failure, partial startup is rolled back and the exception propagates.
        Task StartProxyAsync(WebServiceProxyOptions options, CancellationToken cancellationToken = default);

        Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null);

        void Reset();
    }
}
