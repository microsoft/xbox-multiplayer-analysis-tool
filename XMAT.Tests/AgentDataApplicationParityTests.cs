// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentDataApplicationParityTests
    {
        [Fact]
        public async Task RawDetailsRequireBothExactGrantsAndConnectionOwnershipAndBindCursorsToTheItem()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("owner");
            var borrower = runtime.Connect("borrower");
            var investigation = new Investigation();
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: false, new(responseRows: 1, responseBytes: 4096));
            var result = await Run(application, runtime, owner, investigation.SourceId);
            var denied = await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default));
            Assert.Equal(AgentAccessError.PermissionDenied, denied.Error);
            await runtime.SetPermissionsAsync(owner,
                [Summary(investigation.SourceId), Raw(Guid.NewGuid())], new(responseRows: 1, responseBytes: 4096));
            Assert.Equal(AgentAccessError.PermissionDenied, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default))).Error);
            await Grant(runtime, owner, investigation.SourceId, raw: true, new(responseRows: 1, responseBytes: 4096));
            var page = await application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default);
            Assert.Equal("private", page.GetProperty("rows")[0].GetProperty("cells")[1].GetProperty("value").GetString());
            Assert.Equal(investigation.SourceId, page.GetProperty("sourceId").GetGuid());
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 4096);
            var cursor = application.ReadPage(owner, result, CaptureDataSection.Records, null).GetProperty("nextCursor").GetString();
            Assert.Equal(AgentAccessError.OperationNotFound, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, cursor, default))).Error);
            await Grant(runtime, borrower, investigation.SourceId, raw: true);
            Assert.Equal(AgentAccessError.OperationNotFound, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(borrower, result, CaptureDetailKind.Record, 1, null, default))).Error);
            await application.ReleaseAsync(owner, result);
            Assert.True(investigation.Instances.Single().Disposed);
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task ReleasingAResultDrainsAnAlreadyAdmittedRawReaderInsteadOfClosingItsSnapshot()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("reader drain");
            var investigation = new Investigation { BlockReads = true };
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: true);
            var result = await Run(application, runtime, owner, investigation.SourceId);
            var read = application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default);
            var details = investigation.Instances.Single();
            await details.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var release = application.ReleaseAsync(owner, result);
            await Task.Delay(50);
            Assert.False(release.IsCompleted);
            Assert.False(details.Disposed);
            details.Continue.TrySetResult();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(details.Disposed);
            Assert.False(details.DisposedDuringRead);
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task ReleaseDuringInitialRawBindingCannotStrandALateOwnedSnapshotResource()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("raw binding race");
            var investigation = new Investigation();
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: true);
            var result = await Run(application, runtime, owner, investigation.SourceId);
            using var started = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var submitting = new ThreadLocal<bool>();
            var paused = 0;
            EventHandler handler = (_, _) =>
            {
                if (submitting.Value)
                    return;
                if (runtime.GetOperations().Any(operation => operation.Action == "data.bind_details" &&
                    operation.State == AgentOperationState.Running) && Interlocked.Exchange(ref paused, 1) == 0)
                {
                    started.Set();
                    resume.Wait(TimeSpan.FromSeconds(10));
                }
            };
            runtime.Changed += handler;
            try
            {
                submitting.Value = true;
                var read = application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default);
                submitting.Value = false;
                Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))));
                var release = application.ReleaseAsync(owner, result);
                await Task.Delay(50);
                Assert.False(release.IsCompleted);
                resume.Set();
                try
                { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (AgentAccessException exception)
                { Assert.Contains(exception.Error, new[] { AgentAccessError.OperationNotFound, AgentAccessError.SourceChanged }); }
                await release.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(investigation.Instances.Single().Disposed);
                Assert.Empty(runtime.GetOwnedResources());
            }
            finally { resume.Set(); runtime.Changed -= handler; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DisconnectOrRevocationCancelsAndDrainsRawReadersBeforeDisposal(bool revokeSession)
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("raw reader cleanup");
            var investigation = new Investigation { BlockReads = true };
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: true);
            var result = await Run(application, runtime, owner, investigation.SourceId);
            var read = application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default);
            var details = investigation.Instances.Single();
            await details.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.EndSessionAsync(owner, revokeSession).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(() => read);
            Assert.True(details.Disposed);
            Assert.False(details.DisposedDuringRead);
            Assert.Empty(runtime.GetOwnedResources());
            await application.DisposeResultsAsync();
        }

        [Fact]
        public async Task RevokingOnlyRawAccessKeepsSafeSummariesButRegrantCannotResurrectTheOriginalDetails()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("raw invalidation");
            var investigation = new Investigation();
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: true);
            var result = await Run(application, runtime, owner, investigation.SourceId);
            await application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default);
            await Grant(runtime, owner, investigation.SourceId, raw: false);
            Assert.True(investigation.Instances.Single().Disposed);
            Assert.Equal(2, application.ReadPage(owner, result, CaptureDataSection.Records, null).GetProperty("rows").GetArrayLength());
            Assert.Single(runtime.GetOwnedResources());
            await Grant(runtime, owner, investigation.SourceId, raw: true);
            Assert.Equal(AgentAccessError.OperationNotFound, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default))).Error);
            await application.ReleaseAsync(owner, result);
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task FailedResultReceiptPublicationDisposesTheUnannouncedSnapshotAndRestoresAdmission()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("small receipt");
            var investigation = new Investigation();
            var application = new AgentDataApplication(runtime, investigation, () => true);
            await Grant(runtime, owner, investigation.SourceId, raw: false, new(responseBytes: 256, maximumQueryResults: 1));
            foreach (var key in new[] { "first", "second" })
            {
                var operation = application.Run(owner, investigation.SourceId, CaptureDataKind.WebProxy, null, key, false, null, null);
                await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(AgentAccessError.LimitExceeded, runtime.GetOperation(owner, operation).Error);
                Assert.Empty(runtime.GetOwnedResources());
            }
            Assert.Equal(2, investigation.Instances.Count);
            Assert.All(investigation.Instances, details => Assert.True(details.Disposed));
        }

        [Fact]
        public async Task ApprovedReportPublicationPinsTheWholeResultAndReplayNeverPublishesTwice()
        {
            using var directory = new InvestigationDirectory();
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("report export");
            var sourceId = Guid.NewGuid();
            var files = new InvestigationReportFiles(Path.Combine(directory.Path, "source"), sourceId);
            var investigation = new Investigation(sourceId) { Files = files };
            var exports = new PausedExports();
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var application = new AgentDataApplication(runtime, investigation, () => true, exports);
            await GrantExport(runtime, owner, sourceId, folder.Id);
            var result = await Run(application, runtime, owner, sourceId, analyze: true);
            var operation = await application.ExportReportAsync(owner, result, folder.Id, "report", "export", default);
            Assert.Equal(AgentOperationState.AwaitingApproval, runtime.GetOperation(owner, operation).State);
            Assert.False(Directory.Exists(Path.Combine(destinationPath, "report")));
            Assert.True(runtime.Approve(operation));
            await exports.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var release = application.ReleaseAsync(owner, result);
            await Task.Delay(50);
            Assert.False(release.IsCompleted);
            Assert.False(investigation.Instances.Single().Disposed);
            exports.Continue.TrySetResult();
            await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, operation).State);
            var receipt = runtime.GetResult(owner, operation);
            Assert.True(receipt.GetProperty("published").GetBoolean());
            Assert.Equal(files.Report.SnapshotId, receipt.GetProperty("snapshotId").GetGuid());
            Assert.True(files.Disposed);
            Assert.True(investigation.Instances.Single().Disposed);
            Assert.Equal(operation, await application.ExportReportAsync(owner, result, folder.Id, "report", "export", default));
            Assert.Equal(AgentAccessError.IdempotencyConflict, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ExportReportAsync(owner, result, folder.Id, "different", "export", default))).Error);
            Assert.Equal(1, exports.SaveCount);
            await runtime.EndSessionAsync(owner);
            await application.DisposeResultsAsync();
            Assert.True(Directory.Exists(Path.Combine(destinationPath, "report")));
            Assert.Empty(runtime.GetOwnedResources());
        }

        [Fact]
        public async Task PublishedReportCleanupFailuresRemainOwnedAndDoNotConsumeLaterExportAdmission()
        {
            using var directory = new InvestigationDirectory();
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("report cleanup");
            var sourceId = Guid.NewGuid();
            var original = new InvestigationReportFiles(Path.Combine(directory.Path, "source"), sourceId);
            var files = new FailingReportFiles(original);
            var investigation = new Investigation(sourceId) { Files = files };
            var exports = new PausedExports { PublishDirectly = true };
            exports.Continue.TrySetResult();
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var application = new AgentDataApplication(runtime, investigation, () => true, exports);
            await GrantExport(runtime, owner, sourceId, folder.Id);
            await runtime.SetPermissionsAsync(owner, runtime.GetSession(owner).Permissions,
                new AgentLimits(maximumQueryResults: 1));
            try
            {
                var result = await Run(application, runtime, owner, sourceId, analyze: true);
                for (var index = 0; index < 5; index++)
                {
                    var operation = await application.ExportReportAsync(owner, result, folder.Id,
                        $"report{index}", $"export{index}", default);
                    Assert.True(runtime.Approve(operation));
                    await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, operation).State);
                    var receipt = runtime.GetResult(owner, operation);
                    Assert.True(receipt.GetProperty("published").GetBoolean());
                    Assert.True(receipt.GetProperty("cleanupRequired").GetBoolean());
                }
                Assert.Equal(5, runtime.GetOwnedResources().Count(item => item.Resource.Kind == AgentResourceKind.Artifact));
                Assert.Equal(5, exports.SaveCount);
            }
            finally
            {
                files.FailDisposal = false;
                await runtime.EndSessionAsync(owner);
                await application.DisposeResultsAsync();
            }
            Assert.Empty(runtime.GetOwnedResources());
            Assert.True(original.Disposed);
            Assert.Equal(5, Directory.EnumerateDirectories(destinationPath).Count());
        }

        [Fact]
        public async Task UnpublishedReportResidueKeepsItsProtectedReceiptAndOwnedCleanup()
        {
            using var directory = new InvestigationDirectory();
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("report residue");
            var sourceId = Guid.NewGuid();
            var files = new InvestigationReportFiles(Path.Combine(directory.Path, "source"), sourceId);
            var investigation = new Investigation(sourceId) { Files = files };
            var exports = new PausedExports();
            exports.Continue.TrySetResult();
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var application = new AgentDataApplication(runtime, investigation, () => true, exports);
            FileStream locked = null;
            exports.BeforePublish = () =>
            {
                var staging = Directory.GetDirectories(destinationPath).Single();
                locked = File.Open(Path.Combine(staging, "calls.js"), FileMode.Open, FileAccess.Read, FileShare.None);
                throw new IOException("The report could not be published.");
            };
            try
            {
                await GrantExport(runtime, owner, sourceId, folder.Id);
                var result = await Run(application, runtime, owner, sourceId, analyze: true);
                var operation = await application.ExportReportAsync(owner, result, folder.Id, "report", "residue", default);
                Assert.True(runtime.Approve(operation));
                await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AgentOperationState.RecoveryRequired, runtime.GetOperation(owner, operation).State);
                var receipt = runtime.GetResult(owner, operation);
                Assert.False(receipt.GetProperty("published").GetBoolean());
                Assert.True(receipt.GetProperty("cleanupRequired").GetBoolean());
                Assert.Single(runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Artifact);
                Assert.False(Directory.Exists(Path.Combine(destinationPath, "report")));
            }
            finally
            {
                locked?.Dispose();
                await runtime.EndSessionAsync(owner);
                await application.DisposeResultsAsync();
            }
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Empty(Directory.GetFileSystemEntries(destinationPath));
        }

        [Fact]
        public async Task UncertainPublicationReceiptSurvivesASecondPrivateCleanupFailure()
        {
            using var directory = new InvestigationDirectory();
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("uncertain report cleanup");
            var sourceId = Guid.NewGuid();
            var files = new FailingReportFiles(new InvestigationReportFiles(Path.Combine(directory.Path, "source"), sourceId));
            var investigation = new Investigation(sourceId) { Files = files };
            var exports = new PausedExports();
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var folder = await exports.RegisterFolderAsync(destinationPath);
            using var pinned = new WindowsExportFolder(destinationPath);
            var residue = pinned.CreateTemporaryDirectory();
            exports.Failure = new AnalysisReportExportException(AnalysisReportExportFailureKind.PublishFailed,
                "Publication could not be verified.", publicationState: AnalysisReportPublicationState.Unknown)
            { ResiduePath = residue.Path, OwnedResidue = residue };
            var application = new AgentDataApplication(runtime, investigation, () => true, exports);
            try
            {
                await GrantExport(runtime, owner, sourceId, folder.Id);
                var result = await Run(application, runtime, owner, sourceId, analyze: true);
                var operation = await application.ExportReportAsync(owner, result, folder.Id, "report", "unknown", default);
                Assert.True(runtime.Approve(operation));
                await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(AgentOperationState.RecoveryRequired, runtime.GetOperation(owner, operation).State);
                var receipt = runtime.GetResult(owner, operation);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, receipt.GetProperty("published").ValueKind);
                Assert.Equal("Unknown", receipt.GetProperty("publicationState").GetString());
                Assert.Equal(Path.GetFileName(residue.Path), receipt.GetProperty("residueDirectory").GetString());
                Assert.True(receipt.GetProperty("cleanupRequired").GetBoolean());
                Assert.Equal(2, runtime.GetOwnedResources().Count(item => item.Resource.Kind == AgentResourceKind.Artifact));
            }
            finally
            {
                files.FailDisposal = false;
                await runtime.EndSessionAsync(owner);
                await application.DisposeResultsAsync();
            }
            Assert.Empty(runtime.GetOwnedResources());
            Assert.Empty(Directory.GetFileSystemEntries(destinationPath));
        }

        [Fact]
        public async Task RevokingRawDuringReportCopyCancelsUnpublishedOutputAndInvalidatesItsDetails()
        {
            using var directory = new InvestigationDirectory();
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("report cancellation");
            var sourceId = Guid.NewGuid();
            var files = new InvestigationReportFiles(Path.Combine(directory.Path, "source"), sourceId);
            var investigation = new Investigation(sourceId) { Files = files };
            var exports = new PausedExports();
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var application = new AgentDataApplication(runtime, investigation, () => true, exports);
            await GrantExport(runtime, owner, sourceId, folder.Id);
            var result = await Run(application, runtime, owner, sourceId, analyze: true);
            var operation = await application.ExportReportAsync(owner, result, folder.Id, "report", "export", default);
            Assert.True(runtime.Approve(operation));
            await exports.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Grant(runtime, owner, sourceId, raw: false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AgentOperationState.Cancelled, runtime.GetOperation(owner, operation).State);
            Assert.Empty(Directory.GetFileSystemEntries(destinationPath));
            Assert.True(files.Disposed);
            Assert.True(investigation.Instances.Single().Disposed);
            await Grant(runtime, owner, sourceId, raw: true);
            Assert.Equal(AgentAccessError.OperationNotFound, (await Assert.ThrowsAsync<AgentAccessException>(() =>
                application.ReadDetailsAsync(owner, result, CaptureDetailKind.Record, 1, null, default))).Error);
            await application.ReleaseAsync(owner, result);
            Assert.Empty(runtime.GetOwnedResources());
        }

        private static AgentPermission Summary(Guid source) => new(AgentCapability.SummaryRead, AgentScope.Source(source));
        private static AgentPermission Raw(Guid source) => new(AgentCapability.RawDataRead, AgentScope.Source(source));
        private static Task Grant(AgentRuntime runtime, AgentSession owner, Guid source, bool raw, AgentLimits limits = null) =>
            runtime.SetPermissionsAsync(owner, raw ? [Summary(source), Raw(source)] : [Summary(source)], limits);

        private static Task GrantExport(AgentRuntime runtime, AgentSession owner, Guid source, Guid folder)
        {
            Assert.True(Enum.TryParse<AgentCapability>("AnalysisReportExport", out var capability),
                "The host must register the distinct exact-source report export capability.");
            return runtime.SetPermissionsAsync(owner,
                [Summary(source), Raw(source), new(capability, AgentScope.Source(source)),
                    new(AgentCapability.FileWrite, AgentScope.ExportFolder(folder))]);
        }

        private static async Task<Guid> Run(
            AgentDataApplication application, AgentRuntime runtime, AgentSession owner, Guid sourceId, bool analyze = false)
        {
            var operation = application.Run(owner, sourceId, CaptureDataKind.WebProxy, null, Guid.NewGuid().ToString("N"), analyze, null, null);
            await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, operation).State);
            return runtime.GetResult(owner, operation).GetProperty("resultId").GetGuid();
        }

        private sealed class Investigation(Guid? sourceId = null) : ICaptureInvestigation
        {
            internal Guid SourceId { get; } = sourceId ?? Guid.NewGuid();
            internal bool BlockReads { get; set; }
            internal ICaptureReportFiles Files { get; set; }
            internal List<Details> Instances { get; } = new();
            public Task<IReadOnlyList<CaptureAnalyzerDescriptor>> GetAnalyzersAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<CaptureAnalyzerDescriptor>>([]);
            public Task<CaptureDataResult> SearchAsync(
                Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
                CancellationToken cancellationToken = default) => Create(kind, false);
            public Task<CaptureDataResult> AnalyzeAsync(
                Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
                CancellationToken cancellationToken = default) => Create(kind, true);

            private Task<CaptureDataResult> Create(CaptureDataKind kind, bool analyze)
            {
                var details = new Details(BlockReads, Files);
                Instances.Add(details);
                return Task.FromResult(new CaptureDataResult(Guid.NewGuid(), SourceId, details.SnapshotId, DateTimeOffset.UtcNow, kind,
                    analyze, 2, 2, 0, 0, null, null,
                    Enumerable.Range(1, 2).Select(id => new CaptureRecordSummary(id, "device", DateTimeOffset.UtcNow,
                        id, "api.xboxlive.com", "GET", 200, 1, null, null, null, null, false, false)).ToArray(), [], [], new Dictionary<long, IReadOnlyList<CaptureEvidence>>())
                { Details = details });
            }
        }

        private sealed class Details(bool block, ICaptureReportFiles files) : ICaptureDataDetails
        {
            private int _disposed;
            private int _readers;
            internal Guid SnapshotId { get; } = files?.Report.SnapshotId ?? Guid.NewGuid();
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            internal bool DisposedDuringRead { get; private set; }
            public CaptureCompletedReportDescriptor Report => files?.Report;

            public async Task<QueryPage> ReadPageAsync(
                CaptureDetailTarget target, long offset, int maximumRows, int maximumBytes, QueryExecutionOptions options,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _readers);
                try
                {
                    Started.TrySetResult();
                    if (block)
                        await Continue.Task.WaitAsync(cancellationToken);
                    ObjectDisposedException.ThrowIf(Disposed, this);
                    await using var table = new CaptureDetailTable(SnapshotId, ["field", "value"], [["test", "private"]]);
                    return await table.ReadPageAsync(offset, maximumRows, maximumBytes, cancellationToken);
                }
                finally { Interlocked.Decrement(ref _readers); }
            }

            public Task<QueryValueChunk> ReadValueAsync(
                CaptureDetailTarget target, long row, int column, long byteOffset, int maximumBytes, QueryExecutionOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new QueryValueChunk(QueryValueKind.Text, 0, 7, Encoding.UTF8.GetBytes("private"), false));
            public Task<ICaptureReportFiles> CreateReportFilesAsync(long maximumBytes, CancellationToken cancellationToken = default) =>
                Task.FromResult<ICaptureReportFiles>(files);
            public Task<IReadOnlyList<CaptureReportArtifact>> GetReportManifestAsync(long maximumBytes, CancellationToken cancellationToken = default) =>
                Task.FromResult(files.Manifest);
            public ValueTask DisposeAsync()
            {
                DisposedDuringRead |= Volatile.Read(ref _readers) != 0;
                Interlocked.Exchange(ref _disposed, 1);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FailingReportFiles(ICaptureReportFiles original) : ICaptureReportFiles
        {
            public string SourceDirectory => original.SourceDirectory;
            public CaptureCompletedReportDescriptor Report => original.Report;
            public IReadOnlyList<CaptureReportArtifact> Manifest => original.Manifest;
            internal bool FailDisposal { get; set; } = true;
            public ValueTask DisposeAsync() => FailDisposal
                ? ValueTask.FromException(new IOException("The temporary report files are locked."))
                : original.DisposeAsync();
        }

        private sealed class PausedExports : ICaptureFileExports
        {
            private readonly CaptureFileExports _files = new();
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int SaveCount { get; private set; }
            internal Action BeforePublish { get; set; }
            internal AnalysisReportExportException Failure { get; set; }
            internal bool PublishDirectly { get; set; }
            public Task<ExportFolderDescriptor> RegisterFolderAsync(string path, CancellationToken cancellationToken = default) =>
                _files.RegisterFolderAsync(path, cancellationToken);
            public IReadOnlyList<ExportFolderDescriptor> GetFolders() => _files.GetFolders();
            public Task ValidateReportAsync(CaptureReportDestination destination, CancellationToken cancellationToken = default) =>
                _files.ValidateReportAsync(destination, cancellationToken);
            public Task<CaptureReportExportReceipt> SaveReportAsync(
                ICaptureReportFiles files, CaptureReportDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
            {
                SaveCount++;
                if (Failure != null)
                    return Task.FromException<CaptureReportExportReceipt>(Failure);
                if (PublishDirectly)
                    return PublishDirectlyAsync(files, destination, beforePublish, cancellationToken);
                return _files.SaveReportAsync(files, destination, maximumBytes, async token =>
                {
                    Started.TrySetResult();
                    await Continue.Task.WaitAsync(token);
                    await beforePublish(token);
                    BeforePublish?.Invoke();
                }, cancellationToken);
            }
            private async Task<CaptureReportExportReceipt> PublishDirectlyAsync(
                ICaptureReportFiles files, CaptureReportDestination destination,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
            {
                await beforePublish(cancellationToken);
                // Keep adapter-cleanup tests independent of the separately tested native directory rename.
                var folder = GetFolders().Single(item => item.Id == destination.FolderId);
                var path = Path.Combine(folder.Path, destination.DirectoryName);
                Directory.CreateDirectory(path);
                foreach (var artifact in files.Manifest)
                    File.Copy(Path.Combine(files.SourceDirectory, artifact.RelativePath), Path.Combine(path, artifact.RelativePath));
                return new(files.Report.Id, files.Report.SourceId, files.Report.SnapshotId,
                    destination.FolderId, destination.DirectoryName, files.Manifest.Sum(item => item.Length), files.Manifest.Count, true);
            }
            public Task ValidateAsync(QueryFileDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task ValidateCaptureAsync(CaptureFileDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task ValidateScriptAsync(ScriptFileDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ScriptFileExportReceipt> SaveScriptAsync(
                CaptureScriptSource source, ScriptFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<long> SaveCaptureAsync(
                ICaptureArchive archive, CaptureFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<QueryFileExportReceipt> SaveQueryAsync(
                IQueryResult result, QueryFileDestination destination, long maximumBytes,
                Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
