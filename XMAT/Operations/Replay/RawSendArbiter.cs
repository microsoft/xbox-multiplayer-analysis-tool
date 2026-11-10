// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Replay
{
    // The events that end, or move, a raw send.
    internal enum RawSendEvent
    {
        Begin,
        SetupFailure,
        Ready,
        Response,
        ReaderFailure,
        ReadFailure,
        Deadline,
        Cancel,
        WriterFailure,
        GraceExpired,
    }

    internal enum RawArbiterState
    {
        NotBegun,
        Setup,
        Open,
        Grace,
        Committed,
    }

    // The operations that a raw send starts, each through a lease (operation leases).
    internal enum RawOperation
    {
        Lookup,
        Connect,
        Tls,
        Writer,
        Reader,
    }

    // The clock that the deadline and the grace run on: the system's, or a test's that delivers their events when told.
    internal interface IRawSendClock
    {
        TimeSpan Now { get; }

        IDisposable Schedule(TimeSpan due, Action callback);
    }

    internal sealed class SystemRawSendClock : IRawSendClock
    {
        private readonly long _origin = Stopwatch.GetTimestamp();

        internal static SystemRawSendClock Instance { get; } = new();

        public TimeSpan Now => Stopwatch.GetElapsedTime(_origin);

        public IDisposable Schedule(TimeSpan due, Action callback) =>
            new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
    }

    // One raw send's terminal arbiter. It takes one event at a time, in the order that it receives them,
    // and commits exactly one cause; every later event is ignored. Only after it has committed does it cancel its own
    // token, which every operation of the send observes, and dispose the streams that it owns, so a fault that the
    // closing causes is a consequence, never a cause. Every operation is leased (Reserve, Invoke, Attach), and SettleAsync
    // awaits them all after the commit.
    internal sealed class RawSendArbiter
    {
        // The most that a writer failure waits for a response that is already arriving.
        internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

        private readonly object _lock = new();
        private readonly CancellationTokenSource _token = new();
        private readonly IRawSendClock _clock;
        private readonly List<IDisposable> _owned = new();
        private readonly List<Lease> _leases = new();
        private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable _deadline;
        private IDisposable _grace;
        private TimeSpan? _deadlineAt;

        internal RawSendArbiter(IRawSendClock clock = null)
        {
            _clock = clock ?? SystemRawSendClock.Instance;
        }

        internal RawArbiterState State { get; private set; }

        // The committed cause and its code and response; a Cancel that came before Begin is NotBegun's.
        internal RawSendEvent? Cause { get; private set; }

        internal string Code { get; private set; }

        internal RawResponse Response { get; private set; }

        internal bool CancelledBeforeBegin { get; private set; }

        internal bool IsCommitted
        {
            get
            {
                lock (_lock)
                {
                    return State == RawArbiterState.Committed;
                }
            }
        }

        // The token that every operation observes: cancelled only after the commit.
        internal CancellationToken Token => _token.Token;

        internal Task Committed => _committed.Task;

        // Begin's step, its first half: Begin, which starts the deadline, and the first operation's Reserve, in one ordering
        // point. Null when a Cancel came first, so the send never began.
        internal Lease Begin(TimeSpan timeout, RawOperation first)
        {
            lock (_lock)
            {
                if (State != RawArbiterState.NotBegun)
                {
                    return null;
                }

                State = RawArbiterState.Setup;
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    _deadlineAt = _clock.Now + timeout;
                    _deadline = _clock.Schedule(timeout, () => Deliver(RawSendEvent.Deadline));
                }

                return ReserveLocked(first);
            }
        }

        // An operation's Reserve; null once the arbiter has committed, so the operation is never started.
        internal Lease Reserve(RawOperation operation)
        {
            lock (_lock)
            {
                return State == RawArbiterState.Committed ? null : ReserveLocked(operation);
            }
        }

        // A resource that the commit disposes: at once if the arbiter has already committed.
        internal void Own(IDisposable resource)
        {
            lock (_lock)
            {
                if (State != RawArbiterState.Committed)
                {
                    _owned.Add(resource);
                    return;
                }
            }

            DisposeQuietly(resource);
        }

        internal void Deliver(RawSendEvent received, string code = null, RawResponse response = null)
        {
            lock (_lock)
            {
                switch (State)
                {
                    case RawArbiterState.NotBegun when received == RawSendEvent.Cancel:
                        CancelledBeforeBegin = true;
                        CommitLocked(RawSendEvent.Cancel, null, null);
                        break;

                    case RawArbiterState.Setup when received == RawSendEvent.Ready:
                        State = RawArbiterState.Open;
                        return;

                    case RawArbiterState.Setup when received is RawSendEvent.SetupFailure or RawSendEvent.Deadline or
                        RawSendEvent.Cancel:
                        CommitLocked(received, code, null);
                        break;

                    case RawArbiterState.Open when received == RawSendEvent.WriterFailure:
                        State = RawArbiterState.Grace;
                        if (_deadlineAt == null || _clock.Now + Grace < _deadlineAt)
                        {
                            _grace = _clock.Schedule(Grace, () => Deliver(RawSendEvent.GraceExpired));
                        }

                        return;

                    case RawArbiterState.Open when received is RawSendEvent.Response or RawSendEvent.ReaderFailure or
                        RawSendEvent.ReadFailure or RawSendEvent.Deadline or RawSendEvent.Cancel:
                    case RawArbiterState.Grace when received is RawSendEvent.Response or RawSendEvent.Deadline or
                        RawSendEvent.Cancel:
                        CommitLocked(received, code, response);
                        break;

                    case RawArbiterState.Grace when received is RawSendEvent.ReaderFailure or RawSendEvent.ReadFailure or
                        RawSendEvent.GraceExpired:
                        CommitLocked(RawSendEvent.WriterFailure, null, null);
                        break;

                    default:
                        return;
                }
            }

            Close();
        }

        // After the commit: every reserved operation, once attached, awaited, its exception observed and discarded.
        internal async Task SettleAsync()
        {
            Lease[] leases;
            lock (_lock)
            {
                leases = _leases.ToArray();
            }

            foreach (Lease lease in leases)
            {
                Task task = await lease.Attached.ConfigureAwait(false);
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A consequence of the closing, or a failure that a committed cause already accounts for.
                }
            }
        }

        private Lease ReserveLocked(RawOperation operation)
        {
            var lease = new Lease(operation);
            _leases.Add(lease);
            return lease;
        }

        private void CommitLocked(RawSendEvent cause, string code, RawResponse response)
        {
            State = RawArbiterState.Committed;
            Cause = cause;
            Code = cause switch
            {
                RawSendEvent.Response => response?.TooLarge == true ? ReplaySender.TooLargeCode : null,
                RawSendEvent.Deadline => ReplaySender.TimeoutCode,
                RawSendEvent.Cancel => RawCodes.SendCancelled,
                RawSendEvent.WriterFailure => ReplaySender.SendFailedCode,
                _ => code ?? ReplaySender.SendFailedCode,
            };
            Response = response;
        }

        // The commit's closing, outside the lock: the timers stopped, the token cancelled, then every owned stream disposed.
        private void Close()
        {
            IDisposable[] owned;
            lock (_lock)
            {
                owned = _owned.ToArray();
                _owned.Clear();
            }

            DisposeQuietly(_deadline);
            DisposeQuietly(_grace);
            try
            {
                _token.Cancel();
            }
            catch (AggregateException)
            {
                // A consequence of the closing.
            }

            foreach (IDisposable resource in owned)
            {
                DisposeQuietly(resource);
            }

            _committed.TrySetResult();
        }

        private static void DisposeQuietly(IDisposable resource)
        {
            try
            {
                resource?.Dispose();
            }
            catch (Exception)
            {
                // A consequence of the closing.
            }
        }

        // One operation's reservation: its task once attached.
        internal sealed class Lease
        {
            private readonly TaskCompletionSource<Task> _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Lease(RawOperation operation)
            {
                Operation = operation;
            }

            internal RawOperation Operation { get; }

            internal Task<Task> Attached => _attached.Task;

            internal void Attach(Task task) => _attached.TrySetResult(task);
        }
    }
}
