// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class AdministrationArtifacts(ICaptureFileExports folders)
    {
        internal async Task ValidateAsync(
            AdministrationDestination destination, AdministrationArtifactKind kind, CancellationToken cancellationToken)
        {
            using var directory = await OpenAsync(destination, cancellationToken);
            Validate(directory, destination, kind);
        }

        internal async Task<long> PublishAsync(
            AdministrationDestination destination, AdministrationArtifactKind kind, long maximumBytes,
            Func<Stream, CancellationToken, Task> serialize, Func<CancellationToken, Task> beforePublish,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(beforePublish);
            using var directory = await OpenAsync(destination, cancellationToken);
            var path = Validate(directory, destination, kind);
            await using var file = directory.CreateTemporary();
            var published = false;
            try
            {
                using (var bounded = new AdministrationOutputStream(file, maximumBytes, cancellationToken))
                    await serialize(bounded, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
                await beforePublish(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                WindowsExportFolder.Publish(file, path);
                published = true;
                return file.Length;
            }
            finally
            {
                if (!published)
                    WindowsExportFolder.DeleteTemporary(file);
            }
        }

        private async Task<WindowsExportFolder> OpenAsync(
            AdministrationDestination destination, CancellationToken cancellationToken)
        {
            if (destination == null)
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var folder = folders.GetFolders().SingleOrDefault(folder => folder.Id == destination.FolderId)
                ?? throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var directory = new WindowsExportFolder(folder.Path);
            try
            {
                // Keep our directory pinned while the authoritative registry checks its original identity.
                await folders.ValidateScriptAsync(
                    new(destination.FolderId, $".xmat-diagnostics-{Guid.NewGuid():N}.txt"), cancellationToken);
                return directory;
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        private static string Validate(
            WindowsExportFolder directory, AdministrationDestination destination, AdministrationArtifactKind kind)
        {
            var extension = kind switch
            {
                AdministrationArtifactKind.DiagnosticLogs => ".zip",
                AdministrationArtifactKind.PublicRootCertificateDer => ".cer",
                AdministrationArtifactKind.PublicRootCertificatePem => ".pem",
                _ => throw new CaptureFileException(CaptureFileFailure.InvalidPath)
            };
            var path = directory.Destination(destination.FileName);
            if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            WindowsExportFolder.DemandNew(path);
            return path;
        }
    }

    internal sealed class AdministrationOutputStream(
        Stream inner, long maximumBytes, CancellationToken cancellationToken) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Reserve(buffer.Length);
            await inner.WriteAsync(buffer, token);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();

        private void Reserve(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumBytes <= 0 || count > maximumBytes - _written)
                throw new AdministrationException(AdministrationFailure.LimitExceeded);
            _written += count;
        }
    }
}
