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
        public async Task ValidationDoesNotResolveLoadedFilesOrAdditionalReferences()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, $"ScriptCompileFixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "private.csx");
                const string secret = "PRIVATE_FILE_CONTENT_MUST_NOT_BE_COMPILED";
                File.WriteAllText(path, $"{secret}();");
                var source = $"#load @\"{path.Replace("\"", "\"\"")}\"\n#r @\"{path.Replace("\"", "\"\"")}\"\nParams.Continue = false;";
                var host = new ScriptHost();
                var diagnostics = await host.ValidateScriptAsync<WebServiceCaptureScriptParams>(source);
                Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                Assert.DoesNotContain(secret, string.Join("\n", diagnostics.Select(diagnostic => diagnostic.GetMessage())));
                Assert.Null(await host.RunScriptAsync(new WebServiceCaptureScriptParams()));
                diagnostics = await host.CompileScriptAsync<WebServiceCaptureScriptParams>(source, true, default);
                Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                Assert.DoesNotContain(secret, string.Join("\n", diagnostics.Select(diagnostic => diagnostic.GetMessage())));
                Assert.Null(await host.RunScriptAsync(new WebServiceCaptureScriptParams()));
            }
            finally { Directory.Delete(directory, recursive: true); }
        }

        [Fact]
        public async Task ValidateScriptAsync_DoesNotInstallOrExecuteCandidate()
        {
            var host = new ScriptHost();
            var diagnostics = await host.ValidateScriptAsync<WebServiceCaptureScriptParams>(
                "Params.Continue = false;");
            var state = new WebServiceCaptureScriptParams();

            Assert.Empty(diagnostics);
            Assert.Null(await host.RunScriptAsync(state));
            Assert.True(state.Continue);
        }

        [Theory]
        [InlineData("Params.Continue = true;", false)]
        [InlineData("Params.Continue = ;", true)]
        public async Task ValidateScriptAsync_PreservesInstalledCode(string candidate, bool hasErrors)
        {
            var host = new ScriptHost();
            await host.CompileScriptAsync<WebServiceCaptureScriptParams>("Params.Continue = false;");

            var diagnostics = await host.ValidateScriptAsync<WebServiceCaptureScriptParams>(candidate);
            var state = new WebServiceCaptureScriptParams();
            Assert.Same(state, await host.RunScriptAsync(state));

            Assert.Equal(hasErrors, diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error));
            Assert.False(state.Continue);
        }

        [Fact]
        public async Task ValidateScriptAsync_CancellationPreservesInstalledCode()
        {
            var host = new ScriptHost();
            await host.CompileScriptAsync<WebServiceCaptureScriptParams>("Params.Continue = false;");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.ValidateScriptAsync<WebServiceCaptureScriptParams>(
                    "Params.Continue = true;", cancellation.Token));

            var state = new WebServiceCaptureScriptParams();
            Assert.Same(state, await host.RunScriptAsync(state));
            Assert.False(state.Continue);
        }

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
