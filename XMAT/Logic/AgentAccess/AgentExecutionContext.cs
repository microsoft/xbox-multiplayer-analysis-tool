// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentExecutionContext
    {
        private readonly object _gate = new();
        private Action<AgentResourceKey, string, Func<Task<AgentCleanupOutcome>>> _register;
        private bool _closed;

        internal CancellationToken CancellationToken { get; }

        internal AgentExecutionContext(
            Action<AgentResourceKey, string, Func<Task<AgentCleanupOutcome>>> register,
            CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            _register = register;
        }

        internal void OwnResource(
            AgentResourceKey resource,
            string revision,
            Func<Task<AgentCleanupOutcome>> cleanup)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("The operation has finished.");
                }
                _register(resource, revision, cleanup);
            }
        }

        internal void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _register = null;
            }
        }
    }
}
