// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;

namespace XMAT.Telemetry
{
    internal static class TelemetryTraffic
    {
        private static readonly Dictionary<(string Host, int Port), int> _destinations = new();
        private static readonly object _gate = new();

        internal static IDisposable Exclude(Uri endpoint)
        {
            var destination = (endpoint.IdnHost.ToUpperInvariant(), endpoint.Port);
            lock (_gate)
            {
                _destinations.TryGetValue(destination, out var references);
                _destinations[destination] = references + 1;
            }
            return new Exclusion(destination);
        }

        internal static bool IsExcluded(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }
            lock (_gate)
            {
                return _destinations.ContainsKey((host.ToUpperInvariant(), port));
            }
        }

        private sealed class Exclusion((string Host, int Port) destination) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_destinations[destination] == 1)
                    {
                        _destinations.Remove(destination);
                    }
                    else
                    {
                        _destinations[destination]--;
                    }
                }
            }
        }
    }
}
