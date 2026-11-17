// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public sealed record CaptureOpenReceipt(
        Guid SourceId, CaptureInputKind InputKind, string InputSha256, long InputBytes,
        int AddedContexts, int UpdatedContexts, bool Published, bool ViewsNotified, bool ActiveDataChanged = false);

    public interface ICaptureReadonlySources : IAsyncDisposable
    {
        Task<CaptureOpenReceipt> OpenInputAsync(
            Guid inputId, CaptureLoadState expectedState, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default);
    }
}
