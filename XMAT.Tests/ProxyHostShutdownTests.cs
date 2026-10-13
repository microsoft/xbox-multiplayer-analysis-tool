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

        private sealed class TestHost : IHost
        {
            public IServiceProvider Services => null;
            public Exception StopException { get; set; }
            public Exception DisposeException { get; set; }
            public bool StopCalled { get; private set; }
            public bool DisposeCalled { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCalled = true;
                return StopException == null
                    ? Task.CompletedTask
                    : Task.FromException(StopException);
            }

            public void Dispose()
            {
                DisposeCalled = true;
                if (DisposeException != null)
                {
                    throw DisposeException;
                }
            }
        }
    }
}
