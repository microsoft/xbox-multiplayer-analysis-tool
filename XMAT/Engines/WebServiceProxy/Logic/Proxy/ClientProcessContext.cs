// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Net;
using System.Threading;
using XMAT.Operations.Processes;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed class ClientProcessContext
    {
        private readonly object _publicationGate = new();
        private ClientProcessInfo _identity = ClientProcessInfo.NotRecorded;
        private long _nextPublicationOrder;
        private long _publishedOrder;

        internal ClientProcessContext(Guid runId, long connectionToken, IPEndPoint clientEndPoint, IPEndPoint
            proxyEndPoint, DateTimeOffset acceptedAtUtc, long acceptedAtTimestamp)
        {
            RunId = runId;
            ConnectionToken = connectionToken;
            ClientEndPoint = clientEndPoint;
            ProxyEndPoint = proxyEndPoint;
            AcceptedAtUtc = acceptedAtUtc;
            AcceptedAtTimestamp = acceptedAtTimestamp;
        }

        internal Guid RunId { get; }
        internal long ConnectionToken { get; }
        internal IPEndPoint ClientEndPoint { get; }
        internal IPEndPoint ProxyEndPoint { get; }
        internal DateTimeOffset AcceptedAtUtc { get; }
        internal long AcceptedAtTimestamp { get; }

        internal ClientProcessInfo Identity => Volatile.Read(ref _identity);

        internal event Action<ClientProcessContext, ClientProcessInfo> IdentityChanged;

        internal void Publish(ClientProcessInfo info)
        {
            long order = ReservePublicationOrder();
            _ = TryPublish(info, order);
        }

        internal long ReservePublicationOrder()
        {
            lock (_publicationGate)
            {
                return checked(++_nextPublicationOrder);
            }
        }

        internal bool TryPublish(ClientProcessInfo info, long publicationOrder)
        {
            ClientProcessInfo value = info ?? ClientProcessInfo.NotRecorded;
            lock (_publicationGate)
            {
                if (publicationOrder <= _publishedOrder)
                {
                    return false;
                }
                _publishedOrder = publicationOrder;
                if (_nextPublicationOrder < publicationOrder)
                {
                    _nextPublicationOrder = publicationOrder;
                }
                ClientProcessInfo previous = Interlocked.Exchange(ref _identity, value);
                if (previous != value)
                {
                    IdentityChanged?.Invoke(this, value);
                }
            }
            return true;
        }
    }
}
