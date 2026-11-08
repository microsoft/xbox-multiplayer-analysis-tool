// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Net;
using System.Text;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // A connection that a test scripts: response bytes pushed as the test chooses, reads that fail, write calls that wait
    // for a gate, fault or complete, and disposal that ends whatever is pending, as closing a socket does.
    internal sealed class SeamConnection : Stream
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _incoming = new();
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        private bool _ended;
        private Exception _readFault;
        private int _writesStarted;

        internal MemoryStream Written { get; } = new();

        internal int WritesStarted => Volatile.Read(ref _writesStarted);

        // Before write call n completes: the task that it waits for (null: none).
        internal Func<int, Task> WriteGate { get; set; }

        // Write call n throws this as it starts (null: none).
        internal Func<int, Exception> WriteFault { get; set; }

        // Called at the end of write call n, once its bytes are taken, before the call returns.
        internal Action<int> AfterWrite { get; set; }

        // Whether a waiting write call ends when the connection is disposed or its token is cancelled; a write that does
        // not ends only when its gate opens.
        internal bool WritesEnd { get; set; } = true;

        internal bool IsDisposed => _disposed.Task.IsCompleted;

        // Thrown by Dispose once the connection is disposed, as by a stream whose closing fails (null: none).
        internal Exception DisposeFault { get; set; }

        // Signalled when a read call starts waiting with nothing to read.
        internal TaskCompletionSource ReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Respond(string text) => Respond(Encoding.Latin1.GetBytes(text));

        internal void Respond(byte[] bytes)
        {
            lock (_lock)
            {
                _incoming.Enqueue(bytes);
            }

            Changed();
        }

        internal void End()
        {
            lock (_lock)
            {
                _ended = true;
            }

            Changed();
        }

        internal void FailReads(Exception exception)
        {
            lock (_lock)
            {
                _readFault = exception;
            }

            Changed();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(IsDisposed, this);
                    if (_incoming.Count > 0)
                    {
                        byte[] next = _incoming.Peek();
                        int count = Math.Min(buffer.Length, next.Length - _offset);
                        next.AsSpan(_offset, count).CopyTo(buffer.Span);
                        _offset += count;
                        if (_offset == next.Length)
                        {
                            _incoming.Dequeue();
                            _offset = 0;
                        }

                        return count;
                    }

                    // A failure comes after the bytes that were pushed before it, where a reset would.
                    if (_readFault != null)
                    {
                        throw _readFault;
                    }

                    if (_ended)
                    {
                        return 0;
                    }

                    changed = _changed.Task;
                }

                ReadWaiting.TrySetResult();
                await Task.WhenAny(changed, _disposed.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref _writesStarted) - 1;
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Exception fault = WriteFault?.Invoke(index);
            if (fault != null)
            {
                throw fault;
            }

            Task gate = WriteGate?.Invoke(index);
            if (gate != null)
            {
                if (WritesEnd)
                {
                    await Task.WhenAny(gate, _disposed.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(!gate.IsCompleted, this);
                }

                await gate.ConfigureAwait(false);
            }

            lock (_lock)
            {
                Written.Write(buffer.Span);
            }

            AfterWrite?.Invoke(index);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            Changed();
            base.Dispose(disposing);
            if (disposing && DisposeFault != null)
            {
                throw DisposeFault;
            }
        }

        private void Changed()
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
        }
    }

    // The raw sender (slice L, contract 2.6, tests 2.12 item 7) through seams that order its events: Begin on each path,
    // setup failures, the write state in each phase, failed reads, faults that a cancellation causes, operation leases,
    // the grace, the deadline, and the exchange that every send that began gives exactly once.
    public sealed class RawSenderTests
    {
        private const string Ok = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        public static TheoryData<string> Paths => new() { "dns", "literal", "connect-to" };

        // Each path, with a cancellation at each point of Begin's step after Begin itself.
        public static TheoryData<string, string> PathsAndBeginSteps
        {
            get
            {
                var data = new TheoryData<string, string>();
                foreach (string path in new[] { "dns", "literal", "connect-to" })
                {
                    foreach (RawSendPoint step in new[] { RawSendPoint.AfterBegin, RawSendPoint.AfterReserve, RawSendPoint.AfterInvoke })
                    {
                        data.Add(path, step.ToString());
                    }
                }

                return data;
            }
        }

        [Fact]
        public async Task ASend_WritesTheImagesExactBytes_AndReadsTheResponse()
        {
            var connection = new SeamConnection();
            connection.Respond(Ok);
            RawRequestImage image = Image("POST /p?q HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5\r\n\r\nhello");
            var sender = new RawSender(Seams(connection));

            ReplayExchange exchange = await sender.SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Completed, (string)null, 200), (exchange.Outcome, exchange.Code, exchange.StatusCode));
            Assert.Equal(image.CopyHead().Concat(image.CopyWireBody()), connection.Written.ToArray());
            Assert.Equal((true, (long?)image.Length, RawWriteState.Complete), (exchange.Raw, exchange.RequestBytesWritten, exchange.RequestWrite));
            Assert.Equal(("content-length", "ok"), (exchange.ResponseFraming, Encoding.ASCII.GetString(exchange.Response.Body)));
            Assert.Equal(("POST /p?q HTTP/1.1", "http://example.test/p?q"), (exchange.Request.StartLine, exchange.RequestUri.OriginalString));
            Assert.Equal(new Uri("http://example.test/"), exchange.ProvenanceOrigin);
            Assert.Null(exchange.Error);
            Assert.True(connection.IsDisposed);
        }

        [Fact]
        public async Task TheProvenanceOrigin_IsTheSourcesWhenGiven()
        {
            var connection = new SeamConnection();
            connection.Respond(Ok);
            var captured = new Uri("https://captured.example/");

            ReplayExchange exchange = await new RawSender(Seams(connection))
                .SendAsync(Image(), Options() with { ProvenanceOrigin = captured }, CancellationToken.None).WaitAsync(Wait);

            Assert.Same(captured, exchange.ProvenanceOrigin);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task ACancellationBeforeBegin_GivesNoExchange_AndStartsNothing(string path)
        {
            var calls = new List<string>();
            using var source = new CancellationTokenSource();
            RawSenderSetup setup = PathSetup(path, new SeamConnection(), calls) with
            {
                Hooks = new RawSenderHooks(At: (point, _) =>
                {
                    if (point == RawSendPoint.BeforeBegin)
                    {
                        source.Cancel();
                    }
                }),
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new RawSender(setup).SendAsync(PathImage(path), Options(), source.Token).WaitAsync(Wait));

            Assert.Empty(calls);
        }

        // A cancellation anywhere in Begin's step, once Begin has happened, is that send's Cancel: one exchange, and the
        // first operation invoked exactly once, since no attempt is marked but not invoked (Y1, Z1).
        [Theory]
        [MemberData(nameof(PathsAndBeginSteps))]
        public async Task ACancellationAfterBegin_GivesOneExchange_WithNothingWritten(string path, string step)
        {
            var calls = new List<string>();
            using var source = new CancellationTokenSource();
            RawSendPoint cancelAt = Enum.Parse<RawSendPoint>(step);
            RawOperation first = path == "dns" ? RawOperation.Lookup : RawOperation.Connect;
            RawSenderSetup setup = PathSetup(path, new SeamConnection(), calls) with
            {
                Hooks = new RawSenderHooks(At: (point, operation) =>
                {
                    if (point == cancelAt && operation == first && !source.IsCancellationRequested)
                    {
                        source.Cancel();
                    }
                }),
            };

            ReplayExchange exchange = await new RawSender(setup).SendAsync(PathImage(path), Options(), source.Token).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, RawCodes.SendCancelled), (exchange.Outcome, exchange.Code));
            Assert.Equal((RawWriteState.None, (long?)0), (exchange.RequestWrite, exchange.RequestBytesWritten));
            Assert.Null(exchange.Error);
            Assert.Equal(new[] { path == "dns" ? "lookup" : "connect" }, calls);
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public async Task ASynchronousFailureOfTheFirstOperationAfterBegin_IsOneSendFailedExchange(string path)
        {
            RawSenderSetup setup = path == "dns"
                ? new RawSenderSetup(Resolver: (_, _) => throw new IOException("lookup"))
                : new RawSenderSetup(
                    ConnectTo: path == "connect-to" ? ConnectTo() : null,
                    Connector: (_, _) => throw new IOException("connect"));

            ReplayExchange exchange = await new RawSender(setup).SendAsync(PathImage(path), Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode, RawWriteState.None), (exchange.Outcome, exchange.Code, exchange.RequestWrite));
            Assert.Null(exchange.Error);
        }

        [Fact]
        public async Task SetupFailures_AreRecordedWithTheirCodes()
        {
            ReplayExchange lookup = await new RawSender(new RawSenderSetup(
                    Resolver: async (_, _) =>
                    {
                        await Task.Yield();
                        throw new IOException("no such host");
                    }))
                .SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal((ReplaySender.SendFailedCode, RawWriteState.None), (lookup.Code, lookup.RequestWrite));

            var connects = new List<IPEndPoint>();
            ReplayExchange refused = await new RawSender(new RawSenderSetup(
                    Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3") }),
                    Connector: async (endpoint, _) =>
                    {
                        connects.Add(endpoint);
                        await Task.Yield();
                        throw new IOException("refused");
                    }))
                .SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal((ReplaySender.SendFailedCode, RawWriteState.None), (refused.Code, refused.RequestWrite));
            Assert.Equal(new[] { "127.0.0.2:80", "127.0.0.3:80" }, connects.Select(endpoint => endpoint.ToString()));

            ReplayExchange denied = await new RawSender(new RawSenderSetup(
                    Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Any, IPAddress.Parse("224.0.0.1") }),
                    Connector: (_, _) => throw new InvalidOperationException("never")))
                .SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal((RawCodes.TargetDenied, RawWriteState.None), (denied.Code, denied.RequestWrite));
        }

        [Fact]
        public async Task OneLookup_DeniedAddressesDropped_AndFailoverOnlyAcrossFailedConnects()
        {
            var connection = new SeamConnection();
            connection.Respond("HTTP/1.1 200 OK\r\n\r\npartial");
            connection.FailReads(new IOException("reset"));
            int lookups = 0;
            var connects = new List<string>();
            var setup = new RawSenderSetup(
                Resolver: (_, _) =>
                {
                    lookups++;
                    return Task.FromResult(new[] { IPAddress.Any, IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3") });
                },
                Connector: async (endpoint, _) =>
                {
                    connects.Add(endpoint.Address.ToString());
                    await Task.Yield();
                    return endpoint.Address.Equals(IPAddress.Parse("127.0.0.2"))
                        ? throw new IOException("refused")
                        : connection;
                });

            ReplayExchange exchange = await new RawSender(setup).SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal(1, lookups);
            Assert.Equal(new[] { "127.0.0.2", "127.0.0.3" }, connects);
            Assert.Equal(ReplayOutcome.Failed, exchange.Outcome);
        }

        [Fact]
        public async Task TheWriteState_IsWhatEachPhaseImplies_WhenTheCallerCancels()
        {
            Assert.Equal(RawWriteState.None, await CancelledWriteStateAsync("lookup"));
            Assert.Equal(RawWriteState.None, await CancelledWriteStateAsync("connect"));
            Assert.Equal(RawWriteState.Uncertain, await CancelledWriteStateAsync("first-write"));
            Assert.Equal(RawWriteState.Uncertain, await CancelledWriteStateAsync("later-write"));
            Assert.Equal(RawWriteState.Partial, await CancelledWriteStateAsync("between-writes"));
            Assert.Equal(RawWriteState.Complete, await CancelledWriteStateAsync("read"));
        }

        [Theory]
        [InlineData("", ReplaySender.SendFailedCode)]
        [InlineData("HTTP/1.1 200 OK\r\nX-A", RawCodes.ResponseInvalid)]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhe", RawCodes.ResponseIncomplete)]
        [InlineData("HTTP/1.1 200 OK\r\n\r\nhe", RawCodes.ResponseIncomplete)]
        public async Task AFailedRead_IsClassifiedWhereItHappens_AndRecordsNoResponse(string received, string code)
        {
            var connection = new SeamConnection();
            if (received.Length > 0)
            {
                connection.Respond(received);
            }

            connection.FailReads(new IOException("reset"));

            ReplayExchange exchange = await new RawSender(Seams(connection)).SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, code), (exchange.Outcome, exchange.Code));
            Assert.Null(exchange.Response);
            Assert.Null(exchange.Error);
        }

        [Fact]
        public async Task AnEndOfStreamBeforeTheStatusLine_IsSendFailed()
        {
            var connection = new SeamConnection();
            connection.End();

            ReplayExchange exchange = await new RawSender(Seams(connection)).SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode, RawWriteState.Complete), (exchange.Outcome, exchange.Code, exchange.RequestWrite));
        }

        [Fact]
        public async Task TheDeadline_InEachPhase_IsTimedOut_NeverWhatTheClosingThrows()
        {
            foreach (string phase in new[] { "lookup", "connect", "write", "head", "framed-body" })
            {
                var clock = new ManualRawClock();
                var connection = new SeamConnection();
                var stalled = new TaskCompletionSource<Stream>();
                var setup = new RawSenderSetup(
                    Resolver: phase == "lookup"
                        ? (_, token) => Task.Delay(System.Threading.Timeout.Infinite, token).ContinueWith(_ => new[] { IPAddress.Loopback }, TaskScheduler.Default)
                        : (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
                    Connector: phase == "connect"
                        ? (_, token) => stalled.Task.WaitAsync(token)
                        : (_, _) => Task.FromResult<Stream>(connection),
                    Hooks: new RawSenderHooks(Clock: clock));
                if (phase == "write")
                {
                    connection.WriteGate = _ => new TaskCompletionSource().Task;
                }
                else if (phase == "head")
                {
                    connection.Respond("HTTP/1.1 200 OK\r\nX-A: 1\r\n");
                }
                else if (phase == "framed-body")
                {
                    connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nhel");
                }

                Task<ReplayExchange> sending = new RawSender(setup).SendAsync(Image(), Options(), CancellationToken.None);
                await WaitForAsync(() => clock.Scheduled.Count == 1 && (phase is "lookup" or "connect" || connection.WritesStarted > 0));
                if (phase is "head" or "framed-body")
                {
                    await connection.ReadWaiting.Task.WaitAsync(Wait);
                }

                clock.Fire(clock.Scheduled[0]);
                ReplayExchange exchange = await sending.WaitAsync(Wait);

                Assert.True(exchange.Outcome == ReplayOutcome.TimedOut, phase);
                Assert.Equal(ReplaySender.TimeoutCode, exchange.Code);
            }
        }

        [Fact]
        public async Task FaultsThatTheCancellationCauses_NeverReplaceTheCancel()
        {
            foreach (string phase in new[] { "connect", "read" })
            {
                var connection = new SeamConnection();
                using var source = new CancellationTokenSource();
                var setup = new RawSenderSetup(
                    Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
                    Connector: phase == "connect"
                        ? async (_, token) =>
                        {
                            await Task.Delay(System.Threading.Timeout.Infinite, token);
                            return connection;
                        }
                        : (_, _) => Task.FromResult<Stream>(connection));

                Task<ReplayExchange> sending = new RawSender(setup).SendAsync(Image(), Options(), source.Token);
                if (phase == "read")
                {
                    await connection.ReadWaiting.Task.WaitAsync(Wait);
                }
                else
                {
                    await Task.Delay(50);
                }

                source.Cancel();
                ReplayExchange exchange = await sending.WaitAsync(Wait);

                Assert.Equal((ReplayOutcome.Failed, RawCodes.SendCancelled), (exchange.Outcome, exchange.Code));
            }
        }

        // The commit disposes what it owns; a disposal that throws is a consequence of the closing, never its cause (L8-A1).
        [Fact]
        public async Task ADisposalThatThrows_NeverReplacesTheCancelOrTheDeadline()
        {
            var cancelled = new SeamConnection { DisposeFault = new IOException("close failed") };
            using var source = new CancellationTokenSource();
            Task<ReplayExchange> sending = new RawSender(Seams(cancelled)).SendAsync(Image(), Options(), source.Token);
            await cancelled.ReadWaiting.Task.WaitAsync(Wait);
            source.Cancel();
            ReplayExchange exchange = await sending.WaitAsync(Wait);
            Assert.Equal((ReplayOutcome.Failed, RawCodes.SendCancelled), (exchange.Outcome, exchange.Code));
            Assert.True(cancelled.IsDisposed);

            var clock = new ManualRawClock();
            var timedOut = new SeamConnection { DisposeFault = new IOException("close failed") };
            Task<ReplayExchange> timing = new RawSender(Seams(timedOut, clock)).SendAsync(Image(), Options(), CancellationToken.None);
            await timedOut.ReadWaiting.Task.WaitAsync(Wait);
            clock.Fire(Assert.Single(clock.Scheduled));
            exchange = await timing.WaitAsync(Wait);
            Assert.Equal((ReplayOutcome.TimedOut, ReplaySender.TimeoutCode), (exchange.Outcome, exchange.Code));
            Assert.True(timedOut.IsDisposed);
        }

        [Fact]
        public async Task AWriterFailure_WaitsTheGrace_ForAResponseThatIsArriving()
        {
            // A response within the grace: Completed.
            (ReplayExchange within, _) = await WriterFailureAsync(after: (connection, clock, _) => connection.Respond(Ok));
            Assert.Equal((ReplayOutcome.Completed, RawWriteState.Uncertain), (within.Outcome, within.RequestWrite));

            // No response: the grace ends, replay.send.failed.
            (ReplayExchange expired, _) = await WriterFailureAsync(after: (_, clock, _) => clock.Fire(clock.Scheduled[^1]));
            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (expired.Outcome, expired.Code));

            // A cancellation, then a response: cancelled.
            (ReplayExchange cancelled, _) = await WriterFailureAsync(after: (connection, _, source) =>
            {
                source.Cancel();
                connection.Respond(Ok);
            });
            Assert.Equal(RawCodes.SendCancelled, cancelled.Code);

            // The deadline, then a response: TimedOut.
            (ReplayExchange timedOut, _) = await WriterFailureAsync(after: (connection, clock, _) =>
            {
                clock.Fire(clock.Scheduled[0]);
                connection.Respond(Ok);
            });
            Assert.Equal(ReplayOutcome.TimedOut, timedOut.Outcome);
        }

        [Fact]
        public async Task AFirstWriteThatFaults_IsUncertain_WithNothingCounted()
        {
            (ReplayExchange exchange, SeamConnection connection) =
                await WriterFailureAsync(after: (_, clock, _) => clock.Fire(clock.Scheduled[^1]));

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode), (exchange.Outcome, exchange.Code));
            Assert.Equal((RawWriteState.Uncertain, (long?)0), (exchange.RequestWrite, exchange.RequestBytesWritten));
            Assert.Equal(1, connection.WritesStarted);
        }

        [Fact]
        public async Task TooLarge_WhileTheWriterIsStillWriting_IsTooLarge_WithAPartialOrUncertainWrite()
        {
            var connection = new SeamConnection { WriteGate = index => index == 0 ? Task.CompletedTask : new TaskCompletionSource().Task };
            connection.AfterWrite = index =>
            {
                if (index == 0)
                {
                    connection.Respond("HTTP/1.1 413 Payload Too Large\r\nContent-Length: 999999999\r\n\r\n");
                }
            };

            ReplayExchange exchange = await new RawSender(Seams(connection))
                .SendAsync(LargeImage(), Options() with { MaxResponseBytes = 10 }, CancellationToken.None)
                .WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.TooLarge, 413), (exchange.Outcome, exchange.StatusCode));
            Assert.Contains(exchange.RequestWrite, new[] { RawWriteState.Partial, RawWriteState.Uncertain });
            Assert.True(exchange.RequestBytesWritten < LargeImage().Length);
        }

        [Fact]
        public async Task AWriterStuckInAWrite_IsEndedByTheDisposal_AfterTheResponse()
        {
            var connection = new SeamConnection { WriteGate = index => index == 1 ? new TaskCompletionSource().Task : Task.CompletedTask };
            connection.AfterWrite = index =>
            {
                if (index == 0)
                {
                    connection.Respond(Ok);
                }
            };

            ReplayExchange exchange = await new RawSender(Seams(connection))
                .SendAsync(LargeImage(), Options(), CancellationToken.None)
                .WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Completed, RawWriteState.Uncertain), (exchange.Outcome, exchange.RequestWrite));
            Assert.Equal(RawRequestImage.WriteBytes, exchange.RequestBytesWritten);
            Assert.True(connection.IsDisposed);
        }

        [Fact]
        public async Task AWriteThatCompletesAfterTheCommit_Counts()
        {
            var release = new TaskCompletionSource();
            var connection = new SeamConnection { WritesEnd = false, WriteGate = _ => release.Task };
            connection.Respond(Ok);
            RawRequestImage image = Image();

            Task<ReplayExchange> sending = new RawSender(Seams(connection)).SendAsync(image, Options(), CancellationToken.None);
            await WaitForAsync(() => connection.IsDisposed);
            release.SetResult();
            ReplayExchange exchange = await sending.WaitAsync(Wait);

            Assert.Equal(ReplayOutcome.Completed, exchange.Outcome);
            Assert.Equal(((long?)image.Length, RawWriteState.Complete), (exchange.RequestBytesWritten, exchange.RequestWrite));
        }

        [Fact]
        public async Task OperationLeases_ACancelAtEachStep_LeavesNothingUnowned()
        {
            foreach (RawOperation operation in new[] { RawOperation.Connect, RawOperation.Writer, RawOperation.Reader })
            {
                foreach (RawSendPoint step in new[] { RawSendPoint.BeforeReserve, RawSendPoint.AfterReserve, RawSendPoint.AfterInvoke })
                {
                    var connection = new SeamConnection();
                    connection.Respond(Ok);
                    var invoked = new List<RawOperation>();
                    using var source = new CancellationTokenSource();
                    var setup = new RawSenderSetup(
                        Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
                        Connector: (_, token) =>
                        {
                            invoked.Add(RawOperation.Connect);
                            return Task.FromResult<Stream>(connection);
                        },
                        Hooks: new RawSenderHooks(At: (point, at) =>
                        {
                            if (at == operation && point == step)
                            {
                                source.Cancel();
                            }
                        }));

                    ReplayExchange exchange = await new RawSender(setup).SendAsync(Image(), Options(), source.Token).WaitAsync(Wait);

                    string label = $"{operation} {step}";
                    Assert.True(exchange.Code == RawCodes.SendCancelled, label);
                    if (operation == RawOperation.Connect)
                    {
                        Assert.True(step == RawSendPoint.BeforeReserve ? invoked.Count == 0 : invoked.Count == 1, label);
                    }

                    Assert.True(operation != RawOperation.Writer || step != RawSendPoint.BeforeReserve || connection.WritesStarted == 0, label);
                }
            }
        }

        [Fact]
        public async Task OneConnectionPerSend_AndNoRetry()
        {
            int connects = 0;
            var setup = new RawSenderSetup(
                Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") }),
                Connector: (_, _) =>
                {
                    connects++;
                    var connection = new SeamConnection();
                    connection.FailReads(new IOException("reset"));
                    return Task.FromResult<Stream>(connection);
                });
            var sender = new RawSender(setup);

            await sender.SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);
            await sender.SendAsync(Image(), Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal(2, connects);
        }

        [Fact]
        public async Task AWholeRawSend_OverPlainHttp_ToALoopbackServer()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RawRequestImage image = Image(
                $"PUT /x HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: 3\r\n\r\nabc", $"http://127.0.0.1:{port}");
            Task<byte[]> served = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
                using System.Net.Sockets.NetworkStream stream = client.GetStream();
                var received = new byte[image.Length];
                await stream.ReadExactlyAsync(received);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nok\r\n0\r\n\r\n"));
                return received;
            });

            ReplayExchange exchange = await new RawSender().SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);

            Assert.Equal(image.CopyHead().Concat(image.CopyWireBody()), await served.WaitAsync(Wait));
            Assert.Equal((ReplayOutcome.Completed, 201, "chunked"), (exchange.Outcome, exchange.StatusCode, exchange.ResponseFraming));
            Assert.Equal("ok", Encoding.ASCII.GetString(exchange.Response.Body));
            Assert.Equal(RawWriteState.Complete, exchange.RequestWrite);
        }

        [Fact]
        public async Task AnEarly413DuringAnUpload_EndsWithoutDeadlock()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            const int size = 8 * 1024 * 1024;
            RawRequestImage image = RawRequest.FromBytes(
                Target($"http://127.0.0.1:{port}"),
                Encoding.ASCII.GetBytes($"POST /up HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {size}\r\n\r\n").Concat(new byte[size]).ToArray(),
                false,
                CancellationToken.None).Image;
            Task served = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync();
                using System.Net.Sockets.NetworkStream stream = client.GetStream();
                await stream.ReadExactlyAsync(new byte[1024]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 413 Payload Too Large\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                await Task.Delay(200);
            });

            ReplayExchange exchange = await new RawSender().SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);
            await served.WaitAsync(Wait);

            // Loopback buffers may or may not take the whole upload before the response, so its write state varies.
            Assert.Equal((ReplayOutcome.Completed, 413), (exchange.Outcome, exchange.StatusCode));
        }

        // Runs a send whose first write call faults, then the given action once the grace has begun.
        private static async Task<(ReplayExchange Exchange, SeamConnection Connection)> WriterFailureAsync(
            Action<SeamConnection, ManualRawClock, CancellationTokenSource> after)
        {
            var clock = new ManualRawClock();
            var connection = new SeamConnection { WriteFault = _ => new IOException("reset") };
            using var source = new CancellationTokenSource();
            Task<ReplayExchange> sending = new RawSender(Seams(connection, clock)).SendAsync(Image(), Options(), source.Token);
            await WaitForAsync(() => clock.Scheduled.Count == 2);
            after(connection, clock, source);
            return (await sending.WaitAsync(Wait), connection);
        }

        // Cancels the caller's token in one phase of a send, and gives the write state of its one exchange.
        private static async Task<string> CancelledWriteStateAsync(string phase)
        {
            var connection = new SeamConnection();
            using var source = new CancellationTokenSource();
            var lookupStarted = new TaskCompletionSource();
            var connectStarted = new TaskCompletionSource();
            var setup = new RawSenderSetup(
                Resolver: async (_, token) =>
                {
                    lookupStarted.TrySetResult();
                    if (phase == "lookup")
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, token);
                    }

                    return new[] { IPAddress.Loopback };
                },
                Connector: async (_, token) =>
                {
                    connectStarted.TrySetResult();
                    if (phase == "connect")
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, token);
                    }

                    return connection;
                });
            var writeStarted = new TaskCompletionSource();
            if (phase == "first-write")
            {
                connection.WriteGate = _ =>
                {
                    writeStarted.TrySetResult();
                    return new TaskCompletionSource().Task;
                };
            }
            else if (phase == "later-write")
            {
                connection.WriteGate = index =>
                {
                    if (index == 0)
                    {
                        return Task.CompletedTask;
                    }

                    writeStarted.TrySetResult();
                    return new TaskCompletionSource().Task;
                };
            }
            else if (phase == "between-writes")
            {
                connection.AfterWrite = index =>
                {
                    if (index == 0)
                    {
                        source.Cancel();
                    }
                };
            }

            Task<ReplayExchange> sending = new RawSender(setup).SendAsync(LargeImage(), Options(), source.Token);
            switch (phase)
            {
                case "lookup":
                    await lookupStarted.Task.WaitAsync(Wait);
                    source.Cancel();
                    break;
                case "connect":
                    await connectStarted.Task.WaitAsync(Wait);
                    source.Cancel();
                    break;
                case "first-write":
                case "later-write":
                    await writeStarted.Task.WaitAsync(Wait);
                    source.Cancel();
                    break;
                case "read":
                    await connection.ReadWaiting.Task.WaitAsync(Wait);
                    await WaitForAsync(() => connection.Written.Length == LargeImage().Length);
                    source.Cancel();
                    break;
            }

            ReplayExchange exchange = await sending.WaitAsync(Wait);
            Assert.Equal(RawCodes.SendCancelled, exchange.Code);
            return exchange.RequestWrite;
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            for (int attempt = 0; !condition(); attempt++)
            {
                Assert.True(attempt < 3000, "timed out waiting");
                await Task.Delay(10);
            }
        }

        private static RawSenderSetup Seams(SeamConnection connection, ManualRawClock clock = null) => new(
            Resolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
            Connector: (_, _) => Task.FromResult<Stream>(connection),
            Hooks: clock == null ? null : new RawSenderHooks(Clock: clock));

        private static RawSenderSetup PathSetup(string path, SeamConnection connection, List<string> calls) => new(
            ConnectTo: path == "connect-to" ? ConnectTo() : null,
            Resolver: (_, _) =>
            {
                calls.Add("lookup");
                return Task.FromResult(new[] { IPAddress.Loopback });
            },
            Connector: (_, _) =>
            {
                calls.Add("connect");
                return Task.FromResult<Stream>(connection);
            });

        private static RawRequestImage PathImage(string path) =>
            path == "literal" ? Image("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", "http://127.0.0.1") : Image();

        private static ReplayConnectTo ConnectTo()
        {
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:8080", out ReplayConnectTo connectTo));
            return connectTo;
        }

        private static RawSendOptions Options() => new(Timeout, 1024 * 1024);

        private static ReplayRawTarget Target(string text)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));
            return target;
        }

        private static RawRequestImage Image(
            string request = "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n", string target = "http://example.test")
        {
            RawImageResult result = RawRequest.FromBytes(Target(target), Encoding.ASCII.GetBytes(request), false, CancellationToken.None);
            Assert.True(result.Code == null, result.Code);
            return result.Image;
        }

        // A request of three write calls: two of 16 KiB and the rest.
        private static RawRequestImage LargeImage() =>
            Image("POST / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 40000\r\n\r\n" + new string('x', 40_000));
    }
}
