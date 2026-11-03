// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.NetworkTrace.NTDE
{
    internal sealed class NetworkTraceEngineException(
        string localizedMessage, bool mayHaveCommitted) : InvalidOperationException(localizedMessage)
    {
        internal bool MayHaveCommitted { get; } = mayHaveCommitted;
    }
}
