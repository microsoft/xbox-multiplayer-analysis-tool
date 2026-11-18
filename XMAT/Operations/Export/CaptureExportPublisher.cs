// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;

namespace XMAT.Operations.Export
{
    internal sealed class CaptureExportPublisher : ICaptureExportPublisher
    {
        private const int FileSystemAttempts = 10;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        internal Func<string, bool> StagingIsPrivate { get; set; } = PrivateFolder.IsPrivate;
        internal Action<string, string, bool> MoveFile { get; set; } = File.Move;

        public async Task<PublishedCapture> PublishAsync(
            string destination,
            bool replaceExisting,
            bool allowUnprotected,
            Action<string> validateDestination,
            Func<string, CancellationToken, Task> writeStaged,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            ArgumentNullException.ThrowIfNull(validateDestination);
            ArgumentNullException.ThrowIfNull(writeStaged);
            string normalized = Path.GetFullPath(destination);
            string directory = Path.GetDirectoryName(normalized) ??
                throw new IOException("The export destination has no directory.");
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The export destination directory does not exist.");
            }
            validateDestination(normalized);
            await ValidateOpenCaptureAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (Path.Exists(normalized) &&
                (Directory.Exists(normalized) || !replaceExisting))
            {
                throw new CaptureExportDestinationException("export.output.exists");
            }

            string stagingDirectory = CreateStagingDirectory(directory, Path.GetFileName(normalized));
            string staged = Path.Join(stagingDirectory, Path.GetFileName(normalized));
            bool protectedOutput = StagingIsPrivate(stagingDirectory);
            var committed = false;
            PublishedCapture publication = null;
            ExceptionDispatchInfo operationFailure = null;
            Exception cleanupFailure = null;
            try
            {
                if (!protectedOutput && !allowUnprotected)
                {
                    throw new CaptureExportException(
                        CaptureExportErrorCodes.OutputUnprotected,
                        OperationOutcome.Failed);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await writeStaged(staged, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(staged))
                {
                    throw new IOException("The export writer did not produce an output file.");
                }
                long bytes = new FileInfo(staged).Length;
                string hash;
                using (var stream = new FileStream(
                    staged,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan))
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }

                bool replaced = await CommitAsync(
                    staged,
                    normalized,
                    replaceExisting,
                    validateDestination,
                    cancellationToken).ConfigureAwait(false);
                committed = true;
                publication = new PublishedCapture(bytes, hash, replaced, protectedOutput);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                operationFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    Cleanup(staged, stagingDirectory, committed);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    cleanupFailure = exception;
                }
            }
            if (cleanupFailure != null)
            {
                throw new CaptureExportCleanupException(
                    publication,
                    operationFailure?.SourceException,
                    cleanupFailure);
            }
            if (operationFailure != null)
            {
                operationFailure.Throw();
            }
            return publication;
        }

        private static string CreateStagingDirectory(string directory, string destinationName)
        {
            for (var attempt = 0; attempt < FileSystemAttempts; attempt++)
            {
                string candidate = Path.Join(
                    directory,
                    $".{destinationName}.export-{Guid.NewGuid():N}");
                try
                {
                    PrivateFolder.Create(candidate);
                    return candidate;
                }
                catch (IOException) when (Path.Exists(candidate))
                {
                    // A concurrent exporter claimed this random staging name.
                }
            }
            throw new IOException("A unique export staging directory could not be created.");
        }

        private static async Task ValidateOpenCaptureAsync(
            string destination,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (LoadedCaptureDatabaseRegistry.IsLoadedPath(destination))
                    {
                        throw new CaptureSaveException(CaptureSaveFailure.Refused);
                    }
                    return;
                }
                catch (IOException exception) when (IsLocking(exception) && attempt < FileSystemAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception) when (IsLocking(exception))
                {
                    throw new CaptureExportDestinationException("export.output.locked");
                }
            }
        }

        private async Task<bool> CommitAsync(
            string staged,
            string destination,
            bool replaceExisting,
            Action<string> validateDestination,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using IDisposable lease = LoadedCaptureDatabaseRegistry.AcquireSaveDestination(destination);
                    validateDestination(destination);
                    bool exists = Path.Exists(destination);
                    if (exists && (Directory.Exists(destination) || !replaceExisting))
                    {
                        throw new CaptureExportDestinationException("export.output.exists");
                    }
                    MoveFile(staged, destination, replaceExisting);
                    return exists;
                }
                catch (IOException exception) when (IsAlreadyExists(exception))
                {
                    throw new CaptureExportDestinationException("export.output.exists");
                }
                catch (Exception exception) when (IsLocking(exception) && attempt < FileSystemAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsLocking(exception))
                {
                    throw new CaptureExportDestinationException("export.output.locked");
                }
            }
        }

        private static void Cleanup(string staged, string stagingDirectory, bool committed)
        {
            if (!committed)
            {
                DeleteWithRetry(staged);
            }
            DeleteWithRetry(staged + "-journal");
            DeleteWithRetry(staged + "-wal");
            DeleteWithRetry(staged + "-shm");
            RetryFileSystem(() =>
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory);
                }
            });
        }

        private static void DeleteWithRetry(string path) =>
            RetryFileSystem(() =>
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            });

        private static void RetryFileSystem(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception exception) when (
                    (exception is IOException or UnauthorizedAccessException) &&
                    attempt < FileSystemAttempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        private static bool IsAlreadyExists(IOException exception) =>
            (exception.HResult & 0xFFFF) is 80 or 183;

        internal static bool IsLocking(Exception exception)
        {
            if (exception is CaptureSaveException { Failure: CaptureSaveFailure.DestinationLocked })
            {
                return exception.InnerException != null && IsLocking(exception.InnerException);
            }
            return exception is IOException io && (io.HResult & 0xFFFF) is 32 or 33;
        }
    }

    internal sealed class CaptureExportCleanupException : IOException
    {
        internal CaptureExportCleanupException(
            PublishedCapture publication,
            Exception operationFailure,
            Exception cleanupFailure)
            : base(
                "Export staging cleanup failed.",
                operationFailure == null
                    ? cleanupFailure
                    : new AggregateException(operationFailure, cleanupFailure))
        {
            Publication = publication;
        }

        internal PublishedCapture Publication { get; }
    }
}
