// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.AgentAccess
{
    // This connection-owned object is deliberately not reconstructible from its public ID.
    internal sealed class AgentSession
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }
}
