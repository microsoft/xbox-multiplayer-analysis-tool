// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // The raw send's terminal arbiter (slice L, contract 2.6): its states, the one cause that it commits from events in the
    // order that it receives them, the deadline that starts at Begin, the grace that is never armed past the deadline, and
    // a commit that cancels its token and disposes what it owns only after the cause is set.
    public sealed class RawSendArbiterTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private static readonly RawResponse Ok = new(
            "HTTP/1.1 200 OK", 200, "OK", new Version(1, 1), Array.Empty<KeyValuePair<string, string>>(),
            Array.Empty<byte>(), false, "content-length", 0);

        private static readonly RawResponse Big = new(
            "HTTP/1.1 200 OK", 200, "OK", new Version(1, 1), Array.Empty<KeyValuePair<string, string>>(),
            Array.Empty<byte>(), true, "content-length", 0);

        [Fact]
        public void ACancelBeforeBegin_EndsTheSendBeforeItBegan()
        {
            var clock = new ManualRawClock();
            var arbiter = new RawSendArbiter(clock);

            arbiter.Deliver(RawSendEvent.Cancel);

            Assert.True(arbiter.CancelledBeforeBegin);
            Assert.Null(arbiter.Begin(Timeout, RawOperation.Lookup));
            Assert.True(arbiter.Token.IsCancellationRequested);
            Assert.Empty(clock.Scheduled);
        }

        [Fact]
        public void TheDeadline_StartsAtBegin()
        {
            var clock = new ManualRawClock();
            var arbiter = new RawSendArbiter(clock);
            Assert.Empty(clock.Scheduled);
            clock.Now = TimeSpan.FromSeconds(7);

            Assert.NotNull(arbiter.Begin(Timeout, RawOperation.Connect));

            ManualRawClock.Timer deadline = Assert.Single(clock.Scheduled);
            Assert.Equal(TimeSpan.FromSeconds(37), deadline.Due);
            Assert.Equal(RawArbiterState.Setup, arbiter.State);
        }

        [Theory]
        [InlineData("SetupFailure", "replay.raw.alpn", "replay.raw.alpn")]
        [InlineData("Deadline", null, ReplaySender.TimeoutCode)]
        [InlineData("Cancel", null, RawCodes.SendCancelled)]
        public void InSetup_ASetupFailureTheDeadlineOrACancel_Commits(string receivedName, string code, string expected)
        {
            RawSendEvent received = Enum.Parse<RawSendEvent>(receivedName);
            RawSendArbiter arbiter = Begun();

            arbiter.Deliver(received, code);

            Assert.Equal((RawArbiterState.Committed, received, expected), (arbiter.State, arbiter.Cause, arbiter.Code));
        }

        [Theory]
        [InlineData("Response")]
        [InlineData("ReaderFailure")]
        [InlineData("ReadFailure")]
        [InlineData("WriterFailure")]
        [InlineData("GraceExpired")]
        public void InSetup_NoWriterOrReaderEventCanOccur_BeforeReady(string receivedName)
        {
            RawSendEvent received = Enum.Parse<RawSendEvent>(receivedName);
            RawSendArbiter arbiter = Begun();

            arbiter.Deliver(received, RawCodes.ResponseInvalid, Ok);

            Assert.Equal(RawArbiterState.Setup, arbiter.State);
            Assert.False(arbiter.Token.IsCancellationRequested);
            arbiter.Deliver(RawSendEvent.Ready);
            Assert.Equal(RawArbiterState.Open, arbiter.State);
        }

        [Theory]
        [InlineData("ReaderFailure", RawCodes.ResponseInvalid, RawCodes.ResponseInvalid)]
        [InlineData("ReadFailure", RawCodes.ResponseIncomplete, RawCodes.ResponseIncomplete)]
        [InlineData("Deadline", null, ReplaySender.TimeoutCode)]
        [InlineData("Cancel", null, RawCodes.SendCancelled)]
        public void InOpen_AFailureTheDeadlineOrACancel_Commits(string receivedName, string code, string expected)
        {
            RawSendEvent received = Enum.Parse<RawSendEvent>(receivedName);
            RawSendArbiter arbiter = Open();

            arbiter.Deliver(received, code);

            Assert.Equal((received, expected), (arbiter.Cause, arbiter.Code));
        }

        [Fact]
        public void InOpen_AResponseCommits_TooLargeWithItsCode()
        {
            RawSendArbiter complete = Open();
            complete.Deliver(RawSendEvent.Response, null, Ok);
            Assert.Equal((RawSendEvent.Response, (string)null), (complete.Cause, complete.Code));
            Assert.Same(Ok, complete.Response);

            RawSendArbiter tooLarge = Open();
            tooLarge.Deliver(RawSendEvent.Response, null, Big);
            Assert.Equal((RawSendEvent.Response, ReplaySender.TooLargeCode), (tooLarge.Cause, tooLarge.Code));
        }

        [Fact]
        public void AWriterFailure_EntersTheGrace_ForAResponseAlreadyArriving()
        {
            var clock = new ManualRawClock();
            RawSendArbiter arbiter = Open(clock);

            arbiter.Deliver(RawSendEvent.WriterFailure);

            Assert.Equal(RawArbiterState.Grace, arbiter.State);
            Assert.Equal(TimeSpan.FromSeconds(1), clock.Scheduled[^1].Due);
            arbiter.Deliver(RawSendEvent.Response, null, Ok);
            Assert.Equal((RawSendEvent.Response, (string)null), (arbiter.Cause, arbiter.Code));
        }

        [Theory]
        [InlineData("ReaderFailure")]
        [InlineData("ReadFailure")]
        [InlineData("GraceExpired")]
        public void InTheGrace_AReaderFailureOrItsEnd_CommitsTheWriterFailure(string receivedName)
        {
            RawSendEvent received = Enum.Parse<RawSendEvent>(receivedName);
            RawSendArbiter arbiter = Open();
            arbiter.Deliver(RawSendEvent.WriterFailure);

            arbiter.Deliver(received, RawCodes.ResponseIncomplete);

            Assert.Equal((RawSendEvent.WriterFailure, ReplaySender.SendFailedCode), (arbiter.Cause, arbiter.Code));
        }

        [Theory]
        [InlineData("Deadline", ReplaySender.TimeoutCode)]
        [InlineData("Cancel", RawCodes.SendCancelled)]
        public void InTheGrace_TheDeadlineOrACancel_CommitsItself(string receivedName, string code)
        {
            RawSendEvent received = Enum.Parse<RawSendEvent>(receivedName);
            RawSendArbiter arbiter = Open();
            arbiter.Deliver(RawSendEvent.WriterFailure);

            arbiter.Deliver(received);
            arbiter.Deliver(RawSendEvent.Response, null, Ok);

            Assert.Equal((received, code), (arbiter.Cause, arbiter.Code));
            Assert.Null(arbiter.Response);
        }

        [Fact]
        public void AtTheGracesBoundary_TheEventReceivedFirstDecides()
        {
            var clock = new ManualRawClock();
            RawSendArbiter first = Open(clock);
            first.Deliver(RawSendEvent.WriterFailure);
            ManualRawClock.Timer grace = clock.Scheduled[^1];
            first.Deliver(RawSendEvent.Response, null, Ok);
            grace.Callback();
            Assert.Equal(RawSendEvent.Response, first.Cause);

            var late = new ManualRawClock();
            RawSendArbiter expired = Open(late);
            expired.Deliver(RawSendEvent.WriterFailure);
            late.Fire(late.Scheduled[^1]);
            expired.Deliver(RawSendEvent.Response, null, Ok);
            Assert.Equal((RawSendEvent.WriterFailure, ReplaySender.SendFailedCode), (expired.Cause, expired.Code));
            Assert.Null(expired.Response);
        }

        [Fact]
        public void TheGrace_IsNeverArmedPastTheDeadline()
        {
            var clock = new ManualRawClock();
            var arbiter = new RawSendArbiter(clock);
            arbiter.Begin(TimeSpan.FromSeconds(1.5), RawOperation.Connect);
            arbiter.Deliver(RawSendEvent.Ready);
            clock.Now = TimeSpan.FromSeconds(0.6);

            arbiter.Deliver(RawSendEvent.WriterFailure);

            ManualRawClock.Timer deadline = Assert.Single(clock.Scheduled);
            clock.Fire(deadline);
            Assert.Equal((RawSendEvent.Deadline, ReplaySender.TimeoutCode), (arbiter.Cause, arbiter.Code));

            // A grace that would end exactly at the deadline would reach it, so it is not armed either.
            var boundary = new ManualRawClock();
            var reaching = new RawSendArbiter(boundary);
            reaching.Begin(TimeSpan.FromSeconds(1.6), RawOperation.Connect);
            reaching.Deliver(RawSendEvent.Ready);
            boundary.Now = TimeSpan.FromSeconds(0.6);

            reaching.Deliver(RawSendEvent.WriterFailure);

            Assert.Equal(TimeSpan.FromSeconds(1.6), Assert.Single(boundary.Scheduled).Due);
        }

        [Fact]
        public void TheCommit_IsFinal_AndCancelsTheTokenAndDisposesWhatItOwns_OnlyThen()
        {
            RawSendArbiter arbiter = Open();
            var early = new Disposable(arbiter);
            arbiter.Own(early);
            Assert.False(arbiter.Token.IsCancellationRequested);
            Assert.False(early.Disposed);
            RawSendEvent? causeWhenCancelled = null;
            arbiter.Token.Register(() => causeWhenCancelled = arbiter.Cause);

            arbiter.Deliver(RawSendEvent.Cancel);
            arbiter.Deliver(RawSendEvent.Response, null, Ok);
            arbiter.Deliver(RawSendEvent.Deadline);

            Assert.Equal(RawSendEvent.Cancel, causeWhenCancelled);
            Assert.Equal(RawSendEvent.Cancel, arbiter.Cause);
            Assert.True(early.Disposed);
            Assert.Equal(RawSendEvent.Cancel, early.CauseWhenDisposed);
            Assert.True(arbiter.Committed.IsCompleted);

            var late = new Disposable(arbiter);
            arbiter.Own(late);
            Assert.True(late.Disposed);
            Assert.Null(arbiter.Reserve(RawOperation.Writer));
        }

        [Fact]
        public async Task SettleAsync_AwaitsEveryReservedOperation_OnceAttached()
        {
            RawSendArbiter arbiter = Open();
            RawSendArbiter.Lease writer = arbiter.Reserve(RawOperation.Writer);
            var pending = new TaskCompletionSource();
            arbiter.Deliver(RawSendEvent.Cancel);

            Task settling = arbiter.SettleAsync();
            Assert.False(settling.IsCompleted);
            writer.Attach(pending.Task);
            Assert.False(settling.IsCompleted);
            pending.SetException(new IOException("disposed"));

            await settling.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static RawSendArbiter Begun(ManualRawClock clock = null)
        {
            var arbiter = new RawSendArbiter(clock ?? new ManualRawClock());
            RawSendArbiter.Lease first = arbiter.Begin(Timeout, RawOperation.Lookup);
            Assert.NotNull(first);
            first.Attach(Task.CompletedTask);
            return arbiter;
        }

        private static RawSendArbiter Open(ManualRawClock clock = null)
        {
            RawSendArbiter arbiter = Begun(clock);
            arbiter.Deliver(RawSendEvent.Ready);
            Assert.Equal(RawArbiterState.Open, arbiter.State);
            return arbiter;
        }

        // A disposable that records the arbiter's cause when it is first disposed.
        private sealed class Disposable(RawSendArbiter arbiter) : IDisposable
        {
            internal bool Disposed { get; private set; }

            internal RawSendEvent? CauseWhenDisposed { get; private set; }

            public void Dispose()
            {
                if (!Disposed)
                {
                    CauseWhenDisposed = arbiter.Cause;
                }

                Disposed = true;
            }
        }
    }

    // A clock whose timers fire only when a test fires them.
    internal sealed class ManualRawClock : IRawSendClock
    {
        private readonly List<Timer> _scheduled = new();

        public TimeSpan Now { get; set; }

        // The timers not yet fired or disposed, in the order scheduled.
        internal IReadOnlyList<Timer> Scheduled
        {
            get
            {
                lock (_scheduled)
                {
                    return _scheduled.Where(timer => !timer.Done).ToList();
                }
            }
        }

        public IDisposable Schedule(TimeSpan due, Action callback)
        {
            var timer = new Timer(Now + due, callback);
            lock (_scheduled)
            {
                _scheduled.Add(timer);
            }

            return timer;
        }

        internal void Fire(Timer timer)
        {
            Assert.False(timer.Done);
            timer.Done = true;
            timer.Callback();
        }

        internal sealed class Timer(TimeSpan due, Action callback) : IDisposable
        {
            internal TimeSpan Due { get; } = due;

            internal Action Callback { get; } = callback;

            internal bool Done { get; set; }

            public void Dispose() => Done = true;
        }
    }
}
