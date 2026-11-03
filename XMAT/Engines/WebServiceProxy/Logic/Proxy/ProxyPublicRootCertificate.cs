// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace XMAT.WebServiceCapture.Proxy
{
    internal static class ProxyPublicRootCertificate
    {
        private static Func<byte[]> _read;

        internal static void RegisterInitializedReader(Func<byte[]> read) => Volatile.Write(ref _read, read);

        // Reading diagnostics before normal proxy initialization must not construct a certificate manager.
        internal static byte[] Read() => Volatile.Read(ref _read)?.Invoke();
    }
}
