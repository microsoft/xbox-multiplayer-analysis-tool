// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using XMAT.Scripting;

namespace XMAT.Tests
{
    public class ScriptModelTests
    {
        public ScriptModelTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public async Task EnableScriptAsync_WarningActivatesNewCode()
        {
            ScriptModel model = CreateModel();
            model.Script = "Params.Continue = false;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            model.Script = "#warning xmat-test\nParams.Continue = true;";
            ScriptActivationResult result =
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>();

            Assert.Equal(ScriptActivationResult.Enabled, result);
            Assert.True(model.IsEnabled);
            Assert.Contains(
                model.CompilationStatus,
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
            Assert.Equal(
                "Script compiled successfully with warnings.",
                model.CompilationMessage);
            Assert.True(model.HasCompilationMessage);
            Assert.True(model.HasCompilationDiagnostics);
            Assert.True(await RunScriptAsync(model, initialValue: false));
        }

        [Fact]
        public async Task EnableScriptAsync_ErrorDisablesAndClearsPreviousCode()
        {
            ScriptModel model = CreateModel();
            model.Script = "Params.Continue = false;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            model.Script = "Params.Continue = ;";
            ScriptActivationResult result =
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>();

            Assert.Equal(ScriptActivationResult.Failed, result);
            Assert.False(model.IsEnabled);
            Assert.Contains(
                model.CompilationStatus,
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Null(model.CompilationMessage);
            Assert.False(model.HasCompilationMessage);
            Assert.True(model.HasCompilationDiagnostics);
            Assert.True(await RunScriptAsync(model, initialValue: true));
        }

        [Fact]
        public async Task EnableScriptAsync_EmptyTextDisablesAndClearsPreviousCode()
        {
            ScriptModel model = CreateModel();
            model.Script = "Params.Continue = false;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            model.Script = string.Empty;
            ScriptActivationResult result =
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>();

            Assert.Equal(ScriptActivationResult.Failed, result);
            Assert.False(model.IsEnabled);
            Assert.Empty(model.CompilationStatus);
            Assert.True(await RunScriptAsync(model, initialValue: true));
        }

        [Fact]
        public async Task EnableScriptAsync_RemainsDisabledUntilCompilationCompletes()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            var propertyChanges = new List<string>();
            model.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);
            model.Script = "// slow\nParams.Continue = false;";

            Task<ScriptActivationResult> enabling =
                model.EnableScriptAsync<WebServiceCaptureScriptParams>();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(model.IsEnabled);
            Assert.True(model.IsCompiling);
            Assert.Contains(nameof(ScriptModel.IsEnabled), propertyChanges);
            Assert.Contains(nameof(ScriptModel.IsCompiling), propertyChanges);

            gate.Release();
            Assert.Equal(ScriptActivationResult.Enabled, await enabling);
            Assert.True(model.IsEnabled);
            Assert.False(model.IsCompiling);
        }

        [Fact]
        public async Task EnableScriptAsync_OverlappingRequestsLatestWins()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            model.Script = "// slow\nParams.Continue = false;";
            Task<ScriptActivationResult> slowEnable =
                model.EnableScriptAsync<WebServiceCaptureScriptParams>();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                model.Script = "Params.Continue = true;";
                Assert.Equal(
                    ScriptActivationResult.Enabled,
                    await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
                Assert.True(await RunScriptAsync(model, initialValue: false));
            }
            finally
            {
                gate.Release();
            }

            Assert.Equal(ScriptActivationResult.Superseded, await slowEnable);
            Assert.True(model.IsEnabled);
            Assert.True(await RunScriptAsync(model, initialValue: false));
        }

        [Fact]
        public async Task ScriptEdit_DisablesAndClearsCompiledCode()
        {
            ScriptModel model = CreateModel();
            model.Script = "Params.Continue = false;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            model.Script = "Params.Continue = true;";

            Assert.False(model.IsEnabled);
            Assert.True(await RunScriptAsync(model, initialValue: true));
        }

        [Fact]
        public async Task ScriptEdit_DuringCompilationInvalidatesPendingActivation()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            model.Script = "// slow\nParams.Continue = false;";
            Task<ScriptActivationResult> enabling =
                model.EnableScriptAsync<WebServiceCaptureScriptParams>();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            model.Script = "Params.Continue = true;";

            Assert.False(model.IsCompiling);
            Assert.False(model.IsEnabled);
            Assert.Empty(model.CompilationStatus);
            Assert.Null(model.CompilationMessage);

            gate.Release();
            Assert.Equal(ScriptActivationResult.Superseded, await enabling);
            Assert.False(model.IsEnabled);
            Assert.False(model.IsCompiling);
        }

        [Fact]
        public async Task EnableScriptAsync_NewerSuccessSurvivesOlderFailure()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            model.Script = "// slow-fault\nParams.Continue = false;";
            Task<ScriptActivationResult> older =
                model.EnableScriptAsync<WebServiceCaptureScriptParams>();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            model.Script = "Params.Continue = true;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            gate.Release();

            Assert.Equal(ScriptActivationResult.Superseded, await older);
            Assert.True(model.IsEnabled);
            Assert.False(model.IsCompiling);
            Assert.Equal("Script compiled successfully.", model.CompilationMessage);
            Assert.True(await RunScriptAsync(model, initialValue: false));
        }

        [Fact]
        public async Task EnableScriptAsync_UnexpectedFailureRecoversInline()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            model.Script = "// unexpected-fault\nParams.Continue = false;";

            ScriptActivationResult result =
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>();

            Assert.Equal(ScriptActivationResult.Failed, result);
            Assert.False(model.IsEnabled);
            Assert.False(model.IsCompiling);
            Assert.Empty(model.CompilationStatus);
            Assert.Equal(
                "Could not compile; script remains disabled. See the app log.",
                model.CompilationMessage);
        }

        [Fact]
        public async Task EnableScriptAsync_FatalFailurePropagates()
        {
            var gate = new CompileGate();
            ScriptModel model = CreateModel(new ScriptHost(gate.WaitAsync));
            model.Script = "// fatal-fault\nParams.Continue = false;";

            await Assert.ThrowsAsync<BadImageFormatException>(
                () => model.EnableScriptAsync<WebServiceCaptureScriptParams>());
        }

        [Fact]
        public async Task ScriptEdit_ClearsDiagnosticsAndMessage()
        {
            ScriptModel model = CreateModel();
            model.Script = "#warning xmat-test\nParams.Continue = true;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
            Assert.NotEmpty(model.CompilationStatus);
            Assert.NotNull(model.CompilationMessage);

            model.Script = "Params.Continue = false;";

            Assert.Empty(model.CompilationStatus);
            Assert.Null(model.CompilationMessage);
            Assert.False(model.HasCompilationDiagnostics);
            Assert.False(model.HasCompilationMessage);
            Assert.False(model.IsEnabled);
        }

        [Fact]
        public async Task DisableScript_DisablesImmediately()
        {
            ScriptModel model = CreateModel();
            model.Script = "Params.Continue = false;";
            Assert.Equal(
                ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            model.DisableScript();

            Assert.False(model.IsEnabled);
            Assert.True(await RunScriptAsync(model, initialValue: true));
        }

        [Fact]
        public async Task EnableScriptAsync_SeparateModelsDoNotAffectEachOther()
        {
            var gate = new CompileGate();
            ScriptModel first = CreateModel(
                new ScriptHost(gate.WaitAsync),
                WebServiceCaptureScriptableEventType.WebRequest);
            ScriptModel second = CreateModel(
                eventType: WebServiceCaptureScriptableEventType.WebResponse);
            first.Script = "// slow\nParams.Continue = false;";
            second.Script = "Params.Continue = true;";

            Task<ScriptActivationResult> firstEnable =
                first.EnableScriptAsync<WebServiceCaptureScriptParams>();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                Assert.Equal(
                    ScriptActivationResult.Enabled,
                    await second.EnableScriptAsync<WebServiceCaptureScriptParams>());
                Assert.True(second.IsEnabled);
                Assert.True(await RunScriptAsync(second, initialValue: false));
            }
            finally
            {
                gate.Release();
            }

            Assert.Equal(ScriptActivationResult.Enabled, await firstEnable);
            Assert.True(first.IsEnabled);
            Assert.True(second.IsEnabled);
            Assert.False(await RunScriptAsync(first, initialValue: true));
            Assert.True(await RunScriptAsync(second, initialValue: false));
        }

        private static ScriptModel CreateModel(
            ScriptHost host = null,
            WebServiceCaptureScriptableEventType eventType =
                WebServiceCaptureScriptableEventType.WebRequest)
        {
            return new ScriptModel(eventType, host ?? new ScriptHost());
        }

        private static async Task<bool> RunScriptAsync(
            ScriptModel model,
            bool initialValue)
        {
            var state = new WebServiceCaptureScriptParams
            {
                Continue = initialValue
            };
            WebServiceCaptureScriptParams result =
                await model.RunScriptAsync(state);
            Assert.Same(state, result);
            return state.Continue;
        }

        private sealed class CompileGate
        {
            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal async Task WaitAsync(string script)
            {
                if (script.Contains("// unexpected-fault", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unexpected compile failure.");
                }
                if (script.Contains("// fatal-fault", StringComparison.Ordinal))
                {
                    throw new BadImageFormatException("Fatal compile failure.");
                }

                if (!script.Contains("// slow", StringComparison.Ordinal))
                    return;

                Started.TrySetResult(true);
                await _release.Task;
                if (script.Contains("// slow-fault", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Delayed compile failure.");
                }
            }

            internal void Release()
            {
                _release.TrySetResult(true);
            }
        }
    }
}
