// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public sealed record CaptureLoadState(Guid? LoadedSourceId, string Revision);
    public sealed record CaptureLoadReceipt(
        Guid SourceId, int AddedContexts, int UpdatedContexts, bool Published, bool ViewsNotified);

    public interface ICaptureFileLoads : IAsyncDisposable
    {
        bool IsLoading { get; }
        Task<CaptureLoadState> GetStateAsync(CancellationToken cancellationToken = default);
        Task<CaptureLoadReceipt> LoadDesktopAsync(string path, CancellationToken cancellationToken = default);
        Task<CaptureLoadReceipt> LoadInputAsync(
            Guid inputId,
            CaptureLoadState expectedState,
            long maximumBytes,
            Func<CancellationToken, Task> beforePublish,
            CancellationToken cancellationToken = default);
    }
}
