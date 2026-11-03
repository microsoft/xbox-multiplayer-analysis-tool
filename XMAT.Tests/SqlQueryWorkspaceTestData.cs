// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    internal static class SqlQueryWorkspaceTestData
    {
        internal sealed class Sources : ICaptureQueries
        {
            internal Guid ActiveId { get; } = Guid.NewGuid();
            internal Guid LoadedId { get; } = Guid.NewGuid();
            internal IReadOnlyList<QuerySourceDescriptor> Items { get; set; }
            internal IReadOnlyList<QueryTable> Schema { get; set; } =
            [
                new("A table", "table",
                [
                    new(0, "id", "INTEGER", false, true, false),
                    new(1, "some value", "TEXT", true, false, false)
                ])
            ];
            internal Func<Guid, CancellationToken, Task<IReadOnlyList<QueryTable>>> ReadSchema { get; set; }
            internal Func<string, QueryExecutionOptions, CancellationToken, Task<IQueryResult>> Execute { get; set; }
            internal List<string> ExecutedSql { get; } = [];
            internal List<Guid> SessionSources { get; } = [];
            internal int DisposedSessions { get; private set; }
            internal int SchemaReads { get; private set; }
            internal QueryExecutionOptions LastOptions { get; private set; }

            internal Sources()
            {
                Items = [new(ActiveId, QuerySourceKind.Active, "active"), new(LoadedId, QuerySourceKind.Loaded, "loaded.xmatcap")];
                Execute = (_, _, _) => Task.FromResult<IQueryResult>(new Result(1));
            }

            public Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Items);
            }
            public Task<IReadOnlyList<QueryTable>> GetSchemaAsync(Guid sourceId, CancellationToken cancellationToken = default)
            {
                RequireSource(sourceId);
                SchemaReads++;
                return ReadSchema?.Invoke(sourceId, cancellationToken) ?? Task.FromResult(Schema);
            }
            public Task<IQuerySession> CreateSessionAsync(Guid sourceId, CancellationToken cancellationToken = default)
            {
                RequireSource(sourceId);
                cancellationToken.ThrowIfCancellationRequested();
                SessionSources.Add(sourceId);
                return Task.FromResult<IQuerySession>(new Session(this));
            }
            private void RequireSource(Guid id)
            {
                if (!Items.Any(source => source.Id == id))
                    throw new QueryException(QueryError.SourceChanged, "exact stale source");
            }
            private sealed class Session(Sources owner) : IQuerySession
            {
                public Guid SnapshotId { get; } = Guid.NewGuid();
                public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;
                public Task<IQueryResult> ExecuteAsync(
                    string sql, QueryExecutionOptions options, CancellationToken cancellationToken = default)
                {
                    owner.ExecutedSql.Add(sql);
                    owner.LastOptions = options;
                    return owner.Execute(sql, options, cancellationToken);
                }
                public ValueTask DisposeAsync()
                {
                    owner.DisposedSessions++;
                    return ValueTask.CompletedTask;
                }
            }
        }

        internal sealed class Result(long count) : IQueryResult
        {
            internal Func<long, QueryCell[]> Cells { get; set; } =
                row => [new(QueryValueKind.Integer, row, 8, false)];
            internal Func<long, int, int, CancellationToken, Task<QueryPage>> PageReader { get; set; }
            internal Func<long, int, long, int, CancellationToken, Task<QueryValueChunk>> ValueReader { get; set; }
            internal Dictionary<(long Row, int Column), byte[]> FullValues { get; } = [];
            internal int ChunkSize { get; set; } = 64 * 1024;
            internal int PageReads { get; private set; }
            internal int ValueReads { get; private set; }
            internal int DisposeCalls { get; private set; }
            internal bool Disposed => DisposeCalls != 0;
            public Guid Id { get; } = Guid.NewGuid();
            public Guid SnapshotId { get; } = Guid.NewGuid();
            public IReadOnlyList<QueryColumn> Columns { get; set; } = [new(0, "value", "INTEGER", null, null)];
            public long RowCount => count;
            public long StorageBytes => count * 16;
            public Task<QueryPage> ReadPageAsync(
                long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                PageReads++;
                if (PageReader != null)
                    return PageReader(offset, maximumRows, maximumBytes, cancellationToken);
                return Task.FromResult(Page(offset, maximumRows));
            }
            internal QueryPage Page(long offset, int maximumRows) => new(offset, count, Columns,
                Enumerable.Range(0, (int)Math.Min(maximumRows, count - offset))
                    .Select(index => new QueryRow(offset + index, Cells(offset + index))).ToArray(),
                offset + maximumRows < count);
            public Task<QueryValueChunk> ReadValueAsync(
                long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                ValueReads++;
                if (ValueReader != null)
                    return ValueReader(row, column, byteOffset, maximumBytes, cancellationToken);
                var cell = Cells(row)[column];
                var bytes = FullValues.GetValueOrDefault((row, column)) ?? (cell.Kind == QueryValueKind.Blob
                    ? (byte[])cell.Value : Encoding.UTF8.GetBytes((string)cell.Value));
                var length = (int)Math.Min(Math.Min(maximumBytes, ChunkSize), bytes.Length - byteOffset);
                return Task.FromResult(new QueryValueChunk(cell.Kind, byteOffset, bytes.Length,
                    bytes.AsSpan((int)byteOffset, length).ToArray(), byteOffset + length < bytes.Length));
            }
            public ValueTask DisposeAsync()
            {
                DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }

        internal sealed class DirectoryFixture : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(
                Environment.CurrentDirectory, "SqlWorkspaceTests." + Guid.NewGuid().ToString("N"));
            internal DirectoryFixture() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
