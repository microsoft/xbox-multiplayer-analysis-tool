// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    public class AsyncOperationTrackerTests
    {
        [Fact]
        public async Task StopAcceptingAndWaitAsync_WaitsForActiveOperations()
        {
            var tracker = new AsyncOperationTracker();
            tracker.StartAccepting();
            Assert.True(tracker.TryBegin(out var operationLease));

            var drained = tracker.StopAcceptingAndWaitAsync();

            Assert.False(drained.IsCompleted);
            Assert.False(tracker.TryBegin(out var rejectedLease));
            Assert.Null(rejectedLease);

            operationLease.Dispose();
            await drained;
        }

        [Fact]
        public async Task Tracker_CanRestartAfterOperationsDrain()
        {
            var tracker = new AsyncOperationTracker();
            tracker.StartAccepting();
            Assert.True(tracker.TryBegin(out var firstLease));
            var drained = tracker.StopAcceptingAndWaitAsync();
            firstLease.Dispose();
            await drained;

            tracker.StartAccepting();

            Assert.True(tracker.TryBegin(out var secondLease));
            secondLease.Dispose();
            await tracker.StopAcceptingAndWaitAsync();
        }
    }
}
