// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ScriptFileExportTests(CaptureUiApplicationFixture application)
    {
        [Fact]
        public void DesktopLoadAndRevertShareBomDecodingAndNeverEnableTheLoadedSource()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var target = new ScriptCaptureContext();
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var path = Path.Combine(directory.Path, "input.txt");
                const string text = "// \u03bb\U0001F600\r\nINVALID_UNCOMPILED_SOURCE(???);\n";
                File.WriteAllText(path, text, Encoding.Unicode);
                script.Script = "Params.Continue = false;";
                Finish(script.EnableScriptAsync<WebServiceCaptureScriptParams>());
                Assert.True(script.IsEnabled);

                Finish(ScriptFiles.LoadDesktopAsync(script, path, rememberFileName: true));
                Assert.Equal(text, script.Script);
                Assert.False(script.IsEnabled);
                Assert.False(script.IsCompiling);
                Assert.Empty(script.CompilationStatus);
                Assert.Equal(path, script.Filename);

                script.Script = "// newer draft";
                Finish(ScriptFiles.LoadDesktopAsync(script, script.Filename, rememberFileName: false));
                Assert.Equal(text, script.Script);
                Assert.Equal(path, script.Filename);
                Assert.False(script.IsEnabled);
                Assert.Equal(text, File.ReadAllText(path));
            });
        }

        [Fact]
        public void ExportWritesCompleteUtf8SourceWithoutMutatingOrCompilingTheScript()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var text = string.Concat(Enumerable.Repeat("// \u03bb\U0001F600\r\nINVALID_UNCOMPILED_SOURCE(???);\n", 3000));
                script.Script = text;
                script.Filename = Path.Combine(directory.Path, "remembered-not-exported.txt");
                var capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                var source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                try
                {
                    var folder = Complete(model.CaptureFileExports.RegisterFolderAsync(directory.Path));
                    var receipt = Complete(model.CaptureFileExports.SaveScriptAsync(source,
                        new(folder.Id, "export.txt"), 1024 * 1024, _ => Task.CompletedTask));
                    Assert.True(receipt.Published);
                    Assert.Equal(source.Script.Revision, receipt.Revision);
                    Assert.Equal(source.Script.SourceHash, receipt.SourceHash);
                    Assert.Equal(Encoding.UTF8.GetBytes(text), File.ReadAllBytes(Path.Combine(directory.Path, "export.txt")));
                    Assert.Equal(source.Script.SourceBytes, receipt.Length);
                    Assert.Equal(source.Script, Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id)).Script);
                    Assert.False(script.IsEnabled);
                    Assert.False(script.IsCompiling);
                    Assert.Empty(script.CompilationStatus);
                    Assert.False(File.Exists(script.Filename));
                }
                finally { Finish(model.AgentAccess.DisposeAsync().AsTask()); }
            });
        }

        [Fact]
        public void FailedScriptExportsPreserveHumanFilesAndDeleteOnlyTheirUnpublishedOutput()
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                script.Script = new string('x', 5000);
                var capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                var source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                var human = Path.Combine(directory.Path, "human.txt");
                File.WriteAllText(human, "human");
                try
                {
                    var folder = Complete(model.CaptureFileExports.RegisterFolderAsync(directory.Path));
                    var destination = new ScriptFileDestination(folder.Id, "export.txt");
                    Assert.Equal(QueryError.ResultLimit, Assert.Throws<QueryException>(() => Complete(
                        model.CaptureFileExports.SaveScriptAsync(source, destination, 100, _ => Task.CompletedTask))).Error);
                    using var cancellation = new CancellationTokenSource();
                    Assert.ThrowsAny<OperationCanceledException>(() => Complete(model.CaptureFileExports.SaveScriptAsync(
                        source, destination, 10000, _ =>
                        {
                            cancellation.Cancel();
                            return Task.CompletedTask;
                        }, cancellation.Token)));
                    Assert.Equal(CaptureFileFailure.InvalidPath, Assert.Throws<CaptureFileException>(() => Finish(
                        model.CaptureFileExports.ValidateScriptAsync(new(folder.Id, "script.csx")))).Failure);
                    Assert.Equal(human, Assert.Single(Directory.GetFiles(directory.Path)));
                    Assert.Equal("human", File.ReadAllText(human));
                    Assert.False(script.IsEnabled);
                }
                finally { Finish(model.AgentAccess.DisposeAsync().AsTask()); }
            });
        }
    }
}
