// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Threading;

namespace XMAT.Tests
{
    // WpfTestHost runs its tests on the shared WPF application's dispatcher: only that thread ever reads the
    // application's resources, so a test can never run WPF beside other work queued on the application.
    [Collection(WpfTestHostCollection.Name)]
    public class WpfTestHostTests
    {
        [Fact]
        public void Run_ShowsTheTestWindowOnTheApplicationsDispatcher()
        {
            Dispatcher application = null;
            Dispatcher window = null;

            WpfTestHost.Run(test =>
            {
                application = Application.Current?.Dispatcher;
                window = test.Dispatcher;
            });

            Assert.NotNull(application);
            Assert.Same(application, window);
        }

        // A test that is already on the application's dispatcher runs there at once, without waiting for itself.
        [Fact]
        public void Run_FromTheApplicationsDispatcher_RunsTheTestInPlace()
        {
            using var fixture = new CaptureUiApplicationFixture();
            int outer = 0;
            int inner = -1;
            Dispatcher window = null;

            fixture.Run(_ =>
            {
                outer = Environment.CurrentManagedThreadId;
                WpfTestHost.Run(test =>
                {
                    inner = Environment.CurrentManagedThreadId;
                    window = test.Dispatcher;
                });
            });

            Assert.Equal(outer, inner);
            Assert.Same(Application.Current.Dispatcher, window);
        }

        // A task that never finishes fails Complete once its bound passes, instead of hanging the test run.
        [Fact]
        public void Complete_ReportsATaskThatDoesNotFinishInTime() =>
            WpfTestHost.Run(_ => Assert.Throws<TimeoutException>(
                () => WpfTestHost.Complete(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(50))));
    }
}
