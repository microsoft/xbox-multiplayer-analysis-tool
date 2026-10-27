// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
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

        [Fact]
        public async Task CompileScriptAsync_WarningInstallsNewScript()
        {
            var host = new ScriptHost();
            Assert.Empty(await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                "Params.Continue = false;"));

            Diagnostic[] diagnostics =
                await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                    "#warning xmat-test\nParams.Continue = true;");
            var state = new WebServiceCaptureScriptParams { Continue = false };
            WebServiceCaptureScriptParams result =
                await host.RunScriptAsync(state);

            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
            Assert.Same(state, result);
            Assert.True(state.Continue);
        }

        [Fact]
        public async Task CompileScriptAsync_ErrorClearsInstalledScript()
        {
            var host = new ScriptHost();
            Assert.Empty(await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                "Params.Continue = false;"));

            Diagnostic[] diagnostics =
                await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                    "Params.Continue = ;");
            var state = new WebServiceCaptureScriptParams();
            WebServiceCaptureScriptParams result =
                await host.RunScriptAsync(state);

            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Null(result);
            Assert.True(state.Continue);
        }

        [Fact]
        public async Task CompileScriptAsync_EmptyTextClearsInstalledScript()
        {
            var host = new ScriptHost();
            Assert.Empty(await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                "Params.Continue = false;"));

            Diagnostic[] diagnostics =
                await host.CompileScriptAsync<WebServiceCaptureScriptParams>(
                    string.Empty);
            WebServiceCaptureScriptParams result =
                await host.RunScriptAsync(new WebServiceCaptureScriptParams());

            Assert.Empty(diagnostics);
            Assert.Null(result);
        }
    }
}
