// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XMAT.Tests
{
    public class LoggerLifetimeTests
    {
        [Fact]
        public async Task CloseLog_WaitsForTheConsumerBeforeFlushingOrDisposingTheStream()
        {
            using var stream = new BlockingWriteStream();
            var logger = new Logger();
            logger.InitLog(stream, LogLevel.DEBUG);
            stream.BlockWrites = true;
            logger.Log(1, LogLevel.INFO, "retained line");
            Assert.True(stream.WriteEntered.Wait(TimeSpan.FromSeconds(5)));
            Task closing = Task.Factory.StartNew(
                logger.CloseLog, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                // Outlast the old 500 ms shutdown deadline while the consumer is held inside Write.
                await Task.Delay(750);
                Assert.False(closing.IsCompleted);
                Assert.False(stream.ConcurrentWrites);
                Assert.False(stream.DisposedWhileWriting);
            }
            finally
            {
                stream.AllowWrite.Set();
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Contains("retained line", Encoding.UTF8.GetString(stream.ToArray()));
            logger.CloseLog();
        }

        [Fact]
        public async Task CloseLog_ConcurrentClosersDrainEveryAcceptedLineAndDisposeOnce()
        {
            using var stream = new TrackingStream();
            var logger = new Logger();
            logger.InitLog(stream, LogLevel.DEBUG);
            for (int i = 0; i < 200; ++i)
                logger.Log(i, LogLevel.INFO, $"line-{i:D3}");

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(logger.CloseLog)));
            logger.Log(999, LogLevel.INFO, "after close");
            logger.CloseLog();

            string text = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Equal(200, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.DoesNotContain("after close", text);
            Assert.Equal(1, stream.DisposeCalls);
        }

        [Fact]
        public void InitLog_CanReopenAfterCloseWithoutReusingThePreviousQueue()
        {
            using var first = new MemoryStream();
            using var second = new MemoryStream();
            var logger = new Logger();
            logger.InitLog(first, LogLevel.DEBUG);
            logger.Log(1, LogLevel.INFO, "first lifetime");
            logger.CloseLog();
            logger.InitLog(second, LogLevel.DEBUG);
            logger.Log(2, LogLevel.INFO, "second lifetime");
            logger.CloseLog();

            Assert.Contains("first lifetime", Encoding.UTF8.GetString(first.ToArray()));
            Assert.DoesNotContain("second lifetime", Encoding.UTF8.GetString(first.ToArray()));
            Assert.Contains("second lifetime", Encoding.UTF8.GetString(second.ToArray()));
        }

        [Fact]
        public void CloseLog_WithoutInitializationIsSafe()
        {
            using var logger = new Logger();
            logger.CloseLog();
        }

        [Fact]
        public void CloseLog_PropagatesConsumerWriteFailures()
        {
            using var stream = new FailingWriteStream();
            var logger = new Logger();
            logger.InitLog(stream, LogLevel.DEBUG);
            stream.FailWrites = true;
            logger.Log(1, LogLevel.INFO, "cannot write");
            Assert.Throws<IOException>(logger.CloseLog);
        }

        private sealed class BlockingWriteStream : MemoryStream
        {
            internal readonly ManualResetEventSlim WriteEntered = new();
            internal readonly ManualResetEventSlim AllowWrite = new();
            internal volatile bool ConcurrentWrites;
            internal volatile bool DisposedWhileWriting;
            internal bool BlockWrites;
            private int _writers;

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (!BlockWrites)
                {
                    byte[] initialBytes = buffer.ToArray();
                    base.Write(initialBytes, 0, initialBytes.Length);
                    return;
                }
                if (Interlocked.Increment(ref _writers) > 1)
                    ConcurrentWrites = true;
                WriteEntered.Set();
                try
                {
                    if (!AllowWrite.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The test did not release the blocked log write.");
                    byte[] bytes = buffer.ToArray();
                    base.Write(bytes, 0, bytes.Length);
                }
                finally
                {
                    Interlocked.Decrement(ref _writers);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (Volatile.Read(ref _writers) != 0)
                    DisposedWhileWriting = true;
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            internal int DisposeCalls;

            protected override void Dispose(bool disposing)
            {
                Interlocked.Increment(ref DisposeCalls);
                base.Dispose(disposing);
            }
        }

        private sealed class FailingWriteStream : MemoryStream
        {
            internal bool FailWrites;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailWrites)
                    throw new IOException("Injected log write failure.");
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                byte[] bytes = buffer.ToArray();
                Write(bytes, 0, bytes.Length);
            }
        }
    }
}
