// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Windows.Threading;

namespace XMAT.Tests
{
    public class BatchedDispatcherQueueTests
    {
        [Fact]
        public void Drain_ProcessesBoundedBatchesAndPreservesOrder()
        {
            Exception dispatcherException = null;
            using var completed = new ManualResetEventSlim();
            var executed = new List<int>();
            var executedAtYield = -1;

            var dispatcherThread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherTimer timeoutTimer = null;

                try
                {
                    var queue = new BatchedDispatcherQueue(dispatcher, 2);
                    queue.StartAccepting();
                    for (var index = 0; index < 5; index++)
                    {
                        var capturedIndex = index;
                        Assert.True(queue.TryEnqueue(
                            () => executed.Add(capturedIndex)));
                    }

                    var drained = queue.CompleteAndDrainAsync();
                    Assert.Equal(5, queue.PendingCount);
                    Assert.False(queue.TryEnqueue(() => { }));

                    dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => executedAtYield = executed.Count));
                    _ = drained.ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            dispatcherException = task.Exception;
                        }

                        completed.Set();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    });

                    timeoutTimer = new DispatcherTimer(DispatcherPriority.Send)
                    {
                        Interval = TimeSpan.FromSeconds(5)
                    };
                    timeoutTimer.Tick += (sender, args) =>
                    {
                        dispatcherException ??= new TimeoutException(
                            "The dispatcher queue did not drain.");
                        completed.Set();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    };
                    timeoutTimer.Start();

                    Dispatcher.Run();

                    Assert.Equal(0, queue.PendingCount);
                }
                catch (Exception ex)
                {
                    dispatcherException = ex;
                    completed.Set();
                }
                finally
                {
                    timeoutTimer?.Stop();
                }
            });
            dispatcherThread.SetApartmentState(ApartmentState.STA);
            dispatcherThread.Start();

            Assert.True(
                completed.Wait(TimeSpan.FromSeconds(10)),
                "The dispatcher queue test did not complete.");
            Assert.True(
                dispatcherThread.Join(TimeSpan.FromSeconds(10)),
                "The dispatcher queue thread did not stop.");
            Assert.Null(dispatcherException);
            Assert.Equal(2, executedAtYield);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, executed);
        }

        [Fact]
        public void Drain_FaultsWhenDispatcherIsShuttingDown()
        {
            Exception dispatcherException = null;
            using var completed = new ManualResetEventSlim();

            var dispatcherThread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var queue = new BatchedDispatcherQueue(dispatcher, 2);
                    queue.StartAccepting();
                    dispatcher.InvokeShutdown();

                    Assert.True(queue.TryEnqueue(() => { }));
                    var drainTask = queue.CompleteAndDrainAsync();

                    Assert.ThrowsAny<Exception>(
                        () => drainTask.GetAwaiter().GetResult());
                }
                catch (Exception ex)
                {
                    dispatcherException = ex;
                }
                finally
                {
                    completed.Set();
                }
            });
            dispatcherThread.SetApartmentState(ApartmentState.STA);
            dispatcherThread.Start();

            Assert.True(
                completed.Wait(TimeSpan.FromSeconds(10)),
                "The dispatcher shutdown test did not complete.");
            Assert.True(
                dispatcherThread.Join(TimeSpan.FromSeconds(10)),
                "The dispatcher shutdown thread did not stop.");
            Assert.Null(dispatcherException);
        }
    }
}
