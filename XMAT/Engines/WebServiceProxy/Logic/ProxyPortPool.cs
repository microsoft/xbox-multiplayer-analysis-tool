// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace XMAT.WebServiceCapture
{
    internal static class ProxyPortPool
    {
        private static bool _initialized = false;

        public static UInt16 FirstPort { get; set; }
        public static UInt16 LastPort { get; set; }

        public static readonly UInt16 DefaultFirstPort = 8880;
        public static readonly UInt16 DefaultLastPort = 8889;

        private class PooledPort
        {
            public PooledPort(UInt16 port)
            {
                Port = port;
            }

            public UInt16 Port { get; private set; }
            public bool InUse { get; set; }
        }

        private static PooledPort[] _pooledPorts = Array.Empty<PooledPort>();
        private static readonly object _lockObj = new object();

        internal static void Initialize(UInt16 firstPort, UInt16 lastPort)
        {
            if (_initialized && FirstPort == firstPort && LastPort == lastPort)
            {
                return;
            }

            FirstPort = firstPort;
            LastPort = lastPort;

            lock(_lockObj)
            {
                FirstPort = Math.Max((UInt16)0, FirstPort);
                LastPort = Math.Max(FirstPort, LastPort);
                var totalPorts = LastPort - FirstPort + 1;

                _pooledPorts = new PooledPort[totalPorts];
                for (var port = FirstPort; port <= LastPort; port++)
                {
                    _pooledPorts[port - FirstPort] = new PooledPort(port);
                }
            }

            _initialized = true;
        }

        internal static UInt16 ObtainPort()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Please initialize the proxy port pool.");
            }

            lock (_lockObj)
            {
                foreach (var pooledPort in _pooledPorts)
                {
                    if (!pooledPort.InUse)
                    {
                        pooledPort.InUse = true;
                        return pooledPort.Port;
                    }
                }
                return 0;
            }
        }

        internal static void ReleasePort(int port)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Please initialize the proxy port pool.");
            }

            lock (_lockObj)
            {
                foreach (var pooledPort in _pooledPorts)
                {
                    if (pooledPort.Port == port && pooledPort.InUse)
                    {
                        pooledPort.InUse = false;
                    }
                }
            }
        }
    }
}
