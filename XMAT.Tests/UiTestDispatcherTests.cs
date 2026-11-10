// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Windows.Threading;
using XMAT.Tests.Proxy;

namespace XMAT.Tests
{
    // UiTestDispatcher, which runs every UI test on the shared application's dispatcher within its budget. Each test
    // here uses a dispatcher of its own, on its own thread, with plain callbacks and a short budget, so it never blocks
    // the shared application, and it releases whatever it blocked before stopping that dispatcher. A callback here
    // either returns at once or waits until the test releases it, so no result depends on how long anything takes.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class UiTestDispatcherTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        // Run's callback outlives its budget: Run fails with UiTestBudgetException while the callback still runs,
        // naming the test and the budget. Until the callback ends, later Run and RunAsync calls fail at once, name it,
        // and never run their callbacks. Once it ends they run again, and it stays on record as the first overrun, also
        // after another one.
        [Fact]
        public async Task ARunPastItsBudget_FailsWhileItRuns_BlocksLaterCallsUntilItEnds_AndStaysTheFirstOverrun()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Short);
            var release = new ManualResetEventSlim();
            var releaseAgain = new ManualResetEventSlim();
            var ended = new ManualResetEventSlim();
            var refused = 0;
            try
            {
                var expired = Assert.Throws<UiTestBudgetException>(() => runner.Run(
                    () =>
                    {
                        release.Wait(Patience);
                        ended.Set();
                    },
                    "Tests.Stuck"));
                Assert.False(ended.IsSet);
                Assert.Contains("Tests.Stuck did not end within the UI test budget of 1 s.", expired.Message);
                Assert.Contains("still running", expired.Message);
                UiTestOverrun overrun = Assert.Single(runner.Blocking);
                Assert.Same(overrun, runner.FirstOverrun);
                Assert.Contains("not ended: Tests.Stuck.", runner.Report());

                var refusal = Assert.Throws<UiTestBudgetException>(
                    () => runner.Run(() => Interlocked.Increment(ref refused), "Tests.Later"));
                Assert.Contains("still running Tests.Stuck,", refusal.Message);
                var asyncRefusal = await Assert.ThrowsAsync<UiTestBudgetException>(() => runner.RunAsync(
                    () =>
                    {
                        Interlocked.Increment(ref refused);
                        return Task.CompletedTask;
                    },
                    "Tests.LaterAsync"));
                Assert.Contains("still running Tests.Stuck,", asyncRefusal.Message);

                release.Set();
                await overrun.Ended.WaitAsync(Patience);
                Assert.True(ended.IsSet);
                Assert.Empty(runner.Blocking);
                var ran = false;
                runner.Run(() => ran = true, "Tests.After");
                Assert.True(ran);
                Assert.Equal(0, Volatile.Read(ref refused));
                Assert.Same(overrun, runner.FirstOverrun);
                Assert.Equal("The first test to exceed the UI test budget of 1 s was Tests.Stuck.", runner.Report());

                Assert.Throws<UiTestBudgetException>(
                    () => runner.Run(() => releaseAgain.Wait(Patience), "Tests.StuckAgain"));
                Assert.Same(overrun, runner.FirstOverrun);
                Assert.Equal(
                    "Exceeded the UI test budget of 1 s and not ended: Tests.StuckAgain. " +
                        "The first test to exceed the UI test budget of 1 s was Tests.Stuck.",
                    runner.Report());
            }
            finally
            {
                release.Set();
                releaseAgain.Set();
            }
        }

        // RunAsync's callback outlives its budget after its first await, when its dispatcher operation has already
        // completed. Later calls stay blocked until its whole body has ended, and the fault it ended with is kept.
        [Fact]
        public async Task ARunAsyncPastItsBudget_BlocksUntilItsWholeBodyEnds_AndKeepsItsLateFault()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Short);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new InvalidOperationException("The test's late failure.");
            var refused = 0;
            try
            {
                var expired = await Assert.ThrowsAsync<UiTestBudgetException>(() => runner.RunAsync(
                    async () =>
                    {
                        await release.Task;
                        throw late;
                    },
                    "Tests.Awaiting").WaitAsync(Patience));
                Assert.Contains("Tests.Awaiting did not end within the UI test budget of 1 s.", expired.Message);
                UiTestOverrun overrun = Assert.Single(runner.Blocking);
                var refusal = Assert.Throws<UiTestBudgetException>(
                    () => runner.Run(() => Interlocked.Increment(ref refused), "Tests.Later"));
                Assert.Contains("still running Tests.Awaiting,", refusal.Message);

                release.SetResult();
                await overrun.Ended.WaitAsync(Patience);
                Assert.Same(late, overrun.LateFault);
                Assert.Empty(runner.Blocking);
                var ran = false;
                await runner.RunAsync(
                    () =>
                    {
                        ran = true;
                        return Task.CompletedTask;
                    },
                    "Tests.After").WaitAsync(Patience);
                Assert.True(ran);
                Assert.Equal(0, Volatile.Read(ref refused));
                Assert.Contains(
                    $"Tests.Awaiting; it later ended with {typeof(InvalidOperationException).FullName}: {late.Message}",
                    runner.Report());
            }
            finally
            {
                release.TrySetResult();
            }
        }

        // Two RunAsync callbacks outlive their budgets together. The first to end clears only its own block: later
        // calls still fail, naming the other, until that one ends too. The first overrun stays on record throughout.
        [Fact]
        public async Task AnOverrunThatEnds_ClearsOnlyItsOwnBlock()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Short);
            var releaseOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Task one = runner.RunAsync(() => releaseOne.Task, "Tests.One");
                Task two = runner.RunAsync(() => releaseTwo.Task, "Tests.Two");
                await Assert.ThrowsAsync<UiTestBudgetException>(() => one.WaitAsync(Patience));
                await Assert.ThrowsAsync<UiTestBudgetException>(() => two.WaitAsync(Patience));
                UiTestOverrun first = runner.FirstOverrun;
                UiTestOverrun[] blocking = runner.Blocking;
                Assert.Equal(2, blocking.Length);
                UiTestOverrun overrunOne = Assert.Single(blocking, overrun => overrun.Test == "Tests.One");
                UiTestOverrun overrunTwo = Assert.Single(blocking, overrun => overrun.Test == "Tests.Two");

                releaseOne.SetResult();
                await overrunOne.Ended.WaitAsync(Patience);
                Assert.Same(overrunTwo, Assert.Single(runner.Blocking));
                var refusal = Assert.Throws<UiTestBudgetException>(() => runner.Run(() => { }, "Tests.Later"));
                Assert.Contains("still running Tests.Two,", refusal.Message);
                Assert.DoesNotContain("Tests.One", refusal.Message);

                releaseTwo.SetResult();
                await overrunTwo.Ended.WaitAsync(Patience);
                Assert.Empty(runner.Blocking);
                var ran = false;
                runner.Run(() => ran = true, "Tests.After");
                Assert.True(ran);
                Assert.Same(first, runner.FirstOverrun);
            }
            finally
            {
                releaseOne.TrySetResult();
                releaseTwo.TrySetResult();
            }
        }

        // A Run callback that has not started by the end of its budget, because the dispatcher is busy, is aborted. It
        // never runs, even once the dispatcher is free, and it blocks nothing.
        [Fact]
        public async Task ARunThatHasNotStartedByTheEndOfItsBudget_IsAborted_AndNeverRuns()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Short);
            var busy = new ManualResetEventSlim();
            var holding = new ManualResetEventSlim();
            var ran = 0;
            try
            {
                DispatcherOperation hold = dispatcher.Dispatcher.InvokeAsync(() =>
                {
                    holding.Set();
                    busy.Wait(Patience);
                });
                Assert.True(holding.Wait(Patience));
                var expired = Assert.Throws<UiTestBudgetException>(
                    () => runner.Run(() => Interlocked.Increment(ref ran), "Tests.Queued"));
                Assert.Contains("Tests.Queued did not start within the UI test budget of 1 s", expired.Message);
                Assert.Contains("aborted", expired.Message);
                Assert.Empty(runner.Blocking);
                Assert.False(runner.FirstOverrun.Started);

                busy.Set();
                await hold.Task.WaitAsync(Patience);
                runner.Run(() => { }, "Tests.After");
                Assert.Equal(0, Volatile.Read(ref ran));
            }
            finally
            {
                busy.Set();
            }
        }

        // A test's own exceptions, its own TimeoutException and an assertion's failure among them, come out of Run and
        // RunAsync as the very exceptions it threw: not wrapped, and not taken for the budget's. They block nothing and
        // leave nothing on record.
        [Fact]
        public async Task ATestsOwnExceptions_ComeOutUnchanged_AndBlockNothing()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Patience);
            var timeout = new TimeoutException("The test's own timeout.");
            Exception failure = Record.Exception(() => Assert.Fail("The test's own assertion."));

            Assert.Same(timeout, Record.Exception(() => runner.Run(() => throw timeout, "Tests.Timeout")));
            Assert.Same(failure, Record.Exception(() => runner.Run(() => throw failure, "Tests.Failure")));
            Assert.Same(timeout, await Record.ExceptionAsync(() => runner.RunAsync(
                async () =>
                {
                    await Task.Yield();
                    throw timeout;
                },
                "Tests.TimeoutAsync")));
            Assert.Same(
                failure, await Record.ExceptionAsync(() => runner.RunAsync(() => throw failure, "Tests.FailureAsync")));
            Assert.Null(runner.FirstOverrun);
            Assert.Empty(runner.Blocking);
            var ran = false;
            runner.Run(() => ran = true, "Tests.After");
            Assert.True(ran);
        }

        // Run on the dispatcher's own thread runs its callback inline, as Dispatcher.Invoke does: queueing it there and
        // waiting would wait for that very thread.
        [Fact]
        public async Task ARunOnTheDispatchersOwnThread_RunsInline()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Short);
            var order = new List<string>();
            await dispatcher.Dispatcher.InvokeAsync(() =>
            {
                runner.Run(() => order.Add("inner"), "Tests.Inline");
                order.Add("after");
            }).Task.WaitAsync(Patience);
            Assert.Equal(new[] { "inner", "after" }, order);
        }

        // Run queues its callback at Send priority, as Dispatcher.Invoke does, so it runs ahead of Normal work that was
        // queued before it while the dispatcher was busy.
        [Fact]
        public async Task ARunIsQueuedAtSendPriority_AheadOfNormalWorkQueuedBeforeIt()
        {
            using var dispatcher = new PrivateDispatcher();
            var runner = new UiTestDispatcher(dispatcher.Dispatcher, Patience);
            var order = new ConcurrentQueue<string>();
            var busy = new ManualResetEventSlim();
            var holding = new ManualResetEventSlim();
            var posted = new ManualResetEventSlim();
            try
            {
                DispatcherOperation hold = dispatcher.Dispatcher.InvokeAsync(() =>
                {
                    holding.Set();
                    busy.Wait(Patience);
                });
                Assert.True(holding.Wait(Patience));
                DispatcherOperation normal = dispatcher.Dispatcher.InvokeAsync(() => order.Enqueue("normal"));
                dispatcher.Dispatcher.Hooks.OperationPosted += (sender, args) => posted.Set();
                Task run = Task.Run(() => runner.Run(() => order.Enqueue("run"), "Tests.Ordered"));
                Assert.True(posted.Wait(Patience));
                busy.Set();
                await run.WaitAsync(Patience);
                await normal.Task.WaitAsync(Patience);
                await hold.Task.WaitAsync(Patience);
                Assert.Equal(new[] { "run", "normal" }, order);
            }
            finally
            {
                busy.Set();
            }
        }

        // A budget failure names the test that called, also through a helper and after the test's own await, with the
        // call that reached the fixture. With no test on the calling stack, it names the call alone.
        [Fact]
        public async Task TheTestIsNamed_ThroughAHelper_AfterAnAwait()
        {
            await Task.Yield();
            Assert.Equal(
                $"{typeof(UiTestDispatcherTests).FullName}.{nameof(TheTestIsNamed_ThroughAHelper_AfterAnAwait)} " +
                    "(Helper at Helper.cs:7)",
                FindThroughHelper());
            Assert.Equal("Helper at Helper.cs:7", await Task.Run(() => FindThroughHelper()));
        }

        private static string FindThroughHelper() => UiTestName.Find("Helper", @"C:\Tests\Helper.cs", 7);

        // A dispatcher of the test's own, on its own STA thread, for plain callbacks only: no window and no resources.
        private sealed class PrivateDispatcher : IDisposable
        {
            private readonly Thread _thread;

            internal PrivateDispatcher()
            {
                var started = new ManualResetEventSlim();
                Dispatcher dispatcher = null;
                _thread = new Thread(() =>
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    started.Set();
                    Dispatcher.Run();
                })
                { IsBackground = true };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                Assert.True(started.Wait(Patience), "The test's dispatcher did not start.");
                Dispatcher = dispatcher;
            }

            internal Dispatcher Dispatcher { get; }

            // Stops the dispatcher once its current callback has returned, so a test releases whatever it blocked
            // first.
            public void Dispose()
            {
                Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                Assert.True(_thread.Join(Patience), "The test's dispatcher did not stop.");
            }
        }
    }
}
