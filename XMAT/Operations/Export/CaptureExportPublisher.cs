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
        public async Task<PublishedCapture> PublishAsync(
            string destination,
            bool replaceExisting,
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
            if (LoadedCaptureDatabaseRegistry.IsLoadedPath(normalized))
            {
                throw new CaptureSaveException(CaptureSaveFailure.Refused);
            }
            if (!replaceExisting && File.Exists(normalized))
            {
                throw new CaptureExportDestinationException("export.output.exists");
            }

            string stagingDirectory = CreateStagingDirectory(directory, Path.GetFileName(normalized));
            string staged = Path.Join(stagingDirectory, Path.GetFileName(normalized));
            var committed = false;
            PublishedCapture publication = null;
            ExceptionDispatchInfo operationFailure = null;
            Exception cleanupFailure = null;
            try
            {
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

                bool replaced;
                using (LoadedCaptureDatabaseRegistry.AcquireSaveDestination(normalized))
                {
                    validateDestination(normalized);
                    replaced = File.Exists(normalized);
                    if (replaced && !replaceExisting)
                    {
                        throw new CaptureExportDestinationException("export.output.exists");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(staged, normalized, replaceExisting);
                    committed = true;
                }
                publication = new PublishedCapture(bytes, hash, replaced);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                operationFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    if (!committed && File.Exists(staged))
                    {
                        File.Delete(staged);
                    }
                    DeleteIfPresent(staged + "-journal");
                    DeleteIfPresent(staged + "-wal");
                    DeleteIfPresent(staged + "-shm");
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory);
                    }
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
            for (var attempt = 0; attempt < 10; attempt++)
            {
                string candidate = Path.Join(
                    directory,
                    $".{destinationName}.export-{Guid.NewGuid():N}");
                if (Path.Exists(candidate))
                {
                    continue;
                }
                try
                {
                    return Directory.CreateDirectory(candidate).FullName;
                }
                catch (IOException) when (Path.Exists(candidate))
                {
                    // A concurrent exporter claimed this random staging name.
                }
            }
            throw new IOException("A unique export staging directory could not be created.");
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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
