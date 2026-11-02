// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.WebServiceCapture
{
    // A Local PC proxy recovery outcome reported to the user as guidance, such as settings that were kept or a restore
    // that could not complete. It says nothing about whether a capture may be active; that is
    // ProxyActivationUncertainException.
    internal sealed class ProxyRecoveryException : InvalidOperationException
    {
        internal ProxyRecoveryException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }
}
