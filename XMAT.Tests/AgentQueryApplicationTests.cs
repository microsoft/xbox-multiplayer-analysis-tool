// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentQueryApplicationTests
    {
        [Fact]
        public async Task ReleasingAResultWaitsForItsWholeExportRatherThanDisposingBetweenReads()
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("export lifetime");
            var result = new PausedResult();
            var queries = new TestQueries(result);
            var application = new AgentQueryApplication(runtime, queries, () => true);
            await Grant(runtime, owner, queries.SourceId);
            try
            {
                var resultId = await Run(application, runtime, owner, queries.SourceId);
                var exportId = await application.ExportAsync(owner, resultId, QueryExportFormat.Json, "export");
                Assert.True(runtime.Approve(exportId));
                await result.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var release = application.ReleaseAsync(owner, resultId);
                var delay = Task.Delay(100);
                Assert.Same(delay, await Task.WhenAny(release, delay));
                Assert.False(result.Disposed);
                result.Continue.TrySetResult();
                await runtime.WaitForCompletionAsync(owner, exportId).WaitAsync(TimeSpan.FromSeconds(5));
                await release.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(AgentOperationState.Succeeded, runtime.GetOperation(owner, exportId).State);
                Assert.Equal(2, result.PageReads);
                Assert.True(result.Disposed);
                Assert.Single(runtime.GetOwnedResources(), item => item.Resource.Kind == AgentResourceKind.Artifact);
            }
            finally
            {
                result.Continue.TrySetResult();
                await runtime.EndSessionAsync(owner);
                await application.DisposeResultsAsync();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionEndCancelsAnInProgressExportAndCleansBothResultAndPartialArtifact(bool revoked)
        {
            await using var runtime = new AgentRuntime();
            runtime.Enable();
            var owner = runtime.Connect("cancel export");
            var result = new PausedResult();
            var queries = new TestQueries(result);
            var application = new AgentQueryApplication(runtime, queries, () => true);
            await Grant(runtime, owner, queries.SourceId);
            try
            {
                var resultId = await Run(application, runtime, owner, queries.SourceId);
                var exportId = await application.ExportAsync(owner, resultId, QueryExportFormat.Csv, "export");
                Assert.True(runtime.Approve(exportId));
                await result.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await runtime.EndSessionAsync(owner, revoked).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(AgentOperationState.Cancelled, runtime.GetOperations().Single(item => item.Id == exportId).State);
                Assert.True(result.Disposed);
                Assert.Empty(runtime.GetOwnedResources());
                await application.DisposeResultsAsync();
            }
            finally
            {
                result.Continue.TrySetResult();
                await runtime.EndSessionAsync(owner);
                await application.DisposeResultsAsync();
            }
        }

        private static Task Grant(AgentRuntime runtime, AgentSession owner, Guid sourceId) =>
            runtime.SetPermissionsAsync(owner,
                [new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(sourceId)),
                 new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(sourceId))]);

        private static async Task<Guid> Run(
            AgentQueryApplication application, AgentRuntime runtime, AgentSession owner, Guid sourceId)
        {
            var operation = await application.RunAsync(owner, sourceId, "SELECT value", "query", null, null, default);
            await runtime.WaitForCompletionAsync(owner, operation).WaitAsync(TimeSpan.FromSeconds(5));
            return runtime.GetResult(owner, operation).GetProperty("resultId").GetGuid();
        }

        private sealed class TestQueries(PausedResult result) : ICaptureQueries
        {
            internal Guid SourceId { get; } = Guid.NewGuid();
            public Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<QuerySourceDescriptor>>([new(SourceId, QuerySourceKind.Active, "test")]);
            public Task<IReadOnlyList<QueryTable>> GetSchemaAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<QueryTable>>([]);
            public Task<IQuerySession> CreateSessionAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IQuerySession>(new TestSession(result));
        }

        private sealed class TestSession(PausedResult result) : IQuerySession
        {
            public Guid SnapshotId => result.SnapshotId;
            public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;
            public Task<IQueryResult> ExecuteAsync(
                string sql, QueryExecutionOptions options, CancellationToken cancellationToken = default) =>
                Task.FromResult<IQueryResult>(result);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class PausedResult : IQueryResult
        {
            private int _disposed;
            private int _pageReads;
            internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            internal int PageReads => Volatile.Read(ref _pageReads);
            public Guid Id { get; } = Guid.NewGuid();
            public Guid SnapshotId { get; } = Guid.NewGuid();
            public IReadOnlyList<QueryColumn> Columns { get; } = [new(0, "value", null, null, null)];
            public long RowCount => 2;
            public long StorageBytes => 16384;

            public async Task<QueryPage> ReadPageAsync(
                long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
            {
                ReadStarted.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(Disposed, this);
                Interlocked.Increment(ref _pageReads);
                return new(offset, 2, Columns,
                    [new QueryRow(offset, [new QueryCell(QueryValueKind.Integer, offset + 1, 8, false)])], offset == 0);
            }

            public Task<QueryValueChunk> ReadValueAsync(
                long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _disposed, 1);
                return ValueTask.CompletedTask;
            }
        }
    }
}
