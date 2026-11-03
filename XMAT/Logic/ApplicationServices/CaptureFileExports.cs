// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureFileExports(Func<Task<IReadOnlyList<string>>> protectedPaths = null) : ICaptureFileExports
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Folder> _folders = new();

        public Task<ExportFolderDescriptor> RegisterFolderAsync(string path, CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = new WindowsExportFolder(path);
                lock (_gate)
                {
                    var existing = _folders.Values.FirstOrDefault(folder =>
                        folder.Identity == directory.Identity &&
                        folder.Descriptor.Path.Equals(directory.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                        return existing.Descriptor;
                    if (_folders.Count >= 64)
                        throw new CaptureFileException(CaptureFileFailure.Unavailable);
                    var descriptor = new ExportFolderDescriptor(Guid.NewGuid(), directory.Path);
                    _folders.Add(descriptor.Id, new(descriptor, directory.Identity));
                    return descriptor;
                }
            }, cancellationToken);

        public IReadOnlyList<ExportFolderDescriptor> GetFolders()
        {
            lock (_gate)
            { return _folders.Values.Select(folder => folder.Descriptor).ToArray(); }
        }

        public async Task ValidateAsync(QueryFileDestination destination, CancellationToken cancellationToken = default)
        {
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = Open(destination);
                ValidateDestination(directory, destination, protectedFiles);
            }, cancellationToken);
        }

        public async Task ValidateCaptureAsync(CaptureFileDestination destination, CancellationToken cancellationToken = default)
        {
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenCapture(destination);
                ValidatePath(directory, destination.FileName, CaptureExtension(destination.Format), protectedFiles);
            }, cancellationToken);
        }

        public async Task ValidateScriptAsync(ScriptFileDestination destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(destination.FolderId);
                ValidatePath(directory, destination.FileName, ".txt", protectedFiles);
            }, cancellationToken);
        }

        public async Task ValidateReportAsync(CaptureReportDestination destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(destination.FolderId);
                ValidateReportPath(directory, destination.DirectoryName, protectedFiles);
            }, cancellationToken);
        }

        public async Task<CaptureReportExportReceipt> SaveReportAsync(
            ICaptureReportFiles files, CaptureReportDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(beforePublish);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            return await Task.Run(async () =>
            {
                using var directory = OpenFolder(destination.FolderId);
                ValidateReportPath(directory, destination.DirectoryName, protectedFiles);
                var source = new AnalysisExportSource(files.SourceDirectory, "Capture", files.Report.CompletedAt.LocalDateTime,
                    files.Manifest.Select(artifact => new AnalysisArtifact(artifact.RelativePath, artifact.Length, artifact.Sha256)));
                var length = await new AnalysisReportExporter().ExportApprovedAsync(source, directory, destination.DirectoryName,
                    maximumBytes, beforePublish, cancellationToken);
                return new CaptureReportExportReceipt(files.Report.Id, files.Report.SourceId, files.Report.SnapshotId,
                    destination.FolderId, destination.DirectoryName, length, files.Manifest.Count, true);
            }, cancellationToken);
        }

        public async Task<ScriptFileExportReceipt> SaveScriptAsync(
            CaptureScriptSource source, ScriptFileDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            var length = await WriteNewFileAsync(destination.FolderId, destination.FileName, ".txt", maximumBytes,
                (file, token) => ScriptFiles.WriteAsync(source.Source, file, maximumBytes, token),
                beforePublish, cancellationToken);
            return new(source.Script.CaptureId, source.Script.Id, source.Script.Revision, source.Script.SourceHash,
                destination.FolderId, destination.FileName, length, true);
        }

        public async Task<long> SaveCaptureAsync(
            ICaptureArchive archive, CaptureFileDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(beforePublish);
            if (archive.Receipt.Format != destination.Format)
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            if (!Enum.IsDefined(destination.Format))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            return await WriteNewFileAsync(destination.FolderId, destination.FileName, CaptureExtension(destination.Format),
                maximumBytes, (file, token) => archive.CopyToAsync(file, maximumBytes, token), beforePublish, cancellationToken);
        }

        public async Task<QueryFileExportReceipt> SaveQueryAsync(
            IQueryResult result, QueryFileDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(destination);
            if (!Enum.IsDefined(destination.Format))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var length = await WriteNewFileAsync(destination.FolderId, destination.FileName,
                destination.Format == QueryExportFormat.Csv ? ".csv" : ".json", maximumBytes,
                (file, token) => QueryResultExporter.WriteAsync(result, file, destination.Format, maximumBytes, token),
                beforePublish, cancellationToken);
            return new(destination.FolderId, destination.FileName, length, result.RowCount, true);
        }

        private async Task<long> WriteNewFileAsync(
            Guid folderId, string fileName, string extension, long maximumBytes,
            Func<Stream, CancellationToken, Task> serialize,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(beforePublish);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(folderId);
                var path = ValidatePath(directory, fileName, extension, protectedFiles);
                await using var file = directory.CreateTemporary();
                var published = false;
                try
                {
                    await serialize(file, cancellationToken);
                    await file.FlushAsync(cancellationToken);
                    file.Flush(flushToDisk: true);
                    var length = file.Length;
                    if (length > maximumBytes)
                        throw new QueryException(QueryError.ResultLimit, "The file exceeds the approved output limit.");
                    await beforePublish(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    WindowsExportFolder.Publish(file, path);
                    published = true;
                    return length;
                }
                finally
                {
                    if (!published)
                        WindowsExportFolder.DeleteTemporary(file);
                }
            }, cancellationToken);
        }

        private WindowsExportFolder Open(QueryFileDestination destination)
        {
            if (destination == null || !Enum.IsDefined(destination.Format))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            return OpenFolder(destination.FolderId);
        }

        private WindowsExportFolder OpenCapture(CaptureFileDestination destination)
        {
            if (destination == null || !Enum.IsDefined(destination.Format))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            return OpenFolder(destination.FolderId);
        }

        private WindowsExportFolder OpenFolder(Guid folderId)
        {
            Folder folder;
            lock (_gate)
            {
                folder = _folders.GetValueOrDefault(folderId) ??
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            }
            var directory = new WindowsExportFolder(folder.Descriptor.Path);
            if (directory.Identity != folder.Identity)
            {
                directory.Dispose();
                throw new CaptureFileException(CaptureFileFailure.FolderChanged);
            }
            return directory;
        }

        private static string ValidateDestination(
            WindowsExportFolder directory, QueryFileDestination destination, IReadOnlyList<string> protectedFiles)
        {
            var extension = destination.Format == QueryExportFormat.Csv ? ".csv" : ".json";
            return ValidatePath(directory, destination.FileName, extension, protectedFiles);
        }

        private static string CaptureExtension(CaptureArchiveFormat format) =>
            format == CaptureArchiveFormat.CaptureDatabase ? ".xmatcap" : ".saz";

        private static string ValidatePath(
            WindowsExportFolder directory, string fileName, string extension, IReadOnlyList<string> protectedFiles)
        {
            var path = directory.Destination(fileName);
            if (!System.IO.Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            if (protectedFiles.Any(source => source != null &&
                System.IO.Path.GetFullPath(source).Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new CaptureFileException(CaptureFileFailure.ProtectedSource);
            WindowsExportFolder.DemandNew(path);
            return path;
        }

        private static string ValidateReportPath(
            WindowsExportFolder directory, string directoryName, IReadOnlyList<string> protectedFiles)
        {
            var path = directory.Destination(directoryName);
            if (protectedFiles.Any(source => source != null &&
                System.IO.Path.GetFullPath(source).Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new CaptureFileException(CaptureFileFailure.ProtectedSource);
            WindowsExportFolder.DemandNew(path);
            return path;
        }

        private sealed record Folder(
            ExportFolderDescriptor Descriptor, WindowsExportFolder.FileIdentity Identity);
    }
}
