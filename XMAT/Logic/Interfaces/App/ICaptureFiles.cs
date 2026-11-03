// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureArchiveFormat { CaptureDatabase, FiddlerSaz, SanitizedFiddlerSaz }
    public sealed record CaptureSanitization(
        int Sessions, int RedactedValues, int OmittedBodies, int OmittedHeaders, int WorkBudgetOmissions);
    public sealed record CaptureFileDestination(Guid FolderId, string FileName, CaptureArchiveFormat Format);
    public sealed record CaptureArchiveReceipt(
        Guid SourceId, CaptureArchiveFormat Format, DateTimeOffset CapturedAt, long Length,
        string DeviceName = null, CaptureSanitization Sanitization = null);

    public interface ICaptureArchive : IAsyncDisposable
    {
        CaptureArchiveReceipt Receipt { get; }
        Task CopyToAsync(Stream destination, long maximumBytes, CancellationToken cancellationToken = default);
    }

    public interface ICaptureFiles
    {
        Task<ICaptureArchive> CreateArchiveAsync(
            Guid sourceId,
            CaptureArchiveFormat format,
            long maximumBytes,
            CancellationToken cancellationToken = default);
        Task SaveDesktopAsync(
            Guid sourceId,
            CaptureArchiveFormat format,
            string path,
            CancellationToken cancellationToken = default);
        Task<ICaptureArchive> CreateSazArchiveAsync(
            Guid sourceId,
            CaptureArchiveFormat format,
            string deviceName,
            long maximumBytes,
            CancellationToken cancellationToken = default);
    }
}
