// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace XMAT
{
    internal sealed record AnalysisArtifact(
        string RelativePath,
        long Length);

    internal sealed class AnalysisExportSource
    {
        internal string SourceDirectory { get; }
        internal string SourceCaptureName { get; }
        internal DateTime CompletedAt { get; }
        internal IReadOnlyList<AnalysisArtifact> ArtifactManifest { get; }

        internal AnalysisExportSource(
            string sourceDirectory,
            string sourceCaptureName,
            DateTime completedAt,
            IEnumerable<AnalysisArtifact> artifactManifest)
        {
            SourceDirectory = sourceDirectory;
            SourceCaptureName = sourceCaptureName;
            CompletedAt = completedAt;
            ArtifactManifest = Array.AsReadOnly(
                artifactManifest?.ToArray() ??
                Array.Empty<AnalysisArtifact>());
        }
    }

    internal enum AnalysisReportExportFailureKind
    {
        InvalidSource,
        SourceMissing,
        ManifestMismatch,
        InvalidDestination,
        DestinationMissing,
        PathTooLong,
        AccessDenied,
        CopyFailed,
        PublishFailed
    }

    internal enum AnalysisReportPublicationState
    {
        NotPublished,
        Unknown
    }

    internal sealed class AnalysisReportExportException : Exception
    {
        internal AnalysisReportExportFailureKind FailureKind { get; }
        internal AnalysisReportPublicationState PublicationState { get; }
        internal string PublishedPath { get; }
        internal string ResiduePath { get; set; }

        internal AnalysisReportExportException(
            AnalysisReportExportFailureKind failureKind,
            string message,
            Exception innerException = null,
            AnalysisReportPublicationState publicationState =
                AnalysisReportPublicationState.NotPublished,
            string publishedPath = null) :
            base(message, innerException)
        {
            FailureKind = failureKind;
            PublicationState = publicationState;
            PublishedPath = publishedPath;
        }
    }

    internal interface IAnalysisReportExportFileSystem
    {
        bool DirectoryExists(string path);
        bool FileExists(string path);
        long GetFileLength(string path);
        string ComputeSha256(string path);
        void CreateSingleDirectory(string path);
        void CopyFile(
            string sourcePath,
            string destinationPath,
            bool overwrite);
        void MoveDirectory(
            string sourcePath,
            string destinationPath);
        void DeleteDirectory(string path, bool recursive);
    }

    internal sealed class PhysicalAnalysisReportExportFileSystem :
        IAnalysisReportExportFileSystem
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const int ErrorFileExists = 80;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorFilenameExceedsRange = 206;

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateDirectoryW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryNative(
            string path,
            IntPtr securityAttributes);

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public long GetFileLength(string path)
        {
            return new FileInfo(path).Length;
        }

        public string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public void CreateSingleDirectory(string path)
        {
            if (CreateDirectoryNative(path, IntPtr.Zero))
            {
                return;
            }

            int error = Marshal.GetLastWin32Error();
            switch (error)
            {
                case ErrorFileNotFound:
                case ErrorPathNotFound:
                    throw new DirectoryNotFoundException(
                        $"The parent directory for '{path}' does not exist.");

                case ErrorAccessDenied:
                    throw new UnauthorizedAccessException(
                        $"Access to '{path}' was denied.",
                        new Win32Exception(error));

                case ErrorFilenameExceedsRange:
                    throw new PathTooLongException(
                        $"The directory path '{path}' is too long.");

                case ErrorFileExists:
                case ErrorAlreadyExists:
                    throw new IOException(
                        $"The directory '{path}' already exists.",
                        new Win32Exception(error));

                default:
                    throw new IOException(
                        $"The directory '{path}' could not be created.",
                        new Win32Exception(error));
            }
        }

        public void CopyFile(
            string sourcePath,
            string destinationPath,
            bool overwrite)
        {
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void MoveDirectory(
            string sourcePath,
            string destinationPath)
        {
            Directory.Move(sourcePath, destinationPath);
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            Directory.Delete(path, recursive);
        }
    }

    internal sealed class AnalysisReportExporter
    {
        internal const int MaximumCompatiblePathLength = 260;
        internal const int MaximumCaptureNameLength = 60;
        internal const int PublishRetryCount = 3;
        internal const string StagingDirectoryPrefix =
            ".xmat-";

        private const int StagingCreationAttempts = 10;
        private static readonly HashSet<char> InvalidFileNameCharacters =
            new(Path.GetInvalidFileNameChars());

        private readonly IAnalysisReportExportFileSystem _fileSystem;
        private readonly Action<int> _retryDelay;

        internal AnalysisReportExporter() :
            this(
                new PhysicalAnalysisReportExportFileSystem(),
                DelayBeforePublishRetry)
        {
        }

        internal AnalysisReportExporter(
            IAnalysisReportExportFileSystem fileSystem,
            Action<int> retryDelay)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(retryDelay);
            _fileSystem = fileSystem;
            _retryDelay = retryDelay;
        }

        internal string Export(
            AnalysisExportSource source,
            string destinationParent)
        {
            string stagingPath = null;
            bool ownsStaging = false;
            try
            {
                string sourceDirectory = ValidateSourceDirectory(source);
                string destinationDirectory = ValidateDestinationParent(
                    sourceDirectory,
                    destinationParent);
                Dictionary<string, string> sourceHashes =
                    ValidateSourceArtifacts(
                        source,
                        sourceDirectory);

                string baseFolderName = CreateFolderName(
                    source.SourceCaptureName,
                    source.CompletedAt);
                int suffix = 1;
                string publishedPath = FindAvailablePublishedPath(
                    destinationDirectory,
                    baseFolderName,
                    ref suffix);
                EnsureCompatiblePaths(
                    publishedPath,
                    source.ArtifactManifest,
                    destinationDirectory);

                stagingPath = CreateStagingDirectory(
                    destinationDirectory);
                ownsStaging = true;
                CopyAndValidateArtifacts(
                    source,
                    sourceDirectory,
                    sourceHashes,
                    stagingPath);
                publishedPath = PublishStagingDirectory(
                    stagingPath,
                    destinationDirectory,
                    baseFolderName,
                    suffix,
                    source.ArtifactManifest);
                ownsStaging = false;
                return publishedPath;
            }
            catch (AnalysisReportExportException exception)
            {
                CleanupOwnedStaging(
                    stagingPath,
                    ownsStaging,
                    exception);
                throw;
            }
        }

        internal static string CreateFolderName(
            string sourceCaptureName,
            DateTime completedAt)
        {
            string captureName = sourceCaptureName?.Trim() ?? string.Empty;
            captureName = new string(
                captureName.Select(character =>
                    InvalidFileNameCharacters.Contains(character)
                        ? '_'
                        : character)
                    .ToArray());
            captureName = captureName.TrimEnd(' ', '.');
            if (captureName.Length > MaximumCaptureNameLength)
            {
                int length = MaximumCaptureNameLength;
                if (char.IsHighSurrogate(captureName[length - 1]) &&
                    length < captureName.Length &&
                    char.IsLowSurrogate(captureName[length]))
                {
                    length--;
                }
                captureName = captureName
                    .Substring(0, length)
                    .TrimEnd(' ', '.');
            }
            if (string.IsNullOrWhiteSpace(captureName))
            {
                captureName = "Capture";
            }

            string timestamp = completedAt.ToString(
                "yyyy-MM-dd HHmmss",
                CultureInfo.InvariantCulture);
            return $"XMAT Analysis - {captureName} - {timestamp}";
        }

        private static void DelayBeforePublishRetry(
            int retryNumber)
        {
            Thread.Sleep(100 * retryNumber);
        }

        private string ValidateSourceDirectory(AnalysisExportSource source)
        {
            if (source == null ||
                string.IsNullOrWhiteSpace(source.SourceDirectory))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.InvalidSource,
                    "No analysis report was selected for export.");
            }

            string sourceDirectory;
            try
            {
                sourceDirectory = Path.GetFullPath(
                    source.SourceDirectory);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.InvalidSource,
                    "The analysis report source path is invalid.",
                    exception);
            }

            if (!_fileSystem.DirectoryExists(sourceDirectory))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.SourceMissing,
                    $"The analysis report source folder no longer exists: " +
                    $"{sourceDirectory}");
            }

            if (source.ArtifactManifest == null ||
                source.ArtifactManifest.Count == 0)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.ManifestMismatch,
                    "The analysis report does not contain an artifact manifest.");
            }

            return sourceDirectory;
        }

        private string ValidateDestinationParent(
            string sourceDirectory,
            string destinationParent)
        {
            if (string.IsNullOrWhiteSpace(destinationParent))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.InvalidDestination,
                    "No destination folder was selected.");
            }

            string destinationDirectory;
            try
            {
                destinationDirectory = Path.GetFullPath(
                    destinationParent);
            }
            catch (PathTooLongException exception)
            {
                throw CreateDestinationPathTooLongFailure(
                    destinationParent,
                    exception);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.InvalidDestination,
                    "The selected destination path is invalid.",
                    exception);
            }

            if (!_fileSystem.DirectoryExists(destinationDirectory))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.DestinationMissing,
                    $"The selected destination folder no longer exists: " +
                    $"{destinationDirectory}");
            }

            if (IsSameOrDescendant(
                destinationDirectory,
                sourceDirectory))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.InvalidDestination,
                    "The analysis report cannot be exported into its source folder.");
            }

            return destinationDirectory;
        }

        private Dictionary<string, string> ValidateSourceArtifacts(
            AnalysisExportSource source,
            string sourceDirectory)
        {
            var hashes = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (AnalysisArtifact artifact in source.ArtifactManifest)
                {
                    ValidateArtifactDefinition(
                        artifact,
                        sourceDirectory,
                        hashes);
                    string sourcePath = GetArtifactPath(
                        sourceDirectory,
                        artifact.RelativePath);
                    if (!_fileSystem.FileExists(sourcePath) ||
                        _fileSystem.GetFileLength(sourcePath) !=
                            artifact.Length)
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.ManifestMismatch,
                            $"The analysis artifact no longer matches the " +
                            $"completed report: {artifact.RelativePath}");
                    }

                    string sourceHash = _fileSystem.ComputeSha256(sourcePath);
                    hashes.Add(artifact.RelativePath, sourceHash);
                }
            }
            catch (AnalysisReportExportException)
            {
                throw;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.AccessDenied,
                    "Access to the analysis report source was denied.",
                    exception);
            }
            catch (PathTooLongException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.PathTooLong,
                    "An analysis artifact source path is too long.",
                    exception);
            }
            catch (IOException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.ManifestMismatch,
                    "The analysis report changed while it was being prepared " +
                    "for export.",
                    exception);
            }
            catch (CryptographicException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.ManifestMismatch,
                    "An analysis artifact could not be validated.",
                    exception);
            }

            return hashes;
        }

        private static void ValidateArtifactDefinition(
            AnalysisArtifact artifact,
            string sourceDirectory,
            IDictionary<string, string> hashes)
        {
            if (artifact == null ||
                string.IsNullOrWhiteSpace(artifact.RelativePath) ||
                Path.IsPathRooted(artifact.RelativePath) ||
                artifact.Length < 0 ||
                hashes.ContainsKey(artifact.RelativePath) ||
                artifact.RelativePath == "." ||
                artifact.RelativePath == ".." ||
                artifact.RelativePath.Contains(
                    Path.DirectorySeparatorChar) ||
                artifact.RelativePath.Contains(
                    Path.AltDirectorySeparatorChar) ||
                artifact.RelativePath.Contains(':') ||
                artifact.RelativePath.Any(character =>
                    InvalidFileNameCharacters.Contains(character)) ||
                !string.Equals(
                    artifact.RelativePath,
                    artifact.RelativePath.TrimEnd(' ', '.'),
                    StringComparison.Ordinal))
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.ManifestMismatch,
                    "The analysis artifact manifest is invalid.");
            }

            try
            {
                string artifactPath = GetArtifactPath(
                    sourceDirectory,
                    artifact.RelativePath);
                if (!string.Equals(
                    Path.GetFileName(artifactPath),
                    artifact.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new AnalysisReportExportException(
                        AnalysisReportExportFailureKind.ManifestMismatch,
                        $"The analysis artifact path is not a leaf filename: " +
                        $"{artifact.RelativePath}");
                }
            }
            catch (AnalysisReportExportException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.ManifestMismatch,
                    $"The analysis artifact path is invalid: " +
                    $"{artifact.RelativePath}",
                    exception);
            }
        }

        private string CreateStagingDirectory(
            string destinationDirectory)
        {
            for (int attempt = 0;
                attempt < StagingCreationAttempts;
                attempt++)
            {
                string identifier = Guid.NewGuid().ToString("N");
                string stagingName = string.Concat(
                    StagingDirectoryPrefix.AsSpan(),
                    identifier.AsSpan(0, 8));
                string stagingPath = Path.Join(
                    destinationDirectory,
                    stagingName);
                try
                {
                    _fileSystem.CreateSingleDirectory(stagingPath);
                    return stagingPath;
                }
                catch (DirectoryNotFoundException exception)
                {
                    throw new AnalysisReportExportException(
                        AnalysisReportExportFailureKind.DestinationMissing,
                        "The selected destination folder no longer exists.",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new AnalysisReportExportException(
                        AnalysisReportExportFailureKind.AccessDenied,
                        "Access to the selected destination folder was denied.",
                        exception);
                }
                catch (PathTooLongException exception)
                {
                    throw CreateDestinationPathTooLongFailure(
                        destinationDirectory,
                        exception);
                }
                catch (IOException exception)
                {
                    if (!_fileSystem.DirectoryExists(destinationDirectory))
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.DestinationMissing,
                            "The selected destination folder no longer exists.",
                            exception);
                    }
                    if (_fileSystem.DirectoryExists(stagingPath) ||
                        _fileSystem.FileExists(stagingPath))
                    {
                        continue;
                    }

                    throw new AnalysisReportExportException(
                        AnalysisReportExportFailureKind.CopyFailed,
                        "A temporary analysis export folder could not be created.",
                        exception);
                }
            }

            throw new AnalysisReportExportException(
                AnalysisReportExportFailureKind.CopyFailed,
                "A unique temporary analysis export folder could not be created.");
        }

        private void CopyAndValidateArtifacts(
            AnalysisExportSource source,
            string sourceDirectory,
            IReadOnlyDictionary<string, string> sourceHashes,
            string stagingPath)
        {
            try
            {
                foreach (AnalysisArtifact artifact in source.ArtifactManifest)
                {
                    _fileSystem.CopyFile(
                        GetArtifactPath(
                            sourceDirectory,
                            artifact.RelativePath),
                        GetArtifactPath(
                            stagingPath,
                            artifact.RelativePath),
                        false);
                }

                foreach (AnalysisArtifact artifact in source.ArtifactManifest)
                {
                    string stagedPath = GetArtifactPath(
                        stagingPath,
                        artifact.RelativePath);
                    if (!_fileSystem.FileExists(stagedPath) ||
                        _fileSystem.GetFileLength(stagedPath) !=
                            artifact.Length ||
                        !string.Equals(
                            _fileSystem.ComputeSha256(stagedPath),
                            sourceHashes[artifact.RelativePath],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.ManifestMismatch,
                            $"The copied analysis artifact failed validation: " +
                            $"{artifact.RelativePath}");
                    }
                }
            }
            catch (AnalysisReportExportException)
            {
                throw;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.AccessDenied,
                    "Access was denied while copying the analysis report.",
                    exception);
            }
            catch (PathTooLongException exception)
            {
                throw CreateDestinationPathTooLongFailure(
                    Path.GetDirectoryName(stagingPath),
                    exception);
            }
            catch (IOException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.CopyFailed,
                    "The analysis report could not be copied.",
                    exception);
            }
            catch (CryptographicException exception)
            {
                throw new AnalysisReportExportException(
                    AnalysisReportExportFailureKind.CopyFailed,
                    "The copied analysis report could not be validated.",
                    exception);
            }
        }

        private string PublishStagingDirectory(
            string stagingPath,
            string destinationDirectory,
            string baseFolderName,
            int suffix,
            IReadOnlyList<AnalysisArtifact> artifacts)
        {
            string publishedPath = FindAvailablePublishedPath(
                destinationDirectory,
                baseFolderName,
                ref suffix);
            EnsureCompatiblePaths(
                publishedPath,
                artifacts,
                destinationDirectory);
            int retryNumber = 0;

            while (true)
            {
                try
                {
                    _fileSystem.MoveDirectory(
                        stagingPath,
                        publishedPath);
                    return publishedPath;
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new AnalysisReportExportException(
                        AnalysisReportExportFailureKind.PublishFailed,
                        "The analysis report could not be published to the " +
                        "selected destination.",
                        exception);
                }
                catch (PathTooLongException exception)
                {
                    throw CreateDestinationPathTooLongFailure(
                        destinationDirectory,
                        exception);
                }
                catch (IOException exception)
                {
                    if (!_fileSystem.DirectoryExists(
                        destinationDirectory))
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.DestinationMissing,
                            "The selected destination folder no longer exists.",
                            exception,
                            AnalysisReportPublicationState.NotPublished);
                    }

                    bool stagingExists =
                        _fileSystem.DirectoryExists(stagingPath);
                    bool publishedExists = PathExists(publishedPath);
                    if (!stagingExists)
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.PublishFailed,
                            "The destination rename did not report a reliable " +
                            "result. Verify the destination before retrying.",
                            exception,
                            AnalysisReportPublicationState.Unknown,
                            publishedPath);
                    }

                    if (publishedExists)
                    {
                        AdvanceSuffix(ref suffix);
                        publishedPath = FindAvailablePublishedPath(
                            destinationDirectory,
                            baseFolderName,
                            ref suffix);
                        EnsureCompatiblePaths(
                            publishedPath,
                            artifacts,
                            destinationDirectory);
                        retryNumber = 0;
                        continue;
                    }

                    if (retryNumber >= PublishRetryCount)
                    {
                        throw new AnalysisReportExportException(
                            AnalysisReportExportFailureKind.PublishFailed,
                            "The analysis report could not be published after " +
                            "several rename attempts.",
                            exception,
                            AnalysisReportPublicationState.NotPublished);
                    }

                    retryNumber++;
                    _retryDelay(retryNumber);
                }
            }
        }

        private string FindAvailablePublishedPath(
            string destinationDirectory,
            string baseFolderName,
            ref int suffix)
        {
            while (true)
            {
                string folderName = suffix == 1
                    ? baseFolderName
                    : $"{baseFolderName} ({suffix})";
                string candidate = Path.Join(
                    destinationDirectory,
                    folderName);
                if (!PathExists(candidate))
                {
                    return candidate;
                }
                AdvanceSuffix(ref suffix);
            }
        }

        private static void AdvanceSuffix(ref int suffix)
        {
            suffix = suffix == 1 ? 2 : suffix + 1;
        }

        private bool PathExists(string path)
        {
            return _fileSystem.DirectoryExists(path) ||
                _fileSystem.FileExists(path);
        }

        private static void EnsureCompatiblePaths(
            string rootPath,
            IEnumerable<AnalysisArtifact> artifacts,
            string selectedParent)
        {
            EnsureCompatiblePath(rootPath, selectedParent);
            foreach (AnalysisArtifact artifact in artifacts)
            {
                EnsureCompatiblePath(GetArtifactPath(
                    rootPath,
                    artifact.RelativePath),
                    selectedParent);
            }
        }

        private static void EnsureCompatiblePath(
            string path,
            string selectedParent)
        {
            if (path.Length >= MaximumCompatiblePathLength)
            {
                throw CreateDestinationPathTooLongFailure(
                    selectedParent);
            }
        }

        private static AnalysisReportExportException
            CreateDestinationPathTooLongFailure(
                string selectedParent,
                Exception innerException = null)
        {
            return new AnalysisReportExportException(
                AnalysisReportExportFailureKind.PathTooLong,
                $"The selected destination folder does not leave enough " +
                $"path space for the analysis report. Choose a shorter " +
                $"location: {selectedParent}",
                innerException);
        }

        private static string GetArtifactPath(
            string rootPath,
            string relativePath)
        {
            return Path.GetFullPath(Path.Join(
                rootPath,
                relativePath));
        }

        private static bool IsSameOrDescendant(
            string path,
            string parentPath)
        {
            if (string.Equals(
                path,
                parentPath,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string parentWithSeparator =
                parentPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return path.StartsWith(
                parentWithSeparator,
                StringComparison.OrdinalIgnoreCase);
        }

        private void CleanupOwnedStaging(
            string stagingPath,
            bool ownsStaging,
            AnalysisReportExportException exportException)
        {
            if (!ownsStaging ||
                string.IsNullOrEmpty(stagingPath))
            {
                return;
            }

            try
            {
                _fileSystem.DeleteDirectory(stagingPath, true);
            }
            catch (DirectoryNotFoundException)
            {
                // Staging is already absent; nothing remains to clean.
            }
            catch (Exception cleanupException) when (
                !ExceptionUtilities.IsFatal(cleanupException))
            {
                if (exportException != null)
                {
                    exportException.ResiduePath = stagingPath;
                }
            }
        }
    }
}
