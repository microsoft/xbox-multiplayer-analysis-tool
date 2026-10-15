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
    }
}
