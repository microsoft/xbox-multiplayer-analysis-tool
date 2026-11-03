// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;
using static XMAT.Tests.CaptureDeviceTestData;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AgentScriptFileApplicationTests(CaptureUiApplicationFixture application)
    {
        [Theory]
        [InlineData("cancel")]
        [InlineData("revoke")]
        [InlineData("deadline")]
        [InlineData("input")]
        [InlineData("target")]
        public void InputReadChangesOrCancellationCannotOverwriteNewWorkAndAdmissionRecovers(string change)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var path = Path.Combine(directory.Path, "input.txt");
                File.WriteAllText(path, "// loaded source");
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var original = script.Script;
                var capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                var source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                var inputs = new CaptureFileInputs();
                var input = Complete(inputs.RegisterAsync(path, 65536));
                var paused = new PausedInputs(inputs);
                var runtime = new AgentRuntime();
                runtime.Enable();
                var session = runtime.Connect("paused script input");
                var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                var edit = new AgentPermission(AgentCapability.ScriptEdit, AgentScope.Capture(capture.Id));
                var file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                var limits = new AgentLimits(queryTimeoutSeconds: change == "deadline" ? 1 : 60, maximumQueryResults: 1);
                Finish(runtime.SetPermissionsAsync(session, [read, edit, file], limits));
                var operations = new AgentScriptApplication(runtime, model.CaptureScripts, () => true, fileInputs: paused);
                try
                {
                    var operation = Complete(operations.LoadFileAsync(session, capture.Id, script.Id,
                        source.Script.Revision, source.Script.SourceHash, input.Id, "paused", default));
                    paused.PauseNextRead = true;
                    Assert.True(runtime.Approve(operation));
                    PumpUntil(() => paused.ReadStarted.Task.IsCompleted);
                    if (change == "revoke")
                        Finish(runtime.SetPermissionsAsync(session, [read, edit], limits));
                    else if (change == "cancel")
                        Assert.True(Complete(runtime.CancelAsync(session, operation)));
                    else if (change is "input" or "target")
                    {
                        if (change == "input")
                            File.WriteAllText(path, "// changed input");
                        else
                        {
                            original = "// newer human edit";
                            script.Script = original;
                        }
                        paused.Continue.TrySetResult();
                    }
                    Finish(runtime.WaitForCompletionAsync(session, operation));
                    var status = runtime.GetOperation(session, operation);
                    if (change == "deadline")
                        Assert.Equal(AgentAccessError.QueryTimeout, status.Error);
                    else
                        Assert.Equal(change is "input" or "target"
                            ? AgentOperationState.SourceChanged : AgentOperationState.Cancelled, status.State);
                    Assert.Equal(original, script.Script);
                    Assert.False(script.IsEnabled);
                    Assert.False(script.IsCompiling);
                    Assert.Empty(runtime.GetOwnedResources());

                    paused.Continue.TrySetResult();
                    input = Complete(inputs.RegisterAsync(path, 65536));
                    file = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id));
                    Finish(runtime.SetPermissionsAsync(session, [read, edit, file], new AgentLimits(maximumQueryResults: 1)));
                    source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                    var retry = Complete(operations.LoadFileAsync(session, capture.Id, script.Id,
                        source.Script.Revision, source.Script.SourceHash, input.Id, "retry", default));
                    Assert.True(runtime.Approve(retry));
                    Finish(runtime.WaitForCompletionAsync(session, retry));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(session, retry).State);
                    Assert.Equal(File.ReadAllText(path), script.Script);
                    Assert.False(script.IsEnabled);
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData("edit")]
        [InlineData("revoke")]
        [InlineData("deadline")]
        public void EditRevokeOrDeadlineBeforePublicationDiscardsOutputAndReleasesAdmissionForRetry(string change)
        {
            application.Run(_ =>
            {
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var model = new CaptureAppModel();
                var target = new ScriptCaptureContext();
                model.CaptureDeviceContexts.Add(target);
                var script = target.Controller.Scripts[WebServiceCaptureScriptableEventType.WebRequest];
                var capture = Complete(model.CaptureCatalog.GetCapturesAsync()).Single();
                var source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                var folder = Complete(model.CaptureFileExports.RegisterFolderAsync(directory.Path));
                var paused = new PausedExports(model.CaptureFileExports);
                var runtime = new AgentRuntime();
                runtime.Enable();
                var session = runtime.Connect("paused script exporter");
                var read = new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id));
                var export = new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id));
                var write = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id));
                var limits = new AgentLimits(queryTimeoutSeconds: change == "deadline" ? 1 : 60, maximumQueryResults: 1);
                Finish(runtime.SetPermissionsAsync(session, [read, export, write], limits));
                var operations = new AgentScriptApplication(runtime, model.CaptureScripts, () => true, paused);
                try
                {
                    var operation = Complete(operations.SaveFileAsync(session, capture.Id, script.Id,
                        source.Script.Revision, source.Script.SourceHash, folder.Id, "stale.txt", "paused", default));
                    Assert.True(runtime.Approve(operation));
                    PumpUntil(() => paused.BeforePublication.Task.IsCompleted);
                    Assert.False(File.Exists(Path.Combine(directory.Path, "stale.txt")));
                    if (change == "revoke")
                    {
                        Finish(runtime.SetPermissionsAsync(session, [read, write], limits));
                        Assert.Equal(AgentOperationState.Cancelled, runtime.GetOperation(session, operation).State);
                    }
                    else if (change == "deadline")
                    {
                        Finish(runtime.WaitForCompletionAsync(session, operation));
                        Assert.Equal(AgentAccessError.QueryTimeout, runtime.GetOperation(session, operation).Error);
                    }
                    else
                    {
                        script.Script = "// newer human draft";
                        paused.Continue.TrySetResult();
                        Finish(runtime.WaitForCompletionAsync(session, operation));
                        Assert.Equal(AgentOperationState.SourceChanged, runtime.GetOperation(session, operation).State);
                    }
                    Assert.Empty(Directory.GetFiles(directory.Path));
                    Assert.False(script.IsEnabled);
                    Assert.False(script.IsCompiling);
                    Assert.Empty(runtime.GetOwnedResources());

                    paused.Continue.TrySetResult();
                    Finish(runtime.SetPermissionsAsync(session, [read, export, write], new AgentLimits(maximumQueryResults: 1)));
                    source = Complete(model.CaptureScripts.ReadAsync(capture.Id, script.Id));
                    var retry = Complete(operations.SaveFileAsync(session, capture.Id, script.Id,
                        source.Script.Revision, source.Script.SourceHash, folder.Id, "retry.txt", "retry", default));
                    Assert.True(runtime.Approve(retry));
                    Finish(runtime.WaitForCompletionAsync(session, retry));
                    Assert.True(runtime.GetResult(session, retry).GetProperty("published").GetBoolean());
                    Assert.Equal(source.Source, File.ReadAllText(Path.Combine(directory.Path, "retry.txt")));
                    Assert.Single(Directory.GetFiles(directory.Path));
                }
                finally
                {
                    paused.Continue.TrySetResult();
                    Finish(runtime.DisposeAsync().AsTask());
                    Finish(model.AgentAccess.DisposeAsync().AsTask());
                }
            });
        }

        private sealed class PausedExports(ICaptureFileExports inner) : ICaptureFileExports
        {
            internal TaskCompletionSource BeforePublication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<ExportFolderDescriptor> GetFolders() => inner.GetFolders();
            public Task ValidateScriptAsync(ScriptFileDestination destination, CancellationToken cancellationToken = default) =>
                inner.ValidateScriptAsync(destination, cancellationToken);
            public Task<ScriptFileExportReceipt> SaveScriptAsync(
                CaptureScriptSource source, ScriptFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
                inner.SaveScriptAsync(source, destination, maximumBytes, async token =>
                {
                    BeforePublication.TrySetResult();
                    await Continue.Task.WaitAsync(token);
                    await beforePublish(token);
                }, cancellationToken);

            public Task<ExportFolderDescriptor> RegisterFolderAsync(string path, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task ValidateAsync(QueryFileDestination destination, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task ValidateCaptureAsync(CaptureFileDestination destination, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task<long> SaveCaptureAsync(
                ICaptureArchive archive, CaptureFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task<QueryFileExportReceipt> SaveQueryAsync(
                IQueryResult result, QueryFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class PausedInputs(ICaptureFileInputs inner) : ICaptureFileInputs
        {
            internal bool PauseNextRead { get; set; }
            internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IReadOnlyList<InputFileDescriptor> GetInputs() => inner.GetInputs();
            public Task<InputFileDescriptor> RegisterAsync(string path, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.RegisterAsync(path, maximumBytes, cancellationToken);
            public Task ValidateAsync(Guid inputId, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.ValidateAsync(inputId, maximumBytes, cancellationToken);
            public Task<InputFileDescriptor> CopyCaptureAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.CopyCaptureAsync(inputId, destination, maximumBytes, cancellationToken);
            public Task<InputFileDescriptor> CopySazAsync(
                Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
                inner.CopySazAsync(inputId, destination, maximumBytes, cancellationToken);
            public async Task<ScriptFileInput> ReadScriptAsync(
                Guid inputId, long maximumBytes, CancellationToken cancellationToken = default)
            {
                if (PauseNextRead)
                {
                    PauseNextRead = false;
                    ReadStarted.TrySetResult();
                    await Continue.Task.WaitAsync(cancellationToken);
                }
                return await inner.ReadScriptAsync(inputId, maximumBytes, cancellationToken);
            }
        }
    }
}
