// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Capture;

namespace XMAT.Tests.Storage
{
    public sealed class CaptureArchivePublisherTests : IDisposable
    {
        private readonly string _directory =
            Path.Join(
                Path.GetTempPath(),
                "xmat-s2-publisher-" +
                    Guid.NewGuid().ToString("N"));
        private readonly string _work;

        public CaptureArchivePublisherTests()
        {
            Directory.CreateDirectory(_directory);
            _work = Path.Join(_directory, "work");
            Directory.CreateDirectory(_work);
        }

        [Fact]
        public async Task T9_PrivateKindCreatesANewArchive()
        {
            string path = Path.Join(
                _directory,
                "created.saz");

            OperationResult<SazExportResult> result =
                await RunAsync(path);

            Assert.Equal(
                OperationOutcome.Succeeded,
                result.Outcome);
            Assert.Equal(
                SazExportPublication.Created,
                result.Value.Publication);
            Assert.Null(result.Value.Code);
            Assert.True(File.Exists(path));
            Assert.Empty(
                Directory.GetFiles(
                    _directory,
                    ".xmat-*.tmp"));
        }

        [Fact]
        public async Task T9_ConfirmedPrivateFileIsReplaced()
        {
            string path = Path.Join(
                _directory,
                "replaced.saz");
            File.WriteAllBytes(
                path,
                "earlier"u8.ToArray());

            OperationResult<SazExportResult> result =
                await RunAsync(
                    path,
                    replace: true);

            Assert.Equal(
                SazExportPublication.Replaced,
                result.Value.Publication);
            Assert.False(
                File.ReadAllBytes(path)
                    .SequenceEqual(
                        "earlier"u8.ToArray()));
            Assert.Empty(result.Value.Residues);
        }

        [Fact]
        public async Task T9_AConfirmedFileThatChangesIsNotPublished()
        {
            string path = Path.Join(
                _directory,
                "changed.saz");
            File.WriteAllText(path, "first");
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> plan =
                await PrepareAsync(operation);
            using CaptureArchiveTarget target =
                await operation.AdmitTargetAsync(
                        path,
                        new OperationContext(_work),
                        CancellationToken.None);
            CaptureArchiveReplaceDecision decision =
                target.ConfirmReplace();
            File.WriteAllText(
                path,
                "changed after admission");

            OperationResult<SazExportResult> result =
                await ExecuteAsync(
                    operation,
                    plan.Value,
                    target,
                    decision);

            Assert.Equal(
                OperationOutcome.Conflict,
                result.Outcome);
            Assert.Equal(
                SazExportCodes.Changed,
                result.Value.Code);
            Assert.Equal(
                "changed after admission",
                File.ReadAllText(path));
        }

        [Fact]
        public async Task T9_AFileThatAppearsAfterAdmissionIsNeverReplaced()
        {
            string path = Path.Join(
                _directory,
                "appeared.saz");
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> plan =
                await PrepareAsync(operation);
            using CaptureArchiveTarget target =
                await operation.AdmitTargetAsync(
                        path,
                        new OperationContext(_work),
                        CancellationToken.None);
            File.WriteAllText(
                path,
                "other writer");

            OperationResult<SazExportResult> result =
                await ExecuteAsync(
                    operation,
                    plan.Value,
                    target,
                    decision: null);

            Assert.Equal(
                SazExportCodes.Exists,
                result.Value.Code);
            Assert.Equal(
                "other writer",
                File.ReadAllText(path));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task T9_CopyKindCreatesOrReplacesThroughItsHeldFolders(
            bool replace)
        {
            string path = Path.Join(
                _directory,
                replace
                    ? "copy-replaced.saz"
                    : "copy-created.saz");
            if (replace)
            {
                File.WriteAllText(
                    path,
                    "earlier");
            }

            OperationResult<SazExportResult> result =
                await RunAsync(
                    path,
                    replace,
                    new SazExportTestHooks(
                        AdmitAsKind:
                            CaptureArchiveKind.Copy,
                        AdmitAsRemote: false));

            Assert.Equal(
                replace
                    ? SazExportPublication.Replaced
                    : SazExportPublication.Created,
                result.Value.Publication);
            Assert.True(File.Exists(path));
            Assert.Empty(
                Directory.GetFiles(
                    _work,
                    ".xmat-*.tmp"));
        }

        [Fact]
        public async Task T9_CancellationBeforeWritingPublishesNothing()
        {
            string path = Path.Join(
                _directory,
                "cancelled.saz");
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> plan =
                await PrepareAsync(operation);
            using CaptureArchiveTarget target =
                await operation.AdmitTargetAsync(
                        path,
                        new OperationContext(_work),
                        CancellationToken.None);
            using var cancellation =
                new CancellationTokenSource();
            cancellation.Cancel();

            OperationResult<SazExportResult> result =
                await operation.ExecuteAsync(
                        new(
                            plan.Value,
                            new SazExportDestination.ToTarget(
                                target),
                            null),
                        new OperationContext(_work),
                        cancellation.Token);

            Assert.Equal(
                OperationOutcome.Cancelled,
                result.Outcome);
            Assert.False(File.Exists(path));
            Assert.Empty(
                Directory.GetFiles(
                    _directory,
                    ".xmat-*.tmp"));
        }

        [Fact]
        public async Task T9_AdmissionRefusesNonSazAndNonFileEntries()
        {
            var operation = new SazExportOperation();
            SazExportException extension =
                await Assert.ThrowsAsync<SazExportException>(
                    () => operation.AdmitTargetAsync(
                            Path.Join(
                                _directory,
                                "capture.xmatcap"),
                            new OperationContext(_work),
                            CancellationToken.None));
            string folder = Path.Join(
                _directory,
                "folder.saz");
            Directory.CreateDirectory(folder);
            SazExportException notFile =
                await Assert.ThrowsAsync<SazExportException>(
                    () => operation.AdmitTargetAsync(
                            folder,
                            new OperationContext(_work),
                            CancellationToken.None));

            Assert.Equal(
                SazExportCodes.SazRequired,
                extension.Code);
            Assert.Equal(
                SazExportCodes.NotAFile,
                notFile.Code);
        }

        public void Dispose() =>
            TestFolderCleanup.Delete(_directory);

        private async Task<OperationResult<SazExportResult>> RunAsync(
            string path,
            bool replace = false,
            SazExportTestHooks hooks = null)
        {
            var operation =
                hooks == null
                    ? new SazExportOperation()
                    : new SazExportOperation(hooks);
            OperationResult<SazExportPlan> plan =
                await PrepareAsync(operation);
            using CaptureArchiveTarget target =
                await operation.AdmitTargetAsync(
                        path,
                        new OperationContext(_work),
                        CancellationToken.None);
            CaptureArchiveReplaceDecision decision =
                replace
                    ? target.ConfirmReplace()
                    : null;
            return await ExecuteAsync(
                operation,
                plan.Value,
                target,
                decision);
        }

        private async Task<OperationResult<SazExportResult>> ExecuteAsync(
            SazExportOperation operation,
            SazExportPlan plan,
            CaptureArchiveTarget target,
            CaptureArchiveReplaceDecision decision) =>
            await operation.ExecuteAsync(
                    new(
                        plan,
                        new SazExportDestination.ToTarget(
                            target,
                            decision),
                        null),
                    new OperationContext(_work),
                    CancellationToken.None);

        private static async Task<OperationResult<SazExportPlan>> PrepareAsync(
            SazExportOperation operation)
        {
            var source =
                new SazExportTestSupport.ScriptedSource(
                    new[]
                    {
                        new SazExportKey(1, 1, 0),
                    },
                    (_, _) =>
                        SazExportTestSupport.Stored(
                            new XMAT.WebServiceCapture.Models
                                .ProxyConnectionModel(
                                    1,
                                    "200",
                                    DateTime.UnixEpoch,
                                    TimeSpan.Zero,
                                    "https",
                                    "example.test:443",
                                    "GET",
                                    "/",
                                    "GET / HTTP/1.1\r\n" +
                                    "Host: example.test\r\n\r\n",
                                    Array.Empty<byte>(),
                                    "HTTP/1.1 200 OK\r\n\r\n",
                                    Array.Empty<byte>(),
                                    "10.0.0.1")));
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                        new(
                            source,
                            SazExportKind.Plain,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        CancellationToken.None);
            Assert.True(prepared.Succeeded);
            return prepared;
        }
    }
}
