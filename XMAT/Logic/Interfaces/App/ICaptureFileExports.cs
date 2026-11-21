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
        SchemaLimit, LiveDatabase, UnsupportedLocation, PublicationUncertain, UnsupportedVersion
    }

    public sealed class CaptureFileException(
        CaptureFileFailure failure, string recoveryTemporaryPath = null, string recoveryDestinationPath = null,
        int? nativeErrorCode = null)
        : InvalidOperationException(failure.ToString())
    {
        public CaptureFileFailure Failure { get; } = failure;
        public string RecoveryTemporaryPath { get; } = recoveryTemporaryPath;
        public string RecoveryDestinationPath { get; } = recoveryDestinationPath;
        public int? NativeErrorCode { get; } = nativeErrorCode;
    }

    public sealed record ExportFolderDescriptor(Guid Id, string Path);
    public sealed record FileOverwriteState(string Identity, long Length, string Sha256);
    public sealed record CaptureLocalDestination(Guid FolderId, string FileName, FileOverwriteState Overwrite);
    public sealed record QueryFileDestination(
        Guid FolderId, string FileName, QueryExportFormat Format, FileOverwriteState Overwrite = null);
    public sealed record QueryFileExportReceipt(Guid FolderId, string FileName, long Length, long RowCount, bool Published);
    public sealed record ScriptFileDestination(Guid FolderId, string FileName, FileOverwriteState Overwrite = null);
    public sealed record ScriptFileExportReceipt(
        Guid CaptureId, Guid ScriptId, long Revision, string SourceHash,
        Guid FolderId, string FileName, long Length, bool Published);

    public interface ICaptureFileExports
    {
        Task<ExportFolderDescriptor> RegisterFolderAsync(string path, CancellationToken cancellationToken = default);
        Task<CaptureLocalDestination> PrepareDestinationAsync(
            string path, bool overwrite, CancellationToken cancellationToken = default) =>
            Task.FromException<CaptureLocalDestination>(new CaptureFileException(CaptureFileFailure.Unavailable));
        Task<CaptureReportDestination> PrepareReportDestinationAsync(
            string path, IReadOnlyList<CaptureReportArtifact> manifest, bool overwrite, CancellationToken cancellationToken = default) =>
            Task.FromException<CaptureReportDestination>(new CaptureFileException(CaptureFileFailure.Unavailable));
        Task SaveSqlDocumentAsync(
            string text, CaptureLocalDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
            Task.FromException(new CaptureFileException(CaptureFileFailure.Unavailable));
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
