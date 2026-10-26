// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace XMAT.WebServiceCapture
{
    internal sealed class ProxyPortPool
    {
        internal static ProxyPortPool Shared { get; } = new();
        public const UInt16 DefaultFirstPort = 8880;
        public const UInt16 DefaultLastPort = 8889;

        private readonly object _lockObj = new();
        private readonly Dictionary<UInt16, Reservation> _reservations = new();
        private string _configurationError;
        public UInt16 FirstPort { get; private set; } = DefaultFirstPort;
        public UInt16 LastPort { get; private set; } = DefaultLastPort;

        internal void Initialize(UInt16 firstPort, UInt16 lastPort)
        {
            if (firstPort == 0 || lastPort < firstPort)
            {
                throw new ArgumentOutOfRangeException(nameof(firstPort),
                    Localization.GetLocalizedString("PROXY_PORT_RANGE_INVALID"));
            }

            lock (_lockObj)
            {
                FirstPort = firstPort;
                LastPort = lastPort;
                _configurationError = null;
            }
        }

        internal void RestoreRange(UInt16 firstPort, UInt16 lastPort)
        {
            try
            {
                Initialize(firstPort, lastPort);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                lock (_lockObj)
                {
                    _configurationError = ex.Message;
                }
                PublicUtilities.AppLog(LogLevel.ERROR, $"Invalid saved proxy allocation range: {ex}");
            }
        }

        internal Reservation ObtainPort(string owner)
        {
            lock (_lockObj)
            {
                if (_configurationError != null)
                {
                    throw new InvalidOperationException(_configurationError);
                }
                for (var port = (int)FirstPort; port <= LastPort; port++)
                {
                    if (!_reservations.ContainsKey((UInt16)port))
                    {
                        return ReservePort((UInt16)port, owner);
                    }
                }
                throw new InvalidOperationException(
                    Localization.GetLocalizedString("PROXY_PORT_EXHAUSTED", FirstPort, LastPort));
            }
        }

        internal Reservation ReservePort(UInt16 port, string owner)
        {
            if (port == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            lock (_lockObj)
            {
                if (_reservations.TryGetValue(port, out var existing))
                {
                    throw new InvalidOperationException(
                        Localization.GetLocalizedString("PROXY_PORT_IN_USE", port, existing.Owner));
                }

                var reservation = new Reservation(this, port, owner);
                _reservations.Add(port, reservation);
                return reservation;
            }
        }

        internal sealed class Reservation : IDisposable
        {
            private readonly ProxyPortPool _pool;
            public UInt16 Port { get; }
            internal string Owner { get; }

            internal Reservation(ProxyPortPool pool, UInt16 port, string owner)
            {
                _pool = pool;
                Port = port;
                Owner = owner;
            }

            public void Dispose()
            {
                lock (_pool._lockObj)
                {
                    if (_pool._reservations.TryGetValue(Port, out var current) &&
                        ReferenceEquals(current, this))
                    {
                        _pool._reservations.Remove(Port);
                    }
                }
            }
        }
    }
}
