// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal static class CaptureFilePublication
    {
        internal static async Task<FileOverwriteState> InspectAsync(
            WindowsExportFolder directory, string fileName, bool overwrite, CancellationToken cancellationToken)
        {
            var path = directory.Destination(fileName);
            DemandUnprotected(path);
            if (!File.Exists(path))
            {
                WindowsExportFolder.DemandNew(path);
                return null;
            }
            if (!overwrite)
                throw new CaptureFileException(CaptureFileFailure.DestinationExists);
            await using var previous = directory.OpenInput(fileName, out var identity);
            if (previous.Length > AgentBackupStore.StorageLimit)
                throw new QueryException(QueryError.ResultLimit, "The existing file exceeds the overwrite review limit.");
            return await StateAsync(previous, identity, cancellationToken);
        }

        internal static async Task ValidateAsync(
            WindowsExportFolder directory, string fileName, FileOverwriteState expected, CancellationToken cancellationToken)
        {
            using var previous = await OpenExpectedAsync(directory, fileName, expected, cancellationToken);
        }

        private static async Task<FileStream> OpenExpectedAsync(
            WindowsExportFolder directory, string fileName, FileOverwriteState expected, CancellationToken cancellationToken)
        {
            var path = directory.Destination(fileName);
            DemandUnprotected(path);
            if (expected == null)
            {
                WindowsExportFolder.DemandNew(path);
                return null;
            }
            if (!File.Exists(path))
                throw new CaptureFileException(CaptureFileFailure.InputChanged);
            FileStream previous = null;
            try
            {
                previous = directory.OpenInput(fileName, out var identity);
                if (await StateAsync(previous, identity, cancellationToken) != expected)
                    throw new CaptureFileException(CaptureFileFailure.InputChanged);
                previous.Position = 0;
                return previous;
            }
            catch
            {
                previous?.Dispose();
                throw;
            }
        }

        private static async Task<FileOverwriteState> StateAsync(
            FileStream file, WindowsExportFolder.FileIdentity identity, CancellationToken cancellationToken) =>
            new(Identity(identity), file.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)));

        internal static string Identity(WindowsExportFolder.FileIdentity identity) =>
            $"{identity.Volume:X8}-{identity.High:X8}-{identity.Low:X8}";

        internal static async Task PublishAsync(
            WindowsExportFolder directory, string fileName, FileOverwriteState expected, FileStream prepared,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            await using var publication = await PrepareAsync(directory, fileName, expected, prepared, cancellationToken);
            await beforePublish(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            publication.Commit();
        }

        internal static async Task<Prepared> PrepareAsync(
            WindowsExportFolder directory, string fileName, FileOverwriteState expected, FileStream prepared,
            CancellationToken cancellationToken)
        {
            var path = directory.Destination(fileName);
            var previous = await OpenExpectedAsync(directory, fileName, expected, cancellationToken);
            try
            {
                var backup = previous != null && AgentMutationProtection.Current is { } origin
                    ? await origin.BeforeFileAsync(path, previous, cancellationToken)
                    : new AgentMutationProtection.Protection(null, null);
                return new(path, expected != null, prepared, previous, backup);
            }
            catch { previous?.Dispose(); throw; }
        }

        internal sealed class Prepared(
            string path, bool replace, FileStream prepared, FileStream previous,
            AgentMutationProtection.Protection backup) : IAsyncDisposable
        {
            private readonly string _temporary = WindowsExportFolder.FilePath(prepared);
            internal bool Published { get; private set; }
            internal bool Uncertain { get; private set; }
            internal void Demand() => backup.Demand();
            internal bool ResolveOutcome()
            {
                if (!Uncertain)
                    return true;
                var current = WindowsExportFolder.FilePath(prepared);
                if (string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                {
                    Published = true;
                    backup.Committed();
                }
                else if (_temporary == null || !string.Equals(current, _temporary, StringComparison.OrdinalIgnoreCase))
                    return false;
                Uncertain = false;
                return true;
            }
            internal void Commit()
            {
                backup.Demand();
                // Close the old no-write/no-rename handle only for the final atomic replacement.
                previous?.Dispose();
                try
                {
                    using var sourceProtection = DataAbstractionLayer.DataAbstractionLayer.ReserveFilePublication(path);
                    WindowsExportFolder.Publish(prepared, path, replace);
                    Published = true;
                    backup.Committed();
                }
                catch (DataAbstractionLayer.CaptureSaveException exception)
                {
                    throw new CaptureFileException(exception.Failure == DataAbstractionLayer.CaptureSaveFailure.Refused
                        ? CaptureFileFailure.ProtectedSource : CaptureFileFailure.Unavailable);
                }
                catch (CaptureFileException exception) when (exception.Failure == CaptureFileFailure.PublicationUncertain)
                {
                    Uncertain = true;
                    throw;
                }
            }
            public async ValueTask DisposeAsync()
            {
                previous?.Dispose();
                await backup.DisposeAsync();
            }
        }

        private static void DemandUnprotected(string path)
        {
            try
            {
                using var lease = DataAbstractionLayer.DataAbstractionLayer.ReserveFilePublication(path);
            }
            catch (DataAbstractionLayer.CaptureSaveException exception)
            {
                throw new CaptureFileException(exception.Failure == DataAbstractionLayer.CaptureSaveFailure.Refused
                    ? CaptureFileFailure.ProtectedSource : CaptureFileFailure.Unavailable);
            }
        }
    }
}
