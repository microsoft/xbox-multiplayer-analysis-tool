// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public sealed record CaptureImportTarget(Guid SourceId, string FileName, string DeviceName, Guid? CaptureId, string Revision);
    public sealed record CaptureImportReceipt(
        Guid SourceId, Guid CaptureId, string DeviceName, int RecordCount, bool AddedContext,
        bool Published, bool ViewsNotified);

    public interface ICaptureFileImports : IAsyncDisposable
    {
        Task<CaptureImportTarget> GetTargetAsync(string fileName, CancellationToken cancellationToken = default);
        Task<CaptureImportReceipt> ImportDesktopAsync(string path, CancellationToken cancellationToken = default);
        Task<CaptureImportReceipt> ImportInputAsync(
            Guid inputId, CaptureImportTarget expectedTarget, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default);
    }

    public interface ICaptureEtlImports : ICaptureFileImports { }
}
