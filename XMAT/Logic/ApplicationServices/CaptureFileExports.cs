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

        public async Task<CaptureLocalDestination> PrepareDestinationAsync(
            string path, bool overwrite, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var folder = await RegisterFolderAsync(Path.GetDirectoryName(path), cancellationToken);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            return await Task.Run(async () =>
            {
                using var directory = OpenFolder(folder.Id);
                var name = Path.GetFileName(path);
                var destination = directory.Destination(name);
                DemandUnprotectedPath(destination, protectedFiles);
                var state = await CaptureFilePublication.InspectAsync(directory, name, overwrite, cancellationToken);
                return new CaptureLocalDestination(folder.Id, name, state);
            }, cancellationToken);
        }

        public async Task ValidateAsync(QueryFileDestination destination, CancellationToken cancellationToken = default)
        {
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = Open(destination);
                ValidateDestination(directory, destination, protectedFiles);
                await CaptureFilePublication.ValidateAsync(directory, destination.FileName, destination.Overwrite, cancellationToken);
            }, cancellationToken);
        }

        public async Task ValidateCaptureAsync(CaptureFileDestination destination, CancellationToken cancellationToken = default)
        {
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenCapture(destination);
                ValidatePath(directory, destination.FileName, CaptureExtension(destination.Format), protectedFiles);
                await CaptureFilePublication.ValidateAsync(directory, destination.FileName, destination.Overwrite, cancellationToken);
            }, cancellationToken);
        }

        public async Task ValidateScriptAsync(ScriptFileDestination destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(destination.FolderId);
                ValidatePath(directory, destination.FileName, ".txt", protectedFiles);
                await CaptureFilePublication.ValidateAsync(directory, destination.FileName, destination.Overwrite, cancellationToken);
            }, cancellationToken);
        }

        public async Task ValidateReportAsync(CaptureReportDestination destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(destination.FolderId);
                await ValidateReportPathAsync(directory, destination, protectedFiles, cancellationToken);
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
                await ValidateReportPathAsync(directory, destination, protectedFiles, cancellationToken);
                var source = new AnalysisExportSource(files.SourceDirectory, "Capture", files.Report.CompletedAt.LocalDateTime,
                    files.Manifest.Select(artifact => new AnalysisArtifact(artifact.RelativePath, artifact.Length, artifact.Sha256)));
                var exporter = new AnalysisReportExporter();
                var length = destination.ExistingDirectoryIdentity == null
                    ? await exporter.ExportApprovedAsync(source, directory, destination.DirectoryName, maximumBytes, beforePublish, cancellationToken)
                    : await exporter.ExportApprovedReplacementAsync(source, directory, destination, maximumBytes, beforePublish, cancellationToken);
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
                beforePublish, destination.Overwrite, cancellationToken);
            return new(source.Script.CaptureId, source.Script.Id, source.Script.Revision, source.Script.SourceHash,
                destination.FolderId, destination.FileName, length, true);
        }

        public async Task SaveSqlDocumentAsync(
            string text, CaptureLocalDestination destination, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default) =>
            await WriteNewFileAsync(destination.FolderId, destination.FileName, ".sql", maximumBytes,
                (file, token) => ScriptFiles.WriteAsync(text, file, maximumBytes, token),
                beforePublish, destination.Overwrite, cancellationToken);

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
                maximumBytes, (file, token) => archive.CopyToAsync(file, maximumBytes, token),
                beforePublish, destination.Overwrite, cancellationToken);
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
                beforePublish, destination.Overwrite, cancellationToken);
            return new(destination.FolderId, destination.FileName, length, result.RowCount, true);
        }

        private async Task<long> WriteNewFileAsync(
            Guid folderId, string fileName, string extension, long maximumBytes,
            Func<Stream, CancellationToken, Task> serialize,
            Func<CancellationToken, Task> beforePublish, FileOverwriteState overwrite, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(beforePublish);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var directory = OpenFolder(folderId);
                ValidatePath(directory, fileName, extension, protectedFiles);
                await CaptureFilePublication.ValidateAsync(directory, fileName, overwrite, cancellationToken);
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
                    await CaptureFilePublication.PublishAsync(directory, fileName, overwrite, file, beforePublish, cancellationToken);
                    published = true;
                    return length;
                }
                catch (CaptureFileException exception) when (exception.Failure == CaptureFileFailure.PublicationUncertain)
                {
                    published = true;
                    throw;
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
            DemandUnprotectedPath(path, protectedFiles);
            return path;
        }

        private static void DemandUnprotectedPath(string path, IReadOnlyList<string> protectedFiles)
        {
            if (protectedFiles.Any(source => source != null &&
                System.IO.Path.GetFullPath(source).Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new CaptureFileException(CaptureFileFailure.ProtectedSource);
        }

        public async Task<CaptureReportDestination> PrepareReportDestinationAsync(
            string path, IReadOnlyList<CaptureReportArtifact> manifest, bool overwrite, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || manifest is not { Count: > 0 and <= 256 })
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            path = Path.TrimEndingDirectorySeparator(path);
            var folder = await RegisterFolderAsync(Path.GetDirectoryName(path), cancellationToken);
            var protectedFiles = protectedPaths == null ? [] : await protectedPaths();
            return await Task.Run(async () =>
            {
                using var directory = OpenFolder(folder.Id);
                var name = Path.GetFileName(path);
                var destination = directory.Destination(name);
                DemandUnprotectedPath(destination, protectedFiles);
                if (!Directory.Exists(destination))
                {
                    WindowsExportFolder.DemandNew(destination);
                    return new CaptureReportDestination(folder.Id, name);
                }
                if (!overwrite)
                    throw new CaptureFileException(CaptureFileFailure.DestinationExists);
                using var existing = new WindowsExportFolder(destination);
                var files = new List<CaptureReportFileReplacement>();
                foreach (var member in manifest)
                {
                    DemandUnprotectedPath(existing.Destination(member.RelativePath), protectedFiles);
                    files.Add(new(member.RelativePath,
                        await CaptureFilePublication.InspectAsync(existing, member.RelativePath, true, cancellationToken)));
                }
                return new CaptureReportDestination(folder.Id, name, CaptureFilePublication.Identity(existing.Identity), files);
            }, cancellationToken);
        }

        private static async Task ValidateReportPathAsync(
            WindowsExportFolder directory, CaptureReportDestination destination, IReadOnlyList<string> protectedFiles,
            CancellationToken cancellationToken)
        {
            var path = directory.Destination(destination.DirectoryName);
            DemandUnprotectedPath(path, protectedFiles);
            if (destination.ExistingDirectoryIdentity == null)
            {
                WindowsExportFolder.DemandNew(path);
                return;
            }
            using var existing = new WindowsExportFolder(path);
            if (CaptureFilePublication.Identity(existing.Identity) != destination.ExistingDirectoryIdentity ||
                destination.Files is not { Count: > 0 and <= 256 } ||
                destination.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != destination.Files.Count)
                throw new CaptureFileException(CaptureFileFailure.FolderChanged);
            foreach (var member in destination.Files)
            {
                DemandUnprotectedPath(existing.Destination(member.RelativePath), protectedFiles);
                await CaptureFilePublication.ValidateAsync(existing, member.RelativePath, member.Previous, cancellationToken);
            }
        }

        private sealed record Folder(
            ExportFolderDescriptor Descriptor, WindowsExportFolder.FileIdentity Identity);
    }
}
