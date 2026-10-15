// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Scripting;

namespace XMAT.Tests
{
    public class ScriptHostTests
    {
        [Fact]
        public async Task RunScriptAsync_ObservesCancellationToken()
        {
            var host = new ScriptHost();
            var diagnostics = await host.CompileScriptAsync<object>(
                "System.Threading.Thread.Sleep(1000);");
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            Assert.Empty(diagnostics);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => host.RunScriptAsync(
                    new object(),
                    cancellationTokenSource.Token));
        }
    }
}
