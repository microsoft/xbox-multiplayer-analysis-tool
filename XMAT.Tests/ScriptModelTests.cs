// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using XMAT.Scripting;

namespace XMAT.Tests
{
    public class ScriptModelTests
    {
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
            Assert.Contains(nameof(ScriptModel.IsEnabled), propertyChanges);

            gate.Release();
            Assert.Equal(ScriptActivationResult.Enabled, await enabling);
            Assert.True(model.IsEnabled);
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

            internal Task WaitAsync(string script)
            {
                if (!script.Contains("// slow", StringComparison.Ordinal))
                    return Task.CompletedTask;

                Started.TrySetResult(true);
                return _release.Task;
            }

            internal void Release()
            {
                _release.TrySetResult(true);
            }
        }
    }
}
