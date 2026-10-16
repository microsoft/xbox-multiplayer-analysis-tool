// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public class LoggerTests
    {
        // The stop-deadline callback can log from a timer thread while the log is closing.
        [Fact]
        public async Task Log_ConcurrentWithCloseLog_NeverThrows()
        {
            for (int round = 0; round < 20; round++)
            {
                var logger = new Logger();
                logger.InitLog("xmat-logger-race-tests.log", LogLevel.DEBUG);
                using var stop = new CancellationTokenSource();

                // Dedicated threads: spinning writers on the thread pool starve the delays below
                // (and everything else in the process) on a small runner.
                var writers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        logger.Log(0, LogLevel.INFO, "concurrent line");
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

                await Task.Delay(20);
                logger.CloseLog();
                await Task.Delay(20);
                stop.Cancel();

                await Task.WhenAll(writers);
            }
        }
    }
}
