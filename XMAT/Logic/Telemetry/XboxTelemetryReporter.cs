// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace XMAT.Telemetry
{
    internal interface IXboxTelemetryClient : IDisposable
    {
        void TraceEvent(string payload);
        void Shutdown();
    }

    internal sealed class XboxTelemetryReporter : ITelemetryReporter
    {
        private static readonly Uri Collector = new("https://mobile.events.data.microsoft.com/OneCollector/1.0/");
        private readonly IXboxTelemetryClient _client;
        private readonly IDisposable _exclusion;
        private readonly Func<TelemetryEvent, Guid, string> _serialize;
        private int _disposed;

        private XboxTelemetryReporter(IXboxTelemetryClient client, IDisposable exclusion,
            Func<TelemetryEvent, Guid, string> serialize)
        {
            _client = client;
            _exclusion = exclusion;
            _serialize = serialize;
        }

        internal static XboxTelemetryReporter Create(Func<IXboxTelemetryClient> createClient,
            Func<TelemetryEvent, Guid, string> serialize)
        {
            var exclusion = TelemetryTraffic.Exclude(Collector);
            // If activation fails, retain the exclusion: native teardown may still be in progress.
            return new(createClient(), exclusion, serialize);
        }

        public void Send(TelemetryEvent item, Guid sessionId)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _client.TraceEvent(_serialize(item, sessionId));
        }

        public void Revoke() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                _client.Shutdown();
            }
            finally
            {
                _client.Dispose();
                _exclusion.Dispose();
            }
        }
    }
}
