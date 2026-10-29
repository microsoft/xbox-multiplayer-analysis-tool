// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    public class AnalysisReportExportWorkflowTests
    {
        [Fact]
        public async Task PickerCancel_PerformsNoWriteAndReleasesGuard()
        {
            var owner = new object();
            var dialogs = new TestDialogService(owner);
            int exportCount = 0;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) =>
                {
                    exportCount++;
                    return Task.FromResult(destination);
                });

            Assert.Equal(
                AnalysisReportExportWorkflowResult.PickerCancelled,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(0, exportCount);
            Assert.False(workflow.IsRunning);

            dialogs.PickerAsync = () =>
                Task.FromResult(@"C:\destination");
            dialogs.ConsentAsync = defaultCancel =>
                Task.FromResult(true);

            Assert.Equal(
                AnalysisReportExportWorkflowResult.Succeeded,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(1, exportCount);
        }

        [Fact]
        public async Task ConsentCancel_IsDefaultAndPerformsNoWrite()
        {
            var owner = new object();
            var dialogs = new TestDialogService(owner)
            {
                PickerAsync = () =>
                    Task.FromResult(@"C:\destination"),
                ConsentAsync = defaultCancel =>
                {
                    Assert.True(defaultCancel);
                    return Task.FromResult(false);
                }
            };
            int exportCount = 0;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) =>
                {
                    exportCount++;
                    return Task.FromResult(destination);
                });

            Assert.Equal(
                AnalysisReportExportWorkflowResult.ConsentCancelled,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(0, exportCount);
            Assert.False(workflow.IsRunning);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CloseDuringExport_DoesNotAutomaticallyClose(
            bool failExport)
        {
            var owner = new object();
            var exportCompletion =
                new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = CreateApprovedDialogs(owner);
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) => exportCompletion.Task);

            Task<AnalysisReportExportWorkflowResult> runTask =
                workflow.RunAsync(CreateSource());
            Assert.True(workflow.IsRunning);

            Assert.False(await workflow.RequestCloseAsync());
            Assert.False(await workflow.RequestCloseAsync());
            Assert.Equal(2, dialogs.CloseBlockedCount);

            if (failExport)
            {
                exportCompletion.SetException(
                    new IOException("Injected export failure."));
            }
            else
            {
                exportCompletion.SetResult(
                    @"C:\destination\report");
            }

            Assert.Equal(
                failExport
                    ? AnalysisReportExportWorkflowResult.Failed
                    : AnalysisReportExportWorkflowResult.Succeeded,
                await runTask);
            Assert.False(workflow.IsRunning);
            Assert.False(workflow.ClosingStarted);
            Assert.Equal(failExport ? 1 : 0, dialogs.ErrorCount);
        }

        [Theory]
        [InlineData(DialogStage.Picker)]
        [InlineData(DialogStage.Consent)]
        [InlineData(DialogStage.Result)]
        public async Task CloseWhileDialogOpen_DoesNotStackNotice(
            DialogStage stage)
        {
            var owner = new object();
            var pickerCompletion =
                new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var consentCompletion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var resultCompletion =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = CreateApprovedDialogs(owner);
            if (stage == DialogStage.Picker)
            {
                dialogs.PickerAsync = () => pickerCompletion.Task;
            }
            else if (stage == DialogStage.Consent)
            {
                dialogs.ConsentAsync = defaultCancel =>
                    consentCompletion.Task;
            }
            else
            {
                dialogs.SuccessAsync = publishedPath =>
                    resultCompletion.Task;
            }

            var workflow = CreateWorkflow(
                owner,
                dialogs,
                SuccessfulExport);
            Task<AnalysisReportExportWorkflowResult> runTask =
                workflow.RunAsync(CreateSource());
            Assert.True(workflow.IsDialogOpen);

            Assert.False(await workflow.RequestCloseAsync());
            Assert.Equal(0, dialogs.CloseBlockedCount);

            AnalysisReportExportWorkflowResult expected;
            if (stage == DialogStage.Picker)
            {
                pickerCompletion.SetResult(null);
                expected =
                    AnalysisReportExportWorkflowResult.PickerCancelled;
            }
            else if (stage == DialogStage.Consent)
            {
                consentCompletion.SetResult(false);
                expected =
                    AnalysisReportExportWorkflowResult.ConsentCancelled;
            }
            else
            {
                resultCompletion.SetResult();
                expected = AnalysisReportExportWorkflowResult.Succeeded;
            }

            Assert.Equal(expected, await runTask);
            Assert.False(workflow.ClosingStarted);
        }

        [Fact]
        public async Task ExportIsRejectedAfterClosingStarts()
        {
            var owner = new object();
            var dialogs = CreateApprovedDialogs(owner);
            int exportCount = 0;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) =>
                {
                    exportCount++;
                    return Task.FromResult(destination);
                });

            Assert.True(await workflow.RequestCloseAsync());
            Assert.True(workflow.ClosingStarted);
            Assert.Equal(
                AnalysisReportExportWorkflowResult.Rejected,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(0, dialogs.PickerCount);
            Assert.Equal(0, exportCount);
        }

        [Fact]
        public async Task CancelClosing_AllowsExportRetry()
        {
            var owner = new object();
            var workflow = CreateWorkflow(
                owner,
                CreateApprovedDialogs(owner),
                SuccessfulExport);

            Assert.True(await workflow.RequestCloseAsync());

            workflow.CancelClosing();

            Assert.False(workflow.ClosingStarted);
            Assert.Equal(
                AnalysisReportExportWorkflowResult.Succeeded,
                await workflow.RunAsync(CreateSource()));
        }

        [Fact]
        public async Task Failure_ShowsErrorReleasesGuardAndRetrySucceeds()
        {
            var owner = new object();
            var dialogs = CreateApprovedDialogs(owner);
            int exportCount = 0;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) =>
                {
                    exportCount++;
                    return exportCount == 1
                        ? Task.FromException<string>(
                            new IOException("Injected export failure."))
                        : Task.FromResult(
                            @"C:\destination\report");
                });

            Assert.Equal(
                AnalysisReportExportWorkflowResult.Failed,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(1, dialogs.ErrorCount);
            Assert.False(workflow.IsRunning);

            Assert.Equal(
                AnalysisReportExportWorkflowResult.Succeeded,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(2, exportCount);
            Assert.Equal(1, dialogs.SuccessCount);
        }

        [Fact]
        public async Task CompletionNotification_WaitsForCloseNotice()
        {
            var owner = new object();
            var exportCompletion =
                new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var noticeCompletion =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = CreateApprovedDialogs(owner);
            dialogs.CloseBlockedAsync = message =>
                noticeCompletion.Task;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) => exportCompletion.Task);

            Task<AnalysisReportExportWorkflowResult> runTask =
                workflow.RunAsync(CreateSource());
            Task<bool> closeTask = workflow.RequestCloseAsync();
            Assert.True(workflow.IsDialogOpen);
            Assert.Equal(1, dialogs.CloseBlockedCount);
            Assert.False(await workflow.RequestCloseAsync());
            Assert.Equal(1, dialogs.CloseBlockedCount);

            exportCompletion.SetResult(@"C:\destination\report");
            Assert.False(runTask.IsCompleted);
            Assert.Equal(0, dialogs.SuccessCount);

            noticeCompletion.SetResult();
            Assert.False(await closeTask);
            Assert.Equal(
                AnalysisReportExportWorkflowResult.Succeeded,
                await runTask);
            Assert.Equal(1, dialogs.SuccessCount);
        }

        [Fact]
        public async Task RunAsync_RejectsSecondRunWhileFirstIsRunning()
        {
            var owner = new object();
            var exportCompletion =
                new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = CreateApprovedDialogs(owner);
            int exportCount = 0;
            var workflow = CreateWorkflow(
                owner,
                dialogs,
                (source, destination) =>
                {
                    exportCount++;
                    return exportCompletion.Task;
                });

            Task<AnalysisReportExportWorkflowResult> first =
                workflow.RunAsync(CreateSource());

            Assert.Equal(
                AnalysisReportExportWorkflowResult.Rejected,
                await workflow.RunAsync(CreateSource()));
            Assert.Equal(1, dialogs.PickerCount);
            Assert.Equal(1, exportCount);

            exportCompletion.SetResult(@"C:\destination\report");
            Assert.Equal(
                AnalysisReportExportWorkflowResult.Succeeded,
                await first);
        }

        private static AnalysisReportExportWorkflow CreateWorkflow(
            object owner,
            TestDialogService dialogs,
            Func<
                AnalysisExportSource,
                string,
                Task<string>> exportAsync)
        {
            return new AnalysisReportExportWorkflow(
                owner,
                dialogs,
                exportAsync,
                () => "close blocked");
        }

        private static TestDialogService CreateApprovedDialogs(
            object owner)
        {
            return new TestDialogService(owner)
            {
                PickerAsync = () =>
                    Task.FromResult(@"C:\destination"),
                ConsentAsync = defaultCancel =>
                {
                    Assert.True(defaultCancel);
                    return Task.FromResult(true);
                }
            };
        }

        private static Task<string> SuccessfulExport(
            AnalysisExportSource source,
            string destination)
        {
            return Task.FromResult(
                @"C:\destination\report");
        }

        private static AnalysisExportSource CreateSource()
        {
            return new AnalysisExportSource(
                @"C:\source",
                "Capture",
                new DateTime(
                    2026,
                    10,
                    26,
                    14,
                    23,
                    21,
                    DateTimeKind.Local),
                new[]
                {
                    new AnalysisArtifact(
                        "index.html",
                        5)
                });
        }

        public enum DialogStage
        {
            Picker,
            Consent,
            Result
        }

        private sealed class TestDialogService :
            IAnalysisReportExportDialogService
        {
            private readonly object _expectedOwner;

            internal Func<Task<string>> PickerAsync { get; set; }
            internal Func<bool, Task<bool>> ConsentAsync { get; set; }
            internal Func<string, Task> SuccessAsync { get; set; }
            internal Func<string, Task> ErrorAsync { get; set; }
            internal Func<string, Task> CloseBlockedAsync { get; set; }

            internal int PickerCount { get; private set; }
            internal int SuccessCount { get; private set; }
            internal int ErrorCount { get; private set; }
            internal int CloseBlockedCount { get; private set; }

            internal TestDialogService(object expectedOwner)
            {
                _expectedOwner = expectedOwner;
                PickerAsync = () => Task.FromResult<string>(null);
                ConsentAsync = defaultCancel => Task.FromResult(false);
                SuccessAsync = publishedPath => Task.CompletedTask;
                ErrorAsync = message => Task.CompletedTask;
                CloseBlockedAsync = message => Task.CompletedTask;
            }

            public Task<string> ShowFolderPickerAsync(object owner)
            {
                Assert.Same(_expectedOwner, owner);
                PickerCount++;
                return PickerAsync();
            }

            public Task<bool> ShowConsentAsync(
                object owner,
                bool defaultCancel)
            {
                Assert.Same(_expectedOwner, owner);
                return ConsentAsync(defaultCancel);
            }

            public Task ShowSuccessAsync(
                object owner,
                string publishedPath)
            {
                Assert.Same(_expectedOwner, owner);
                SuccessCount++;
                return SuccessAsync(publishedPath);
            }

            public Task ShowErrorAsync(
                object owner,
                string errorMessage)
            {
                Assert.Same(_expectedOwner, owner);
                ErrorCount++;
                return ErrorAsync(errorMessage);
            }

            public Task ShowCloseBlockedAsync(
                object owner,
                string message)
            {
                Assert.Same(_expectedOwner, owner);
                CloseBlockedCount++;
                return CloseBlockedAsync(message);
            }
        }
    }
}
