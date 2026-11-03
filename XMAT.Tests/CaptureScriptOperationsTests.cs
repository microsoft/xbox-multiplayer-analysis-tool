// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using XMAT.Models;
using XMAT.Scripting;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureScriptOperationsTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void ExplicitScriptTargetsReuseTheDesktopModelsAndExposeExistingSymbols()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                var selected = new ScriptCaptureContext("selected but unrelated");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(selected);
                model.SelectedDeviceContext = selected;
                var captureId = Complete(model.CaptureCatalog.GetCapturesAsync()).Single(capture => capture.DeviceName == target.DeviceName).Id;
                var scripts = Complete(Task.Run(() => model.CaptureScripts.GetScriptsAsync(captureId)));
                Assert.Equal(new[] { CaptureScriptEvent.SslConnectionRequest, CaptureScriptEvent.WebRequest, CaptureScriptEvent.WebResponse },
                    scripts.Select(script => script.Event));
                Assert.Equal(3, scripts.Select(script => script.Id).Distinct().Count());
                var request = scripts.Single(script => script.Event == CaptureScriptEvent.WebRequest);
                var source = Complete(model.CaptureScripts.ReadAsync(captureId, request.Id));
                Assert.Equal(target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest].Script, source.Source);
                var symbols = Complete(model.CaptureScripts.GetSymbolsAsync(captureId));
                Assert.Contains(symbols, symbol => symbol.Name == "Params" && symbol.Properties.Any(property => property.Name == "Continue"));
                Assert.Same(selected, model.SelectedDeviceContext);
                Assert.Equal(request.Id, Complete(model.CaptureScripts.GetScriptsAsync(captureId))
                    .Single(script => script.Event == CaptureScriptEvent.WebRequest).Id);
            });
        }

        [Fact]
        public void EditingDisablesTheOldExecutableAndCompileOnlyValidationNeverInstallsTheDraft()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var captureId = Complete(model.CaptureCatalog.GetCapturesAsync()).Single().Id;
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                Assert.Equal(ScriptActivationResult.Enabled, Complete(script.EnableScriptAsync<WebServiceCaptureScriptParams>()));
                var before = Complete(model.CaptureScripts.GetScriptsAsync(captureId)).Single(item => item.Id == script.Id);
                var result = Complete(Task.Run(() => model.CaptureScripts.UpdateAsync(captureId, script.Id,
                    before.Revision, before.SourceHash, "#warning test-warning\nParams.Continue = true;")));
                Assert.True(result.Changed);
                Assert.False(script.IsEnabled);
                var validation = Complete(model.CaptureScripts.ValidateAsync(captureId, script.Id,
                    result.Script.Revision, result.Script.SourceHash));
                Assert.True(validation.IsCurrent);
                Assert.False(validation.HasErrors);
                Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Severity == CaptureScriptDiagnosticSeverity.Warning);
                Assert.False(script.IsEnabled);
                var parameters = new WebServiceCaptureScriptParams();
                Assert.True(Complete(script.RunScriptAsync(parameters)).Continue);
                var activated = Complete(model.CaptureScripts.SetEnabledAsync(captureId, script.Id,
                    result.Script.Revision, result.Script.SourceHash, true));
                Assert.Equal(CaptureScriptOutcome.Enabled, activated.Outcome);
                Assert.True(activated.Script.IsEnabled);
                Assert.True(script.IsEnabled);
                var unchanged = Complete(model.CaptureScripts.SetEnabledAsync(captureId, script.Id,
                    activated.Script.Revision, activated.Script.SourceHash, true));
                Assert.False(unchanged.Changed);
                Assert.Equal(activated.Script.Revision, unchanged.Script.Revision);
            });
        }

        [Fact]
        public void StaleRevisionsAndReplacedModelsCannotReuseSourceHashApproval()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var captureId = Complete(model.CaptureCatalog.GetCapturesAsync()).Single().Id;
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                var before = Complete(model.CaptureScripts.ReadAsync(captureId, script.Id));
                script.Script = "Params.Continue = true;";
                script.Script = before.Source;
                Assert.Equal(before.Script.SourceHash, script.CaptureState().SourceHash);
                Assert.Equal(CaptureScriptError.SourceChanged, Assert.Throws<CaptureScriptException>(() =>
                    Complete(model.CaptureScripts.SetEnabledAsync(captureId, script.Id,
                        before.Script.Revision, before.Script.SourceHash, true))).Error);
                target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest] =
                    new ScriptModel(WebServiceCaptureScriptableEventType.WebRequest);
                Assert.Equal(CaptureScriptError.TargetNotFound, Assert.Throws<CaptureScriptException>(() =>
                    Complete(model.CaptureScripts.ReadAsync(captureId, script.Id))).Error);
                Assert.False(script.IsEnabled);
            });
        }

        [Fact]
        public void OwnershipCleanupOnlyDisablesTheCommittedActivationNotAHumanRestart()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var captureId = Complete(model.CaptureCatalog.GetCapturesAsync()).Single().Id;
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = "Params.Continue = false;";
                var before = script.CaptureState();
                var receipt = Complete(model.CaptureScripts.SetEnabledAsync(captureId, script.Id,
                    before.Revision, before.SourceHash, true));
                script.DisableScript();
                Complete(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                Assert.False(Complete(model.CaptureScripts.DisableOwnedAsync(captureId, script.Id, receipt.Script.Revision)));
                Assert.True(script.IsEnabled);
                Assert.True(Complete(model.CaptureScripts.DisableOwnedAsync(captureId, script.Id, script.CaptureState().Revision)));
                Assert.False(script.IsEnabled);
            });
        }

        [Fact]
        public void ReadOnlyOrTransitioningCapturesRejectAgentChangesWithoutEffects()
        {
            application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext(readOnly: true);
                model.CaptureDeviceContexts.Add(target);
                var captureId = Complete(model.CaptureCatalog.GetCapturesAsync()).Single().Id;
                Assert.Equal(CaptureScriptError.ReadOnlyCapture, Assert.Throws<CaptureScriptException>(() =>
                    Complete(model.CaptureScripts.GetScriptsAsync(captureId))).Error);
                target.IsReadOnly = false;
                var script = Complete(model.CaptureScripts.GetScriptsAsync(captureId)).First();
                target.Controller.IsTransitioning = true;
                Assert.Equal(CaptureScriptError.Busy, Assert.Throws<CaptureScriptException>(() =>
                    Complete(model.CaptureScripts.UpdateAsync(captureId, script.Id, script.Revision, script.SourceHash, "Params.Continue = false;"))).Error);
                Assert.Equal(script.SourceHash, Complete(model.CaptureScripts.ReadAsync(captureId, script.Id)).Script.SourceHash);
                Assert.False(Complete(model.CaptureScripts.ReadAsync(captureId, script.Id)).Script.IsEnabled);
            });
        }

        private static T Complete<T>(Task<T> task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The script operation did not complete.");
            return task.GetAwaiter().GetResult();
        }
    }
}
