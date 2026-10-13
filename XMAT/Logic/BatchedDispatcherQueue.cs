// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XMAT
{
    internal sealed class BatchedDispatcherQueue
    {
        internal int PendingCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _pendingActions.Count;
                }
            }
        }

        internal BatchedDispatcherQueue(
            Dispatcher dispatcher,
            int batchSize,
            Action<Exception> errorHandler = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
            _batchSize = batchSize;
            _errorHandler = errorHandler;
        }

        internal void StartAccepting()
        {
            lock (_syncRoot)
            {
                if (_isAccepting || _isDrainScheduled || _pendingActions.Count != 0)
                {
                    throw new InvalidOperationException(
                        "Cannot restart while dispatcher work is pending.");
                }

                _isAccepting = true;
                _drained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal bool TryEnqueue(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            var scheduleDrain = false;
            lock (_syncRoot)
            {
                if (!_isAccepting)
                {
                    return false;
                }

                _pendingActions.Enqueue(action);
                if (!_isDrainScheduled)
                {
                    _isDrainScheduled = true;
                    scheduleDrain = true;
                }
            }

            if (scheduleDrain)
            {
                ScheduleDrain();
            }

            return true;
        }

        internal Task CompleteAndDrainAsync()
        {
            lock (_syncRoot)
            {
                _isAccepting = false;
                CompleteDrainIfReady();
                return _drained?.Task ?? Task.CompletedTask;
            }
        }

        private void ScheduleDrain()
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                FailDrain(new InvalidOperationException(
                    "The UI dispatcher shut down before capture updates drained."));
                return;
            }

            try
            {
                var operation = _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(DrainBatch));
                operation.Aborted += (sender, args) =>
                    FailDrain(new TaskCanceledException(
                        "The UI dispatcher aborted a capture update batch."));

                if (operation.Status == DispatcherOperationStatus.Aborted)
                {
                    FailDrain(new TaskCanceledException(
                        "The UI dispatcher aborted a capture update batch."));
                }
            }
            catch (Exception ex)
            {
                FailDrain(ex);
            }
        }

        private void DrainBatch()
        {
            for (var index = 0; index < _batchSize; index++)
            {
                Action action;
                lock (_syncRoot)
                {
                    if (_pendingActions.Count == 0)
                    {
                        _isDrainScheduled = false;
                        CompleteDrainIfReady();
                        return;
                    }

                    action = _pendingActions.Dequeue();
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _errorHandler?.Invoke(ex);
                }
            }

            lock (_syncRoot)
            {
                if (_pendingActions.Count == 0)
                {
                    _isDrainScheduled = false;
                    CompleteDrainIfReady();
                    return;
                }
            }

            ScheduleDrain();
        }

        private void CompleteDrainIfReady()
        {
            if (!_isAccepting &&
                !_isDrainScheduled &&
                _pendingActions.Count == 0)
            {
                _drained?.TrySetResult();
            }
        }

        private void FailDrain(Exception exception)
        {
            lock (_syncRoot)
            {
                _isAccepting = false;
                _isDrainScheduled = false;
                _pendingActions.Clear();
                _drained?.TrySetException(exception);
            }
        }

        private readonly object _syncRoot = new();
        private readonly Dispatcher _dispatcher;
        private readonly int _batchSize;
        private readonly Action<Exception> _errorHandler;
        private readonly Queue<Action> _pendingActions = new();
        private bool _isAccepting;
        private bool _isDrainScheduled;
        private TaskCompletionSource _drained;
    }
}
