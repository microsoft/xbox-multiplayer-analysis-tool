// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDatabase
    {
        public Task<IReadOnlyList<QueryTable>> GetQuerySchemaAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() => SqLtQuerySupport.ReadSchema(_connection, cancellationToken), cancellationToken);

        public Task<IQuerySession> CreateQuerySessionAsync(CancellationToken cancellationToken = default) =>
            SqLtQuerySession.CreateAsync(_connection, cancellationToken);
    }
}
