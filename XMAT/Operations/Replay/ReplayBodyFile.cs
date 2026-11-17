// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Replay
{
    // A request body from a file that the user chose: xmat's --body-file and the composer's Upload File. The whole file is
    // read once into a new array, at most MaxBytes, which also holds when the file grows while it is read. Callers keep
    // the bytes and never the path, so what is sent cannot change after the file was read.
    internal static class ReplayBodyFile
    {
        internal const long MaxBytes = 64L * 1024 * 1024;

        internal const string TooLargeCode = "replay.body.too-large";
        internal const string NotFoundCode = "replay.body.not-found";
        internal const string UnreadableCode = "replay.body.unreadable";

        private const int ChunkBytes = 81920;

        // Throws OperationCanceledException when cancelled; any other failure is a result with its code.
        internal static Task<ReplayBodyRead> ReadAsync(string path, CancellationToken cancellationToken) =>
            ReadAsync(path, MaxBytes, cancellationToken);

        // The file's bytes, refused as soon as they exceed maxBytes: before anything of a longer file's size is allocated,
        // and once a file that grows while it is read passes it. A maxBytes of 0 reads only an empty file.
        internal static async Task<ReplayBodyRead> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    ChunkBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await ReadAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ReplayBodyRead.Failed(NotFoundCode);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
                    NotSupportedException)
            {
                return ReplayBodyRead.Failed(UnreadableCode);
            }
        }

        // The stream's bytes from its position to its end, refused as soon as they exceed maxBytes, whatever its length
        // said at first.
        internal static async Task<ReplayBodyRead> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            long expected = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;
            if (expected > maxBytes)
            {
                return ReplayBodyRead.Failed(TooLargeCode);
            }

            using var body = new MemoryStream((int)expected);
            byte[] chunk = new byte[ChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > maxBytes)
                {
                    return ReplayBodyRead.Failed(TooLargeCode);
                }

                body.Write(chunk, 0, read);
            }

            // The array itself when the file kept the length it had, so a large body is not copied.
            return new ReplayBodyRead(body.Length == body.Capacity ? body.GetBuffer() : body.ToArray(), null);
        }
    }

    // Body is null when the file could not be read, and Code says why.
    internal sealed record ReplayBodyRead(byte[] Body, string Code)
    {
        internal bool Succeeded => Body != null;

        internal static ReplayBodyRead Failed(string code) => new(null, code);
    }
}
