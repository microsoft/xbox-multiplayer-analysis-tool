// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum QuerySourceKind { Active, Loaded }
    public sealed record QuerySourceDescriptor(Guid Id, QuerySourceKind Kind, string DisplayName);

    public interface ICaptureQueries
    {
        Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<QueryTable>> GetSchemaAsync(Guid sourceId, CancellationToken cancellationToken = default);
        Task<IQuerySession> CreateSessionAsync(Guid sourceId, CancellationToken cancellationToken = default);
    }
}
