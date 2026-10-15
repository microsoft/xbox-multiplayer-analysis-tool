// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.WebServiceCapture.Proxy
{
    internal enum ProxyState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        // Cleanup failed. Start is rejected until a later stop succeeds.
        Faulted
    }
}