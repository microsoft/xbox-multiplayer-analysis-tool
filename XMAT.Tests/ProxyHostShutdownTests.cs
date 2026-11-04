// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class ProxyHostShutdownTests
    {
        [Fact]
        public async Task StopAndDisposeAsync_StopsAndDisposesHost()
        {
            var host = new TestHost();

            await ProxyHostShutdown.StopAndDisposeAsync(
                host,
                TimeSpan.FromSeconds(1));

            Assert.True(host.StopCalled);
            Assert.True(host.DisposeCalled);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task StopAndDisposeAsync_DoesNotRunBlockingTeardownOnCallingThread(
            bool blockDuringStop)
        {
            using var teardownStarted = new ManualResetEventSlim();
            using var releaseTeardown = new ManualResetEventSlim();
            var callingThreadId = Environment.CurrentManagedThreadId;
            var teardownThreadId = callingThreadId;
            Action blockingTeardown = () =>
            {
                teardownThreadId = Environment.CurrentManagedThreadId;
                teardownStarted.Set();
                releaseTeardown.Wait(TimeSpan.FromSeconds(5));
            };
            var host = new TestHost
            {
                StopAction = blockDuringStop ? blockingTeardown : null,
                DisposeAction = blockDuringStop ? null : blockingTeardown
            };

            var stopTask = ProxyHostShutdown.StopAndDisposeAsync(
                host,
                TimeSpan.FromSeconds(1));
            try
            {
                Assert.True(teardownStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(stopTask.IsCompleted);
                Assert.NotEqual(callingThreadId, teardownThreadId);
            }
            finally
            {
                releaseTeardown.Set();
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(host.StopCalled);
            Assert.True(host.DisposeCalled);
        }

        [Fact]
        public async Task StopAndDisposeAsync_PrefersAndAwaitsAsynchronousDisposal()
        {
            var disposalStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDisposal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new AsyncTestHost
            {
                DisposeAsyncAction = () =>
                {
                    disposalStarted.SetResult();
                    return new ValueTask(releaseDisposal.Task);
                }
            };

            var stopTask = ProxyHostShutdown.StopAndDisposeAsync(
                host,
                TimeSpan.FromSeconds(1));
            try
            {
                await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(stopTask.IsCompleted);
                Assert.False(host.DisposeCalled);
            }
            finally
            {
                releaseDisposal.SetResult();
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(host.StopCalled);
            Assert.True(host.DisposeAsyncCalled);
            Assert.False(host.DisposeCalled);
        }

        [Fact]
        public async Task StopAndDisposeAsync_DisposesHostWhenStopFails()
        {
            var host = new TestHost
            {
                StopException = new InvalidOperationException("Stop failed")
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProxyHostShutdown.StopAndDisposeAsync(
                    host,
                    TimeSpan.FromSeconds(1)));

            Assert.Equal("Stop failed", exception.Message);
            Assert.True(host.StopCalled);
            Assert.True(host.DisposeCalled);
        }

        [Fact]
        public async Task StopAndDisposeAsync_ReportsDisposalFailure()
        {
            var host = new TestHost
            {
                DisposeException = new InvalidOperationException("Dispose failed")
            };

            var exception = await Assert.ThrowsAsync<ProxyHostDisposalException>(
                () => ProxyHostShutdown.StopAndDisposeAsync(
                    host,
                    TimeSpan.FromSeconds(1)));

            Assert.Contains("could not be disposed", exception.Message);
            Assert.True(host.StopCalled);
            Assert.True(host.DisposeCalled);
        }

        [Fact]
        public async Task StopAndDisposeAsync_PreservesOperationalStopFailures()
        {
            var exceptions = new Exception[]
            {
                new TimeoutException("Stop timed out"),
                new OperationCanceledException("Stop was canceled"),
                new IOException("Transport failed"),
                new AggregateException(new InvalidOperationException("Service failed"))
            };

            foreach (var expectedException in exceptions)
            {
                var host = new TestHost { StopException = expectedException };

                var exception = await Assert.ThrowsAnyAsync<Exception>(
                    () => ProxyHostShutdown.StopAndDisposeAsync(
                        host,
                        TimeSpan.FromSeconds(1)));

                Assert.Same(expectedException, exception);
                Assert.True(host.StopCalled);
                Assert.True(host.DisposeCalled);
            }
        }

        [Fact]
        public async Task StopAndDisposeAsync_DoesNotCatchFatalStopFailures()
        {
            var exceptions = new Exception[]
            {
                new OutOfMemoryException("Test failure"),
                new AccessViolationException("Test failure"),
                new InvalidProgramException("Test failure"),
                new AggregateException(new OutOfMemoryException("Test failure")),
                new InvalidOperationException(
                    "Wrapped failure",
                    new OutOfMemoryException("Test failure"))
            };

            foreach (var expectedException in exceptions)
            {
                var host = new TestHost { StopException = expectedException };

                var exception = await Assert.ThrowsAnyAsync<Exception>(
                    () => ProxyHostShutdown.StopAndDisposeAsync(
                        host,
                        TimeSpan.FromSeconds(1)));

                Assert.Same(expectedException, exception);
                Assert.True(host.StopCalled);
                Assert.False(host.DisposeCalled);
            }
        }

        [Fact]
        public async Task StopAndDisposeAsync_DoesNotWrapFatalDisposalFailure()
        {
            var expectedException = new OutOfMemoryException("Test failure");
            var host = new TestHost { DisposeException = expectedException };

            var exception = await Assert.ThrowsAsync<OutOfMemoryException>(
                () => ProxyHostShutdown.StopAndDisposeAsync(
                    host,
                    TimeSpan.FromSeconds(1)));

            Assert.Same(expectedException, exception);
            Assert.True(host.StopCalled);
            Assert.True(host.DisposeCalled);
        }

        [Fact]
        public async Task StopAndDisposeAsync_PreservesBothOperationalFailures()
        {
            var stopException = new TimeoutException("Stop timed out");
            var disposeException = new InvalidOperationException("Dispose failed");
            var host = new TestHost
            {
                StopException = stopException,
                DisposeException = disposeException
            };

            var exception = await Assert.ThrowsAsync<ProxyHostDisposalException>(
                () => ProxyHostShutdown.StopAndDisposeAsync(
                    host,
                    TimeSpan.FromSeconds(1)));

            var aggregateException = Assert.IsType<AggregateException>(
                exception.InnerException);
            Assert.Equal(2, aggregateException.InnerExceptions.Count);
            Assert.Same(stopException, aggregateException.InnerExceptions[0]);
            Assert.Same(disposeException, aggregateException.InnerExceptions[1]);
        }

        private class TestHost : IHost
        {
            public IServiceProvider Services => null;
            public Exception StopException { get; set; }
            public Exception DisposeException { get; set; }
            public Action StopAction { get; set; }
            public Action DisposeAction { get; set; }
            public bool StopCalled { get; private set; }
            public bool DisposeCalled { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCalled = true;
                StopAction?.Invoke();
                return StopException == null
                    ? Task.CompletedTask
                    : Task.FromException(StopException);
            }

            public void Dispose()
            {
                DisposeCalled = true;
                DisposeAction?.Invoke();
                if (DisposeException != null)
                {
                    throw DisposeException;
                }
            }
        }

        private sealed class AsyncTestHost : TestHost, IAsyncDisposable
        {
            public Func<ValueTask> DisposeAsyncAction { get; set; }
            public bool DisposeAsyncCalled { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCalled = true;
                return DisposeAsyncAction?.Invoke() ?? ValueTask.CompletedTask;
            }
        }
    }
}
