// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public interface IQueryDatabase
    {
        Task<IReadOnlyList<QueryTable>> GetQuerySchemaAsync(CancellationToken cancellationToken = default);
        Task<IQuerySession> CreateQuerySessionAsync(CancellationToken cancellationToken = default);
    }

    public interface IQuerySession : IAsyncDisposable
    {
        Guid SnapshotId { get; }
        DateTimeOffset CapturedAt { get; }
        Task<IQueryResult> ExecuteAsync(
            string sql,
            QueryExecutionOptions options,
            CancellationToken cancellationToken = default);
    }

    public interface IQueryResult : IAsyncDisposable
    {
        Guid Id { get; }
        Guid SnapshotId { get; }
        IReadOnlyList<QueryColumn> Columns { get; }
        long RowCount { get; }
        long StorageBytes { get; }
        Task<QueryPage> ReadPageAsync(
            long offset, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default);
        Task<QueryValueChunk> ReadValueAsync(
            long row, int column, long byteOffset, int maximumBytes, CancellationToken cancellationToken = default);
    }
}
