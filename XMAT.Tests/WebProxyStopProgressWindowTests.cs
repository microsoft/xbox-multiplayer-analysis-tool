// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    public class WebProxyStopProgressWindowTests
    {
        [Fact]
        public async Task ShouldShowAsync_ReturnsFalseForFastOperation()
        {
            var shouldShow = await WebProxyStopProgressWindow.ShouldShowAsync(
                Task.CompletedTask,
                TimeSpan.FromMilliseconds(50));

            Assert.False(shouldShow);
        }

        [Fact]
        public async Task ShouldShowAsync_ReturnsTrueForSlowOperation()
        {
            var operationCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var shouldShow = await WebProxyStopProgressWindow.ShouldShowAsync(
                operationCompletion.Task,
                TimeSpan.FromMilliseconds(10));
            operationCompletion.SetResult();

            Assert.True(shouldShow);
        }

        [Fact]
        public async Task ShouldShowAsync_DoesNotObserveOperationFailureOrCancellation()
        {
            Task failed = Task.FromException(new InvalidOperationException("failed"));
            Task canceled = Task.FromCanceled(new CancellationToken(canceled: true));

            Assert.False(await WebProxyStopProgressWindow.ShouldShowAsync(failed, TimeSpan.FromSeconds(1)));
            Assert.False(await WebProxyStopProgressWindow.ShouldShowAsync(canceled, TimeSpan.FromSeconds(1)));
            Assert.IsType<InvalidOperationException>(failed.Exception?.InnerException);
        }
    }
}
