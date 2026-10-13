// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;

namespace XMAT
{
    internal sealed class AsyncOperationTracker
    {
        internal void StartAccepting()
        {
            lock (_syncRoot)
            {
                if (_activeOperationCount != 0)
                {
                    throw new InvalidOperationException("Cannot restart while operations are still active.");
                }

                _isAccepting = true;
            }
        }

        internal bool TryBegin(out IDisposable operationLease)
        {
            lock (_syncRoot)
            {
                if (!_isAccepting)
                {
                    operationLease = null;
                    return false;
                }

                if (_activeOperationCount == 0)
                {
                    _operationsDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeOperationCount++;
                operationLease = new OperationLease(this);
                return true;
            }
        }

        internal Task StopAcceptingAndWaitAsync()
        {
            lock (_syncRoot)
            {
                _isAccepting = false;
                return _activeOperationCount == 0
                    ? Task.CompletedTask
                    : _operationsDrained.Task;
            }
        }

        private void EndOperation()
        {
            TaskCompletionSource operationsDrained = null;

            lock (_syncRoot)
            {
                _activeOperationCount--;
                if (_activeOperationCount == 0)
                {
                    operationsDrained = _operationsDrained;
                }
            }

            operationsDrained?.TrySetResult();
        }

        private readonly object _syncRoot = new();
        private bool _isAccepting;
        private int _activeOperationCount;
        private TaskCompletionSource _operationsDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class OperationLease : IDisposable
        {
            private AsyncOperationTracker _owner;

            internal OperationLease(AsyncOperationTracker owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.EndOperation();
            }
        }
    }
}
