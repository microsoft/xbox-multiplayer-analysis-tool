// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.WebServiceCapture.Proxy
{
    internal static class ProxyShutdownPolicy
    {
        // One budget for stopping both proxy hosts. It does not cover the capture callback drain.
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    }
}
