// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureFileFailure
    {
        InvalidPath, FolderChanged, DestinationExists, ProtectedSource, Unavailable, InputChanged, InvalidCapture,
        SchemaLimit, LiveDatabase, UnsupportedLocation
    }

    public sealed class CaptureFileException(CaptureFileFailure failure)
        : InvalidOperationException(failure.ToString())
    {
        public CaptureFileFailure Failure { get; } = failure;
    }

    public sealed record ExportFolderDescriptor(Guid Id, string Path);
    public sealed record QueryFileDestination(Guid FolderId, string FileName, QueryExportFormat Format);
    public sealed record QueryFileExportReceipt(Guid FolderId, string FileName, long Length, long RowCount, bool Published);
    public sealed record ScriptFileDestination(Guid FolderId, string FileName);
    public sealed record ScriptFileExportReceipt(
        Guid CaptureId, Guid ScriptId, long Revision, string SourceHash,
        Guid FolderId, string FileName, long Length, bool Published);

    public interface ICaptureFileExports
    {
        Task<ExportFolderDescriptor> RegisterFolderAsync(string path, CancellationToken cancellationToken = default);
        IReadOnlyList<ExportFolderDescriptor> GetFolders();
        Task ValidateAsync(QueryFileDestination destination, CancellationToken cancellationToken = default);
        Task ValidateCaptureAsync(CaptureFileDestination destination, CancellationToken cancellationToken = default);
        Task ValidateScriptAsync(ScriptFileDestination destination, CancellationToken cancellationToken = default);
        Task ValidateReportAsync(CaptureReportDestination destination, CancellationToken cancellationToken = default) =>
            Task.FromException(new CaptureFileException(CaptureFileFailure.Unavailable));
        Task<CaptureReportExportReceipt> SaveReportAsync(
            ICaptureReportFiles files, CaptureReportDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
            Task.FromException<CaptureReportExportReceipt>(new CaptureFileException(CaptureFileFailure.Unavailable));
        Task<ScriptFileExportReceipt> SaveScriptAsync(
            CaptureScriptSource source,
            ScriptFileDestination destination,
            long maximumBytes,
            Func<CancellationToken, Task> beforePublish,
            CancellationToken cancellationToken = default);
        Task<long> SaveCaptureAsync(
            ICaptureArchive archive,
            CaptureFileDestination destination,
            long maximumBytes,
            Func<CancellationToken, Task> beforePublish,
            CancellationToken cancellationToken = default);
        Task<QueryFileExportReceipt> SaveQueryAsync(
            IQueryResult result,
            QueryFileDestination destination,
            long maximumBytes,
            Func<CancellationToken, Task> beforePublish,
            CancellationToken cancellationToken = default);
    }
}
