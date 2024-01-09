// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

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

        void StartProxy(WebServiceProxyOptions options);

        void StopProxy();

        void Reset();
    }
}
